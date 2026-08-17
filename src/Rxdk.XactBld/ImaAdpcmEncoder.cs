// Xbox (IMA) ADPCM encoder.
//
// A .xap wave entry can carry "ADPCM Filter = 1", which asks the build to store that entry
// as Xbox ADPCM instead of PCM. The step tables and the quantizer below are a port of the
// runtime codec in libdsound (common/imaadpcm.cpp) so an entry encoded here decodes to the
// same samples the hardware voice produces - and so the bytes match what the XDK's own
// xactbld emits for the same source wave.

namespace Rxdk.XactBld;

internal static class ImaAdpcmEncoder
{
    public const int BitsPerSample = 4;
    public const int HeaderLength = 4;      // per channel: predicted sample + step index
    public const int SamplesPerBlock = 64;  // per channel

    // Step index delta, indexed by the encoded nibble.
    private static readonly short[] NextStep =
    {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8,
    };

    private static readonly short[] Step =
    {
            7,     8,     9,    10,    11,    12,    13,
           14,    16,    17,    19,    21,    23,    25,
           28,    31,    34,    37,    41,    45,    50,
           55,    60,    66,    73,    80,    88,    97,
          107,   118,   130,   143,   157,   173,   190,
          209,   230,   253,   279,   307,   337,   371,
          408,   449,   494,   544,   598,   658,   724,
          796,   876,   963,  1060,  1166,  1282,  1411,
         1552,  1707,  1878,  2066,  2272,  2499,  2749,
         3024,  3327,  3660,  4026,  4428,  4871,  5358,
         5894,  6484,  7132,  7845,  8630,  9493, 10442,
        11487, 12635, 13899, 15289, 16818, 18500, 20350,
        22385, 24623, 27086, 29794, 32767,
    };

    /// <summary>
    /// nBlockAlign for an encoded block, which the format's samples-per-block dictates. The
    /// first sample of each block lives in the header, and the stereo encoder emits whole
    /// pairs of DWORDs, so the payload rounds up to 8 bytes.
    /// </summary>
    public static int BlockAlign(int channels)
    {
        int encodedSampleBits = channels * BitsPerSample;
        int headerBytes = channels * HeaderLength;

        int align = SamplesPerBlock - 1;
        align = (align * encodedSampleBits + 7) / 8;
        align = ((align + 7) / 8) * 8;
        return align + headerBytes;
    }

    /// <summary>
    /// Encodes 16-bit PCM to Xbox ADPCM. The tail is padded with silence to fill the last
    /// block, since a block is only ever read whole.
    /// </summary>
    public static byte[] Encode(byte[] pcm, int channels)
    {
        if (channels is not (1 or 2))
            throw new XactBldException($"ADPCM supports 1 or 2 channels, not {channels}.");

        int frames = pcm.Length / (2 * channels);
        int blocks = (frames + SamplesPerBlock - 1) / SamplesPerBlock;
        int blockAlign = BlockAlign(channels);

        var src = new short[blocks * SamplesPerBlock * channels];
        for (int i = 0; i < frames * channels; i++)
            src[i] = (short)(pcm[i * 2] | (pcm[i * 2 + 1] << 8));

        var dst = new byte[blocks * blockAlign];
        if (channels == 1)
            EncodeMono(src, dst, blocks, blockAlign);
        else
            EncodeStereo(src, dst, blocks, blockAlign);
        return dst;
    }

    private static void EncodeMono(short[] src, byte[] dst, int blocks, int blockAlign)
    {
        int s = 0;
        int stepIndex = 0;

        for (int b = 0; b < blocks; b++)
        {
            int p = b * blockAlign;
            int predicted = src[s++];

            WriteHeader(dst, p, predicted, stepIndex);
            p += HeaderLength;

            int remaining = SamplesPerBlock - 1;
            while (remaining > 0)
            {
                int low = EncodeSample(src[s++], ref predicted, Step[stepIndex]);
                stepIndex = NextStepIndex(low, stepIndex);
                remaining--;

                int high = 0;
                if (remaining > 0)
                {
                    high = EncodeSample(src[s++], ref predicted, Step[stepIndex]);
                    stepIndex = NextStepIndex(high, stepIndex);
                    remaining--;
                }

                dst[p++] = (byte)(low | (high << 4));
            }
        }
    }

    private static void EncodeStereo(short[] src, byte[] dst, int blocks, int blockAlign)
    {
        int s = 0;
        int stepIndexL = 0;
        int stepIndexR = 0;

        for (int b = 0; b < blocks; b++)
        {
            int p = b * blockAlign;
            int predictedL = src[s++];
            int predictedR = src[s++];

            WriteHeader(dst, p, predictedL, stepIndexL);
            WriteHeader(dst, p + HeaderLength, predictedR, stepIndexR);
            p += 2 * HeaderLength;

            // Each pass emits one DWORD of left nibbles followed by one of right nibbles,
            // built from the interleaved source a frame at a time.
            int remaining = SamplesPerBlock - 1;
            while (remaining > 0)
            {
                int count = Math.Min(remaining, 8);
                uint left = 0;
                uint right = 0;

                for (int i = 0; i < count; i++)
                {
                    int encodedL = EncodeSample(src[s++], ref predictedL, Step[stepIndexL]);
                    stepIndexL = NextStepIndex(encodedL, stepIndexL);
                    left |= (uint)encodedL << (4 * i);

                    int encodedR = EncodeSample(src[s++], ref predictedR, Step[stepIndexR]);
                    stepIndexR = NextStepIndex(encodedR, stepIndexR);
                    right |= (uint)encodedR << (4 * i);
                }

                WriteUInt32(dst, p, left);
                WriteUInt32(dst, p + 4, right);
                p += 8;
                remaining -= count;
            }
        }
    }

    private static void WriteHeader(byte[] dst, int offset, int predicted, int stepIndex)
    {
        WriteUInt32(dst, offset, (uint)((ushort)predicted | (stepIndex << 16)));
    }

    private static void WriteUInt32(byte[] dst, int offset, uint value)
    {
        dst[offset] = (byte)value;
        dst[offset + 1] = (byte)(value >> 8);
        dst[offset + 2] = (byte)(value >> 16);
        dst[offset + 3] = (byte)(value >> 24);
    }

    private static int NextStepIndex(int encodedSample, int stepIndex)
    {
        stepIndex += NextStep[encodedSample];
        if (stepIndex < 0) return 0;
        if (stepIndex >= Step.Length) return Step.Length - 1;
        return stepIndex;
    }

    // Quantizes one sample against the running predictor, and advances the predictor to the
    // value a decoder will reconstruct so the two stay in step.
    private static int EncodeSample(int inputSample, ref int predictedSample, int stepSize)
    {
        int predicted = predictedSample;
        int difference = inputSample - predicted;
        int encoded = 0;

        if (difference < 0)
        {
            encoded = 8;
            difference = -difference;
        }

        if (difference >= stepSize)
        {
            encoded |= 4;
            difference -= stepSize;
        }

        stepSize >>= 1;

        if (difference >= stepSize)
        {
            encoded |= 2;
            difference -= stepSize;
        }

        stepSize >>= 1;

        if (difference >= stepSize)
        {
            encoded |= 1;
            difference -= stepSize;
        }

        predicted = (encoded & 8) != 0
            ? inputSample + difference - (stepSize >> 1)
            : inputSample - difference + (stepSize >> 1);

        predictedSample = Math.Clamp(predicted, short.MinValue, short.MaxValue);
        return encoded;
    }
}
