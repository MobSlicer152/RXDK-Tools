using System.Text;

namespace Rxdk.Xsasm;

/// <summary>
/// The C preprocessor pass that runs before assembly, as it does in the original
/// (xsasm /p runs only this, /P skips it). Shader sources use it for #include and
/// for configuration flags — the Fur sample builds eight fin_wind*_local*_self*
/// variants by #define-ing flags and #include-ing one shared shader.
///
/// Output is text carrying '#line N "file"' directives, which is how the assembler
/// reports errors against the original source rather than the expanded stream.
/// </summary>
internal sealed class Preprocessor
{
    private readonly List<string> _includePaths;
    private readonly Dictionary<string, string> _macros = new(StringComparer.Ordinal);
    private readonly List<Diagnostic> _diags;
    private readonly StringBuilder _out = new();

    private string _lastFile = "";
    private int _lastLine = -1;

    /// <summary>Guards against an #include cycle.</summary>
    private readonly HashSet<string> _active = new(StringComparer.OrdinalIgnoreCase);

    public Preprocessor(IEnumerable<string> includePaths, IEnumerable<string> defines,
                        List<Diagnostic> diags)
    {
        _includePaths = includePaths.ToList();
        _diags = diags;

        foreach (string d in defines)
        {
            int eq = d.IndexOf('=');
            if (eq < 0) _macros[d] = "";
            else _macros[d[..eq]] = d[(eq + 1)..];
        }
    }

    public string Process(string path)
    {
        Include(path);
        return _out.ToString();
    }

    private void Include(string path)
    {
        string full = Path.GetFullPath(path);

        if (!_active.Add(full))
        {
            _diags.Add(Diagnostic.Err(path, 0, "#include cycle"));
            return;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(full);
        }
        catch (IOException)
        {
            _diags.Add(Diagnostic.Err(path, 0, $"cannot open '{path}'"));
            _active.Remove(full);
            return;
        }

        // One entry per open conditional: true while its branch is being emitted.
        var conditionals = new Stack<CondState>();

        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            string trimmed = line.TrimStart();
            bool emitting = conditionals.All(c => c.Emitting);

            // A directive is a '#' first on the line. ';' and '//' start comments, so
            // '; No wind  #define WIND' is a comment and must NOT define anything --
            // which is exactly how the Fur shaders spell a disabled flag.
            if (trimmed.StartsWith('#'))
            {
                Directive(trimmed[1..].TrimStart(), full, i + 1, conditionals, emitting);
                continue;
            }

            if (!emitting) continue;

            // NVASM-style macros: 'macro name params...' / body / 'endm'. Distinct
            // from #define -- these span lines and take arguments, referenced in the
            // body as %param.
            string[] words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            if (words.Length >= 2 && words[0] == "macro")
            {
                var m = new NvasmMacro { Parameters = words.Skip(2).ToArray() };

                while (++i < lines.Length &&
                       lines[i].TrimStart().Split((char[]?)null,
                           StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != "endm")
                {
                    m.Body.Add(lines[i]);
                }

                _nvasmMacros[words[1]] = m;
                continue;
            }

            if (words.Length >= 1 && _nvasmMacros.TryGetValue(words[0], out var macro))
            {
                string[] arguments = words.Skip(1).ToArray();

                foreach (string bodyLine in macro.Body)
                {
                    string expanded = bodyLine;
                    for (int p = 0; p < macro.Parameters.Length && p < arguments.Length; p++)
                        expanded = expanded.Replace("%" + macro.Parameters[p], arguments[p]);

                    // Attributed to the invocation, so an error in expanded code
                    // points at the call rather than at the definition.
                    Emit(full, i + 1, Substitute(expanded));
                }

                _lastLine = -1;
                continue;
            }

            Emit(full, i + 1, Substitute(line));
        }

        if (conditionals.Count > 0)
            _diags.Add(Diagnostic.Err(path, lines.Length, "unterminated #ifdef/#ifndef"));

        _active.Remove(full);
    }

    /// <summary>A multi-line NVASM macro: 'macro name p1 p2' / body / 'endm'.</summary>
    private sealed class NvasmMacro
    {
        public string[] Parameters = Array.Empty<string>();
        public List<string> Body = new();
    }

    private readonly Dictionary<string, NvasmMacro> _nvasmMacros = new(StringComparer.Ordinal);

    private sealed class CondState
    {
        public bool Emitting;       // is this branch the live one
        public bool TakenAlready;   // has any branch of this conditional been taken
    }

    private void Directive(string body, string file, int line,
                           Stack<CondState> conditionals, bool emitting)
    {
        string name = new string(body.TakeWhile(c => char.IsLetter(c)).ToArray());
        string rest = body[name.Length..].Trim();

        switch (name)
        {
            case "ifdef":
            case "ifndef":
            {
                string sym = FirstToken(rest);
                bool defined = _macros.ContainsKey(sym);
                bool take = emitting && (name == "ifdef" ? defined : !defined);
                conditionals.Push(new CondState { Emitting = take, TakenAlready = take });
                return;
            }

            case "else":
            {
                if (conditionals.Count == 0)
                {
                    _diags.Add(Diagnostic.Err(file, line, "#else without #ifdef"));
                    return;
                }

                var top = conditionals.Peek();
                bool outer = conditionals.Skip(1).All(c => c.Emitting);
                top.Emitting = outer && !top.TakenAlready;
                top.TakenAlready = true;
                return;
            }

            case "endif":
                if (conditionals.Count == 0)
                    _diags.Add(Diagnostic.Err(file, line, "#endif without #ifdef"));
                else
                    conditionals.Pop();
                return;
        }

        // Everything below only acts inside a live branch.
        if (!emitting) return;

        switch (name)
        {
            case "define":
            {
                string sym = FirstToken(rest);
                _macros[sym] = rest[sym.Length..].Trim();
                return;
            }

            case "undef":
                _macros.Remove(FirstToken(rest));
                return;

            case "include":
            {
                string? target = IncludeTarget(rest);
                if (target is null)
                {
                    _diags.Add(Diagnostic.Err(file, line, "malformed #include"));
                    return;
                }

                string? resolved = Resolve(target, Path.GetDirectoryName(file) ?? ".");
                if (resolved is null)
                {
                    _diags.Add(Diagnostic.Err(file, line, $"cannot find include '{target}'"));
                    return;
                }

                Include(resolved);

                // Force a #line so the rest of this file is attributed to it again.
                _lastLine = -1;
                return;
            }

            case "pragma":
                // Passed through untouched -- '#pragma screenspace' is the assembler's.
                Emit(file, line, "#pragma " + rest);
                return;

            default:
                _diags.Add(Diagnostic.Warn(file, line, $"ignoring unsupported directive '#{name}'"));
                return;
        }
    }

    private static string FirstToken(string s)
    {
        int n = 0;
        while (n < s.Length && (char.IsLetterOrDigit(s[n]) || s[n] == '_')) n++;
        return s[..n];
    }

    private static string? IncludeTarget(string rest)
    {
        if (rest.StartsWith('"'))
        {
            int end = rest.IndexOf('"', 1);
            return end > 0 ? rest[1..end] : null;
        }

        if (rest.StartsWith('<'))
        {
            int end = rest.IndexOf('>', 1);
            return end > 0 ? rest[1..end] : null;
        }

        return null;
    }

    private string? Resolve(string target, string relativeTo)
    {
        // The including file's own directory first, then the -I paths in order.
        string local = Path.Combine(relativeTo, target);
        if (File.Exists(local)) return local;

        foreach (string dir in _includePaths)
        {
            string candidate = Path.Combine(dir, target);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// Object-like macro replacement on identifier boundaries. Shader sources have
    /// no function-like macros, so there are no arguments to expand; the loop is
    /// bounded to stop a self-referential #define from spinning.
    /// </summary>
    private string Substitute(string line)
    {
        if (_macros.Count == 0) return line;

        for (int pass = 0; pass < 16; pass++)
        {
            var sb = new StringBuilder(line.Length);
            bool changed = false;
            int i = 0;

            while (i < line.Length)
            {
                char c = line[i];

                if (!char.IsLetter(c) && c != '_')
                {
                    sb.Append(c);
                    i++;
                    continue;
                }

                int start = i;
                while (i < line.Length && (char.IsLetterOrDigit(line[i]) || line[i] == '_')) i++;
                string word = line[start..i];

                if (_macros.TryGetValue(word, out string? replacement) && replacement != word)
                {
                    sb.Append(replacement);
                    changed = true;
                }
                else
                {
                    sb.Append(word);
                }
            }

            line = sb.ToString();
            if (!changed) break;
        }

        return line;
    }

    private void Emit(string file, int line, string text)
    {
        if (file != _lastFile || line != _lastLine + 1)
        {
            // The assembler's lexer collapses doubled backslashes, so double them here.
            _out.Append("#line ").Append(line).Append(" \"")
                .Append(file.Replace("\\", "\\\\")).Append("\"\n");
        }

        _out.Append(text).Append('\n');
        _lastFile = file;
        _lastLine = line;
    }
}
