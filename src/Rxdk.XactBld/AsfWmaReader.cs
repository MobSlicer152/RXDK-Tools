using System;
using System.Collections.Generic;
using System.IO;

namespace AsfWma
{
    /// <summary>
    /// Result of demuxing an ASF-containerized WMA file: the WAVEFORMATEX fields,
    /// the codec setup (extradata) bytes, and the raw concatenated WMA codec packets.
    /// </summary>
    public sealed class AsfWmaStream
    {
        public ushort FormatTag;      // wFormatTag   (0x0161 = WMAv2)
        public ushort Channels;       // nChannels
        public uint   SamplesPerSec;  // nSamplesPerSec
        public uint   AvgBytesPerSec; // nAvgBytesPerSec
        public ushort BlockAlign;     // nBlockAlign  (== one WMA packet size)
        public ushort BitsPerSample;  // wBitsPerSample
        public byte[] ExtraData = Array.Empty<byte>(); // cbSize codec-setup bytes
        public byte[] WmaData   = Array.Empty<byte>(); // concatenated raw WMA packets (whole multiple of BlockAlign)
    }

    /// <summary>
    /// Pure-managed (System.* only) demuxer for ASF / .wma files. Extracts the audio
    /// stream's WAVEFORMATEX + extradata and reconstructs the raw WMA elementary
    /// bitstream by concatenating the audio-stream payloads out of the ASF Data Object.
    /// </summary>
    public static class AsfWmaReader
    {
        // ---- ASF object GUIDs (little-endian byte order as stored on disk) ----
        private static readonly Guid GUID_Header           = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C");
        private static readonly Guid GUID_FileProperties    = new Guid("8CABDCA1-A947-11CF-8EE4-00C00C205365");
        private static readonly Guid GUID_StreamProperties  = new Guid("B7DC0791-A9B7-11CF-8EE6-00C00C205365");
        private static readonly Guid GUID_HeaderExtension   = new Guid("5FBF03B5-A92E-11CF-8EE3-00C00C205365");
        private static readonly Guid GUID_Data              = new Guid("75B22636-668E-11CF-A6D9-00AA0062CE6C");
        private static readonly Guid GUID_StreamTypeAudio   = new Guid("F8699E40-5B4D-11CF-A8FD-00805F5C442B");

        public static AsfWmaStream Read(string path) => ReadBytes(File.ReadAllBytes(path));

        public static AsfWmaStream ReadBytes(byte[] asf)
        {
            if (asf == null) throw new ArgumentNullException(nameof(asf));
            if (asf.Length < 30) throw new InvalidDataException("File too small to be ASF.");

            var result = new AsfWmaStream();

            // ---- Top-level object walk: find the Header Object and the Data Object ----
            long pos = 0;
            bool haveFormat = false, haveData = false;
            int audioStreamNumber = -1;
            uint dataPacketCount = 0;
            long dataPacketSize = 0; // ASF packet size (min == max for CBR)

            while (pos + 24 <= asf.Length)
            {
                Guid g = ReadGuid(asf, pos);
                ulong objSize = ReadU64(asf, pos + 16);
                if (objSize < 24 || pos + (long)objSize > asf.Length)
                    throw new InvalidDataException($"Corrupt object size {objSize} at {pos}.");

                if (g == GUID_Header)
                {
                    ParseHeader(asf, pos, (long)objSize, result,
                                ref audioStreamNumber, ref dataPacketCount, ref dataPacketSize,
                                ref haveFormat);
                }
                else if (g == GUID_Data)
                {
                    if (!haveFormat)
                        throw new InvalidDataException("Data Object encountered before audio stream format was found.");
                    result.WmaData = ParseData(asf, pos, (long)objSize, audioStreamNumber,
                                               dataPacketCount, dataPacketSize, result.BlockAlign);
                    haveData = true;
                }

                pos += (long)objSize;
            }

            if (!haveFormat) throw new InvalidDataException("No WMA audio Stream Properties Object found.");
            if (!haveData)   throw new InvalidDataException("No ASF Data Object found.");
            return result;
        }

        // ---------------------------------------------------------------------
        // Header Object: walk its sub-objects (and the Header Extension's children).
        // ---------------------------------------------------------------------
        private static void ParseHeader(byte[] b, long start, long size, AsfWmaStream r,
                                        ref int audioStreamNumber, ref uint dataPacketCount,
                                        ref long dataPacketSize, ref bool haveFormat)
        {
            // Header Object: GUID(16) Size(8) NumberOfHeaderObjects(4) Reserved1(1) Reserved2(1) = 30 bytes header
            long p = start + 30;
            long end = start + size;
            while (p + 24 <= end)
            {
                Guid g = ReadGuid(b, p);
                ulong objSize = ReadU64(b, p + 16);
                if (objSize < 24 || p + (long)objSize > end)
                    throw new InvalidDataException($"Corrupt sub-object size {objSize} at {p}.");

                if (g == GUID_FileProperties)
                {
                    // File Properties body (80 bytes): FileId(16) FileSize(8) CreationDate(8)
                    //   DataPacketsCount(8)@32 PlayDuration(8) SendDuration(8) Preroll(8)
                    //   Flags(4)@64 MinDataPacketSize(4)@68 MaxDataPacketSize(4)@72 MaxBitrate(4)@76
                    long fpBody = p + 24;
                    dataPacketCount = (uint)ReadU64(b, fpBody + 32);
                    uint minPkt = ReadU32(b, fpBody + 68);
                    uint maxPkt = ReadU32(b, fpBody + 72);
                    // CBR => min == max. If they differ we still use maxPkt as the fixed slot size,
                    // which is what CBR WMA always produces.
                    dataPacketSize = maxPkt != 0 ? maxPkt : minPkt;
                    if (minPkt != maxPkt)
                        dataPacketSize = maxPkt; // VBR is out of scope; treat as fixed max slot.
                }
                else if (g == GUID_StreamProperties)
                {
                    ParseStreamProperties(b, p + 24, (long)objSize - 24, r, ref audioStreamNumber, ref haveFormat);
                }
                else if (g == GUID_HeaderExtension)
                {
                    // Header Extension Object body: Reserved1(16) Reserved2(2) DataSize(4) then nested objects.
                    long hxBody = p + 24;
                    uint hxDataSize = ReadU32(b, hxBody + 18);
                    long np = hxBody + 22;
                    long nend = np + hxDataSize;
                    while (np + 24 <= nend)
                    {
                        Guid ng = ReadGuid(b, np);
                        ulong nsz = ReadU64(b, np + 16);
                        if (nsz < 24 || np + (long)nsz > nend) break;
                        // Extended Stream Properties etc. could live here; the WAVEFORMATEX we need
                        // is always in the top-level Stream Properties Object for WMA, so nothing to do.
                        np += (long)nsz;
                    }
                }

                p += (long)objSize;
            }
        }

        // ---------------------------------------------------------------------
        // Stream Properties Object: for an audio stream the Type-Specific Data IS a WAVEFORMATEX.
        // ---------------------------------------------------------------------
        private static void ParseStreamProperties(byte[] b, long body, long len, AsfWmaStream r,
                                                  ref int audioStreamNumber, ref bool haveFormat)
        {
            // body: StreamType(16) ErrorCorrectionType(16) TimeOffset(8)
            //       TypeSpecificDataLength(4) ErrorCorrectionDataLength(4)
            //       Flags(2) Reserved(4) TypeSpecificData[...] ErrorCorrectionData[...]
            Guid streamType = ReadGuid(b, body);
            uint typeSpecificLen = ReadU32(b, body + 40);
            ushort flags = ReadU16(b, body + 48);
            int streamNumber = flags & 0x7F;
            long tsd = body + 54;

            if (streamType != GUID_StreamTypeAudio) return; // ignore video/other streams
            if (haveFormat) return;                         // first audio stream wins
            if (typeSpecificLen < 18) throw new InvalidDataException("Audio TypeSpecificData too short for WAVEFORMATEX.");

            // WAVEFORMATEX
            r.FormatTag      = ReadU16(b, tsd + 0);
            r.Channels       = ReadU16(b, tsd + 2);
            r.SamplesPerSec  = ReadU32(b, tsd + 4);
            r.AvgBytesPerSec = ReadU32(b, tsd + 8);
            r.BlockAlign     = ReadU16(b, tsd + 12);
            r.BitsPerSample  = ReadU16(b, tsd + 14);
            ushort cbSize    = ReadU16(b, tsd + 16);

            if (18 + cbSize > typeSpecificLen)
                cbSize = (ushort)Math.Max(0, (int)typeSpecificLen - 18); // clamp to declared TSD length
            r.ExtraData = new byte[cbSize];
            Array.Copy(b, tsd + 18, r.ExtraData, 0, cbSize);

            audioStreamNumber = streamNumber;
            haveFormat = true;
        }

        // ---------------------------------------------------------------------
        // Data Object: after the 50-byte header, `count` fixed-size ASF data packets.
        // Concatenate the audio-stream payload bytes in file order.
        // ---------------------------------------------------------------------
        private static byte[] ParseData(byte[] b, long start, long size, int audioStreamNumber,
                                        uint dataPacketCount, long dataPacketSize, ushort blockAlign)
        {
            // Data Object: GUID(16) Size(8) FileId(16) TotalDataPackets(8) Reserved(2) = 50 bytes header.
            long p = start + 50;
            long end = start + size;

            if (dataPacketSize <= 0)
            {
                // Fall back: derive fixed packet size from the object body.
                if (dataPacketCount == 0) throw new InvalidDataException("Unknown ASF packet size and packet count.");
                dataPacketSize = (end - p) / dataPacketCount;
            }
            if (dataPacketCount == 0)
                dataPacketCount = (uint)((end - p) / dataPacketSize);

            var outBuf = new MemoryStream();
            for (uint i = 0; i < dataPacketCount; i++)
            {
                long packetStart = p + (long)i * dataPacketSize;
                if (packetStart + dataPacketSize > end + 1) break; // last packet may touch object end
                ParsePacket(b, packetStart, dataPacketSize, audioStreamNumber, outBuf);
            }

            byte[] data = outBuf.ToArray();
            // CBR WMA must be a whole number of block_align packets; trim any stray tail defensively.
            if (blockAlign > 0)
            {
                int whole = data.Length - (data.Length % blockAlign);
                if (whole != data.Length)
                {
                    var trimmed = new byte[whole];
                    Array.Copy(data, trimmed, whole);
                    data = trimmed;
                }
            }
            return data;
        }

        // Length-type field -> byte count (0/1/2/4).
        private static int LenBytes(int lenType) => lenType == 3 ? 4 : lenType; // 0,1,2, or 4

        private static ulong ReadVar(byte[] b, ref long o, int lenType)
        {
            switch (lenType)
            {
                case 0: return 0;
                case 1: return b[o++];
                case 2: { ulong v = ReadU16(b, o); o += 2; return v; }
                case 3: { ulong v = ReadU32(b, o); o += 4; return v; }
                default: throw new InvalidDataException("Bad length type.");
            }
        }

        private static void ParsePacket(byte[] b, long packetStart, long packetSize,
                                        int audioStreamNumber, MemoryStream outBuf)
        {
            long o = packetStart;
            long packetEnd = packetStart + packetSize;

            // ---- Error Correction Data ----
            byte ecFlags = b[o];
            if ((ecFlags & 0x80) != 0)
            {
                o++; // consume EC flags
                int ecLenType = (ecFlags >> 5) & 0x03; // Error Correction Length Type
                int ecDataLen;
                if (ecLenType == 0)
                    ecDataLen = ecFlags & 0x0F;         // Error Correction Data Length
                else
                    ecDataLen = LenBytes(ecLenType);    // rarely used
                o += ecDataLen;
            }
            // else: no EC; the byte we peeked is the Length Type Flags below.

            // ---- Payload Parsing Information ----
            byte lenTypeFlags = b[o++];
            byte propFlags    = b[o++];

            bool multiplePayloads = (lenTypeFlags & 0x01) != 0;
            int  seqLenType       = (lenTypeFlags >> 1) & 0x03;
            int  padLenType       = (lenTypeFlags >> 3) & 0x03;
            int  pktLenType       = (lenTypeFlags >> 5) & 0x03;

            int repLenType    = (propFlags     ) & 0x03; // Replicated Data Length Type
            int offLenType    = (propFlags >> 2) & 0x03; // Offset Into Media Object Length Type
            int moNumLenType  = (propFlags >> 4) & 0x03; // Media Object Number Length Type
            int strNumLenType = (propFlags >> 6) & 0x03; // Stream Number Length Type

            ulong packetLength = ReadVar(b, ref o, pktLenType); // present only if type != 0
            /* sequence     */   ReadVar(b, ref o, seqLenType);
            ulong paddingLength = ReadVar(b, ref o, padLenType);

            o += 4; // Send Time (DWORD)
            o += 2; // Duration  (WORD)

            // The usable region of this packet (excludes trailing padding).
            // For CBR the slot is fixed at packetSize; declared packetLength (if any) matches.
            long regionEnd = packetEnd - (long)paddingLength;

            int payloadCount;
            int payloadLenType = 0;
            if (multiplePayloads)
            {
                byte payloadFlags = b[o++];
                payloadCount   = payloadFlags & 0x3F;      // Number of Payloads
                payloadLenType = (payloadFlags >> 6) & 0x03; // Payload Length Type
            }
            else
            {
                payloadCount = 1;
            }

            for (int pi = 0; pi < payloadCount; pi++)
            {
                byte streamByte = b[o++];
                int streamNumber = streamByte & 0x7F;
                // (streamByte & 0x80) == key frame flag, unused here.

                /* media object number */ ReadVar(b, ref o, moNumLenType);
                ulong offsetOrPresTime  =  ReadVar(b, ref o, offLenType);
                ulong repDataLen        =  ReadVar(b, ref o, repLenType);

                if (repDataLen == 1)
                {
                    // ---- Compressed payload ----
                    // The 1 replicated byte is the Presentation Time Delta; offsetOrPresTime is the
                    // Presentation Time. Payload data is a run of sub-payloads each prefixed by a
                    // 1-byte length. In a multiple-payloads packet the run length is the payload
                    // length field; in a single-payload packet it fills the region.
                    o += 1; // presentation time delta
                    long subEnd;
                    if (multiplePayloads)
                    {
                        ulong plen = ReadVar(b, ref o, payloadLenType);
                        subEnd = o + (long)plen;
                    }
                    else
                    {
                        subEnd = regionEnd;
                    }
                    while (o < subEnd)
                    {
                        int subLen = b[o++];
                        if (o + subLen > subEnd) subLen = (int)(subEnd - o);
                        if (streamNumber == audioStreamNumber && subLen > 0)
                            outBuf.Write(b, (int)o, subLen);
                        o += subLen;
                    }
                    o = subEnd;
                }
                else
                {
                    // ---- Normal payload ----
                    // repData: when repDataLen >= 8, first 4 = media object size, next 4 = presentation time.
                    o += (long)repDataLen; // skip replicated data

                    long payloadDataLen;
                    if (multiplePayloads)
                    {
                        ulong plen = ReadVar(b, ref o, payloadLenType);
                        payloadDataLen = (long)plen;
                    }
                    else
                    {
                        // Single payload: fills the rest of the region (packet minus padding).
                        payloadDataLen = regionEnd - o;
                    }
                    if (payloadDataLen < 0) payloadDataLen = 0;
                    if (o + payloadDataLen > regionEnd) payloadDataLen = regionEnd - o;

                    if (streamNumber == audioStreamNumber && payloadDataLen > 0)
                        outBuf.Write(b, (int)o, (int)payloadDataLen);
                    o += payloadDataLen;
                }
            }
        }

        // ---------------------------------------------------------------------
        // Little-endian readers
        // ---------------------------------------------------------------------
        private static ushort ReadU16(byte[] b, long o) => (ushort)(b[o] | (b[o + 1] << 8));
        private static uint   ReadU32(byte[] b, long o) => (uint)b[o] | ((uint)b[o + 1] << 8) | ((uint)b[o + 2] << 16) | ((uint)b[o + 3] << 24);
        private static ulong  ReadU64(byte[] b, long o)
        {
            ulong lo = ReadU32(b, o);
            ulong hi = ReadU32(b, o + 4);
            return lo | (hi << 32);
        }

        // ASF stores GUIDs as {DWORD, WORD, WORD, 8xBYTE} little-endian, which is exactly
        // how System.Guid(byte[16]) interprets its input, so the string GUIDs above match.
        private static Guid ReadGuid(byte[] b, long o)
        {
            var tmp = new byte[16];
            Array.Copy(b, o, tmp, 0, 16);
            return new Guid(tmp);
        }
    }
}
