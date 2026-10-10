using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

// HFE container layout/opcode semantics adapted from bales0/HxCFloppyEmulator.
// Copyright (C) 2006-2026 Jean-François DEL NERO. GPL-2.0-or-later.
// No warranty, including merchantability or fitness for a particular purpose.
// See THIRD_PARTY_NOTICES.md and https://www.gnu.org/licenses/.
internal sealed class HfeImage : PhysicalDiskImage, IMediaFormat
{
    private byte[]? original;
    private byte[]? header;
    internal bool IsV3 { get; private init; }
    public string Id => "hfe";
    public string DisplayName => "HFE / HFEv3";
    public IReadOnlyList<string> Extensions => [".hfe"];
    public MediaCapabilities Capabilities => MediaCapabilities.Detect | MediaCapabilities.Read | MediaCapabilities.Write | MediaCapabilities.PhysicalTrackModel | MediaCapabilities.SectorModel | MediaCapabilities.PreserveTiming | MediaCapabilities.PreserveWeakBits;
    public MediaDetection Identify(ReadOnlySpan<byte> source) => source.Length >= 8
        ? new(source[..8].SequenceEqual("HXCPICFE"u8) || source[..8].SequenceEqual("HXCHFEV3"u8), source[..8].SequenceEqual("HXCHFEV3"u8) ? "HFEv3" : "HFE") : new(false, "");
    private static ushort U(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static void U(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), checked((ushort)value));

    internal static HfeImage Parse(byte[] bytes)
    {
        if (bytes.Length < 512 || !new HfeImage().Identify(bytes).Matches) throw new InvalidDataException("HFE signature/header is missing or truncated.");
        bool v3 = bytes.AsSpan(0, 8).SequenceEqual("HXCHFEV3"u8);
        if (bytes[8] != 0 || bytes[9] == 0 || bytes[10] is not (1 or 2)) throw new InvalidDataException("Unsupported HFE revision or geometry.");
        int cylinders = bytes[9], sides = bytes[10], table = U(bytes, 18) * 512;
        if (table < 512 || table > bytes.Length - cylinders * 4) throw new InvalidDataException("HFE track table is outside the image.");
        int bitrate = U(bytes, 12) * 1000; int rpm = U(bytes, 14);
        var tracks = new List<PhysicalTrack>(); var ranges = new List<(int Start, int End)>();
        for (int cylinder = 0; cylinder < cylinders; cylinder++)
        {
            int start = U(bytes, table + cylinder * 4) * 512, len = U(bytes, table + cylinder * 4 + 2);
            int stored = (len + 511) / 512 * 512;
            if (len == 0 || (len & 1) != 0 || start < 512 || start > bytes.Length - stored || (start < table + cylinders * 4 && start + stored > table))
                throw new InvalidDataException("HFE track length, bounds or metadata overlap is invalid.");
            if (ranges.Any(r => start < r.End && start + stored > r.Start)) throw new InvalidDataException("HFE track data overlaps another track.");
            ranges.Add((start, start + stored));
            for (int side = 0; side < sides; side++)
            {
                int encoding = bytes[11];
                if (cylinder == 0 && bytes[22 + side * 2] == 0) encoding = bytes[23 + side * 2];
                var stream = new byte[len / 2];
                for (int i = 0; i < stream.Length; i++) stream[i] = bytes[start + (i / 256) * 512 + side * 256 + i % 256];
                var decoded = DecodeStream(stream, v3, bitrate);
                var track = new PhysicalTrack { Cylinder = cylinder, Side = side, BitRate = bitrate == 0 ? null : bitrate, Rpm = rpm == 0 ? null : rpm,
                    Encoding = encoding switch { 0 => PhysicalEncoding.Mfm, 2 => PhysicalEncoding.Fm, _ => PhysicalEncoding.Unknown }, ContainerEncoding = (byte)encoding, PackedBitCells = decoded.Bits, BitCellCount = decoded.Count,
                    WeakBitMask = decoded.Weak, Timing = decoded.Timing, IndexCells = decoded.Index };
                track.Sectors = track.Encoding == PhysicalEncoding.Unknown ? [] : FloppyTrackCodec.Decode(track); tracks.Add(track);
            }
        }
        return new HfeImage { IsV3 = v3, Cylinders = cylinders, Sides = sides, Tracks = tracks, original = (byte[])bytes.Clone(), header = bytes[..512] };
    }

    private static (byte[] Bits, int Count, byte[] Weak, PhysicalTiming[] Timing, long[] Index) DecodeStream(byte[] stream, bool v3, int rate)
    {
        if (!v3) return ((byte[])stream.Clone(), stream.Length * 8, [], [], []);
        var bits = new byte[stream.Length]; var weak = new byte[stream.Length];
        var timing = new List<PhysicalTiming>(); var indexes = new List<long>(); int count = 0, skip = 0; bool pendingSkip = false;
        for (int i = 0; i < stream.Length; i++)
        {
            byte value = PackedBitCells.Reverse(stream[i]); bool random = false;
            if (value >= 0xF0)
            {
                switch (value)
                {
                    case 0xF0: continue;
                    case 0xF1: indexes.Add(count); continue;
                    case 0xF2:
                        if (++i >= stream.Length) throw new InvalidDataException("Truncated HFEv3 bitrate opcode.");
                        int divisor = PackedBitCells.Reverse(stream[i]);
                        if (divisor == 0) throw new InvalidDataException("HFEv3 bitrate divisor is zero.");
                        timing.Add(new(count, divisor)); continue;
                    case 0xF3:
                        if (pendingSkip || ++i >= stream.Length) throw new InvalidDataException("Invalid HFEv3 skip opcode.");
                        skip = PackedBitCells.Reverse(stream[i]);
                        if (skip > 7) throw new InvalidDataException("Unsupported HFEv3 skip count.");
                        pendingSkip = true;
                        continue;
                    case 0xF4: random = true; value = 0; break;
                    default: throw new InvalidDataException($"Unsupported HFEv3 opcode {value:X2}; no silent loss is allowed.");
                }
            }
            for (int bit = skip; bit < 8; bit++)
            {
                PackedBitCells.Set(bits, count, (value & (0x80 >> bit)) != 0);
                PackedBitCells.Set(weak, count, random); count++;
            }
            skip = 0; pendingSkip = false;
        }
        if (pendingSkip || count == 0) throw new InvalidDataException("HFEv3 stream is empty or ends in a pending skip.");
        return (bits[..((count + 7) / 8)], count, weak[..((count + 7) / 8)], timing.ToArray(), indexes.ToArray());
    }

    internal static HfeImage FromDsk(DskDocument document)
    {
        var image = document.Image;
        if (image.Tracks.Any(t => t == null || t.Sectors.Count == 0)) throw new InvalidDataException("DSK → HFE requires complete populated tracks; missing tracks are not fabricated.");
        return new HfeImage { IsV3 = true, Cylinders = image.TrackCount, Sides = image.SideCount,
            Tracks = image.Tracks.Select((t, i) => FloppyTrackCodec.Encode(t!, i / image.SideCount, i % image.SideCount)).ToArray() };
    }

    internal byte[] Serialize(bool v3, bool preserveOriginal = true)
    {
        if (preserveOriginal && original != null && v3 == IsV3) return (byte[])original.Clone();
        if (Tracks.Any(t => t.Encoding == PhysicalEncoding.Unknown))
            throw new InvalidDataException("Unknown HFE encoding supports physical inspection and byte-identical Save As in its original variant only. Re-encoding/conversion is unavailable.");
        int rate = Tracks[0].BitRate ?? 0;
        if (rate % 1000 != 0) throw new InvalidDataException("HFE header bitrate must be expressible in whole kbit/s.");
        var trackStreams = new List<byte[]>();
        for (int cylinder = 0; cylinder < Cylinders; cylinder++)
        {
            var sides = Tracks.Where(t => t.Cylinder == cylinder).OrderBy(t => t.Side).ToArray();
            if (sides.Length != Sides) throw new InvalidDataException("Missing physical side.");
            if (!v3 && sides.Any(t => t.BitCellCount % 8 != 0 || t.BitRate != Tracks[0].BitRate || t.WeakBitMask.Any(b => b != 0) || t.Timing.Count != 0 || t.IndexCells.Any(i => i != 0)))
                throw new InvalidDataException("Legacy HFE cannot preserve these timing, weak bits or index/length properties. Select HFEv3.");
            if (!v3 && sides.Select(t => t.BitCellCount).Distinct().Count() != 1) throw new InvalidDataException("Legacy HFE cannot preserve unequal side lengths. Select HFEv3.");
            var streams = sides.Select(t => v3 ? EncodeStream(t, rate) : t.PackedBitCells[..checked((int)(t.BitCellCount / 8))]).ToArray();
            int length = streams.Max(s => s.Length);
            if (length * 2 > ushort.MaxValue) throw new InvalidDataException("HFE opcode stream exceeds the 16-bit track length limit.");
            var block = Enumerable.Repeat(v3 ? PackedBitCells.Reverse(0xF0) : (byte)0, (length * 2 + 511) / 512 * 512).ToArray();
            for (int side = 0; side < Sides; side++) for (int i = 0; i < streams[side].Length; i++) block[(i / 256) * 512 + side * 256 + i % 256] = streams[side][i];
            // Prefix the declared length for table assembly below.
            var packed = new byte[block.Length + 2]; U(packed, 0, length * 2); block.CopyTo(packed, 2); trackStreams.Add(packed);
        }
        int tableBlocks = (Cylinders * 4 + 511) / 512;
        var output = Enumerable.Repeat((byte)0xFF, 512 + tableBlocks * 512 + trackStreams.Sum(t => t.Length - 2)).ToArray();
        if (header != null) header.CopyTo(output, 0);
        (v3 ? "HXCHFEV3"u8 : "HXCPICFE"u8).CopyTo(output);
        output[8] = 0; output[9] = checked((byte)Cylinders); output[10] = checked((byte)Sides);
        output[11] = Tracks.First(t => t.Cylinder != 0 || Cylinders == 1).Encoding == PhysicalEncoding.Mfm ? (byte)0 : (byte)2;
        if (Tracks.Any(t => t.Cylinder > 0 && (t.Encoding == PhysicalEncoding.Mfm ? 0 : 2) != output[11])) throw new InvalidDataException("Mixed track encoding outside cylinder zero is not representable by HFE.");
        U(output, 12, rate / 1000); U(output, 14, checked((int)(Tracks[0].Rpm ?? 0))); U(output, 18, 1);
        if (header == null) { output[16] = 0; output[17] = 1; output[20] = 0xFF; output[21] = 0xFF; }
        for (int side = 0; side < Sides; side++) { output[22 + side * 2] = 0; output[23 + side * 2] = Tracks.First(t => t.Cylinder == 0 && t.Side == side).Encoding == PhysicalEncoding.Mfm ? (byte)0 : (byte)2; }
        int position = 512 + tableBlocks * 512;
        for (int c = 0; c < Cylinders; c++) { U(output, 512 + c * 4, position / 512); U(output, 514 + c * 4, U(trackStreams[c], 0)); trackStreams[c].AsSpan(2).CopyTo(output.AsSpan(position)); position += trackStreams[c].Length - 2; }
        var check = Parse(output); VerifyPhysical(this, check);
        return output;
    }

    private static byte[] EncodeStream(PhysicalTrack track, int globalRate)
    {
        var output = new List<byte>();
        void Emit(byte b) => output.Add(PackedBitCells.Reverse(b));
        var timing = track.Timing.GroupBy(t => t.CellOffset).ToDictionary(g => g.Key, g => g.Last().Divisor);
        if (track.BitRate is int trackRate && trackRate != globalRate && !timing.ContainsKey(0))
        {
            int divisor = 18_000_000 / trackRate;
            if (divisor is < 1 or > 255 || 18_000_000 % trackRate != 0) throw new InvalidDataException("Track bitrate cannot be represented exactly by HFEv3.");
            timing[0] = divisor;
        }
        var indexes = track.IndexCells.ToHashSet();
        long count = track.BitCellCount;
        for (int cell = 0; cell < count;)
        {
            if (indexes.Contains(cell)) Emit(0xF1);
            if (timing.TryGetValue(cell, out int divisor)) { Emit(0xF2); Emit(checked((byte)divisor)); }
            bool weak = track.WeakBitMask.Length > 0 && PackedBitCells.Get(track.WeakBitMask, cell);
            int n = 1;
            while (n < 8 && cell + n < count && !indexes.Contains(cell + n) && !timing.ContainsKey(cell + n) &&
                (track.WeakBitMask.Length > 0 && PackedBitCells.Get(track.WeakBitMask, cell + n)) == weak) n++;
            byte value = 0;
            for (int i = 0; i < n; i++) value = (byte)((value << 1) | (PackedBitCells.Get(track.PackedBitCells, cell + i) ? 1 : 0));
            if (!weak && n == 8 && value >= 0xF0) { n = 4; value >>= 4; }
            if (n < 8) { Emit(0xF3); Emit((byte)(8 - n)); }
            Emit(weak ? (byte)0xF4 : value); cell += n;
        }
        if (indexes.Contains(count)) Emit(0xF1);
        if (timing.TryGetValue(count, out int endDivisor)) { Emit(0xF2); Emit(checked((byte)endDivisor)); }
        return output.ToArray();
    }

    internal static void VerifyPhysical(HfeImage source, HfeImage target)
    {
        if (source.Tracks.Count != target.Tracks.Count) throw new InvalidDataException("HFE physical track count changed.");
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            var a = source.Tracks[i]; var b = target.Tracks[i];
            if (a.BitCellCount != b.BitCellCount || a.Encoding != b.Encoding || a.Rpm != b.Rpm || !a.IndexCells.SequenceEqual(b.IndexCells)) throw new InvalidDataException("HFE physical metadata changed.");
            for (int cell = 0; cell < a.BitCellCount; cell++)
            {
                bool aw = a.WeakBitMask.Length > 0 && PackedBitCells.Get(a.WeakBitMask, cell), bw = b.WeakBitMask.Length > 0 && PackedBitCells.Get(b.WeakBitMask, cell);
                if (aw != bw || (!aw && PackedBitCells.Get(a.PackedBitCells, cell) != PackedBitCells.Get(b.PackedBitCells, cell))) throw new InvalidDataException("HFE bitcell/weak mask changed.");
            }
            double Effective(PhysicalTrack t, long offset) => t.Timing.LastOrDefault(p => p.CellOffset <= offset)?.BitRate ?? t.BitRate ?? 0;
            foreach (long offset in a.Timing.Select(t => t.CellOffset).Concat(b.Timing.Select(t => t.CellOffset)).Append(0).Distinct())
                if (Math.Abs(Effective(a, offset) - Effective(b, offset)) > 0.001) throw new InvalidDataException("HFE timing changed.");
        }
    }
}
