namespace Rxdk.Xsasm;

/// <summary>
/// Opcode spelling -> (opcode, arity) as CD3DXAssembler::DecodeOpcode() defines it.
/// Arity is carried as the grammar's token class: T_OP0..T_OP7 is literally the
/// operand count, which is how the parser knows how many operands to read.
/// </summary>
internal static class Opcodes
{
    private record Entry(Op Op, Tok Arity);

    // Recognised in both shader kinds.
    private static readonly Dictionary<string, Entry> Common = new(StringComparer.OrdinalIgnoreCase)
    {
        ["nop"] = new(Op.Nop, Tok.Op0),
        ["mov"] = new(Op.Mov, Tok.Op2),
        ["add"] = new(Op.Add, Tok.Op3),
        ["sub"] = new(Op.Sub, Tok.Op3),
        ["mad"] = new(Op.Mad, Tok.Op4),
        ["mul"] = new(Op.Mul, Tok.Op3),
        ["dp3"] = new(Op.Dp3, Tok.Op3),
        ["dp4"] = new(Op.Dp4, Tok.Op3),
    };

    private static readonly Dictionary<string, Entry> Pixel = new(StringComparer.OrdinalIgnoreCase)
    {
        ["lrp"] = new(Op.Lrp, Tok.Op4),
        ["cnd"] = new(Op.Cnd, Tok.Op4),
        ["texcoord"] = new(Op.TexCoord, Tok.Op1),
        ["texkill"] = new(Op.TexKill, Tok.Op1),
        ["tex"] = new(Op.Tex, Tok.Op1),
        ["texbem"] = new(Op.TexBem, Tok.Op2),
        ["texbeml"] = new(Op.TexBemL, Tok.Op2),
        ["texreg2ar"] = new(Op.TexReg2Ar, Tok.Op2),
        ["texreg2gb"] = new(Op.TexReg2Gb, Tok.Op2),
        ["texm3x2pad"] = new(Op.TexM3x2Pad, Tok.Op2),
        ["texm3x2tex"] = new(Op.TexM3x2Tex, Tok.Op2),
        ["texm3x3pad"] = new(Op.TexM3x3Pad, Tok.Op2),
        ["texm3x3tex"] = new(Op.TexM3x3Tex, Tok.Op2),
        ["texm3x3diff"] = new(Op.TexM3x3Diff, Tok.Op2),
        ["texm3x3spec"] = new(Op.TexM3x3Spec, Tok.Op3),
        ["texm3x3vspec"] = new(Op.TexM3x3VSpec, Tok.Op2),
        // Xbox extensions
        ["xmma"] = new(Op.Xmma, Tok.Op7),
        ["xmmc"] = new(Op.Xmmc, Tok.Op7),
        ["xdm"] = new(Op.Xdm, Tok.Op6),
        ["xdd"] = new(Op.Xdd, Tok.Op6),
        ["xfc"] = new(Op.Xfc, Tok.Op5),
        ["texm3x2depth"] = new(Op.TexM3x2Depth, Tok.Op2),
        ["texbrdf"] = new(Op.TexBrdf, Tok.Op1),
    };

    private static readonly Dictionary<string, Entry> Vertex = new(StringComparer.OrdinalIgnoreCase)
    {
        ["rcp"] = new(Op.Rcp, Tok.Op2),
        ["rsq"] = new(Op.Rsq, Tok.Op2),
        ["min"] = new(Op.Min, Tok.Op3),
        ["max"] = new(Op.Max, Tok.Op3),
        ["slt"] = new(Op.Slt, Tok.Op3),
        ["sge"] = new(Op.Sge, Tok.Op3),
        ["exp"] = new(Op.Exp, Tok.Op2),
        ["log"] = new(Op.Log, Tok.Op2),
        ["lit"] = new(Op.Lit, Tok.Op2),
        ["dst"] = new(Op.Dst, Tok.Op3),
        ["frc"] = new(Op.Frc, Tok.Op2),
        ["m4x4"] = new(Op.M4x4, Tok.Op3),
        ["m4x3"] = new(Op.M4x3, Tok.Op3),
        ["m3x4"] = new(Op.M3x4, Tok.Op3),
        ["m3x3"] = new(Op.M3x3, Tok.Op3),
        ["m3x2"] = new(Op.M3x2, Tok.Op3),
        ["expp"] = new(Op.Expp, Tok.Op2),
        ["logp"] = new(Op.Logp, Tok.Op2),
    };

    // Xbox-only vertex opcodes, gated on an xvs* version.
    private static readonly Dictionary<string, Entry> VertexXbox = new(StringComparer.OrdinalIgnoreCase)
    {
        ["dph"] = new(Op.Dph, Tok.Op3),
        ["rcc"] = new(Op.Rcc, Tok.Op2),
    };

    /// <summary>
    /// Splits "op_mod" and resolves it. Anything unrecognised comes back as Tok.Id --
    /// register names reach here too, and the parser sorts them out by position.
    /// </summary>
    public static Tok Decode(string text, bool pixel, bool xbox, out Op op, out uint shiftSat)
    {
        op = Op.Nop;
        shiftSat = 0;

        string[] parts = text.Split('_');
        if (parts.Length > 2) return Tok.Id;   // only one destination modifier allowed

        string name = parts[0];

        if (!Common.TryGetValue(name, out var e))
        {
            var table = pixel ? Pixel : Vertex;
            if (!table.TryGetValue(name, out e) &&
                !(!pixel && xbox && VertexXbox.TryGetValue(name, out e)))
            {
                return Tok.Id;
            }
        }

        if (parts.Length == 2)
        {
            // Only pixel shaders take a destination shift/bias.
            if (!pixel) return Tok.Id;

            switch (parts[1].ToLowerInvariant())
            {
                case "x4": shiftSat = (2u << Isa.DstShiftShift) & Isa.DstShiftMask; break;
                case "x2": shiftSat = (1u << Isa.DstShiftShift) & Isa.DstShiftMask; break;
                case "d2": shiftSat = unchecked((uint)(-1 << Isa.DstShiftShift)) & Isa.DstShiftMask; break;
                case "bias": shiftSat = Isa.DstModBias; break;
                case "bx2":
                    shiftSat = ((1u << Isa.DstShiftShift) & Isa.DstShiftMask) | Isa.DstModBias;
                    break;
                default: return Tok.Id;
            }
        }

        op = e!.Op;
        return e.Arity;
    }
}
