using System;
using System.IO;
using System.Linq;
using System.Threading;

namespace MZTools;

internal static class TapeDocumentIO
{
    internal static TapeDocument Read(string path)
    {
        var document = new TapeDocument { FilePath = path };
        string extension = Path.GetExtension(path).ToLowerInvariant();
        switch (extension)
        {
            case ".mzf": case ".m12": case ".mz0": case ".mz7":
                document.Format = extension == ".m12" ? TapeDocumentFormat.M12 : TapeDocumentFormat.Mzf;
                document.Records.Add(new MZTFileReader().ReadStandaloneMzf(path));
                document.SidecarPath = SidecarService.LoadForMzf(path, document.Records[0]); break;
            case ".mzt":
                document.Format = TapeDocumentFormat.Mzt;
                if (new FileInfo(path).Length != 0)
                {
                    var result = new MZTFileReader().ReadMzt(path);
                    document.Records.AddRange(result.Records); document.ContainerTrailingData = result.ContainerTrailingData;
                }
                document.SidecarPath = SidecarService.LoadForMzt(path, document.Records); break;
            case ".mzq": case ".qdf":
                document.Format = extension == ".mzq" ? TapeDocumentFormat.Mzq : TapeDocumentFormat.Qdf;
                var records = extension == ".mzq" ? new MZQFileReader().ReadFile(path) : new QDFFileReader().ReadFile(path);
                document.Records.AddRange(records.Select(r => TapeRecord.FromLegacy(r.Item1, r.Item2)));
                document.QuickDiskSourceImage = File.ReadAllBytes(path); break;
            case ".qd":
                var bytes = File.ReadAllBytes(path); var qd = QdImageReaderWriter.Read(bytes);
                document.Format = qd.DocumentFormat; document.QuickDiskProfile = qd.PhysicalProfile;
                document.QuickDiskReadResult = qd; document.QuickDiskSourceImage = bytes;
                document.Records.AddRange(qd.Records); break;
            default: throw new NotSupportedException("Open supports MZF, M12, MZT, MZQ, QDF and QD. Use Import for WAV/LEP/L16. Disk/audio editors are in subsequent phases.");
        }
        return document;
    }

    internal static byte[] Serialize(TapeDocument document, TapeDocumentFormat format, bool preserveTrailing)
    {
        if (!TapeSavePolicy.Validate(document, format, TapeSavePolicy.Extension(format), out string error)) throw new InvalidDataException(error);
        if (format is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12)
        {
            if (document.Records.Count != 1) throw new InvalidDataException("MZF/M12 requires exactly one record; use MZT or Export separate files.");
            return TapeDocumentWriter.SerializeMzf(document.Records[0], preserveTrailing);
        }
        if (format == TapeDocumentFormat.Mzt) return TapeDocumentWriter.SerializeMzt(document.Records);
        bool imported = TapeSavePolicy.CanPreserveImported(document, format);
        if (format == TapeDocumentFormat.Qdf) return QDFFileReader.BuildImage(document.Records, imported);
        if (format == TapeDocumentFormat.Mzq)
        {
            using var stream = new MemoryStream(); var writer = new MZQFileReader();
            writer.WriteMZQHeaderToFile(stream, checked((byte)(document.Records.Count * 2)), imported);
            foreach (var record in document.Records) { writer.WriteMZQFileHeaderToFile(stream, record.Header); writer.WriteMZQFileBodyToFile(stream, record.Body); }
            return stream.ToArray();
        }
        return QdImageReaderWriter.Write(document.Records, format switch
        {
            TapeDocumentFormat.QdSharpLegacy => QdImageFormat.SharpLegacyLogical,
            TapeDocumentFormat.QdHxc => QdImageFormat.HxcPhysical,
            TapeDocumentFormat.QdFlashFloppy => QdImageFormat.FlashFloppyPhysical,
            _ => throw new NotSupportedException("Unsupported output format.")
        }, format == document.Format ? document.QuickDiskProfile : null, imported);
    }
}
