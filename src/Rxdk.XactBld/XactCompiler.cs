// Compiles a parsed .xap into the three build artifacts the XACT runtime + sample consume:
//
//   * XactSounds.h  - #defines mapping cue/wave friendly names to indices
//   * <name>.xwb    - wave bank (WAVEBANKHEADER + WAVEBANKENTRY[] + PCM), per libxact
//                     wavbndlr.h; loaded by IXACTEngine::RegisterWaveBank.
//   * <name>.xsb    - sound bank (XACT_SOUNDBANK_FILE_HEADER + cue/sound/wavebank/track
//                     tables + packed track events), per libxact xactp.h + the on-disk
//                     layout in the leak's filegen.cpp CreateBinaryImage; loaded by
//                     IXACTEngine::CreateSoundBank.
//
// On-disk struct sizes (default MSVC packing on these DWORD/WORD structs = natural,
// no trailing padding):
//   XACT_SOUNDBANK_FILE_HEADER          36  (5*DWORD + CHAR[16])
//   XACT_SOUNDBANK_CUE_ENTRY            24  (2*DWORD + CHAR[16])
//   XACT_SOUNDBANK_SOUND_ENTRY         28  (4*DWORD + 6*WORD)
//   XACT_SOUNDBANK_WAVEBANK_TABLE_ENTRY 20 (CHAR[16] + DWORD)
//   XACT_SOUNDBANK_TRACK_ENTRY          8  (2*WORD + DWORD)
//   XACT_TRACK_EVENT_HEADER             12 (2*WORD + DWORD + ULONG)
//   WAVEBANKHEADER                      36 (5*DWORD + CHAR[16])
//   WAVEBANKENTRY                       20 (DWORD fmt + 2*(DWORD,DWORD) regions)

using System.Text;

namespace Rxdk.XactBld;

internal sealed class XactCompiler
{
    // XACT_TRACK_EVENT_TYPES (xactp.h) - order is load-bearing (matches the runtime enum).
    private const int EvtPlay = 0;
    private const int EvtPlayWithPitchVolVar = 1;
    private const int EvtStop = 2;
    private const int EvtSetVolume = 5;
    private const int EvtMarker = 10;

    private const uint XsbSignature = 0x4B424453; // 'KBDS' -> "SDBK" on disk (LE)
    private const uint XsbVersion = 1;
    private const uint XwbSignature = 0x444E4257; // 'DNBW' -> "WBND" on disk (LE)
    private const uint XwbVersion = 2;
    private const uint XwbAlignment = 2048;

    private const int FriendlyNameLen = 16;

    private readonly string _xapDir;
    private readonly XapNode _root;
    private readonly Action<string>? _log;

    public XactCompiler(string xapPath, Action<string>? log)
    {
        _xapDir = Path.GetDirectoryName(Path.GetFullPath(xapPath)) ?? ".";
        _root = XapParser.Parse(File.ReadAllText(xapPath));
        _log = log;
    }

    public void Run()
    {
        // ---- Sound layers: name -> index (in declaration order) ----
        var layerIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var layer in _root.ChildrenNamed("Sound Layer"))
        {
            var nm = layer.Prop("Name") ?? "";
            if (!layerIndex.ContainsKey(nm)) layerIndex[nm] = layerIndex.Count;
        }

        var waveBanks = _root.ChildrenNamed("Wave Bank").ToList();
        var soundBanks = _root.ChildrenNamed("Sound Bank").ToList();

        // ---- Build every wave bank (.xwb) and index entry names per bank ----
        // waveEntryIndex[bankName][entryName] = entry index (used by Registered Wave refs).
        var waveEntryIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var waveEntryRate = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        foreach (var wb in waveBanks)
        {
            var bankName = wb.Prop("Name") ?? "";
            var (perName, perRate) = BuildWaveBank(wb, bankName);
            waveEntryIndex[bankName] = perName;
            waveEntryRate[bankName] = perRate;
        }

        // ---- Build every sound bank (.xsb) ----
        foreach (var sb in soundBanks)
            BuildSoundBank(sb, layerIndex, waveEntryIndex, waveEntryRate);

        // ---- Emit the C header (+ optional cue-list text file) ----
        EmitHeader(waveBanks, soundBanks);
    }

    // =====================================================================
    // Wave bank (.xwb)
    // =====================================================================

    private (Dictionary<string, int> names, Dictionary<string, int> rates) BuildWaveBank(XapNode wb, string bankName)
    {
        var entries = wb.ChildrenNamed("Entry").ToList();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var meta = new List<(uint fmt, uint playStart, uint playLen, uint loopStart, uint loopLen)>();
        var data = new MemoryStream();

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var name = e.Prop("Name") ?? $"entry{i}";
            var file = e.Prop("File") ?? throw new XactBldException($"Wave entry '{name}' has no File");
            var wavPath = ResolvePath(file);
            if (!File.Exists(wavPath))
                throw new XactBldException($"Wave file not found: {wavPath}");

            var wav = WavReader.Read(wavPath);
            if (wav.FormatTag != 1)
                throw new XactBldException(
                    $"Wave '{name}' uses unsupported WAV format tag {wav.FormatTag} " +
                    "(only uncompressed PCM is ported; re-author ADPCM/XMA as PCM).");

            names[name] = i;
            rates[name] = wav.SamplesPerSec;

            // Pad the running data segment to alignment before this wave.
            AlignStream(data, XwbAlignment);
            uint playStart = (uint)data.Length;
            data.Write(wav.Pcm, 0, wav.Pcm.Length);
            uint playLen = (uint)wav.Pcm.Length;

            uint fmt = PackMiniFormat(wav);
            meta.Add((fmt, playStart, playLen, 0, 0));
        }

        // Serialize: header + metadata table + data segment.
        var outBytes = new MemoryStream();
        var w = new BinaryWriter(outBytes);
        w.Write(XwbSignature);
        w.Write(XwbVersion);
        w.Write((uint)0);               // dwFlags
        w.Write((uint)entries.Count);   // dwEntryCount
        w.Write(XwbAlignment);          // dwAlignment
        WriteFixedString(w, bankName, FriendlyNameLen);
        foreach (var m in meta)
        {
            w.Write(m.fmt);
            w.Write(m.playStart);
            w.Write(m.playLen);
            w.Write(m.loopStart);
            w.Write(m.loopLen);
        }
        w.Write(data.ToArray());
        w.Flush();

        var bankFile = wb.Prop("Bank File");
        if (string.IsNullOrWhiteSpace(bankFile))
            throw new XactBldException($"Wave Bank '{bankName}' has no Bank File");
        WriteOutput(bankFile!, outBytes.ToArray());
        return (names, rates);
    }

    private static uint PackMiniFormat(WavData wav)
    {
        uint tag = 0;                              // WAVEBANKMINIFORMAT_TAG_PCM
        uint channels = (uint)(wav.Channels & 0x7);
        uint rate = (uint)(wav.SamplesPerSec & 0x7FFFFFF);
        uint bits = (uint)(wav.BitsPerSample == 16 ? 1 : 0); // 16-bit -> 1, 8-bit -> 0
        return (tag & 0x1) | (channels << 1) | (rate << 4) | (bits << 31);
    }

    // =====================================================================
    // Sound bank (.xsb)
    // =====================================================================

    private void BuildSoundBank(
        XapNode sb,
        Dictionary<string, int> layerIndex,
        Dictionary<string, Dictionary<string, int>> waveEntryIndex,
        Dictionary<string, Dictionary<string, int>> waveEntryRate)
    {
        var bankName = sb.Prop("Name") ?? "";
        var sounds = sb.ChildrenNamed("Sound").ToList();
        var cues = sb.ChildrenNamed("Cue").ToList();

        // Map sound name -> index for cue resolution.
        var soundNameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < sounds.Count; i++)
            soundNameToIndex[sounds[i].Prop("Name") ?? $"sound{i}"] = i;

        // Per-sound compiled data.
        var soundWaveBankNames = new List<List<string>>();   // distinct wave-bank names per sound
        var soundTracks = new List<List<byte[]>>();          // per sound: per track: event bytes
        var soundLayers = new List<int>();

        foreach (var snd in sounds)
        {
            var tracksNodes = snd.ChildrenNamed("Track").ToList();

            // Pre-scan for the sound's playback rate (first Registered Wave). Marker
            // timestamps convert ms -> samples at this rate; the play event itself sits
            // at sample 0, so both share one clock.
            int soundRate = ResolveSoundRate(tracksNodes, waveEntryRate);

            var waveBankNames = new List<string>();
            var tracks = new List<byte[]>();
            foreach (var tr in tracksNodes)
            {
                var events = new MemoryStream();
                foreach (var evNode in tr.Children)
                {
                    var ev = CompileEvent(evNode, waveBankNames, waveEntryIndex, soundRate);
                    if (ev != null) events.Write(ev, 0, ev.Length);
                }
                tracks.Add(events.ToArray());
            }

            soundWaveBankNames.Add(waveBankNames);
            soundTracks.Add(tracks);
            var layerName = snd.Prop("Layer") ?? "";
            soundLayers.Add(layerIndex.TryGetValue(layerName, out var li) ? li : 0);
        }

        int nSounds = sounds.Count;
        int nCues = cues.Count;

        // ---- Compute absolute file offsets (cumulative; matches filegen for 1 sound) ----
        const int headerSize = 36, cueSize = 24, soundSize = 28, wbEntrySize = 20, trackSize = 8;
        int soundTableOff = headerSize + cueSize * nCues;
        int threeDOff = soundTableOff + soundSize * nSounds;
        int wavebankTableOff = threeDOff; // no 3D params emitted (no 3D sounds in ported samples)

        int totalWbEntries = soundWaveBankNames.Sum(l => l.Count);
        int totalTracks = soundTracks.Sum(t => t.Count);
        int trackTableOff = wavebankTableOff + wbEntrySize * totalWbEntries;
        int eventDataOff = trackTableOff + trackSize * totalTracks;

        // ---- Serialize ----
        var outMs = new MemoryStream();
        var w = new BinaryWriter(outMs);

        // File header
        w.Write(XsbSignature);
        w.Write(XsbVersion);
        w.Write((uint)0);          // dwFlags
        w.Write((uint)nSounds);    // dwSoundEntryCount
        w.Write((uint)nCues);      // dwCueEntryCount
        WriteFixedString(w, bankName, FriendlyNameLen);

        // Cue table
        foreach (var cue in cues)
        {
            var cueSound = cue.Child("Sound");
            int soundIndex = cueSound?.IntProp("Sound Index", -1) ?? -1;
            if (soundIndex < 0)
            {
                var sname = cueSound?.Prop("Sound") ?? "";
                soundIndex = soundNameToIndex.TryGetValue(sname, out var si) ? si : 0;
            }
            w.Write((uint)0);                 // dwFlags
            w.Write((uint)soundIndex);        // dwSoundIndex
            WriteFixedString(w, cue.Prop("Name") ?? "", FriendlyNameLen);
        }

        // Sound table (offsets computed cumulatively)
        int wbCursor = 0, trackCursor = 0;
        for (int i = 0; i < nSounds; i++)
        {
            int wbCount = soundWaveBankNames[i].Count;
            int trkCount = soundTracks[i].Count;

            w.Write((uint)0);                                             // dwFlags (no 3D)
            w.Write((uint)0);                                             // dw3DParametersOffset (unused; non-3D)
            w.Write((uint)(trackTableOff + trackCursor * trackSize));     // dwTrackTableOffset
            w.Write((uint)(wavebankTableOff + wbCursor * wbEntrySize));   // dwWaveBankTableOffset
            w.Write((ushort)0);                                           // wPriority
            w.Write((ushort)soundLayers[i]);                             // wLayer
            w.Write((ushort)0);                                           // wGroupNumber
            w.Write((ushort)trkCount);                                    // wTrackCount
            w.Write((ushort)wbCount);                                     // wWaveBankCount
            w.Write((ushort)0);                                           // wSliderCount

            wbCursor += wbCount;
            trackCursor += trkCount;
        }

        // Wave-bank tables (per sound)
        foreach (var wbNames in soundWaveBankNames)
        {
            foreach (var name in wbNames)
            {
                WriteFixedString(w, name, FriendlyNameLen);
                w.Write((uint)0);  // dwDataOffset (runtime resolves by friendly name)
            }
        }

        // Track tables (per sound), with cumulative event-data offsets
        int eventCursor = 0;
        foreach (var tracks in soundTracks)
        {
            foreach (var evBytes in tracks)
            {
                int eventCount = CountEvents(evBytes);
                w.Write((ushort)0);                              // wFlags
                w.Write((ushort)eventCount);                     // wEventEntryCount
                w.Write((uint)(eventDataOff + eventCursor));     // dwEventDataOffset
                eventCursor += evBytes.Length;
            }
        }

        // Event data (per track)
        foreach (var tracks in soundTracks)
            foreach (var evBytes in tracks)
                w.Write(evBytes);

        w.Flush();

        var bankFile = sb.Prop("Bank File");
        if (string.IsNullOrWhiteSpace(bankFile))
            throw new XactBldException($"Sound Bank '{bankName}' has no Bank File");
        WriteOutput(bankFile!, outMs.ToArray());
    }

    // Resolve a sound's playback sample rate from the first Registered Wave reference.
    private static int ResolveSoundRate(
        List<XapNode> tracksNodes,
        Dictionary<string, Dictionary<string, int>> waveEntryRate)
    {
        foreach (var tr in tracksNodes)
            foreach (var evNode in tr.Children)
            {
                var reg = evNode.Child("Registered Wave");
                if (reg == null) continue;
                var wbName = reg.Prop("Wave Bank") ?? "";
                if (!waveEntryRate.TryGetValue(wbName, out var rm)) continue;
                var entryName = reg.Prop("Wave Entry") ?? "";
                if (rm.TryGetValue(entryName, out var rr)) return rr;
                if (rm.Count > 0) return rm.Values.First();
            }
        return 44100;
    }

    // Compile one track event block into its on-disk bytes (12-byte header + payload).
    // Returns null for an unsupported/ignored block. Grows waveBankNames as new banks
    // are referenced. soundRate converts Marker timestamps (ms) to samples.
    private byte[]? CompileEvent(
        XapNode ev,
        List<string> waveBankNames,
        Dictionary<string, Dictionary<string, int>> waveEntryIndex,
        int soundRate)
    {
        int type;
        var body = new MemoryStream();
        var bw = new BinaryWriter(body);
        long sampleTime = 0;

        switch (ev.Name.ToLowerInvariant())
        {
            case "play":
            case "play with pitch and volume variation":
            {
                bool withVar = ev.Name.StartsWith("play with", StringComparison.OrdinalIgnoreCase);
                type = withVar ? EvtPlayWithPitchVolVar : EvtPlay;

                var reg = ev.Child("Registered Wave");
                ushort waveIndex = 0, bankIndex = 0;
                if (reg != null)
                {
                    var wbName = reg.Prop("Wave Bank") ?? "";
                    bankIndex = (ushort)WaveBankTableIndex(waveBankNames, wbName);

                    int wi = reg.IntProp("Wave Entry Index", -1);
                    if (wi < 0)
                    {
                        var entryName = reg.Prop("Wave Entry") ?? "";
                        if (waveEntryIndex.TryGetValue(wbName, out var m) && m.TryGetValue(entryName, out var idx))
                            wi = idx;
                        else wi = 0;
                    }
                    waveIndex = (ushort)wi;
                }

                // XACT_EVENT_PLAY_DESC.WaveSource { WORD wWaveIndex; WORD wBankIndex; }
                bw.Write(waveIndex);
                bw.Write(bankIndex);
                if (withVar)
                {
                    // XACT_EVENT_PITCH_VOLUME_VAR_DESC { SHORT PitchLo,PitchHi; SHORT VolLo,VolHi; }
                    bw.Write((short)ev.IntProp("Pitch Low"));
                    bw.Write((short)ev.IntProp("Pitch High"));
                    bw.Write((short)ev.IntProp("Volume Low"));
                    bw.Write((short)ev.IntProp("Volume High"));
                }
                break;
            }

            case "marker":
            {
                type = EvtMarker;
                long ms = ev.LongProp("Timestamp");
                sampleTime = (long)Math.Round(ms * (double)soundRate / 1000.0);
                // XACT_TRACK_EVENT_MARKER { BYTE bData[8] }. Best-effort payload: Value, Duration.
                bw.Write((uint)ev.IntProp("Value"));
                bw.Write((uint)ev.IntProp("Duration"));
                break;
            }

            case "stop":
                type = EvtStop;
                break;

            case "set volume":
                type = EvtSetVolume;
                bw.Write((short)ev.IntProp("Volume"));
                break;

            default:
                _log?.Invoke($"Note: unsupported track event '{ev.Name}' skipped");
                return null;
        }

        bw.Flush();
        byte[] payload = body.ToArray();

        // XACT_TRACK_EVENT_HEADER { WORD wType; WORD wSize; DWORD dwFlags; ULONG lSampleTime; }
        var ms2 = new MemoryStream();
        var w = new BinaryWriter(ms2);
        w.Write((ushort)type);
        w.Write((ushort)payload.Length);
        w.Write((uint)0);                    // dwFlags
        w.Write((uint)sampleTime);           // lSampleTime (samples)
        w.Write(payload);
        w.Flush();
        return ms2.ToArray();
    }

    private static int WaveBankTableIndex(List<string> waveBankNames, string name)
    {
        for (int i = 0; i < waveBankNames.Count; i++)
            if (string.Equals(waveBankNames[i], name, StringComparison.OrdinalIgnoreCase))
                return i;
        waveBankNames.Add(name);
        return waveBankNames.Count - 1;
    }

    // Count packed events in an event-data blob (walk 12-byte headers + wSize payloads).
    private static int CountEvents(byte[] blob)
    {
        int count = 0, pos = 0;
        while (pos + 12 <= blob.Length)
        {
            ushort size = (ushort)(blob[pos + 2] | (blob[pos + 3] << 8)); // wSize (LE)
            pos += 12 + size;
            count++;
        }
        return count;
    }

    // =====================================================================
    // Header (XactSounds.h) + cue-list text file
    // =====================================================================

    private void EmitHeader(List<XapNode> waveBanks, List<XapNode> soundBanks)
    {
        var options = _root.Child("Options");
        var headerFile = options?.Prop("Project Header File");
        if (string.IsNullOrWhiteSpace(headerFile))
        {
            _log?.Invoke("Note: no 'Project Header File' in Options; skipping header emit");
        }
        else
        {
            var sb = new StringBuilder();
            var guard = SanitizeMacro(Path.GetFileNameWithoutExtension(headerFile)) + "_H__";
            sb.Append("//\n// ").Append(Path.GetFileName(headerFile))
              .Append(" - generated by xactbld from the XACT project (.xap).\n// Do not edit.\n//\n\n");
            sb.Append("#ifndef __").Append(guard).Append("\n#define __").Append(guard).Append("\n\n");

            foreach (var sbk in soundBanks)
            {
                var bankName = sbk.Prop("Name") ?? "";
                var cues = sbk.ChildrenNamed("Cue").ToList();
                if (cues.Count == 0) continue;
                sb.Append("// Sound bank \"").Append(bankName).Append("\" cue indices\n");
                for (int i = 0; i < cues.Count; i++)
                {
                    var name = cues[i].Prop("Name") ?? $"cue{i}";
                    sb.Append("#define XACT_SOUNDBANK_")
                      .Append(SanitizeMacro(bankName)).Append('_').Append(SanitizeMacro(name))
                      .Append(' ').Append(i).Append('\n');
                }
                sb.Append('\n');
            }

            foreach (var wbk in waveBanks)
            {
                var bankName = wbk.Prop("Name") ?? "";
                var entries = wbk.ChildrenNamed("Entry").ToList();
                if (entries.Count == 0) continue;
                sb.Append("// Wave bank \"").Append(bankName).Append("\" entry indices\n");
                for (int i = 0; i < entries.Count; i++)
                {
                    var name = entries[i].Prop("Name") ?? $"entry{i}";
                    sb.Append("#define XACT_WAVEBANK_")
                      .Append(SanitizeMacro(bankName)).Append('_').Append(SanitizeMacro(name))
                      .Append(' ').Append(i).Append('\n');
                }
                sb.Append('\n');
            }

            sb.Append("#endif\n");
            WriteOutputText(headerFile!, sb.ToString());
        }

        // Optional cue-list text file (Options -> Cue List File).
        var cueListFile = options?.Prop("Cue List File");
        if (!string.IsNullOrWhiteSpace(cueListFile))
        {
            var sb = new StringBuilder();
            foreach (var sbk in soundBanks)
            {
                var cues = sbk.ChildrenNamed("Cue").ToList();
                for (int i = 0; i < cues.Count; i++)
                    sb.Append(cues[i].Prop("Name") ?? $"cue{i}").Append('\t').Append(i).Append('\n');
            }
            WriteOutputText(cueListFile!, sb.ToString());
        }
    }

    private static string SanitizeMacro(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsAsciiLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        return sb.ToString();
    }

    // =====================================================================
    // File / path helpers
    // =====================================================================

    private string ResolvePath(string relative)
    {
        var norm = relative.Trim().Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);
        return Path.IsPathRooted(norm) ? norm : Path.GetFullPath(Path.Combine(_xapDir, norm));
    }

    private void WriteOutput(string relative, byte[] bytes)
    {
        var path = ResolvePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        _log?.Invoke($"Wrote {path} ({bytes.Length} bytes)");
    }

    private void WriteOutputText(string relative, string text)
    {
        var path = ResolvePath(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        _log?.Invoke($"Wrote {path}");
    }

    private static void AlignStream(MemoryStream ms, uint align)
    {
        long rem = ms.Length % align;
        if (rem != 0)
            for (long i = 0; i < align - rem; i++) ms.WriteByte(0);
    }

    private static void WriteFixedString(BinaryWriter w, string s, int len)
    {
        var bytes = new byte[len];
        var src = Encoding.ASCII.GetBytes(s);
        Array.Copy(src, bytes, Math.Min(src.Length, len - 1)); // always NUL-terminated
        w.Write(bytes);
    }
}
