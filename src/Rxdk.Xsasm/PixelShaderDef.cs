namespace Rxdk.Xsasm;

/// <summary>
/// D3DPIXELSHADERDEF -- 60 DWORDs, in exactly the declaration order d3d8types.h
/// gives them, because the whole struct is memcpy'd to the GPU.
///
/// This is not a program. NV2A's pixel pipeline is a fixed set of register
/// combiners -- 8 general stages plus a final combiner, fed by 4 texture stages --
/// and this struct is their configuration register block. Assembling a pixel
/// shader means deciding how to *configure* that block, which is why the encoding
/// looks nothing like the vertex microcode.
/// </summary>
internal sealed class PixelShaderDef
{
    public const int DwordCount = 60;

    /// <summary>Tag written ahead of the struct in a .xpu file: 'PSB0'.</summary>
    public const uint FileId = 0x30425350;

    public uint[] AlphaInputs = new uint[8];
    public uint FinalCombinerInputsAbcd;
    public uint FinalCombinerInputsEfg;
    public uint[] Constant0 = new uint[8];
    public uint[] Constant1 = new uint[8];
    public uint[] AlphaOutputs = new uint[8];
    public uint[] RgbInputs = new uint[8];
    public uint CompareMode;
    public uint FinalCombinerConstant0;
    public uint FinalCombinerConstant1;
    public uint[] RgbOutputs = new uint[8];
    public uint CombinerCount;
    public uint TextureModes;
    public uint DotMapping;
    public uint InputTexture;
    public uint C0Mapping;
    public uint C1Mapping;
    public uint FinalCombinerConstants;

    public uint[] ToDwords()
    {
        var d = new uint[DwordCount];
        int i = 0;

        void Put(uint v) => d[i++] = v;
        void PutAll(uint[] v) { foreach (uint x in v) d[i++] = x; }

        PutAll(AlphaInputs);
        Put(FinalCombinerInputsAbcd);
        Put(FinalCombinerInputsEfg);
        PutAll(Constant0);
        PutAll(Constant1);
        PutAll(AlphaOutputs);
        PutAll(RgbInputs);
        Put(CompareMode);
        Put(FinalCombinerConstant0);
        Put(FinalCombinerConstant1);
        PutAll(RgbOutputs);
        Put(CombinerCount);
        Put(TextureModes);
        Put(DotMapping);
        Put(InputTexture);
        Put(C0Mapping);
        Put(C1Mapping);
        Put(FinalCombinerConstants);

        if (i != DwordCount)
            throw new InvalidOperationException($"D3DPIXELSHADERDEF is {i} DWORDs, expected {DwordCount}");

        return d;
    }

    public byte[] ToBytes(bool withFileId)
    {
        uint[] d = ToDwords();
        var bytes = new byte[(withFileId ? 4 : 0) + d.Length * 4];
        int o = 0;

        if (withFileId)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(o), FileId);
            o += 4;
        }

        foreach (uint v in d)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(o), v);
            o += 4;
        }

        return bytes;
    }
}
