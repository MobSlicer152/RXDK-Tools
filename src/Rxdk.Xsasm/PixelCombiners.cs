namespace Rxdk.Xsasm;

/// <summary>
/// NV2A register-combiner encodings (the PS_* enums in d3d8types.h) and the tables
/// that map D3D8 shader concepts onto them.
/// </summary>
internal static class Ps
{
    // Input mapping -- what a combiner does to an input before using it.
    public const uint UnsignedIdentity = 0x00;
    public const uint UnsignedInvert = 0x20;
    public const uint ExpandNormal = 0x40;
    public const uint ExpandNegate = 0x60;
    public const uint HalfBiasNormal = 0x80;
    public const uint HalfBiasNegate = 0xa0;
    public const uint SignedIdentity = 0xc0;
    public const uint SignedNegate = 0xe0;

    // Combiner registers.
    public const uint RegZero = 0x00;
    public const uint RegDiscard = 0x00;
    public const uint RegC0 = 0x01;
    public const uint RegC1 = 0x02;
    public const uint RegFog = 0x03;

    // There is no literal "1" register: a constant is spelled as the ZERO register
    // put through an input mapping. 1 is invert(0); -1 is expand(0) = 2*0 - 1.
    public const uint RegOne = RegZero | UnsignedInvert;
    public const uint RegNegativeOne = RegZero | ExpandNormal;

    // Channel selects. RGB and BLUE share the value 0 -- which one it means depends
    // on whether the input is feeding the RGB or the alpha combiner.
    public const uint ChannelRgb = 0x00;
    public const uint ChannelBlue = 0x00;
    public const uint ChannelAlpha = 0x10;

    public static uint CombinerInputs(uint a, uint b, uint c, uint d) =>
        (a << 24) | (b << 16) | (c << 8) | d;

    // Output mapping -- the scale/bias applied to a combiner's result.
    public const uint OutIdentity = 0x00;
    public const uint OutBias = 0x08;
    public const uint OutShiftLeft1 = 0x10;
    public const uint OutShiftLeft1Bias = 0x18;
    public const uint OutShiftLeft2 = 0x20;
    public const uint OutShiftRight1 = 0x30;

    public const uint OutAbBlueToAlpha = 0x80;
    public const uint OutCdBlueToAlpha = 0x40;
    public const uint OutAbMultiply = 0x00;
    public const uint OutAbDotProduct = 0x02;
    public const uint OutCdMultiply = 0x00;
    public const uint OutCdDotProduct = 0x01;
    public const uint OutAbCdSum = 0x00;
    public const uint OutAbCdMux = 0x04;

    // Texture addressing modes.
    public const uint TexNone = 0x00;
    public const uint TexProject2D = 0x01;
    public const uint TexProject3D = 0x02;
    public const uint TexCubeMap = 0x03;
    public const uint TexPassThru = 0x04;
    public const uint TexClipPlane = 0x05;
    public const uint TexBumpEnvMap = 0x06;
    public const uint TexBumpEnvMapLum = 0x07;
    public const uint TexBrdf = 0x08;
    public const uint TexDotSt = 0x09;
    public const uint TexDotZw = 0x0a;
    public const uint TexDotRflctDiff = 0x0b;
    public const uint TexDotRflctSpec = 0x0c;
    public const uint TexDotStr3D = 0x0d;
    public const uint TexDotStrCube = 0x0e;
    public const uint TexDpndntAr = 0x0f;
    public const uint TexDpndntGb = 0x10;
    public const uint TexDotProduct = 0x11;
    public const uint TexDotRflctSpecConst = 0x12;

    public const uint DotMappingZeroToOne = 0x00;

    /// <summary>Sentinel for a combiner constant slot that no D3D constant claims yet.</summary>
    public const uint ConstantUnused = 0xFFFFFFFF;

    public const int MaxCombinerStages = 8;
    public const int MaxShaderStages = 4;
    public const int MaxConstants = 8;

    public static uint TextureModes(uint t0, uint t1, uint t2, uint t3) =>
        (t3 << 15) | (t2 << 10) | (t1 << 5) | t0;

    public static uint DotMapping(uint t1, uint t2, uint t3) =>
        (t3 << 8) | (t2 << 4) | t1;

    public static uint CombinerCount(uint count, uint flags) => (flags << 8) | count;

    public static uint CombinerOutputs(uint ab, uint cd, uint muxSum, uint flags) =>
        (flags << 12) | (muxSum << 8) | (ab << 4) | cd;

    /// <summary>D3DSPSM_* source modifier -> NV2A input mapping.</summary>
    public static readonly uint[] D3DModToNvMod =
    {
        SignedIdentity,     // D3DSPSM_NONE
        SignedNegate,       // D3DSPSM_NEG
        HalfBiasNormal,     // D3DSPSM_BIAS
        HalfBiasNegate,     // D3DSPSM_BIASNEG
        ExpandNormal,       // D3DSPSM_SIGN
        ExpandNegate,       // D3DSPSM_SIGNNEG
        UnsignedInvert,     // D3DSPSM_COMP
        UnsignedIdentity,   // D3DSPSM_SAT
    };

    /// <summary>
    /// The complement of an input mapping. Note SIGNED_IDENTITY inverts to
    /// UNSIGNED_INVERT rather than to a signed form -- the combiners have no
    /// "signed invert", so the mapping is deliberately lossy here.
    /// </summary>
    public static readonly uint[] NvModToNvModInvert =
    {
        UnsignedInvert,     // from UNSIGNED_IDENTITY
        UnsignedIdentity,   // from UNSIGNED_INVERT
        ExpandNormal,       // from EXPAND_NORMAL
        ExpandNegate,       // from EXPAND_NEGATE
        HalfBiasNormal,     // from HALFBIAS_NORMAL
        HalfBiasNegate,     // from HALFBIAS_NEGATE
        UnsignedInvert,     // from SIGNED_IDENTITY
        SignedNegate,       // from SIGNED_NEGATE
    };

    /// <summary>
    /// [register file][register number] -> combiner register. This is where the
    /// pixel shader's r0/r1, v0/v1, c0/c1 and t0..t3 land in the combiner register
    /// space -- note r0 is 0xC, not 0, and that 'zero' (r2) maps to the ZERO register.
    /// </summary>
    public static readonly uint[][] TypeOffsetToCombinerReg =
    {
        new uint[] { 0xC, 0xD, 0x0, 0x3 },   // TEMP:    r0, r1, zero, fog
        new uint[] { 0x4, 0x5, 0xE, 0xF },   // INPUT:   v0, v1, sum, prod
        new uint[] { 0x1, 0x2, 0x0, 0x0 },   // CONST:   c0, c1
        new uint[] { 0x8, 0x9, 0xA, 0xB },   // TEXTURE: t0..t3
        new uint[] { 0x0, 0x0, 0x0, 0x0 },
        new uint[] { 0x0, 0x0, 0x0, 0x0 },
    };

    /// <summary>Texture opcode (offset from D3DSIO_TEXCOORD) -> texture addressing mode.</summary>
    public static readonly uint[] D3DOpToTexMode =
    {
        TexPassThru,            // texcoord
        TexClipPlane,           // texkill
        TexProject2D,           // tex
        TexBumpEnvMap,          // texbem
        TexBumpEnvMapLum,       // texbeml
        TexDpndntAr,            // texreg2ar
        TexDpndntGb,            // texreg2gb
        TexDotProduct,          // texm3x2pad
        TexDotSt,               // texm3x2tex
        TexDotProduct,          // texm3x3pad
        TexDotStr3D,            // texm3x3tex
        TexDotRflctDiff,        // texm3x3diff
        TexDotRflctSpecConst,   // texm3x3spec
        TexDotRflctSpec,        // texm3x3vspec
    };

    /// <summary>
    /// Destination shift + bias -> combiner output mapping. Only NONE and X2
    /// have a biased form; the hardware has no biased x4 or /2.
    /// </summary>
    public static uint ShiftAndBiasToMap(uint shift, uint bias)
    {
        if (bias != 0)
        {
            return shift switch
            {
                0 => OutBias,
                1 => OutShiftLeft1Bias,
                _ => OutIdentity,
            };
        }

        return shift switch
        {
            0 => OutIdentity,
            1 => OutShiftLeft1,
            2 => OutShiftLeft2,
            0xF => OutShiftRight1,   // -1, as a 4-bit field
            _ => OutIdentity,
        };
    }
}
