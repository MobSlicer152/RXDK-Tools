using System.Text;

namespace Rxdk.Xsasm;

internal enum Tok
{
    Vs = 256, Xvs, Xvsw, Xvss, Ps, Xps, Def,
    Op0, Op1, Op2, Op3, Op4, Op5, Op6, Op7,
    Id, Num, Flt, Eof,
    /// <summary>Any other single character is its own token, carried as (Tok)ch.</summary>
    Char = 0,
}

/// <summary>
/// Tokenizer for Xbox shader assembly. Mirrors CD3DXAssembler::Token(): ';' and
/// '//' start comments, '#line'/'#pragma screenspace' are recognised inline, and
/// any other '#' directive is an error -- the C preprocessor is a separate pass
/// that runs before this one.
/// </summary>
internal sealed class Lexer
{
    private readonly string _src;
    private int _pos;
    private int _lineStart;
    private int _lineNext = 1;

    public string Text = "";        // spelling of the token just returned
    public int Line;                // line the token started on
    public string File = "";        // current file, tracked through #line
    public bool ScreenSpace;        // set by '#pragma screenspace'
    public Op Opcode;               // set when an opcode token is returned
    public uint ShiftSat;           // destination shift/bias decoded from '_x2' etc.

    /// <summary>
    /// False until the version token has been consumed. Before that a '.' cannot
    /// begin a float, or "ps.1.1" would lex as one number instead of three tokens.
    /// </summary>
    public bool VersionSeen;

    /// <summary>Selects the pixel or vertex opcode table.</summary>
    public bool Pixel;

    /// <summary>Enables the Xbox-only vertex opcodes (dph, rcc).</summary>
    public bool Xbox;

    private readonly List<Diagnostic> _diags;

    public Lexer(string source, List<Diagnostic> diags)
    {
        _src = source;
        _diags = diags;
    }

    private static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\f' or '\v';

    public Tok Next()
    {
        while (_pos < _src.Length)
        {
            char ch = _src[_pos];

            if (ch == '\n')
            {
                _pos++;
                _lineStart = _pos;
                _lineNext++;
            }
            else if (IsSpace(ch))
            {
                _pos++;
            }
            else if (ch == '#' && _pos == _lineStart)
            {
                Directive();
            }
            else if (ch == ';' || (ch == '/' && _pos + 1 < _src.Length && _src[_pos + 1] == '/'))
            {
                for (_pos++; _pos < _src.Length && _src[_pos] != '\n'; _pos++) { }
            }
            else if (char.IsLetter(ch))
            {
                Line = _lineNext;
                int start = _pos;
                for (_pos++; _pos < _src.Length; _pos++)
                {
                    char c = _src[_pos];
                    if (char.IsLetterOrDigit(c) || c == '_') continue;
                    // 'c-3' -- a negative constant register, written as one identifier.
                    if (c == '-' && _src[start] == 'c' && _pos == start + 1) continue;
                    break;
                }

                Text = _src[start.._pos];

                switch (Text.ToLowerInvariant())
                {
                    case "vs": return Tok.Vs;
                    case "xvs": return Tok.Xvs;
                    case "xvsw": return Tok.Xvsw;
                    case "xvss": return Tok.Xvss;
                    case "ps": return Tok.Ps;
                    case "xps": return Tok.Xps;
                    case "def": return Tok.Def;
                }

                return Opcodes.Decode(Text, Pixel, Xbox, out Opcode, out ShiftSat);
            }
            else if (char.IsDigit(ch))
            {
                Line = _lineNext;
                int start = _pos;
                var kind = Tok.Num;

                for (_pos++; _pos < _src.Length; _pos++)
                {
                    char c = _src[_pos];
                    if (char.IsDigit(c)) continue;

                    if (VersionSeen && (c == '.' || c == 'e' ||
                        ((c == '+' || c == '-') && _pos > 0 && _src[_pos - 1] == 'e')))
                    {
                        kind = Tok.Flt;
                        continue;
                    }

                    break;
                }

                Text = _src[start.._pos];

                if (VersionSeen && _pos < _src.Length && _src[_pos] == 'f')
                {
                    kind = Tok.Flt;
                    _pos++;
                }

                return kind;
            }
            else
            {
                Line = _lineNext;
                _pos++;
                Text = ch.ToString();
                return (Tok)ch;
            }
        }

        return Tok.Eof;
    }

    private void Directive()
    {
        int start = _pos;
        for (_pos++; _pos < _src.Length && _src[_pos] != '\n'; _pos++) { }
        string line = _src[start.._pos];

        if (line.StartsWith("#line", StringComparison.Ordinal))
        {
            // '#line <num> "<file>"' -- the preprocessor's way of pointing errors back
            // at the original source. -1 because the '\n' ending this line bumps it again.
            var parts = line.Split(' ', '\t');
            foreach (var p in parts.Skip(1))
            {
                if (p.Length > 0 && int.TryParse(p, out int n)) { _lineNext = n - 1; break; }
            }

            int q = line.IndexOf('"');
            if (q >= 0)
            {
                int q2 = line.IndexOf('"', q + 1);
                if (q2 > q)
                {
                    // Collapse the doubled backslashes the preprocessor emits.
                    File = line[(q + 1)..q2].Replace("\\\\", "\\");
                }
            }
        }
        else if (line.StartsWith("#pragma", StringComparison.Ordinal))
        {
            if (line[7..].TrimStart().StartsWith("screenspace", StringComparison.Ordinal))
                ScreenSpace = true;
            else
                _diags.Add(Diagnostic.Warn(File, _lineNext, "unknown pragma"));
        }
        else
        {
            _diags.Add(Diagnostic.Err(File, _lineNext,
                "preprocessor directives are not supported."));
        }
    }
}
