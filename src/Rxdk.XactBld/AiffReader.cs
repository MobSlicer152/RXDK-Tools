// Minimal AIFF / AIFF-C reader. XDK samples (e.g. XActInteractiveAudio) ship their audio as
// big-endian AIFF rather than RIFF/WAVE; xactbld's wave-bank packer only understands PCM WAV, so
// this converts an uncompressed AIFF into the same little-endian PCM WavData the WAV path produces.
// Supports FORM/AIFF (big-endian PCM) and FORM/AIFC with compression "NONE" (big-endian) or "sowt"
// (already little-endian). Sample data is byte-swapped to little-endian; 8-bit signed AIFF samples
// are rebiased to the unsigned convention WAV/PCM uses.

using System.Buffers.Binary;

namespace Rxdk.XactBld;

internal static class AiffReader
{
    public static WavData Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || b[0] != 'F' || b[1] != 'O' || b[2] != 'R' || b[3] != 'M')
            throw new XactBldException($"Not a FORM/AIFF file: {path}");
        string formType = System.Text.Encoding.ASCII.GetString(b, 8, 4);
        if (formType != "AIFF" && formType != "AIFC")
            throw new XactBldException($"Not an AIFF/AIFC file (FORM type '{formType}'): {path}");

        var wav = new WavData { FormatTag = 1 };
        bool haveComm = false, haveSsnd = false;
        bool littleEndian = false; // AIFC "sowt" stores LE samples
        int bits = 0;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            uint size = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(pos + 4, 4));
            int body = pos + 8;
            if (body + (int)size > b.Length)
                size = (uint)(b.Length - body); // tolerate a truncated final chunk

            if (id == "COMM")
            {
                wav.Channels = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(body + 0, 2));
                // body+2: numSampleFrames (u32) — not needed, we take the SSND byte count.
                bits = BinaryPrimitives.ReadInt16BigEndian(b.AsSpan(body + 6, 2));
                wav.BitsPerSample = bits;
                wav.SamplesPerSec = (int)Math.Round(ParseExtended80(b.AsSpan(body + 8, 10)));
                if (formType == "AIFC" && size >= 22)
                {
                    string comp = System.Text.Encoding.ASCII.GetString(b, body + 18, 4);
                    if (comp == "sowt" || comp == "SOWT") littleEndian = true;
                    else if (comp != "NONE" && comp != "none")
                        throw new XactBldException(
                            $"AIFF '{Path.GetFileName(path)}' uses compression '{comp}' (only NONE/sowt PCM is supported).");
                }
                haveComm = true;
            }
            else if (id == "SSND")
            {
                // SSND: offset (u32), blockSize (u32), then the sample frames.
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(body + 0, 4));
                int dataStart = body + 8 + (int)offset;
                int dataLen = (int)size - 8 - (int)offset;
                if (dataLen < 0 || dataStart + dataLen > b.Length)
                    throw new XactBldException($"AIFF SSND chunk is malformed: {path}");
                wav.Pcm = b.AsSpan(dataStart, dataLen).ToArray();
                haveSsnd = true;
            }

            pos = body + (int)size + ((size & 1) == 1 ? 1 : 0); // chunks are word-aligned
        }

        if (!haveComm) throw new XactBldException($"AIFF has no COMM chunk: {path}");
        if (!haveSsnd) throw new XactBldException($"AIFF has no SSND chunk: {path}");

        if (!littleEndian)
            ConvertToLittleEndianPcm(wav.Pcm, bits);
        return wav;
    }

    /// <summary>Converts big-endian AIFF samples in place to the little-endian PCM WAV uses.</summary>
    private static void ConvertToLittleEndianPcm(byte[] data, int bits)
    {
        switch (bits)
        {
            case 8:
                // AIFF 8-bit is signed; WAV 8-bit PCM is unsigned. Rebias by 128.
                for (int i = 0; i < data.Length; i++)
                    data[i] = (byte)(data[i] + 128);
                break;
            case 16:
                for (int i = 0; i + 1 < data.Length; i += 2)
                    (data[i], data[i + 1]) = (data[i + 1], data[i]);
                break;
            case 24:
                for (int i = 0; i + 2 < data.Length; i += 3)
                    (data[i], data[i + 2]) = (data[i + 2], data[i]);
                break;
            case 32:
                for (int i = 0; i + 3 < data.Length; i += 4)
                {
                    (data[i], data[i + 3]) = (data[i + 3], data[i]);
                    (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
                }
                break;
            default:
                throw new XactBldException($"Unsupported AIFF sample size {bits} bits.");
        }
    }

    /// <summary>Parses an 80-bit IEEE-754 extended-precision big-endian float (the AIFF sample rate).</summary>
    private static double ParseExtended80(ReadOnlySpan<byte> b)
    {
        int sign = (b[0] & 0x80) != 0 ? -1 : 1;
        int exponent = ((b[0] & 0x7F) << 8) | b[1];
        ulong mantissa = 0;
        for (int i = 0; i < 8; i++)
            mantissa = (mantissa << 8) | b[2 + i];
        if (exponent == 0 && mantissa == 0)
            return 0.0;
        exponent -= 16383;
        return sign * (double)mantissa * Math.Pow(2.0, exponent - 63);
    }
}
