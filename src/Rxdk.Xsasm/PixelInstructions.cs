namespace Rxdk.Xsasm;

/// <summary>
/// Operands of one decoded pixel-shader instruction, already mapped into combiner
/// space by <see cref="PixelShaderCompiler"/>.
/// </summary>
internal sealed class PsOperands
{
    public uint[] Dst = new uint[4];
    public uint[] OutputMap = new uint[4];
    public uint[] Mask = new uint[4];
    public uint[] Src = new uint[8];
    public uint[] Swizzle = new uint[8];
    public uint[] InputMod = new uint[8];
}

/// <summary>
/// Per-instruction lowering onto a combiner stage, from pixelshader.cpp's
/// Instruction* handlers.
///
/// Every general stage has an RGB half and an alpha half, each computing
/// AB, CD and a third output that is either AB+CD or a mux between them. An
/// instruction is expressed by choosing what to feed A/B/C/D and where the three
/// outputs go -- so 'mov' is A*1 with C and D zeroed, 'add' is A*1 + C*1, and
/// 'sub' is A*1 + C*(-1). The colour and alpha halves are programmed separately,
/// which is why each handler has two arms and why co-issue works at all.
/// </summary>
internal static class PixelInstructions
{
    public const bool Colour = true;
    public const bool Alpha = false;

    private static uint RgbChannel(uint swizzle) =>
        // .a replicated across the source selects the alpha channel as an RGB input.
        swizzle == ReplicateAlpha ? Ps.ChannelAlpha : Ps.ChannelRgb;

    private static uint AlphaChannel(uint swizzle) =>
        // .b replicated into alpha selects the blue channel as the alpha input.
        (swizzle & (3u << (Isa.SwizzleShift + 6))) == (2u << (Isa.SwizzleShift + 6))
            ? Ps.ChannelBlue
            : Ps.ChannelAlpha;

    /// <summary>A swizzle of .aaaa -- every component takes w.</summary>
    private const uint ReplicateAlpha =
        (3u << Isa.SwizzleShift) | (3u << (Isa.SwizzleShift + 2)) |
        (3u << (Isa.SwizzleShift + 4)) | (3u << (Isa.SwizzleShift + 6));

    /// <summary>Assembles one combiner input slot: register, input mapping, channel.</summary>
    private static uint In(PsOperands o, int i, bool colour) =>
        o.Src[i] | o.InputMod[i] |
        (colour ? RgbChannel(o.Swizzle[i]) : AlphaChannel(o.Swizzle[i]));

    public static void Emit(Op op, PixelShaderDef psd, int stage, bool colour, PsOperands o)
    {
        switch (op)
        {
            case Op.Mov: Mov(psd, stage, colour, o); break;
            case Op.Add: Add(psd, stage, colour, o); break;
            case Op.Sub: Sub(psd, stage, colour, o); break;
            case Op.Mad: Mad(psd, stage, colour, o); break;
            case Op.Mul: Mul(psd, stage, colour, o); break;
            case Op.Dp3: Dp3(psd, stage, colour, o); break;
            case Op.Lrp: Lrp(psd, stage, colour, o); break;
            case Op.Cnd: Cnd(psd, stage, colour, o); break;
            case Op.Xmma: Extended(psd, stage, colour, o, Ps.OutAbCdSum, Ps.OutAbMultiply, Ps.OutCdMultiply, o.Dst[2]); break;
            case Op.Xmmc: Extended(psd, stage, colour, o, Ps.OutAbCdMux, Ps.OutAbMultiply, Ps.OutCdMultiply, o.Dst[2]); break;
            case Op.Xdm: Xdm(psd, stage, colour, o); break;
            case Op.Xdd: Xdd(psd, stage, colour, o); break;
            case Op.Xfc: Xfc(psd, o); break;
            default:
                throw new AssemblyException($"instruction '{op}' is not valid in a pixel shader");
        }
    }

    // dst = src0.  A*B + C*D with B=1, C=D=0.
    private static void Mov(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelRgb,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelAlpha,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelAlpha,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelAlpha);
            p.AlphaOutputs[s] = Ps.CombinerOutputs(o.Dst[0], Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src0 * src1.  A*B, with C=D=0.
    private static void Mul(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c),
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c),
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelAlpha,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelAlpha);
            p.AlphaOutputs[s] = Ps.CombinerOutputs(o.Dst[0], Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src0 . src1.  The dot product is an RGB-only capability; the alpha arm
    // just routes AB's blue into alpha rather than computing anything.
    private static void Dp3(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c),
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb,
                Ps.RegZero | Ps.UnsignedIdentity | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbDotProduct | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.RgbOutputs[s] |= Ps.OutAbBlueToAlpha << 12;
        }
    }

    // dst = src0 + src1.  A*1 + C*1, result taken from the sum output.
    private static void Add(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelRgb,
                In(o, 1, c), Ps.RegOne | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelAlpha,
                In(o, 1, c), Ps.RegOne | Ps.ChannelAlpha);
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src0 - src1.  Same as add, but D is -1 so CD contributes negatively.
    private static void Sub(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelRgb,
                In(o, 1, c), Ps.RegNegativeOne | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), Ps.RegOne | Ps.ChannelAlpha,
                In(o, 1, c), Ps.RegNegativeOne | Ps.ChannelAlpha);
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src0*src1 + src2.  AB is the product, CD is src2*1.
    private static void Mad(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c),
                Ps.RegOne | Ps.ChannelRgb, In(o, 2, c));
            p.RgbOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c),
                Ps.RegOne | Ps.ChannelAlpha, In(o, 2, c));
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src1*src0 + src2*(1-src0). D reuses src0 through the inverted mapping,
    // which is why src0's modifier is first forced to a form that HAS an inverse.
    private static void Lrp(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        // The leak forces a non-unsigned interpolant to UNSIGNED_IDENTITY here
        // ("force interpolant to be unsigned"). 5849 does not: Fire's golden .xpu
        // keeps SIGNED_IDENTITY in the A slot for `lrp r0.a, t2.a, ...`. Dropping
        // the force is what reproduces it, and it cannot change the D slot either
        // way, since the inversion table sends both signed and unsigned identity to
        // UNSIGNED_INVERT. Legacy ps.1.x is unaffected because its sources already
        // decode to UNSIGNED_IDENTITY (see PixelShaderCompiler.DecodeSrc).
        uint inverted = Ps.NvModToNvModInvert[o.InputMod[0] >> 5];

        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c),
                o.Src[0] | inverted | RgbChannel(o.Swizzle[0]));
            p.RgbOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c),
                o.Src[0] | inverted | AlphaChannel(o.Swizzle[0]));
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbCdSum);
        }
    }

    // dst = src1 or src2 depending on r0.a. The mux output selects between AB and CD.
    private static void Cnd(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 2, c), Ps.RegOne | Ps.ChannelRgb,
                In(o, 1, c), Ps.RegOne | Ps.ChannelRgb);
            p.RgbOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbMultiply | Ps.OutCdMultiply | Ps.OutAbCdMux);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 2, c), Ps.RegOne | Ps.ChannelAlpha,
                In(o, 1, c), Ps.RegOne | Ps.ChannelAlpha);
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, o.Dst[0],
                o.OutputMap[0] | Ps.OutAbCdMux);
        }
    }

    /// <summary>
    /// xmma/xmmc: the raw combiner exposed. All four inputs and all three outputs are
    /// named by the instruction, which is what the general form can express and the
    /// DX8 opcodes cannot.
    /// </summary>
    private static void Extended(PixelShaderDef p, int s, bool c, PsOperands o,
                                 uint sumMux, uint abDot, uint cdDot, uint dst2)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], o.Dst[1], dst2,
                o.OutputMap[0] | abDot | cdDot | sumMux);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.AlphaOutputs[s] = Ps.CombinerOutputs(o.Dst[0], o.Dst[1], dst2,
                o.OutputMap[0] | sumMux);

            // A dot product has no alpha result of its own, so an alpha write is
            // satisfied by routing the RGB dot's blue channel across.
            if (abDot == Ps.OutAbDotProduct && (o.Mask[0] & Isa.WriteMask3) != 0)
                p.RgbOutputs[s] |= Ps.OutAbBlueToAlpha << 12;
            if (cdDot == Ps.OutCdDotProduct && (o.Mask[1] & Isa.WriteMask3) != 0)
                p.RgbOutputs[s] |= Ps.OutCdBlueToAlpha << 12;
        }
    }

    // xdm: one dot product (AB) alongside one multiply (CD).
    private static void Xdm(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], o.Dst[1], Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbDotProduct | Ps.OutCdMultiply | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, o.Dst[1], Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbCdSum);
            p.RgbOutputs[s] |= Ps.OutAbBlueToAlpha << 12;
        }
    }

    // xdd: two dot products in one stage. Neither has an alpha result, so both
    // alpha writes come from the RGB halves' blue channels.
    private static void Xdd(PixelShaderDef p, int s, bool c, PsOperands o)
    {
        if (c)
        {
            p.RgbInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.RgbOutputs[s] = Ps.CombinerOutputs(o.Dst[0], o.Dst[1], Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbDotProduct | Ps.OutCdDotProduct | Ps.OutAbCdSum);
        }
        else
        {
            p.AlphaInputs[s] = Ps.CombinerInputs(In(o, 0, c), In(o, 1, c), In(o, 2, c), In(o, 3, c));
            p.AlphaOutputs[s] = Ps.CombinerOutputs(Ps.RegDiscard, Ps.RegDiscard, Ps.RegDiscard,
                o.OutputMap[0] | Ps.OutAbCdSum);
            p.RgbOutputs[s] |= Ps.OutAbBlueToAlpha << 12;
            p.RgbOutputs[s] |= Ps.OutCdBlueToAlpha << 12;
        }
    }

    /// <summary>
    /// xfc: the final combiner. Not a stage -- it is separate hardware after the
    /// eight general stages, with seven inputs A..G and no destination, since its
    /// result IS the pixel. Always programmed from the RGB channel selectors.
    /// </summary>
    private static void Xfc(PixelShaderDef p, PsOperands o)
    {
        p.FinalCombinerInputsAbcd = Ps.CombinerInputs(
            In(o, 0, Colour), In(o, 1, Colour), In(o, 2, Colour), In(o, 3, Colour));

        p.FinalCombinerInputsEfg = Ps.CombinerInputs(
            In(o, 4, Colour), In(o, 5, Colour), In(o, 6, Colour), FinalCombinerClampSum);
    }

    /// <summary>PS_FINALCOMBINERSETTING_CLAMP_SUM, in the G slot's byte.</summary>
    private const uint FinalCombinerClampSum = 0x80;
}
