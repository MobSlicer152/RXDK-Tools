namespace Rxdk.Xsasm;

/// <summary>
/// One NV2A vertex-program instruction: 128 bits of bitfields spread across four
/// DWORDs, from microcodeformat.h's D3DVsInstruction.
///
/// A single instruction drives two functional units at once -- the MAC (multiply/
/// accumulate: mov, mul, add, mad, dp3, dp4, dst, min, max, slt, sge, arl) and the
/// ILU (inverse/logic: rcp, rcc, rsq, exp, log, lit) -- reading three shared
/// operand slots A, B and C. That is what makes an instruction "paired": two source
/// operations can occupy one slot when one is a MAC op and the other an ILU op.
/// </summary>
internal sealed class VsInstruction
{
    // MAC opcodes.
    public const uint MacNop = 0x00, MacMov = 0x01, MacMul = 0x02, MacAdd = 0x03;
    public const uint MacMad = 0x04, MacDp3 = 0x05, MacDph = 0x06, MacDp4 = 0x07;
    public const uint MacDst = 0x08, MacMin = 0x09, MacMax = 0x0a, MacSlt = 0x0b;
    public const uint MacSge = 0x0c, MacArl = 0x0d;

    // ILU opcodes.
    public const uint IluNop = 0x00, IluMov = 0x01, IluRcp = 0x02, IluRcc = 0x03;
    public const uint IluRsq = 0x04, IluExp = 0x05, IluLog = 0x06, IluLit = 0x07;

    // Operand mux: which register file a slot reads from.
    public const uint MuxM = 0, MuxR = 1, MuxV = 2, MuxC = 3;

    // Output mux: whether the write takes the MAC's result or the ILU's.
    public const uint OmMac = 0, OmIlu = 1;

    public uint Eos;    // last instruction
    public uint Cin;    // context-indexed addressing (c[a0.x + n])
    public uint Om;     // output mux
    public uint Oc;     // output write control
    public uint Owm;    // output write mask
    public uint Swm;    // secondary register write mask
    public uint Rw;     // register write
    public uint Rwm;    // primary register write mask
    public uint Cmx, Crr, Cws, Czs, Cys, Cxs, Cne;   // operand C
    public uint Bmx, Brr, Bws, Bzs, Bys, Bxs, Bne;   // operand B
    public uint Amx, Arr, Aws, Azs, Ays, Axs, Ane;   // operand A
    public uint Va;     // input buffer address
    public uint Ca;     // context (constant) address
    public uint Mac;    // MAC opcode
    public uint Ilu;    // ILU opcode

    /// <summary>
    /// The four words, in the order they are written to file. X is always zero --
    /// the hardware reads 128-bit instructions but only 96 bits carry data.
    /// </summary>
    public uint[] Pack() => new[] { 0u, WordY(), WordZ(), WordW() };

    private uint WordW() =>
        (Eos << 0) | (Cin << 1) | (Om << 2) | (Oc << 3) | (Owm << 12) |
        (Swm << 16) | (Rw << 20) | (Rwm << 24) | (Cmx << 28) | (Crr << 30);

    private uint WordZ() =>
        (Crr >> 2) | (Cws << 2) | (Czs << 4) | (Cys << 6) | (Cxs << 8) | (Cne << 10) |
        (Bmx << 11) | (Brr << 13) | (Bws << 17) | (Bzs << 19) | (Bys << 21) |
        (Bxs << 23) | (Bne << 25) | (Amx << 26) | (Arr << 28);

    private uint WordY() =>
        (Aws << 0) | (Azs << 2) | (Ays << 4) | (Axs << 6) | (Ane << 8) |
        (Va << 9) | (Ca << 13) | (Mac << 21) | (Ilu << 25);

    /// <summary>Inverse of <see cref="Pack"/>, for reading existing microcode.</summary>
    public static VsInstruction Unpack(uint x, uint y, uint z, uint w)
    {
        static uint F(uint word, int shift, int bits) => (word >> shift) & ((1u << bits) - 1);

        var i = new VsInstruction
        {
            Eos = F(w, 0, 1),
            Cin = F(w, 1, 1),
            Om = F(w, 2, 1),
            Oc = F(w, 3, 9),
            Owm = F(w, 12, 4),
            Swm = F(w, 16, 4),
            Rw = F(w, 20, 4),
            Rwm = F(w, 24, 4),
            Cmx = F(w, 28, 2),

            // crr straddles the word boundary: two low bits at the top of W, two
            // more at the bottom of Z.
            Crr = F(w, 30, 2) | (F(z, 0, 2) << 2),

            Cws = F(z, 2, 2),
            Czs = F(z, 4, 2),
            Cys = F(z, 6, 2),
            Cxs = F(z, 8, 2),
            Cne = F(z, 10, 1),
            Bmx = F(z, 11, 2),
            Brr = F(z, 13, 4),
            Bws = F(z, 17, 2),
            Bzs = F(z, 19, 2),
            Bys = F(z, 21, 2),
            Bxs = F(z, 23, 2),
            Bne = F(z, 25, 1),
            Amx = F(z, 26, 2),
            Arr = F(z, 28, 4),

            Aws = F(y, 0, 2),
            Azs = F(y, 2, 2),
            Ays = F(y, 4, 2),
            Axs = F(y, 6, 2),
            Ane = F(y, 8, 1),
            Va = F(y, 9, 4),
            Ca = F(y, 13, 8),
            Mac = F(y, 21, 4),
            Ilu = F(y, 25, 3),
        };

        _ = x;  // always zero
        return i;
    }
}

/// <summary>What kind of vertex shader a .xvu holds, per its two-character tag.</summary>
internal enum VertexShaderKind
{
    /// <summary>"x " -- an ordinary vertex shader.</summary>
    Ordinary,
    /// <summary>"xw" -- a read/write vertex shader.</summary>
    ReadWrite,
    /// <summary>"xs" -- a vertex state shader.</summary>
    State,
}

/// <summary>
/// The .xvu container: a two-character tag, a WORD instruction count, then that
/// many 16-byte instructions. Note the leading DWORD reads as 0x2078 for an
/// ordinary shader simply because that is 'x' followed by a space.
/// </summary>
internal static class XvuFile
{
    private static readonly char[] KindChars = { ' ', 'w', 's' };

    public static byte[] Write(VertexShaderKind kind, IReadOnlyList<VsInstruction> code)
    {
        var bytes = new byte[4 + code.Count * 16];
        bytes[0] = (byte)'x';
        bytes[1] = (byte)KindChars[(int)kind];
        BitConverter.TryWriteBytes(bytes.AsSpan(2), (ushort)code.Count);

        int o = 4;
        foreach (var i in code)
        {
            foreach (uint word in i.Pack())
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(o), word);
                o += 4;
            }
        }

        return bytes;
    }

    public static (VertexShaderKind Kind, List<VsInstruction> Code) Read(byte[] bytes)
    {
        if (bytes.Length < 4 || bytes[0] != (byte)'x')
            throw new AssemblyException("not a .xvu file");

        int k = Array.IndexOf(KindChars, (char)bytes[1]);
        if (k < 0) throw new AssemblyException($"unknown vertex shader kind '{(char)bytes[1]}'");

        int count = BitConverter.ToUInt16(bytes, 2);
        if (bytes.Length != 4 + count * 16)
            throw new AssemblyException("truncated .xvu file");

        var code = new List<VsInstruction>(count);
        for (int i = 0; i < count; i++)
        {
            int o = 4 + i * 16;
            code.Add(VsInstruction.Unpack(
                BitConverter.ToUInt32(bytes, o),
                BitConverter.ToUInt32(bytes, o + 4),
                BitConverter.ToUInt32(bytes, o + 8),
                BitConverter.ToUInt32(bytes, o + 12)));
        }

        return ((VertexShaderKind)k, code);
    }
}
