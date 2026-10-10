using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal sealed record MediaConversionPreview(byte[] Output, string Report, bool IsLossy);
internal static class MediaConversionService
{
    internal static MediaConversionPreview DskToHfe(DskDocument source, bool v3)
    {
        var physical = HfeImage.FromDsk(source);
        byte[] output = physical.Serialize(v3);
        HfeImage.VerifyPhysical(physical, HfeImage.Parse(output));
        bool status = source.Image.Tracks.SelectMany(t => t!.Sectors).Any(s => s.FdcStatus1 != 0 || (s.FdcStatus2 & ~0x40) != 0);
        return new(output, $"DSK → {(v3 ? "HFEv3" : "HFE")}\nPreserved: C/H/R/N, sector payloads and physical descriptor order, deleted-data marks.\nGenerated: MFM gaps, valid CRC, 250/500 kbit/s and 300 RPM. DSK has no original physical timing.\nDSK ST1/ST2 do not prove physical bad CRC; no CRC corruption is fabricated.\nLost: DSK creator, container padding/trailing bytes and FDC status metadata.\n" + (status ? "WARNING: FDC status metadata is present and will not be encoded as physical errors.\n" : ""), true);
    }

    internal static MediaConversionPreview HfeToDsk(HfeImage source)
    {
        if (source.Cylinders * source.Sides > DskImage.MaximumAbsoluteTracks) throw new InvalidDataException("HFE geometry exceeds Extended DSK limits.");
        var first = source.Tracks[0];
        if (source.Tracks.Any(t => t.Sectors.Count is < 1 or > DskImage.MaximumSectorsPerTrack || t.Sectors.Any(s => s.N > 3 || s.Data.Length != (128 << s.N))))
            throw new InvalidDataException("All tracks must contain fully decoded sectors with supported Extended DSK sizes (128..1024 B).");
        var image = DskImage.CreateUniform(source.Cylinders, source.Sides, first.Sectors.Count, first.Sectors[0].Data.Length, 1, 0x4E, 0xE5, "MZTools HFE");
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            var track = source.Tracks[i];
            // ReplaceTrackGeometry is uniform per track; mixed N cannot safely be fabricated.
            if (track.Sectors.Select(s => s.N).Distinct().Count() != 1) throw new InvalidDataException("Mixed sector sizes within a physical track are not supported by this conversion.");
            // Duplicate IDs are representable in DSK but not by the geometry helper.
            image.ReplaceTrackGeometry(i, track.Sectors.Count, track.Sectors[0].Data.Length, Enumerable.Range(1, track.Sectors.Count).ToArray(), 0x4E, 0xE5);
            for (int p = 0; p < track.Sectors.Count; p++)
            {
                var s = track.Sectors[p]; var d = image.Tracks[i]!.Sectors[p];
                d.Cylinder = s.C; d.Side = s.H; d.SectorId = s.R; d.SizeCode = s.N; d.Data = (byte[])s.Data.Clone();
                d.FdcStatus1 = !s.HeaderCrcValid || !s.DataCrcValid ? (byte)0x20 : (byte)0;
                d.FdcStatus2 = (byte)((!s.DataCrcValid ? 0x20 : 0) | (s.DataMark == 0xF8 ? 0x40 : 0));
            }
        }
        byte[] output = image.Serialize(); var check = DskImage.Parse(output);
        for (int i = 0; i < source.Tracks.Count; i++) for (int p = 0; p < source.Tracks[i].Sectors.Count; p++)
            if (!source.Tracks[i].Sectors[p].Data.AsSpan().SequenceEqual(check.Tracks[i]!.Sectors[p].Data)) throw new InvalidDataException("Converted DSK payload failed validation.");
        return new(output, "HFE → Extended CPC DSK (lossy)\nPreserved: decoded C/H/R/N, sector order, data, deleted marks and CRC-error status semantics.\nLost: raw bitcells, exact gaps, physical offsets, encoding, bitrate, RPM, index positions and timing changes.\nUnsupported: weak bits cannot be preserved; decoded weak data is only one deterministic inspection sample.\nUndecoded regions cannot be represented as DSK sectors.\nExplicit loss confirmation is required.\n" + DskAnalyzer.Analyze(check).Report(), true);
    }

    internal static void WriteVerified(string path, byte[] bytes, Action<byte[]> verify, string? sourcePath = null)
    {
        string destination = Path.GetFullPath(path);
        if (sourcePath != null && destination.Equals(Path.GetFullPath(sourcePath), StringComparison.OrdinalIgnoreCase)) throw new IOException("Select a separate output path.");
        verify(bytes);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".tmp");
        bool owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; stream.Write(bytes); stream.Flush(true); }
            verify(File.ReadAllBytes(temporary));
            File.Move(temporary, destination, true);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
}
