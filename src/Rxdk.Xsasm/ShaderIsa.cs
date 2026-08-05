namespace Rxdk.Xsasm;

/// <summary>
/// The D3D8 shader token encoding, as the Xbox assembler uses it. Values are
/// d3d8types.h's D3DSIO_/D3DSP_/D3DVS_ constants -- the intermediate form the
/// assembler produces from source text before lowering to NV2A microcode (vertex)
/// or a D3DPIXELSHADERDEF (pixel).
/// </summary>
internal enum Op
{
    Nop = 0,
    Mov,
    Add,
    Sub,
    Mad,
    Mul,
    Rcp,
    Rsq,
    Dp3,
    Dp4,
    Min,
    Max,
    Slt,
    Sge,
    Exp,
    Log,
    Lit,
    Dst,
    Lrp,
    Frc,
    M4x4,
    M4x3,
    M3x4,
    M3x3,
    M3x2,

    TexCoord = 64,
    TexKill,
    Tex,
    TexBem,
    TexBemL,
    TexReg2Ar,
    TexReg2Gb,
    TexM3x2Pad,
    TexM3x2Tex,
    TexM3x3Pad,
    TexM3x3Tex,
    TexM3x3Diff,
    TexM3x3Spec,
    TexM3x3VSpec,
    Expp,
    Logp,
    Cnd,
    Def,

    // Xbox extensions
    Dph = 256,
    Rcc,
    Xmma,
    Xmmc,
    Xdm,
    Xdd,
    Xfc,
    TexM3x2Depth,
    TexBrdf,

    Comment = 0xFFFE,
    End = 0xFFFF,
}

/// <summary>Register file, in the D3DSPR_ encoding (bits 28..30 of a register token).</summary>
internal enum RegFile
{
    Temp = 0,
    Input = 1,
    Const = 2,

    /// <summary>Address register for vertex shaders; the texture register file for pixel shaders.</summary>
    AddrOrTexture = 3,

    RastOut = 4,
    AttrOut = 5,
    TexCrdOut = 6,
}

internal static class Isa
{
    public const uint RegNumMask = 0x00000FFF;

    public const uint WriteMask0 = 0x00010000;
    public const uint WriteMask1 = 0x00020000;
    public const uint WriteMask2 = 0x00040000;
    public const uint WriteMask3 = 0x00080000;
    public const uint WriteMaskAll = 0x000F0000;

    public const int DstModShift = 20;
    public const uint DstModMask = 0x00F00000;
    public const uint DstModBias = 1u << DstModShift;

    public const int DstShiftShift = 24;
    public const uint DstShiftMask = 0x0F000000;

    public const int RegTypeShift = 28;
    public const uint RegTypeMask = 0x70000000;

    public const int SwizzleShift = 16;
    public const uint SwizzleMask = 0x00FF0000;

    /// <summary>x,y,z,w -- each component takes itself.</summary>
    public const uint NoSwizzle = (0u << SwizzleShift) | (1u << (SwizzleShift + 2)) |
                                  (2u << (SwizzleShift + 4)) | (3u << (SwizzleShift + 6));

    public const int SrcModShift = 24;
    public const uint SrcModMask = 0x0F000000;

    // D3DSPSM_ source modifiers.
    public const uint SrcModNone = 0u << SrcModShift;
    public const uint SrcModNeg = 1u << SrcModShift;
    public const uint SrcModBias = 2u << SrcModShift;
    public const uint SrcModBiasNeg = 3u << SrcModShift;
    public const uint SrcModSign = 4u << SrcModShift;
    public const uint SrcModSignNeg = 5u << SrcModShift;
    public const uint SrcModComp = 6u << SrcModShift;
    public const uint SrcModSat = 7u << SrcModShift;

    /// <summary>Top bit of every non-final token in a D3D8 shader stream.</summary>
    public const uint TokenPresent = 0x80000000;

    public static uint MakeVersion(bool pixel, int major, int minor) =>
        (pixel ? 0xFFFF0000u : 0xFFFE0000u) | (uint)((major << 8) | minor);
}
