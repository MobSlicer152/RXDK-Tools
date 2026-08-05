namespace Rxdk.Xsasm;

/// <summary>What the version line declared.</summary>
internal enum ShaderKind { Vertex, Pixel }

internal sealed class ParseResult
{
    public ShaderKind Kind;
    public bool Xbox;               // xps/xvs/xvsw/xvss rather than ps/vs
    public bool Writable;           // xvsw/xvss -- read/write vertex shader
    public bool StateShader;        // xvss
    public bool ScreenSpace;        // '#pragma screenspace'
    public uint Version;
    public int VersionMajor;
    public int VersionMinor;

    /// <summary>The D3D8 token stream: version, instructions, then D3DSIO_END.</summary>
    public List<uint> Code = new();

    /// <summary>'def' constants, by register number.</summary>
    public Dictionary<int, float[]> Constants = new();
}

/// <summary>
/// Recursive-descent parser over shadeasm.y's grammar, carrying the same semantic
/// actions CD3DXAssembler::Production() performs. The grammar is small and needs no
/// backtracking beyond one lookahead token, so a table-driven parser buys nothing here.
/// </summary>
internal sealed class Parser
{
    private readonly Lexer _lex;
    private readonly List<Diagnostic> _diags;
    private readonly ParseResult _result = new();

    private Tok _tok;
    private string _text = "";
    private Op _op;
    private uint _shiftSat;
    private int _line;

    public Parser(string source, List<Diagnostic> diags)
    {
        _diags = diags;
        _lex = new Lexer(source, diags);
    }

    private void Advance()
    {
        _tok = _lex.Next();
        _text = _lex.Text;
        _op = _lex.Opcode;
        _shiftSat = _lex.ShiftSat;
        _line = _lex.Line;
    }

    private void Error(string message) =>
        _diags.Add(Diagnostic.Err(_lex.File, _line, message));

    private void Expect(char c)
    {
        if ((char)_tok != c) throw new AssemblyException($"expected '{c}'");
        Advance();
    }

    public ParseResult Parse()
    {
        Advance();
        ParseVersion();      // also switches the lexer to this shader's opcode table

        _result.Code.Add(_result.Version);

        while (_tok == Tok.Def)
        {
            Advance();
            ParseConstant();
        }

        while (_tok != Tok.Eof)
            ParseStatement();

        _result.Code.Add((uint)Op.End);
        _result.ScreenSpace = _lex.ScreenSpace;
        return _result;
    }

    private void ParseVersion()
    {
        ShaderKind kind;
        bool xbox = false, writable = false, state = false;
        int minorBias = 0;

        switch (_tok)
        {
            case Tok.Vs: kind = ShaderKind.Vertex; break;
            case Tok.Ps: kind = ShaderKind.Pixel; break;
            // xps encodes its minor version +10 so the runtime can tell an Xbox
            // pixel shader from a stock DX8 one.
            case Tok.Xps: kind = ShaderKind.Pixel; xbox = true; minorBias = 10; break;
            case Tok.Xvs: kind = ShaderKind.Vertex; xbox = true; break;
            case Tok.Xvsw: kind = ShaderKind.Vertex; xbox = true; writable = true; break;
            case Tok.Xvss: kind = ShaderKind.Vertex; xbox = true; writable = true; state = true; break;
            default: throw new AssemblyException("expected a shader version (vs/ps/xvs/xps/...)");
        }

        Advance();
        Expect('.');
        int major = ParseNumber();
        Expect('.');

        // Read the minor number WITHOUT advancing past it: the very next token is
        // the first instruction, and decoding that needs the opcode table this
        // version selects. Advancing first would lex 'tex' against the vertex table.
        if (_tok != Tok.Num) throw new AssemblyException($"expected a number, saw '{_text}'");
        int minor = int.Parse(_text);

        if (((major | minor) & ~0xff) != 0)
            throw new AssemblyException("invalid version");

        _lex.Pixel = kind == ShaderKind.Pixel;
        _lex.Xbox = xbox;
        _lex.VersionSeen = true;
        Advance();

        _result.Kind = kind;
        _result.Xbox = xbox;
        _result.Writable = writable;
        _result.StateShader = state;
        _result.VersionMajor = major;
        _result.VersionMinor = minor;
        _result.Version = Isa.MakeVersion(kind == ShaderKind.Pixel, major, minor + minorBias);
    }

    private int ParseNumber()
    {
        if (_tok != Tok.Num) throw new AssemblyException($"expected a number, saw '{_text}'");
        int n = int.Parse(_text);
        Advance();
        return n;
    }

    /// <summary>
    /// def cN, f, f, f, f
    ///
    /// The constant is emitted INTO the token stream as a D3DSIO_DEF instruction
    /// followed by the register and four raw floats, not kept beside it: the pixel
    /// back end reads its constants by walking the stream, so a def held anywhere
    /// else silently assembles to a shader whose constants are all zero.
    /// </summary>
    private void ParseConstant()
    {
        uint reg = ParseRegister();
        int num = (int)(reg & Isa.RegNumMask);

        var v = new float[4];
        for (int i = 0; i < 4; i++)
        {
            Expect(',');
            v[i] = ParseValue();
        }

        _result.Constants[num] = v;

        _result.Code.Add((uint)Op.Def);
        _result.Code.Add(reg | Isa.WriteMaskAll);
        foreach (float f in v)
            _result.Code.Add(BitConverter.SingleToUInt32Bits(f));
    }

    private float ParseValue()
    {
        float sign = 1f;
        if ((char)_tok == '+') Advance();
        else if ((char)_tok == '-') { sign = -1f; Advance(); }

        if (_tok is not (Tok.Num or Tok.Flt))
            throw new AssemblyException($"expected a value, saw '{_text}'");

        string t = _text.TrimEnd('f', 'F');
        float f = float.Parse(t, System.Globalization.CultureInfo.InvariantCulture);
        Advance();
        return sign * f;
    }

    private void ParseStatement()
    {
        bool coIssue = false;

        // A leading '+' co-issues this instruction with the previous one.
        if ((char)_tok == '+')
        {
            coIssue = true;
            Advance();
        }

        if (_tok is < Tok.Op0 or > Tok.Op7)
            throw new AssemblyException($"expected an instruction, saw '{_text}'");

        // Operand shape per grammar production. The count is NOT the T_OPn index:
        // T_OP5 is xfc, seven sources and no destination.
        (int operands, int dstCount) = _tok switch
        {
            Tok.Op0 => (0, 0),
            Tok.Op1 => (1, 1),
            Tok.Op2 => (2, 1),
            Tok.Op3 => (3, 1),
            Tok.Op4 => (4, 1),
            Tok.Op5 => (7, 0),   // xfc:       Src x7
            Tok.Op6 => (6, 2),   // xdm/xdd:   Dst x2, Src x4
            _ => (7, 3),         // xmma/xmmc: Dst x3, Src x4
        };

        uint opcode = (uint)_op;
        uint shiftSat = _shiftSat;

        if (coIssue)
        {
            if (_result.Kind == ShaderKind.Vertex && !_result.Xbox)
                Error("Instruction combination is not allowed in a vs shader. Use xvs instead.");
            else
                opcode |= 0x40000000;   // D3DSI_COISSUE
        }

        Advance();

        var parts = new List<uint>();
        for (int i = 0; i < operands; i++)
        {
            if (i > 0) Expect(',');
            parts.Add(i < dstCount ? ParseDst() : ParseSrc());
        }

        // Vertex shaders have no SUB; it is an ADD with the second source negated.
        if (_result.Kind == ShaderKind.Vertex && (Op)(opcode & 0xFFFF) == Op.Sub && parts.Count == 3)
        {
            opcode = (opcode & ~0xFFFFu) | (uint)Op.Add;
            parts[2] ^= Isa.SrcModNeg;
        }

        _result.Code.Add(opcode);
        for (int i = 0; i < parts.Count; i++)
            _result.Code.Add(i == 0 && dstCount > 0 ? parts[i] | shiftSat : parts[i]);
    }

    private uint ParseDst()
    {
        uint reg = ParseRegister();
        uint mask = Isa.WriteMaskAll;

        if ((char)_tok == '.')
        {
            Advance();
            string sel = _text;
            Advance();
            if (!Registers.TryDecodeMask(sel, out mask))
            {
                Error($"invalid mask '{sel}'");
                mask = Isa.WriteMaskAll;
            }
        }

        reg = (reg & ~Isa.WriteMaskAll) | mask;

        if ((reg & Isa.SrcModMask) != 0)
            Error("source modifiers are not allowed on destination registers");

        return reg;
    }

    private uint ParseSrc()
    {
        bool negate = false;
        bool complement = false;

        if ((char)_tok == '-')
        {
            negate = true;
            Advance();
        }
        else if (_tok == Tok.Num && _text == "1")
        {
            // '1-r0' is the complement modifier, not a subtraction.
            Advance();
            Expect('-');
            complement = true;
        }

        uint reg = ParseRegister();
        uint swizzle = Isa.NoSwizzle;

        if ((char)_tok == '.')
        {
            Advance();
            string sel = _text;
            Advance();
            if (!Registers.TryDecodeSwizzle(sel, out swizzle))
            {
                Error($"invalid swizzle '{sel}'");
                swizzle = Isa.NoSwizzle;
            }
        }

        reg = (reg & ~Isa.SwizzleMask) | swizzle;

        if (negate)
        {
            // Negation composes with an existing bias/sign modifier rather than replacing it.
            reg = (reg & Isa.SrcModMask) switch
            {
                Isa.SrcModNone => (reg & ~Isa.SrcModMask) | Isa.SrcModNeg,
                Isa.SrcModBias => (reg & ~Isa.SrcModMask) | Isa.SrcModBiasNeg,
                Isa.SrcModSign => (reg & ~Isa.SrcModMask) | Isa.SrcModSignNeg,
                _ => reg,
            };
        }

        if (complement)
        {
            if (_result.Kind == ShaderKind.Vertex)
                Error("complement not supported in vertex shaders");
            else if ((reg & Isa.SrcModMask) != Isa.SrcModNone)
                Error("complement cannot be used with other modifiers");

            reg = (reg & ~Isa.SrcModMask) | Isa.SrcModComp;
        }

        return reg;
    }

    /// <summary>
    /// A register name, optionally with a '[...]' index. Returns the token with bit
    /// 31 set, which marks a parameter as present in the D3D8 stream.
    /// </summary>
    private uint ParseRegister()
    {
        if (_tok != Tok.Id) throw new AssemblyException($"expected a register, saw '{_text}'");

        string name = _text;
        Advance();

        uint addr = 0;
        bool indexed = false;

        if ((char)_tok == '[')
        {
            Advance();
            addr = ParseOffset();
            Expect(']');
            indexed = true;
        }

        if (!Registers.TryDecode(name, addr, indexed, _result.Kind == ShaderKind.Pixel,
                                 _result.Xbox, out uint token))
        {
            Error($"invalid register '{name}'");
            return Isa.TokenPresent;
        }

        return token | Isa.TokenPresent;
    }

    /// <summary>
    /// The '[...]' index expression: a constant, 'a0.x', or 'a0.x + n'. Relative
    /// addressing sets D3DVS_ADDRMODE_RELATIVE.
    /// </summary>
    private uint ParseOffset()
    {
        uint flags = 0;
        int value = 0;

        while (true)
        {
            int sign = 1;
            if ((char)_tok == '+') Advance();
            else if ((char)_tok == '-') { sign = -1; Advance(); }

            if (_tok == Tok.Num)
            {
                value += sign * int.Parse(_text);
                Advance();
            }
            else if (_tok == Tok.Id)
            {
                // The address register; its swizzle is fixed at .x on this hardware.
                Advance();
                if ((char)_tok == '.') { Advance(); Advance(); }
                flags |= Registers.AddrModeRelative;
            }
            else
            {
                throw new AssemblyException($"invalid index expression at '{_text}'");
            }

            if ((char)_tok != '+' && (char)_tok != '-') break;
        }

        return ((uint)value & Isa.RegNumMask) | flags;
    }
}
