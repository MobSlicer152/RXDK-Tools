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
using AsfWma;

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
    // Bumped 1 -> 2 when wCategory was added to the sound entry. The entry stride
    // changed, so libxact cannot read a v1 bank with the v2 struct -- but it
    // validates the version strictly and rejects a mismatch outright, so a stale
    // .xsb gives a clean error instead of misread audio. Rebuild banks.
    private const uint XsbVersion = 2;

    // Matches XACT_SOUNDBANK_CATEGORY_UNUSED in libxact's xactp.h.
    private const int CategoryUnused = 0xFFFF;
    private const uint XwbSignature = 0x444E4257; // 'DNBW' -> "WBND" on disk (LE)
    private const uint XwbVersion = 3;            // WAVEBANK_HEADER_VERSION (xactwb.h)

    // xactwb.h on-disk sizes: WAVEBANKHEADER is 2 DWORDs + 4 segment regions; WAVEBANKDATA is
    // 6 DWORDs + CHAR[16]; WAVEBANKENTRY is dwFlags + format + 2 regions.
    private const uint WavebankHeaderSize = 40;
    private const uint WavebankDataSize = 40;
    private const int  WavebankEntrySize = 24;
    private const int  WavebankEntryNameLen = 64;   // WAVEBANK_ENTRYNAME_LENGTH
    private const uint WavebankAlignmentMin = 4;    // WAVEBANK_ALIGNMENT_MIN

    private const uint WavebankTypeStreaming = 0x00000001;  // WAVEBANK_TYPE_STREAMING
    private const uint WavebankFlagsEntryNames = 0x00010000; // WAVEBANK_FLAGS_ENTRYNAMES

    // WAVEBANKENTRY dwFlags (xactwb.h).
    private const uint WavebankEntryLoopCache = 0x00000002;   // a looping sound uses this wave
    private const uint WavebankEntryFilterAdpcm = 0x00010000; // stored as Xbox ADPCM

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

        // Sound categories, indexed in declaration order. This MUST match the order
        // the XACT_CATEGORY_<NAME> enumerators are emitted in the generated header,
        // because a title passes one of those enumerators to GlobalPause /
        // SetMasterVolume and libxact compares it against what is in the bank.
        var categoryIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var cat in _root.ChildrenNamed("Sound Category"))
        {
            var nm = cat.Prop("Name") ?? "";
            if (!categoryIndex.ContainsKey(nm)) categoryIndex[nm] = categoryIndex.Count;
        }

        var waveBanks = _root.ChildrenNamed("Wave Bank").ToList();
        var soundBanks = _root.ChildrenNamed("Sound Bank").ToList();

        // ---- Build every wave bank (.xwb) and index entry names per bank ----
        // waveEntryIndex[bankName][entryName] = entry index (used by Registered Wave refs).
        var waveEntryIndex = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);
        var waveEntryRate = new Dictionary<string, Dictionary<string, int>>(StringComparer.OrdinalIgnoreCase);

        var loopedWaves = CollectLoopedWaves();

        foreach (var wb in waveBanks)
        {
            var bankName = wb.Prop("Name") ?? "";
            loopedWaves.TryGetValue(bankName, out var looped);
            var (perName, perRate) = BuildWaveBank(wb, bankName, looped);
            waveEntryIndex[bankName] = perName;
            waveEntryRate[bankName] = perRate;
        }

        // ---- Build every sound bank (.xsb) ----
        foreach (var sb in soundBanks)
            BuildSoundBank(sb, layerIndex, categoryIndex, waveEntryIndex, waveEntryRate);

        // ---- Emit the C header (+ optional cue-list text file) ----
        EmitHeader(waveBanks, soundBanks);
    }

    // =====================================================================
    // Wave bank (.xwb)
    // =====================================================================

    // WAVE_FORMAT_XBOX_ADPCM, and the fixed Xbox ADPCM block size (per channel):
    // 64 samples packed into 36 bytes.
    private const ushort WaveFormatXboxAdpcm = 0x0069;
    private const int    XboxAdpcmBlockBytes = 36;

    // WAVE_FORMAT_MSAUDIO1 / WAVE_FORMAT_WMAUDIO2, as an ASF audio stream reports them.
    private const ushort WmaV1FormatTag = 0x0160;
    private const ushort WmaV2FormatTag = 0x0161;

    // Per-entry loaded audio: PCM/ADPCM samples (from wav/aiff), or a .wma file stored verbatim.
    private sealed class WaveEntryDesc
    {
        public string Name = "";      // friendly name, written to the entry-name segment
        public ushort FormatTag;      // 1 = PCM, 0x0161 = WMAv2, 0x0160 = WMAv1
        public ushort Channels;
        public uint   SamplesPerSec;
        public uint   AvgBytesPerSec;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public uint   LoopStart;      // bytes into Data; zero length means no loop
        public uint   LoopLength;
        public uint   Flags;          // WAVEBANKENTRY dwFlags
        public byte[] Data = Array.Empty<byte>();        // PCM/ADPCM samples, or the .wma file
    }

    // WAVEBANKENTRY_FLAGS_LOOPCACHE marks a wave that one or more looping sounds use. That is a
    // property of the sound bank (a track's Loop Count), not of the wave, so it has to be
    // collected before any wave bank is written. Keyed wave bank name -> wave entry names.
    private Dictionary<string, HashSet<string>> CollectLoopedWaves()
    {
        var looped = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        static IEnumerable<XapNode> RegisteredWaves(XapNode node)
        {
            foreach (var child in node.Children)
            {
                if (string.Equals(child.Name, "Registered Wave", StringComparison.OrdinalIgnoreCase))
                    yield return child;
                foreach (var nested in RegisteredWaves(child))
                    yield return nested;
            }
        }

        foreach (var sb in _root.ChildrenNamed("Sound Bank"))
        foreach (var sound in sb.ChildrenNamed("Sound"))
        foreach (var track in sound.ChildrenNamed("Track"))
        foreach (var evt in track.Children)
        {
            if (evt.IntProp("Loop Count") == 0)
                continue;
            foreach (var wave in RegisteredWaves(evt))
            {
                var bank = wave.Prop("Wave Bank");
                var entry = wave.Prop("Wave Entry");
                if (bank == null || entry == null)
                    continue;
                if (!looped.TryGetValue(bank, out var entries))
                    looped[bank] = entries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                entries.Add(entry);
            }
        }

        return looped;
    }

    private (Dictionary<string, int> names, Dictionary<string, int> rates) BuildWaveBank(
        XapNode wb, string bankName, HashSet<string>? loopedEntries)
    {
        var entries = wb.ChildrenNamed("Entry").ToList();
        var names = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var rates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        var descs = new List<WaveEntryDesc>();
        bool anyWma = false;

        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            var name = e.Prop("Name") ?? $"entry{i}";
            var file = e.Prop("File") ?? throw new XactBldException($"Wave entry '{name}' has no File");
            var wavPath = ResolvePath(file);
            if (!File.Exists(wavPath))
                throw new XactBldException($"Wave file not found: {wavPath}");
            var ext = Path.GetExtension(wavPath).ToLowerInvariant();

            WaveEntryDesc d;
            if (ext == ".wma")
            {
                // A WMA entry is stored as the .wma file itself, byte for byte, and carries the
                // mini-format's WMA tag. The runtime plays it by pointing a WMA decoder Xbox Media
                // Object at the entry's offset in the bank file and streaming the PCM it yields, so
                // what has to survive into the bank is the ASF container the decoder parses -- the
                // codec setup lives in its header, which is why a bank entry needs no field for it.
                //
                // The ASF is still read here, for the channel count and sample rate the mini-format
                // carries, and to reject a file the runtime would not be able to decode.
                var s = AsfWmaReader.Read(wavPath);
                d = new WaveEntryDesc
                {
                    FormatTag = s.FormatTag,
                    Channels = s.Channels,
                    SamplesPerSec = s.SamplesPerSec,
                    AvgBytesPerSec = s.AvgBytesPerSec,
                    BlockAlign = s.BlockAlign,
                    BitsPerSample = 16,     // what the decoder emits, whatever the source was
                    Data = File.ReadAllBytes(wavPath),
                };
                anyWma = true;
            }
            else
            {
                var wav = (ext == ".aif" || ext == ".aiff" || ext == ".aifc")
                    ? AiffReader.Read(wavPath)
                    : WavReader.Read(wavPath);
                // Xbox ADPCM (0x69) rides through untouched: the wave-bank mini-format has a
                // PCM/ADPCM tag bit, so the bank carries the compressed blocks natively and the
                // hardware decodes them. Anything else we cannot represent.
                if (wav.FormatTag != 1 && wav.FormatTag != WaveFormatXboxAdpcm)
                    throw new XactBldException(
                        $"Wave '{name}' uses unsupported WAV format tag {wav.FormatTag} " +
                        "(only uncompressed PCM, Xbox ADPCM, AIFF, and WMA are supported).");
                bool isAdpcm = wav.FormatTag == WaveFormatXboxAdpcm;
                var pcm = wav.Pcm;
                uint entryFlags = 0;

                // "ADPCM Filter" asks for this entry to be stored compressed. A wave that is
                // already Xbox ADPCM rides through untouched either way: the mini-format has a
                // PCM/ADPCM tag, so the bank carries the blocks natively for the hardware to
                // decode.
                if (!isAdpcm && e.IntProp("ADPCM Filter") != 0)
                {
                    if (wav.BitsPerSample != 16)
                        throw new XactBldException(
                            $"Wave '{name}' asks for the ADPCM filter but is {wav.BitsPerSample}-bit; " +
                            "only 16-bit PCM can be encoded.");
                    pcm = ImaAdpcmEncoder.Encode(pcm, wav.Channels);
                    isAdpcm = true;
                    entryFlags |= WavebankEntryFilterAdpcm;
                }

                // ADPCM block alignment is fixed by the format: 64 samples per channel packed
                // into 36 bytes.
                ushort blockAlign = isAdpcm
                    ? (ushort)(XboxAdpcmBlockBytes * wav.Channels)
                    : (ushort)(wav.Channels * (wav.BitsPerSample / 8));
                d = new WaveEntryDesc
                {
                    FormatTag = (ushort)(isAdpcm ? WaveFormatXboxAdpcm : 1),
                    Channels = (ushort)wav.Channels,
                    SamplesPerSec = (uint)wav.SamplesPerSec,
                    AvgBytesPerSec = (uint)(wav.SamplesPerSec * blockAlign),
                    BlockAlign = blockAlign,
                    BitsPerSample = (ushort)(isAdpcm ? ImaAdpcmEncoder.BitsPerSample : wav.BitsPerSample),
                    LoopStart = wav.LoopStart,
                    LoopLength = wav.LoopLength,
                    Flags = entryFlags,
                    Data = pcm,
                };
            }

            d.Name = name;
            if (loopedEntries != null && loopedEntries.Contains(name))
                d.Flags |= WavebankEntryLoopCache;
            names[name] = i;
            rates[name] = (int)d.SamplesPerSec;
            descs.Add(d);
        }

        var bankFile = wb.Prop("Bank File");
        if (string.IsNullOrWhiteSpace(bankFile))
            throw new XactBldException($"Wave Bank '{bankName}' has no Bank File");

        // A streaming bank is read from the DVD in place, so the project picks the sector
        // alignment its entries are padded to; an in-memory bank has no such constraint and
        // uses the format minimum.
        bool streaming = wb.IntProp("Streaming") != 0;
        uint alignment = (uint)wb.IntProp("Alignment", (int)WavebankAlignmentMin);
        if (alignment < WavebankAlignmentMin) alignment = WavebankAlignmentMin;
        bool entryNames = wb.IntProp("Entry Names") != 0;

        // WMA can only be played by streaming it past a software decoder, so a bank holding any
        // WMA entry has to be a streaming bank -- the runtime needs the file to still be open, and
        // an offset into it, to decode from.
        if (anyWma && !streaming)
        {
            throw new XactBldException(
                $"Wave Bank '{bankName}' contains WMA but is not a streaming bank. " +
                "WMA is decoded in software as it streams, so it cannot go in an in-memory bank.");
        }

        WriteOutput(bankFile!, BuildXwbBank(bankName, descs, streaming, alignment, entryNames));
        return (names, rates);
    }

    // Standard wave bank (WBND), the version 3 layout the shipped xactwb.h describes: a
    // segment lookup table pointing at WAVEBANKDATA, the entry meta-data array, the optional
    // entry-name array, and the wave data.
    //
    // Titles do parse this file - WaveBank and WaveBankStream read the header themselves with
    // xactwb.h and reject a version they do not know - so the on-disk version is a public
    // contract, not just one between this tool and libxact.
    private static byte[] BuildXwbBank(
        string bankName, List<WaveEntryDesc> descs, bool streaming, uint alignment, bool entryNames)
    {
        // Every entry starts on an alignment boundary, and so does the end of the segment:
        // a streaming bank is read in whole sectors, so the tail is padded like the rest.
        var meta = new List<(uint flags, uint fmt, uint playStart, uint playLen, uint loopStart, uint loopLen)>();
        var data = new MemoryStream();
        foreach (var d in descs)
        {
            AlignStream(data, alignment);
            uint playStart = (uint)data.Length;
            data.Write(d.Data, 0, d.Data.Length);
            meta.Add((d.Flags, PackMiniFormat(d), playStart, (uint)d.Data.Length, d.LoopStart, d.LoopLength));
        }
        AlignStream(data, alignment);

        int entryCount = descs.Count;
        uint bankDataOff = WavebankHeaderSize;
        uint metaOff = bankDataOff + WavebankDataSize;
        uint metaLen = (uint)(entryCount * WavebankEntrySize);
        uint namesOff = entryNames ? metaOff + metaLen : 0;
        uint namesLen = entryNames ? (uint)(entryCount * WavebankEntryNameLen) : 0;
        uint dataOff = AlignUp(metaOff + metaLen + namesLen, alignment);

        var outBytes = new MemoryStream();
        var w = new BinaryWriter(outBytes);

        // WAVEBANKHEADER
        w.Write(XwbSignature);
        w.Write(XwbVersion);
        w.Write(bankDataOff); w.Write(WavebankDataSize);
        w.Write(metaOff);     w.Write(metaLen);
        w.Write(namesOff);    w.Write(namesLen);
        w.Write(dataOff);     w.Write((uint)data.Length);

        // WAVEBANKDATA
        uint flags = (streaming ? WavebankTypeStreaming : 0u)
                   | (entryNames ? WavebankFlagsEntryNames : 0u);
        w.Write(flags);
        w.Write((uint)entryCount);
        WriteFixedString(w, bankName, FriendlyNameLen);
        w.Write((uint)WavebankEntrySize);       // dwEntryMetaDataElementSize
        w.Write((uint)WavebankEntryNameLen);    // dwEntryNameElementSize (set even with no names)
        w.Write(alignment);
        w.Write((uint)0);                       // CompactFormat (compact banks not emitted)

        // WAVEBANKENTRY[]
        foreach (var m in meta)
        {
            w.Write(m.flags);
            w.Write(m.fmt);
            w.Write(m.playStart); w.Write(m.playLen);
            w.Write(m.loopStart); w.Write(m.loopLen);
        }

        if (entryNames)
            foreach (var d in descs)
                WriteFixedString(w, d.Name, WavebankEntryNameLen);

        // Pad out to the aligned start of the wave data segment.
        w.Flush();
        while (outBytes.Length < dataOff) outBytes.WriteByte(0);

        w.Write(data.ToArray());
        w.Flush();
        return outBytes.ToArray();
    }

    // WAVEBANKMINIWAVEFORMAT: wFormatTag:2, nChannels:3, nSamplesPerSec:26, wBitsPerSample:1.
    // The tag is two bits wide here (version 2 used one), which shifts every field above it.
    private static uint PackMiniFormat(WaveEntryDesc d)
    {
        uint tag = d.FormatTag switch
        {
            WaveFormatXboxAdpcm => 1u,          // WAVEBANKMINIFORMAT_TAG_ADPCM
            WmaV1FormatTag or WmaV2FormatTag => 2u,  // WAVEBANKMINIFORMAT_TAG_WMA
            _ => 0u,                            // WAVEBANKMINIFORMAT_TAG_PCM
        };
        return (tag & 0x3)
             | ((uint)(d.Channels & 0x7) << 2)
             | ((d.SamplesPerSec & 0x3FFFFFF) << 5)
             | ((d.BitsPerSample == 16 ? 1u : 0u) << 31);
    }

    private static uint AlignUp(uint value, uint align)
    {
        uint rem = value % align;
        return rem == 0 ? value : value + (align - rem);
    }

    // =====================================================================
    // Sound bank (.xsb)
    // =====================================================================

    private void BuildSoundBank(
        XapNode sb,
        Dictionary<string, int> layerIndex,
        Dictionary<string, int> categoryIndex,
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
        var soundCategories = new List<int>();   // index, or CategoryUnused

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

            // A Sound names its category by name (`Category = SFX;`). A sound with
            // none, or one naming a category the project never declared, gets the
            // unused sentinel rather than index 0 -- otherwise it would silently
            // join whichever category happened to be declared first.
            var catName = snd.Prop("Category") ?? "";
            soundCategories.Add(categoryIndex.TryGetValue(catName, out var ci) ? ci : CategoryUnused);
        }

        int nSounds = sounds.Count;
        int nCues = cues.Count;

        // ---- Compute absolute file offsets (cumulative; matches filegen for 1 sound) ----
        // soundSize is 32, not 28, since wCategory and the padding that keeps the entry 4-aligned
        // were added (see XsbVersion). The runtime indexes the table with sizeof(), so a stride that
        // disagrees with the C struct misreads every entry past the first.
        const int headerSize = 36, cueSize = 24, soundSize = 32, wbEntrySize = 20, trackSize = 8;
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
            w.Write((ushort)soundCategories[i]);                          // wCategory
            w.Write((ushort)0);                                           // wReserved (entry padding)

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

            // Sound categories declared in the .xap become an `XACT_CATEGORY` type plus one
            // `XACT_CATEGORY_<NAME>` enumerator each. The XDK's own samples (TechCertGame,
            // XACTWMAPlayList) use both, and no XDK header defines them -- they only ever
            // existed in this generated header.
            var categories = _root.ChildrenNamed("Sound Category").ToList();
            if (categories.Count > 0)
            {
                sb.Append("// Sound categories\n");
                sb.Append("typedef enum XACT_CATEGORY\n{\n");
                for (int i = 0; i < categories.Count; i++)
                {
                    var name = categories[i].Prop("Name") ?? $"category{i}";
                    sb.Append("    XACT_CATEGORY_").Append(SanitizeMacro(name))
                      .Append(" = ").Append(i).Append(",\n");
                }
                sb.Append("    XACT_CATEGORY_COUNT = ").Append(categories.Count).Append(",\n");
                sb.Append("} XACT_CATEGORY;\n\n");
            }

            // The XDK XACT tool emits each bank's indices as an `enum XACT_SOUNDBANK_<BANK>` (a
            // real type samples use for cue-index fields), with a trailing _CUE_COUNT enumerator.
            // Enumerators double as the named indices, so this is compatible with code that used
            // the old #define spelling.
            foreach (var sbk in soundBanks)
            {
                var bankName = sbk.Prop("Name") ?? "";
                var cues = sbk.ChildrenNamed("Cue").ToList();
                if (cues.Count == 0) continue;
                var macro = SanitizeMacro(bankName);
                sb.Append("// Sound bank \"").Append(bankName).Append("\" cue indices\n");
                sb.Append("typedef enum XACT_SOUNDBANK_").Append(macro).Append("\n{\n");
                for (int i = 0; i < cues.Count; i++)
                {
                    var name = cues[i].Prop("Name") ?? $"cue{i}";
                    sb.Append("    XACT_SOUNDBANK_").Append(macro).Append('_').Append(SanitizeMacro(name))
                      .Append(" = ").Append(i).Append(",\n");
                }
                sb.Append("    XACT_SOUNDBANK_").Append(macro).Append("_CUE_COUNT = ").Append(cues.Count).Append(",\n");
                sb.Append("} XACT_SOUNDBANK_").Append(macro).Append(";\n\n");
            }

            foreach (var wbk in waveBanks)
            {
                var bankName = wbk.Prop("Name") ?? "";
                var entries = wbk.ChildrenNamed("Entry").ToList();
                if (entries.Count == 0) continue;
                var macro = SanitizeMacro(bankName);
                sb.Append("// Wave bank \"").Append(bankName).Append("\" entry indices\n");
                sb.Append("typedef enum XACT_WAVEBANK_").Append(macro).Append("\n{\n");
                for (int i = 0; i < entries.Count; i++)
                {
                    var name = entries[i].Prop("Name") ?? $"entry{i}";
                    sb.Append("    XACT_WAVEBANK_").Append(macro).Append('_').Append(SanitizeMacro(name))
                      .Append(" = ").Append(i).Append(",\n");
                }
                sb.Append("    XACT_WAVEBANK_").Append(macro).Append("_ENTRY_COUNT = ").Append(entries.Count).Append(",\n");
                sb.Append("} XACT_WAVEBANK_").Append(macro).Append(";\n\n");
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

    // A short chunk of 16-bit stereo 44.1kHz silence, used in place of an undecodable WMA source
    // so a title that references streaming WMA still builds. 0.1s keeps the wave bank small.
    private static WavData SilentPlaceholder() => new()
    {
        FormatTag = 1,
        Channels = 2,
        SamplesPerSec = 44100,
        BitsPerSample = 16,
        Pcm = new byte[44100 * 2 * 2 / 10],
    };

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
