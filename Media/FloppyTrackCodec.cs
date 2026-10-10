using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal static class FloppyTrackCodec
{
    internal static ushort Crc(ReadOnlySpan<byte> bytes)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in bytes)
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++) crc = (ushort)((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x1021 : 0));
        }
        return crc;
    }
    private static ushort Word(PhysicalTrack track, int start)
    {
        ushort value = 0; int length = checked((int)track.BitCellCount);
        for (int i = 0; i < 16; i++) value = (ushort)((value << 1) | (PackedBitCells.Get(track.PackedBitCells, (start + i) % length) ? 1 : 0));
        return value;
    }
    private static byte Data(PhysicalTrack track, int start)
    {
        ushort word = Word(track, start); byte value = 0;
        for (int i = 7; i >= 0; i--) value = (byte)((value << 1) | ((word >> (i * 2)) & 1));
        return value;
    }
    internal static IReadOnlyList<PhysicalSector> Decode(PhysicalTrack track)
    {
        var result = new List<PhysicalSector>(); int length = checked((int)track.BitCellCount);
        if (length < 160) return result;
        bool mfm = track.Encoding == PhysicalEncoding.Mfm;
        var marks = new List<(int Cell, byte Mark)>();
        for (int cell = 0; cell < length; cell++)
        {
            if (mfm)
            {
                if (Word(track, cell) != 0x4489 || Word(track, cell + 16) != 0x4489 || Word(track, cell + 32) != 0x4489) continue;
                byte mark = Data(track, cell + 48);
                if (mark is 0xFE or 0xFB or 0xF8) marks.Add((cell, mark));
            }
            else
            {
                ushort raw = Word(track, cell);
                if (raw is 0xF57E or 0xF56F or 0xF56A) marks.Add((cell, raw == 0xF57E ? (byte)0xFE : raw == 0xF56F ? (byte)0xFB : (byte)0xF8));
            }
        }
        for (int index = 0; index < marks.Count; index++)
        {
            var header = marks[index]; if (header.Mark != 0xFE) continue;
            int prefix = mfm ? 3 : 0;
            byte[] id = Enumerable.Range(0, 7).Select(i => Data(track, header.Cell + (prefix + i) * 16)).ToArray();
            if (id[4] > 6) continue;
            var next = marks[(index + 1) % marks.Count];
            if (next.Mark is not (0xFB or 0xF8)) continue;
            int sectorLength = 128 << id[4];
            int dataCell = (next.Cell + (prefix + 1) * 16) % length;
            int distanceToNextHeader = length;
            for (int p = 1; p < marks.Count; p++)
            {
                var mark = marks[(index + 1 + p) % marks.Count];
                if (mark.Mark == 0xFE) { distanceToNextHeader = (mark.Cell - dataCell + length) % length; break; }
            }
            if ((sectorLength + 2) * 16 > Math.Min(length, distanceToNextHeader)) continue;
            byte[] body = Enumerable.Range(0, sectorLength + 2).Select(i => Data(track, dataCell + i * 16)).ToArray();
            byte[] headerCrcInput = (mfm ? new byte[] { 0xA1, 0xA1, 0xA1 } : []).Concat(id.Take(5)).ToArray();
            byte[] bodyCrcInput = (mfm ? new byte[] { 0xA1, 0xA1, 0xA1 } : []).Concat(new[] { next.Mark }).Concat(body.Take(sectorLength)).ToArray();
            var weak = new byte[sectorLength];
            for (int b = 0; b < sectorLength; b++)
                for (int bit = 0; bit < 8; bit++)
                {
                    int pos = (dataCell + b * 16 + bit * 2 + 1) % length;
                    if (track.WeakBitMask.Length > 0 && PackedBitCells.Get(track.WeakBitMask, pos)) weak[b] |= (byte)(1 << (7 - bit));
                }
            result.Add(new(id[1], id[2], id[3], id[4], body[..sectorLength], header.Cell, dataCell,
                Crc(headerCrcInput) == (id[5] << 8 | id[6]), Crc(bodyCrcInput) == (body[^2] << 8 | body[^1]), 0xFE, next.Mark, weak));
        }
        return result.OrderBy(s => s.HeaderCell).ToArray();
    }

    internal static PhysicalTrack Encode(DskImage.DskTrack source, int cylinder, int side)
    {
        // DSK contains sector descriptors, not real gaps/timing. Generate valid MFM
        // CRC independently of ST1/ST2; no physical corruption is inferred.
        var bits = new List<bool>(); bool previous = false;
        void Raw(ushort value) { for (int bit = 15; bit >= 0; bit--) bits.Add(((value >> bit) & 1) != 0); previous = (value & 1) != 0; }
        void Byte(byte value)
        {
            for (int bit = 7; bit >= 0; bit--) { bool current = ((value >> bit) & 1) != 0; bits.Add(!previous && !current); bits.Add(current); previous = current; }
        }
        void Fill(byte value, int count) { for (int i = 0; i < count; i++) Byte(value); }
        void Mark(byte value) { Fill(0, 12); Raw(0x4489); Raw(0x4489); Raw(0x4489); Byte(value); }
        void CrcBytes(byte[] data) { ushort crc = Crc(data); Byte((byte)(crc >> 8)); Byte((byte)crc); }
        Fill(0x4E, 80);
        foreach (var sector in source.Sectors)
        {
            if (sector.Data.Length != (128 << sector.SizeCode)) throw new InvalidDataException("Unsupported DSK sector size.");
            Mark(0xFE);
            byte[] header = [0xA1, 0xA1, 0xA1, 0xFE, sector.Cylinder, sector.Side, sector.SectorId, sector.SizeCode];
            foreach (byte b in header.Skip(4)) Byte(b); CrcBytes(header); Fill(0x4E, 22);
            byte dam = (sector.FdcStatus2 & 0x40) != 0 ? (byte)0xF8 : (byte)0xFB;
            Mark(dam); foreach (byte b in sector.Data) Byte(b);
            CrcBytes(new byte[] { 0xA1, 0xA1, 0xA1, dam }.Concat(sector.Data).ToArray()); Fill(0x4E, source.Gap);
        }
        int rate = bits.Count <= 100_000 ? 250_000 : 500_000;
        if (bits.Count > 200_000) throw new InvalidDataException("The DSK track cannot fit the supported generated DD/HD revolution.");
        // Generated revolution: 300 RPM. This is a conversion choice, not source timing.
        Fill(0x4E, (rate * 2 / 5 - bits.Count + 15) / 16);
        var packed = new byte[(bits.Count + 7) / 8];
        for (int i = 0; i < bits.Count; i++) PackedBitCells.Set(packed, i, bits[i]);
        var track = new PhysicalTrack { Cylinder = cylinder, Side = side, Rpm = 300, BitRate = rate, Encoding = PhysicalEncoding.Mfm, PackedBitCells = packed, BitCellCount = bits.Count };
        track.Sectors = Decode(track);
        if (track.Sectors.Count != source.Sectors.Count || track.Sectors.Any(s => !s.HeaderCrcValid || !s.DataCrcValid)) throw new InvalidDataException("Generated physical sectors did not validate.");
        for (int i = 0; i < source.Sectors.Count; i++)
            if (!source.Sectors[i].Data.AsSpan().SequenceEqual(track.Sectors[i].Data)) throw new InvalidDataException("Generated physical payload differs.");
        return track;
    }
}
