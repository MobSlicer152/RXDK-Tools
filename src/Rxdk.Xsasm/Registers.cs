namespace Rxdk.Xsasm;

/// <summary>
/// Register-name decoding, mirroring CD3DXAssembler::DecodeRegister(). Returns the
/// D3D8 register token: file in bits 28..30, number in bits 0..11, and for pixel
/// shaders a source modifier in bits 24..27.
/// </summary>
internal static class Registers
{
    // DX8 pixel shader file limits.
    private const int PsInputMax = 2;
    private const int PsTempMax = 2;
    private const int PsConstMax = 8;
    private const int PsTextureMax = 4;

    // VS 1.1 file limits.
    private const int VsInputMax = 16;
    private const int VsTempMax = 12;
    private const int VsAddrMax = 1;
    private const int VsTexCrdOutMax = 8;

    // RASTOUT indices.
    private const uint RastOutPosition = 0;
    private const uint RastOutFog = 1;
    private const uint RastOutPointSize = 2;

    public const uint AddrModeRelative = 0x00002000;

    /// <summary>
    /// Pixel-shader source modifier suffixes. Several spellings share an encoding:
    /// the dot-mapping names (_sign1.._hemi3) are aliases the texture-stage syntax
    /// uses for the same four modifier bits.
    /// </summary>
    private static readonly (string Suffix, uint Mod)[] PixelMods =
    {
        ("_bias", Isa.SrcModBias),
        ("_bx2", Isa.SrcModSign),
        ("_sgn", Isa.SrcModSign),
        ("_sat", Isa.SrcModSat),
        ("_sign1", Isa.SrcModSign),
        ("_sign2", Isa.SrcModNeg),
        ("_sign3", Isa.SrcModBias),
        ("_hl", Isa.SrcModBiasNeg),
        ("_hemi1", Isa.SrcModSignNeg),
        ("_hemi2", Isa.SrcModComp),
        ("_hemi3", Isa.SrcModSat),
        // Not in the leak's table, which knows only _hemi1.._hemi3 and would reject
        // HQBumpShader.psh -- a file that ships an assembled .xpu beside it, so 5849
        // clearly accepts the bare spelling. Its encoding is read back OUT of that
        // golden rather than guessed: `texm3x2tex t3, t1_hemi` lands as dotMap[3] =
        // 0x7 (HILO_HEMISPHERE), which is the D3DSPSM_SAT row -- i.e. an alias of
        // _hemi3. The same file's `t0_bx2` lands as 0x1, confirming the table's
        // alignment independently.
        ("_hemi", Isa.SrcModSat),
    };

    /// <summary>
    /// Decodes one register reference. <paramref name="addr"/> supplies the number
    /// when <paramref name="index"/> is set (the 'c[a0.x + 3]' form), where the name
    /// itself carries no digits. Returns false if the name is not a valid register.
    /// </summary>
    public static bool TryDecode(string text, uint addr, bool index, bool pixel, bool xbox,
                                 out uint token)
    {
        token = 0;

        // Split leading letters from the number. 'c-3' is allowed: a negative
        // constant index, which only Xbox vertex shaders accept.
        int i = 0;
        while (i < text.Length && char.IsLetter(text[i])) i++;
        int numStart = i;

        while (i < text.Length &&
               (char.IsDigit(text[i]) || (text[i] == '-' && text[0] == 'c' && i == numStart)))
        {
            i++;
        }

        string name = text[..numStart];
        string numText = text[numStart..i];
        string rest = text[i..];
        bool hasNum = numText.Length > 0;
        uint mod = 0;

        if (pixel && rest.StartsWith('_'))
        {
            // Longest match first: '_sign1' must not be taken as '_sign'.
            var hit = PixelMods
                .Where(m => rest.Equals(m.Suffix, StringComparison.OrdinalIgnoreCase))
                .Select(m => (uint?)m.Mod)
                .FirstOrDefault();

            if (hit is null) return false;
            mod = hit.Value;
            rest = "";
        }

        if (rest.Length != 0) return false;

        if (index)
        {
            // Relative addressing: the number came from the '[...]' expression.
            if (name.Equals("c", StringComparison.OrdinalIgnoreCase) && !hasNum)
            {
                if (pixel && (addr & Isa.RegNumMask) >= PsConstMax) return false;
                token = ((uint)RegFile.Const << Isa.RegTypeShift) | addr | mod;
                return true;
            }
            return false;
        }

        int n = hasNum && int.TryParse(numText, out int parsed) ? parsed : 0;
        uint num = (uint)n & Isa.RegNumMask;

        if (pixel)
        {
            switch (name.ToLowerInvariant())
            {
                case "v" when hasNum && n >= 0 && n < PsInputMax:
                    token = ((uint)RegFile.Input << Isa.RegTypeShift) | num | mod; return true;
                case "r" when hasNum && n >= 0 && n < PsTempMax:
                    token = ((uint)RegFile.Temp << Isa.RegTypeShift) | num | mod; return true;
                case "c" when hasNum && n >= 0 && n < PsConstMax:
                    token = ((uint)RegFile.Const << Isa.RegTypeShift) | num | mod; return true;
                case "t" when hasNum && n >= 0 && n < PsTextureMax:
                    token = ((uint)RegFile.AddrOrTexture << Isa.RegTypeShift) | num | mod; return true;

                // Combiner-syntax aliases for fixed registers.
                case "zero":
                case "discard":
                    token = ((uint)RegFile.Temp << Isa.RegTypeShift) | 2 | mod; return true;
                case "fog":
                    token = ((uint)RegFile.Temp << Isa.RegTypeShift) | 3 | mod; return true;
                case "prod":
                    token = ((uint)RegFile.Input << Isa.RegTypeShift) | 3 | mod; return true;
                case "sum":
                    token = ((uint)RegFile.Input << Isa.RegTypeShift) | 2 | mod; return true;
            }

            return false;
        }

        switch (name.ToLowerInvariant())
        {
            case "opos" when !hasNum:
                token = ((uint)RegFile.RastOut << Isa.RegTypeShift) | RastOutPosition; return true;
            case "opts" when !hasNum:
                token = ((uint)RegFile.RastOut << Isa.RegTypeShift) | RastOutPointSize; return true;
            case "ofog" when !hasNum:
                token = ((uint)RegFile.RastOut << Isa.RegTypeShift) | RastOutFog; return true;

            case "v" when hasNum && n >= 0 && n < VsInputMax:
                token = ((uint)RegFile.Input << Isa.RegTypeShift) | num; return true;

            // Xbox exposes 13 temporaries; r12 doubles as the position register.
            case "r" when hasNum && n >= 0 && n < (xbox ? 13 : VsTempMax):
                token = ((uint)RegFile.Temp << Isa.RegTypeShift) | num; return true;

            // 0..191 normally; Xbox also allows -192..-1, which is why 'c-3' lexes as one token.
            case "c" when hasNum && n >= (xbox ? -192 : 0) && n < 192:
                token = ((uint)RegFile.Const << Isa.RegTypeShift) | num; return true;

            case "a" when hasNum && n >= 0 && n < VsAddrMax:
                token = ((uint)RegFile.AddrOrTexture << Isa.RegTypeShift) | num; return true;

            case "od" when hasNum && n >= 0 && n < (xbox ? 2 : 2):
                token = ((uint)RegFile.AttrOut << Isa.RegTypeShift) | num; return true;

            // Back-facing colours, Xbox only, distinguished by bit 8.
            case "ob" when hasNum && xbox && n >= 0 && n < 2:
                token = ((uint)RegFile.AttrOut << Isa.RegTypeShift) | 0x100 | num; return true;

            case "ot" when hasNum && n >= 0 && n < VsTexCrdOutMax:
                token = ((uint)RegFile.TexCrdOut << Isa.RegTypeShift) | num; return true;
        }

        return false;
    }

    /// <summary>
    /// Write mask from a '.xyzw' suffix. Components must appear in increasing order --
    /// '.xz' is legal, '.zx' is not, because the mask has no way to record an order.
    /// </summary>
    public static bool TryDecodeMask(string? text, out uint mask)
    {
        mask = Isa.WriteMaskAll;
        if (string.IsNullOrEmpty(text)) return true;

        mask = 0;
        int last = -1;

        foreach (char c in text)
        {
            int comp = ComponentIndex(c);
            if (comp < 0) return false;
            if (last >= 0 && comp <= last) return false;

            mask |= Isa.WriteMask0 << comp;
            last = comp;
        }

        return true;
    }

    /// <summary>
    /// Swizzle from a '.xyzw' suffix. A short swizzle repeats its last component --
    /// '.x' is xxxx and '.xy' is xyyy, which is what makes 'r0.x' broadcast.
    /// </summary>
    public static bool TryDecodeSwizzle(string? text, out uint swizzle)
    {
        swizzle = Isa.NoSwizzle;
        if (string.IsNullOrEmpty(text)) return true;

        swizzle = 0;
        uint src = 0;
        int i = 0;

        for (int dst = 0; dst < 4; dst++)
        {
            if (i < text.Length)
            {
                int comp = ComponentIndex(text[i]);
                if (comp < 0) return false;
                src = (uint)comp;
                i++;
            }

            swizzle |= src << (Isa.SwizzleShift + 2 * dst);
        }

        return i == text.Length;
    }

    private static int ComponentIndex(char c) => char.ToLowerInvariant(c) switch
    {
        'x' or 'r' => 0,
        'y' or 'g' => 1,
        'z' or 'b' => 2,
        'w' or 'a' => 3,
        _ => -1,
    };
}
