// Minimal RIFF/WAVE reader. Extracts the PCM 'fmt ' fields and the 'data' chunk
// bytes, skipping any other chunks (samples in the XDK ship with a leading 'bext'
// broadcast-extension chunk before 'fmt '). Uncompressed PCM only; if a compressed
// format tag (ADPCM/XMA) is seen we surface it so the caller can report it.

using System.Buffers.Binary;

namespace Rxdk.XactBld;

internal sealed class WavData
{
    public int FormatTag;        // WAVE_FORMAT_* (1 = PCM)
    public int Channels;
    public int SamplesPerSec;
    public int BitsPerSample;
    public byte[] Pcm = Array.Empty<byte>();
}

internal static class WavReader
{
    public static WavData Read(string path)
    {
        byte[] b = File.ReadAllBytes(path);
        if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F' ||
            b[8] != 'W' || b[9] != 'A' || b[10] != 'V' || b[11] != 'E')
            throw new XactBldException($"Not a RIFF/WAVE file: {path}");

        var wav = new WavData();
        bool haveFmt = false, haveData = false;
        int pos = 12;
        while (pos + 8 <= b.Length)
        {
            string id = System.Text.Encoding.ASCII.GetString(b, pos, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(pos + 4, 4));
            int body = pos + 8;
            if (body + (int)size > b.Length)
                size = (uint)(b.Length - body); // tolerate a truncated final chunk

            if (id == "fmt ")
            {
                wav.FormatTag = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 0, 2));
                wav.Channels = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 2, 2));
                wav.SamplesPerSec = (int)BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(body + 4, 4));
                wav.BitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(body + 14, 2));
                haveFmt = true;
            }
            else if (id == "data")
            {
                wav.Pcm = b.AsSpan(body, (int)size).ToArray();
                haveData = true;
            }

            // Chunks are word-aligned: a pad byte follows an odd-sized body.
            pos = body + (int)size + ((size & 1) == 1 ? 1 : 0);
        }

        if (!haveFmt) throw new XactBldException($"WAV has no 'fmt ' chunk: {path}");
        if (!haveData) throw new XactBldException($"WAV has no 'data' chunk: {path}");
        return wav;
    }
}
