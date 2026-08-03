// Faithful port of the XDK bundler's token.cpp (tokenizer) plus the token-id
// enums from bundler.h. Reads the .rdf resource-description grammar: an ASCII
// stream with // and /* */ comments, whitespace/comma separators, {} braces,
// quoted strings, and typed tokens (identifier/integer/filename/etc).

namespace Rxdk.Bundler;

internal enum TokType { Any, Identifier, HexNumber, Integer, Float, Filename }

/// <summary>Token ids, matching TOKEN_ID in bundler.h (bit-encoded so a property carries its resource type).</summary>
internal static class Tok
{
    public const uint OPENBRACE = 0x00000001;
    public const uint CLOSEBRACE = 0x00000002;
    public const uint EOF = 0x00000003;
    public const uint OUT_VERSION = 0x00000004;
    public const uint OUT_PACKEDRESOURCE = 0x00000005;
    public const uint OUT_HEADER = 0x00000006;
    public const uint OUT_PREFIX = 0x00000007;
    public const uint OUT_ERROR = 0x00000008;

    public const uint RESOURCE_TEXTURE = 0x00010000;
    public const uint RESOURCE_CUBEMAP = 0x00020000;
    public const uint RESOURCE_VOLUMETEXTURE = 0x00040000;
    public const uint RESOURCE_VERTEXBUFFER = 0x00080000;
    public const uint RESOURCE_USERDATA = 0x00100000;
    public const uint RESOURCE_INDEXBUFFER = 0x00200000;

    public const uint PROPERTY = 0x80000000;
    public const uint PROPERTY_NAME = PROPERTY | 0x00000001;

    public const uint PROPERTY_TEXTURE = PROPERTY | RESOURCE_TEXTURE;
    public const uint PROPERTY_TEXTURE_SOURCE = PROPERTY_TEXTURE | 0x00000001;
    public const uint PROPERTY_TEXTURE_ALPHASOURCE = PROPERTY_TEXTURE | 0x00000002;
    public const uint PROPERTY_TEXTURE_FILTER = PROPERTY_TEXTURE | 0x00000003;
    public const uint PROPERTY_TEXTURE_FORMAT = PROPERTY_TEXTURE | 0x00000004;
    public const uint PROPERTY_TEXTURE_WIDTH = PROPERTY_TEXTURE | 0x00000005;
    public const uint PROPERTY_TEXTURE_HEIGHT = PROPERTY_TEXTURE | 0x00000006;
    public const uint PROPERTY_TEXTURE_LEVELS = PROPERTY_TEXTURE | 0x00000007;

    public const uint PROPERTY_CUBEMAP = PROPERTY | RESOURCE_CUBEMAP;
    public const uint PROPERTY_CUBEMAP_SOURCE_XP = PROPERTY_CUBEMAP | 0x00000001;
    public const uint PROPERTY_CUBEMAP_SOURCE_XN = PROPERTY_CUBEMAP | 0x00000002;
    public const uint PROPERTY_CUBEMAP_SOURCE_YP = PROPERTY_CUBEMAP | 0x00000003;
    public const uint PROPERTY_CUBEMAP_SOURCE_YN = PROPERTY_CUBEMAP | 0x00000004;
    public const uint PROPERTY_CUBEMAP_SOURCE_ZP = PROPERTY_CUBEMAP | 0x00000005;
    public const uint PROPERTY_CUBEMAP_SOURCE_ZN = PROPERTY_CUBEMAP | 0x00000006;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_XP = PROPERTY_CUBEMAP | 0x00000011;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_XN = PROPERTY_CUBEMAP | 0x00000012;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_YP = PROPERTY_CUBEMAP | 0x00000013;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_YN = PROPERTY_CUBEMAP | 0x00000014;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_ZP = PROPERTY_CUBEMAP | 0x00000015;
    public const uint PROPERTY_CUBEMAP_ALPHASOURCE_ZN = PROPERTY_CUBEMAP | 0x00000016;
    public const uint PROPERTY_CUBEMAP_SIZE = PROPERTY_CUBEMAP | 0x00000022;

    public const uint PROPERTY_VOLUMETEXTURE = PROPERTY | RESOURCE_VOLUMETEXTURE;
    public const uint PROPERTY_VOLUMETEXTURE_DEPTH = PROPERTY_VOLUMETEXTURE | 0x00000003;

    public const uint PROPERTY_VERTEXBUFFER = PROPERTY | RESOURCE_VERTEXBUFFER;
    public const uint PROPERTY_VERTEXBUFFER_VERTEXDATA = PROPERTY_VERTEXBUFFER | 0x01000001;
    public const uint PROPERTY_VERTEXBUFFER_VERTEXFORMAT = PROPERTY_VERTEXBUFFER | 0x01000002;
    public const uint PROPERTY_VERTEXBUFFER_VERTEXFILE = PROPERTY_VERTEXBUFFER | 0x01000003;

    public const uint PROPERTY_USERDATA = PROPERTY | RESOURCE_USERDATA;
    public const uint PROPERTY_USERDATA_DATAFILE = PROPERTY_USERDATA | 0x00000001;

    public const uint PROPERTY_INDEXBUFFER = PROPERTY | RESOURCE_INDEXBUFFER;
    public const uint PROPERTY_INDEXBUFFER_INDEXDATA = PROPERTY_INDEXBUFFER | 0x00000001;
    public const uint PROPERTY_INDEXBUFFER_INDEXFILE = PROPERTY_INDEXBUFFER | 0x00000002;
}

internal readonly record struct BundlerToken(string Keyword, uint Id, TokType PropType);

/// <summary>Raised when a token fails validation or an unknown keyword is seen (mirrors the tool's error+exit).</summary>
internal sealed class BundlerException : Exception
{
    public BundlerException(string message) : base(message) { }
}

/// <summary>Reads the .rdf byte stream and yields tokens. Port of CBundler's Token.cpp methods.</summary>
internal sealed class RdfReader
{
    private const byte TOKEOF = 0xFF;

    private readonly byte[] _buf;
    private int _pos;

    // 4-char read-ahead, primed to spaces exactly like CBundler's constructor.
    private byte _n0 = (byte)' ', _n1 = (byte)' ', _n2 = (byte)' ', _n3 = (byte)' ';

    public RdfReader(byte[] rdfBytes) => _buf = rdfBytes;

    private byte ReadRaw() => _pos < _buf.Length ? _buf[_pos++] : TOKEOF;

    // Token table (token.cpp g_Tokens). Handler dispatch is done by the caller on Id.
    private static readonly BundlerToken[] Tokens =
    {
        new("", Tok.EOF, TokType.Any),
        new("{", Tok.OPENBRACE, TokType.Any),
        new("}", Tok.CLOSEBRACE, TokType.Any),
        new("out_version", Tok.OUT_VERSION, TokType.Any),
        new("out_packedresource", Tok.OUT_PACKEDRESOURCE, TokType.Any),
        new("out_header", Tok.OUT_HEADER, TokType.Any),
        new("out_prefix", Tok.OUT_PREFIX, TokType.Any),
        new("out_error", Tok.OUT_ERROR, TokType.Any),

        new("Name", Tok.PROPERTY_NAME, TokType.Any),

        new("Texture", Tok.RESOURCE_TEXTURE, TokType.Any),
        new("Source", Tok.PROPERTY_TEXTURE_SOURCE, TokType.Filename),
        new("AlphaSource", Tok.PROPERTY_TEXTURE_ALPHASOURCE, TokType.Filename),
        new("Filter", Tok.PROPERTY_TEXTURE_FILTER, TokType.Any),
        new("Format", Tok.PROPERTY_TEXTURE_FORMAT, TokType.Identifier),
        new("Width", Tok.PROPERTY_TEXTURE_WIDTH, TokType.Integer),
        new("Height", Tok.PROPERTY_TEXTURE_HEIGHT, TokType.Integer),
        new("Levels", Tok.PROPERTY_TEXTURE_LEVELS, TokType.Integer),

        new("Cubemap", Tok.RESOURCE_CUBEMAP, TokType.Any),
        new("SourceXP", Tok.PROPERTY_CUBEMAP_SOURCE_XP, TokType.Filename),
        new("SourceXN", Tok.PROPERTY_CUBEMAP_SOURCE_XN, TokType.Filename),
        new("SourceYP", Tok.PROPERTY_CUBEMAP_SOURCE_YP, TokType.Filename),
        new("SourceYN", Tok.PROPERTY_CUBEMAP_SOURCE_YN, TokType.Filename),
        new("SourceZP", Tok.PROPERTY_CUBEMAP_SOURCE_ZP, TokType.Filename),
        new("SourceZN", Tok.PROPERTY_CUBEMAP_SOURCE_ZN, TokType.Filename),
        new("AlphaSourceXP", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_XP, TokType.Filename),
        new("AlphaSourceXN", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_XN, TokType.Filename),
        new("AlphaSourceYP", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_YP, TokType.Filename),
        new("AlphaSourceYN", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_YN, TokType.Filename),
        new("AlphaSourceZP", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_ZP, TokType.Filename),
        new("AlphaSourceZN", Tok.PROPERTY_CUBEMAP_ALPHASOURCE_ZN, TokType.Filename),
        new("Size", Tok.PROPERTY_CUBEMAP_SIZE, TokType.Integer),

        new("VolumeTexture", Tok.RESOURCE_VOLUMETEXTURE, TokType.Any),
        new("Depth", Tok.PROPERTY_VOLUMETEXTURE_DEPTH, TokType.Integer),

        new("VertexBuffer", Tok.RESOURCE_VERTEXBUFFER, TokType.Any),
        new("VertexData", Tok.PROPERTY_VERTEXBUFFER_VERTEXDATA, TokType.Any),
        new("VertexFormat", Tok.PROPERTY_VERTEXBUFFER_VERTEXFORMAT, TokType.Any),
        new("VertexFile", Tok.PROPERTY_VERTEXBUFFER_VERTEXFILE, TokType.Filename),

        new("UserData", Tok.RESOURCE_USERDATA, TokType.Any),
        new("DataFile", Tok.PROPERTY_USERDATA_DATAFILE, TokType.Any),

        new("IndexBuffer", Tok.RESOURCE_INDEXBUFFER, TokType.Any),
        new("IndexData", Tok.PROPERTY_INDEXBUFFER_INDEXDATA, TokType.Any),
        new("IndexFile", Tok.PROPERTY_INDEXBUFFER_INDEXFILE, TokType.Filename),
    };

    // --- character classes (token.cpp) ---------------------------------------
    private static bool IsAlpha(byte c) => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z');
    private static bool IsNumber(byte c) => c >= '0' && c <= '9';
    private static bool IsIdentifier(byte c) => IsAlpha(c) || IsNumber(c) || c == '_';
    private static bool IsHex(byte c) => (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F') || IsNumber(c);
    private static bool IsWhiteSpace(byte c) => c == '\t' || c == '\r' || c == '\n' || c == ' ' || c == ',';

    private static bool IsFilenameChar(byte c) =>
        c != '[' && c != ']' && c != ':' && c != '\\' && c != '/' && c != '<' && c != '>' &&
        c != '=' && c != ';' && c != ',' && c != '\t' && c != '\r' && c != '\n';

    private static bool TerminatesToken(byte c, bool inQuotes)
    {
        if (c == TOKEOF || c == '\r' || c == '\n') return true;
        if (!inQuotes)
        {
            if (c == '\t' || c == ' ' || c == ',') return true;
            if (c == '{' || c == '}') return true;
        }
        return false;
    }

    /// <summary>Port of CBundler::GetChar — advances the stream, stripping // and /* */ comments.</summary>
    private byte GetChar()
    {
        byte tmp = _n0;
        _n0 = _n1;
        _n1 = _n2;
        _n2 = _n3;
        _n3 = ReadRaw();

        if (_n0 == 0xff && _n1 == 0xfe)
            throw new BundlerException("Unicode files are not supported");

        // // comment: skip to newline/eof
        if (_n2 == '/' && _n3 == '/')
        {
            while (_n2 != '\n' && _n2 != TOKEOF)
            {
                _n2 = _n3;
                _n3 = ReadRaw();
            }
        }
        // /* comment: skip to */ or eof
        if (_n2 == '/' && _n3 == '*')
        {
            while (!((_n2 == '*' && _n3 == '/') || _n2 == TOKEOF))
            {
                _n2 = _n3;
                _n3 = ReadRaw();
            }
            _n2 = ReadRaw();
            _n3 = ReadRaw();
        }
        return tmp;
    }

    private byte PeekChar() => _n0;

    /// <summary>Port of CBundler::GetNextTokenString. Returns the raw token text (may be empty at EOF).</summary>
    public string GetNextTokenString(TokType tt)
    {
        var sb = new System.Text.StringBuilder();

        byte c;
        while (IsWhiteSpace(c = PeekChar()))
            GetChar();

        if (c == TOKEOF)
            return "";

        if (c == '{' || c == '}')
            return ((char)GetChar()).ToString();

        bool inQuotes = false;
        while (true)
        {
            c = PeekChar();
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                GetChar();
            }
            else
            {
                if (TerminatesToken(c, inQuotes))
                    break;
                sb.Append((char)GetChar());
            }
        }

        string token = sb.ToString();
        if (!ValidateType(token, tt))
            throw new BundlerException($"Token <{token}> is not a valid {tt}");
        return token;
    }

    private static bool ValidateType(string s, TokType tt) => tt switch
    {
        TokType.Any => true,
        TokType.Identifier => ValidateIdentifier(s),
        TokType.HexNumber => ValidateHexNumber(s),
        TokType.Integer => ValidateInteger(s),
        TokType.Float => ValidateFloat(s),
        TokType.Filename => ValidateFilename(s),
        _ => true,
    };

    private static bool ValidateIdentifier(string s)
    {
        if (s.Length == 0 || !IsAlpha((byte)s[0])) return false;
        for (int i = 1; i < s.Length; i++)
            if (!IsIdentifier((byte)s[i])) return false;
        return true;
    }

    private static bool ValidateHexNumber(string s)
    {
        if (s.Length < 2 || s[0] != '0' || (s[1] != 'x' && s[1] != 'X')) return false;
        for (int i = 2; i < s.Length; i++)
            if (!IsHex((byte)s[i])) return false;
        return true;
    }

    private static bool ValidateInteger(string s)
    {
        int i = 0;
        if (s.Length > 0 && s[0] == '-') i++;
        for (; i < s.Length; i++)
            if (!IsNumber((byte)s[i])) return false;
        return true;
    }

    private static bool ValidateFloat(string s)
    {
        int i = 0;
        bool dec = false, exp = false;
        if (s.Length > 0 && s[0] == '-') i++;
        for (; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '.')
            {
                if (dec) return false;
                dec = true;
            }
            else if (ch == 'e' || ch == 'E')
            {
                if (exp) return false;
                dec = exp = true;
                if (i + 1 < s.Length && (s[i + 1] == '+' || s[i + 1] == '-')) i++;
            }
            else if (!IsNumber((byte)ch))
            {
                return false;
            }
        }
        return true;
    }

    private static bool ValidateFilename(string s)
    {
        int i = 0;
        bool lastWasSlash = false;
        if (s.Length >= 2 && IsAlpha((byte)s[0]) && s[1] == ':') i = 2;
        for (; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\' || ch == '/') { lastWasSlash = true; continue; }
            if (!IsFilenameChar((byte)ch)) return false;
            lastWasSlash = false;
        }
        return !lastWasSlash;
    }

    /// <summary>Port of GetTokenFromString — case-insensitive keyword lookup.</summary>
    public BundlerToken GetTokenFromString(string s)
    {
        foreach (var t in Tokens)
            if (string.Equals(s, t.Keyword, StringComparison.OrdinalIgnoreCase))
                return t;
        throw new BundlerException($"Unknown token <{s}>");
    }

    /// <summary>Port of GetNextToken — reads the next token string and resolves it.</summary>
    public BundlerToken GetNextToken() => GetTokenFromString(GetNextTokenString(TokType.Any));
}
