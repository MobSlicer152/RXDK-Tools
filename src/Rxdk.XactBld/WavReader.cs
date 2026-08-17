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

    // First 'smpl' loop, in bytes from the start of the sample data, or a zero length when
    // the file declares no loop. The wave bank carries the loop per entry, and the source
    // file is where the real tool takes it from - the .xap's own Loop Region properties go
    // stale as soon as the wav is re-edited, and are ignored.
    public uint LoopStart;
    public uint LoopLength;
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
        bool haveFmt = false, haveData = false, haveLoop = false;
        uint loopStartSample = 0, loopEndSample = 0;
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
            else if (id == "smpl" && size >= 36 + 24)
            {
                // SMPLCHUNK: 28 bytes of tuning/manufacturer fields, then dwSampleLoops and
                // dwSamplerData, then the loop array. Each loop is 24 bytes with the start and
                // end sample at +8 and +12. Only the first loop is representable in a bank.
                uint loops = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(body + 28, 4));
                if (loops > 0)
                {
                    int loop = body + 36;
                    loopStartSample = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(loop + 8, 4));
                    loopEndSample = BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(loop + 12, 4));
                    haveLoop = true;
                }
            }

            // Chunks are word-aligned: a pad byte follows an odd-sized body.
            pos = body + (int)size + ((size & 1) == 1 ? 1 : 0);
        }

        if (!haveFmt) throw new XactBldException($"WAV has no 'fmt ' chunk: {path}");
        if (!haveData) throw new XactBldException($"WAV has no 'data' chunk: {path}");

        // 'smpl' counts in samples and its end sample is inclusive, so the region spans
        // (end - start + 1) frames.
        if (haveLoop && loopEndSample >= loopStartSample)
        {
            int frame = wav.Channels * (wav.BitsPerSample / 8);
            if (frame > 0)
            {
                wav.LoopStart = loopStartSample * (uint)frame;
                wav.LoopLength = (loopEndSample - loopStartSample + 1) * (uint)frame;
            }
        }

        return wav;
    }
}
