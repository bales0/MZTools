using NAudio.SoundFile;
using NAudio.Wave;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace MZTools;

internal static class BatchTapeService
{
    internal static readonly string[] Targets = ["MZF", "M12", "MZT", "WAV", "FLAC", "LEP", "L16", "QD (Sharp)", "QD (HxC)", "QD (FlashFloppy)", "QDF", "MZQ"];
    internal static bool IsTape(string path) => BatchMediaPolicy.TapeExtensions.Split(',').Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    private sealed record TapeInput(IReadOnlyList<TapeRecord> Records, byte[] Trailing, string Report = "", int Errors = 0, QdReadResult? QuickDisk = null)
    {
        internal IReadOnlyList<BatchDetailField> Fields { get; init; } = [];
    }

    internal static void Prepare(BatchRow row, byte[] source, BatchOptions options, CancellationToken cancellationToken = default)
    {
        string extension = Path.GetExtension(row.Input).ToLowerInvariant();
        row.DetectedFormat = extension.TrimStart('.').ToUpperInvariant();
        if (options.ReplaceOriginals && !BatchMediaPolicy.ReadOnly(options.Operation))
            throw new InvalidDataException("Tape, audio and QuickDisk batch outputs use a separate output folder; replacing originals is unavailable for these formats.");
        TapeInput input = Read(row.Input, options.UseHeuristicAnalysis, cancellationToken);
        TrackSidecar(row, extension, input.Records);
        row.DetailFields = input.Fields.Concat(new[] { new BatchDetailField("Trailing container bytes", input.Trailing.Length.ToString("N0")) }).ToArray();
        row.Contents = input.Records.Select((record, index) => new BatchContentItem(index + 1,
            SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname), record.Body.MzfBody.Length,
            record.Header.MzfFtype.ToString("X2"), record.Header.MzfStart, record.Header.MzfExec,
            Profile: extension is ".qd" or ".qdf" or ".mzq" ? "" : TapeProfileNames.ToDisplayName(record.Profile), Compression: MzfLoaderBuilder.DetectCompression(record).ToString(),
            TrailingBytes: record.Body.TrailingData?.Length ?? 0)).ToArray();
        row.Filesystem = input.QuickDisk?.Analysis.Identification.DisplayName ?? "SHARP MZ tape records";
        var report = new StringBuilder(input.Report);
        report.AppendLine($"Container: {row.DetectedFormat}; programs: {input.Records.Count}; trailing container bytes: {input.Trailing.Length}.");
        foreach (var record in input.Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (record.Header.MzfFtype == 0 || record.Body.MzfBody.Length != record.Header.MzfSize || record.Body.DataSize != record.Header.MzfSize) throw new InvalidDataException("Record header/type/body lengths are invalid.");
            string name = SharpMzEncoding.ConvertMzfNameToASCIIString(record.Header.MzfFname);
            report.AppendLine($"{name}: type {record.Header.MzfFtype:X2}, {record.Body.MzfBody.Length} B, LOAD={record.Header.MzfStart:X4}, EXEC={record.Header.MzfExec:X4}, {TapeProfileNames.ToDisplayName(record.Profile)}, {MzfLoaderBuilder.DetectCompression(record)}, trailing {record.Body.TrailingData?.Length ?? 0} B.");
            bool test = options.Operation == BatchOperation.TestIntegrity;
            if (test) row.IntegrityChecks.Add(new(name, "Header / body length", "Passed", $"Declared: {record.Header.MzfSize:N0} B; read: {record.Body.MzfBody.Length:N0} B."));
            if (options.Operation != BatchOperation.Analyze && MzfLoaderBuilder.TryGetCompressionInfo(record, out var info) && info != null)
            {
                var restored = MzfDecompressionService.Decompress(record);
                report.AppendLine($"Recognized compression stream decoded: {restored.Record.Body.MzfBody.Length} B.");
                if (test) row.IntegrityChecks.Add(new(name, "Compressed stream", "Passed", $"Recognized stream decompressed to {restored.Record.Body.MzfBody.Length:N0} B."));
            }
            else if (test) row.IntegrityChecks.Add(new(name, "Compressed stream", "Not applicable", "No recognized MZTools compressed stream; custom compression is not tested."));
            if (test) row.IntegrityChecks.Add(new(name, "Tape / frame checksum", extension is ".mzf" or ".m12" or ".mz0" or ".mz7" or ".mzt" ? "Not available" : "Passed",
                extension is ".mzf" or ".m12" or ".mz0" or ".mz7" or ".mzt" ? "This container has no payload checksum; arbitrary changed bytes cannot be detected." : "Record accepted by the decoder after its tape block / frame checks."));
        }
        if (input.Records.Count == 1) { row.Load = input.Records[0].Header.MzfStart; row.Exec = input.Records[0].Header.MzfExec; }
        if (extension is ".mzf" or ".m12" or ".mz0" or ".mz7" or ".mzt")
            report.AppendLine("MZF/M12/MZT have no payload checksum: header/length and recognized compression checks detect structural errors, not arbitrary data changes or program compatibility.");
        if (extension is ".wav" or ".flac") report.AppendLine("Audio checks validate decoded SHARP tape blocks/checksums; undecodable audio is not reported as a valid program.");
        row.DetailedReport = report.ToString();
        bool unknownQd = input.QuickDisk is { Analysis.Identification.IsNativeSharpMz: false };
        int errors = input.Errors + (unknownQd ? 1 : 0) + (input.Records.Count == 0 && extension is ".wav" or ".flac" or ".lep" or ".l16" ? 1 : 0);
        row.Warnings = unknownQd ? "Non-SHARP/unknown QD: inspection only; program conversion and compression are unavailable." : input.Errors > 0 ? "Some audio blocks could not be recovered; see details." : "";
        row.Catalog = new(Path.GetFileName(row.Input), row.SourceHash, row.DetectedFormat, "", source.Length,
            row.Filesystem, "", "", "", "Not assessed", input.Records.Count, input.Records.Sum(r => (long)r.Body.MzfBody.Length), 0, errors, row.Warnings.Length > 0 ? 1 : 0);
        if (BatchMediaPolicy.ReadOnly(options.Operation))
        {
            row.Result = errors == 0 ? "Will analyze" : options.Operation == BatchOperation.TestIntegrity ? "Error" : "Will analyze";
            bool test = options.Operation == BatchOperation.TestIntegrity;
            if (test)
            {
                row.IntegrityChecks.Add(new("Source", "Complete recovery", unknownQd ? "Not verifiable" : errors == 0 ? "Passed" : "Failed",
                    unknownQd ? "Unknown QD contents cannot be validated as SHARP programs." : $"Complete programs: {input.Records.Count}; recovery errors: {input.Errors}."));
                row.IntegrityChecks.Add(new("Source", "Program execution", "Not tested", "Integrity checks do not run the program or certify game compatibility."));
            }
            row.Integrity = !test ? "Not tested" : unknownQd ? "Not verifiable" : errors == 0 ? "Passed" : "Failed";
            row.Reason = !test ? extension is ".wav" or ".flac" or ".lep" or ".l16" ? "Catalog collected. Audio decoding already checks tape blocks to read program names and data; Test integrity lists those results and additionally tests recognized compressed streams." : "Catalog collected. Test integrity provides individual validation results and decompresses recognized compressed streams; it does not provide additional catalog metadata." : errors == 0 ? "Integrity checks passed. See Integrity checks for individual results and checks unavailable in this format." : "Integrity cannot be confirmed. See Integrity checks for individual results.";
            if (row.Result == "Error") row.Error = row.Reason;
            return;
        }
        if (unknownQd || input.Errors > 0) throw new InvalidDataException("Conversion requires a completely decoded SHARP source. See integrity details; partial audio recovery or unknown QD cannot be converted automatically.");
        if (input.Records.Count == 0) throw new InvalidDataException("There are no complete programs to process.");
        var records = input.Records.Select(r => r.DeepClone()).ToList();
        string target = options.TargetFormat;
        if (options.Operation is BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms)
        {
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                TapeRecord original = records[i];
                bool recognized = MzfLoaderBuilder.TryGetCompressionInfo(original, out var info) && info != null;
                TapeRecord decoded = recognized ? MzfDecompressionService.Decompress(original).Record : original;
                if (options.Operation == BatchOperation.DecompressPrograms)
                {
                    if (!recognized) throw new InvalidDataException("No recognized MZTools ZX0/ZX7 loader in one or more programs; no unknown/custom compression is guessed.");
                    records[i] = decoded;
                }
                else
                {
                    var compression = options.Compression ?? new(MzfCompressionAlgorithm.Auto);
                    if (compression.SkipBytes != 0) throw new InvalidDataException("Batch compression requires the complete program (Skip bytes = 0) so the output can be verified independently.");
                    records[i] = MzfCompressionService.Compress(decoded, compression, CompressionTarget.MzfTape, cancellationToken).Record;
                    TapeRecord check = MzfLoaderBuilder.TryGetCompressionInfo(records[i], out _) ? MzfDecompressionService.Decompress(records[i]).Record : records[i];
                    if (!decoded.Body.MzfBody.AsSpan().SequenceEqual(check.Body.MzfBody) || decoded.Header.MzfStart != check.Header.MzfStart || decoded.Header.MzfExec != check.Header.MzfExec) throw new InvalidDataException("Compression round-trip changed the original program.");
                }
                if (recognized && info!.EmbeddedLoader) row.Warnings += "\nEmbedded ZX7 overwrote the source header description; those original description bytes cannot be restored.";
                row.DetailedReport += $"\n{SharpMzEncoding.ConvertMzfNameToASCIIString(original.Header.MzfFname)}: stored {original.Body.MzfBody.Length} B → {records[i].Body.MzfBody.Length} B; decoded program {decoded.Body.MzfBody.Length} B; output {MzfLoaderBuilder.DetectCompression(records[i])}.\n";
            }
            target = extension switch
            {
                ".m12" => "M12", ".mzf" or ".mz0" or ".mz7" => "MZF", ".qd" => input.QuickDisk!.Format switch { QdImageFormat.HxcPhysical => "QD (HxC)", QdImageFormat.FlashFloppyPhysical => "QD (FlashFloppy)", _ => "QD (Sharp)" },
                ".qdf" => "QDF", ".mzq" => "MZQ", _ => "MZT"
            };
            row.DetailedReport += $"\n{options.Operation}: output {target}; {CompressionOptionsControl.Describe(options.Compression ?? new(MzfCompressionAlgorithm.None))}.\n";
        }
        else if (options.Operation is not (BatchOperation.ConvertFormat or BatchOperation.ExtractAll)) throw new InvalidDataException("This operation is not available for tape/audio/QuickDisk inputs.");

        if (options.Operation == BatchOperation.ExtractAll || (target is "MZF" or "M12" && records.Count > 1))
        {
            string extractTarget = options.Operation == BatchOperation.ExtractAll ? options.ExtractFormat == "Native" ? "MZF" : options.ExtractFormat : target;
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < records.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string name = SharpMzEncoding.ConvertMzfNameToASCIIString(records[i].Header.MzfFname);
                string safe = new(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
                string filename = $"{i + 1:D3}_{safe.Trim().TrimEnd('.')}";
                ExtractRecord(files, filename, records[i], options with { ExtractFormat = extractTarget }, cancellationToken);
            }
            row.ExtractedFiles = files;
            if (input.Trailing.Length > 0) files.Add("container-trailing.bin", input.Trailing);
            row.DetailedReport += $"\nPrograms are exported to a per-input directory as {extractTarget}, with numbered names and applicable sidecars.\n";
        }
        else
        {
            if (!Targets.Contains(target, StringComparer.Ordinal)) throw new InvalidDataException("Tape/audio/QuickDisk input requires a tape, audio or QuickDisk conversion target.");
            if (target != "MZT" && input.Trailing.Length > 0) throw new InvalidDataException("This output cannot retain MZT container trailing data. Use MZT or Extract all instead.");
            bool qdOutput = target is "QDF" or "MZQ" || target.StartsWith("QD (", StringComparison.Ordinal);
            if (qdOutput)
            {
                if (records.Any(r => MzfLoaderBuilder.TryGetCompressionInfo(r, out var info) && info!.EmbeddedLoader || r.Header.MzfExec is >= 0x1108 and < 0x1170))
                    throw new InvalidDataException("QD stores only 38 description bytes and cannot retain this program's executable MZF header/embedded loader. Use MZF/M12/MZT or audio instead.");
                if (records.Any(r => r.GetSerializedHeader().AsSpan(62).ContainsAnyExcept((byte)0)))
                {
                    row.Warnings += "\nQD retains only the first 38 MZF description bytes; the remaining description bytes are lost.";
                    row.RequiresLossConfirmation = true;
                }
                if (records.Any(r => r.Profile != TapeProfile.Normal1_1))
                {
                    row.Warnings += "\nQD containers store program headers and payloads, but do not retain tape speed/loader metadata.";
                    row.RequiresLossConfirmation = true;
                }
            }
            row.OutputExtension = Extension(target);
            row.ImageBytes = Encode(records, target, options.WavSampleRate, input.Trailing, input.QuickDisk?.PhysicalProfile, cancellationToken);
            if (target is "MZF" or "M12" or "MZT") row.SidecarBytes = target == "MZT" ? SidecarService.SerializeMti(records) : SidecarService.SerializeMfi(records[0]);
            // The closure owns cloned records and reopens a temporary copy, including
            // checksum validation for encoded waveform and QD output.
            row.VerifyOutput = (bytes, token) => Verify(bytes, target, records, token);
            if (target is not ("MZF" or "M12" or "MZT") && (input.Trailing.Length > 0 || records.Any(r => r.Body.TrailingData?.Length > 0)))
                throw new InvalidDataException("This output cannot preserve trailing source data. Use MZF/M12/MZT or Extract all instead.");
            if (target is "MZT" && records.Any(r => r.Body.TrailingData?.Length > 0)) throw new InvalidDataException("MZT cannot represent per-record trailing data. Use MZF/M12 instead.");
        }
        if (!File.ReadAllBytes(row.Input).AsSpan().SequenceEqual(source)) throw new IOException("Source changed while preparing the batch preview.");
    }

    internal static void ExtractRecord(Dictionary<string, byte[]> files, string stem, TapeRecord record, BatchOptions options, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = options.ExtractFormat == "Native" ? "MZF" : options.ExtractFormat;
        string filename = stem + Extension(target);
        if (target == "BIN")
        {
            if (!files.TryAdd(filename, record.Body.MzfBody.ToArray())) throw new InvalidDataException("Extracted filenames collide.");
            if (record.Body.TrailingData?.Length > 0) files.Add(filename + ".trailing", record.Body.TrailingData.ToArray());
            return;
        }
        if (!Targets.Contains(target)) throw new InvalidDataException("Unsupported extraction format: " + target);
        if (target is not ("MZF" or "M12") && record.Body.TrailingData?.Length > 0) throw new InvalidDataException("The extraction format cannot preserve trailing record bytes. Choose MZF/M12 or BIN.");
        if ((target is "MZQ" or "QDF" || target.StartsWith("QD (", StringComparison.Ordinal)) && (record.Profile != TapeProfile.Normal1_1 || record.GetSerializedHeader().AsSpan(62).ContainsAnyExcept((byte)0))) throw new InvalidDataException("QD extraction would discard tape/header metadata. Choose MZF/M12/MZT or audio.");
        byte[] bytes = Encode([record], target, options.WavSampleRate, [], null, cancellationToken);
        Verify(bytes, target, [record], cancellationToken);
        if (!files.TryAdd(filename, bytes)) throw new InvalidDataException("Extracted filenames collide.");
        if (target is "MZF" or "M12" or "MZT") files.Add(SidecarService.GetSidecarPath(filename), target == "MZT" ? SidecarService.SerializeMti([record]) : SidecarService.SerializeMfi(record));
    }

    private static void TrackSidecar(BatchRow row, string extension, IReadOnlyList<TapeRecord> records)
    {
        if (extension is not (".mzf" or ".m12" or ".mz0" or ".mz7" or ".mzt")) return;
        string sidecar = SidecarService.GetSidecarPath(row.Input);
        byte[]? bytes = File.Exists(sidecar) ? File.ReadAllBytes(sidecar) : null;
        row.Dependencies[sidecar] = bytes == null ? null : Convert.ToHexString(SHA256.HashData(bytes));
        if (bytes == null) return;
        string[] lines = Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF').Split('\n');
        if (extension == ".mzt")
        {
            var sections = new Dictionary<int, List<string>>();
            List<string>? current = null;
            foreach (string text in lines)
            {
                string line = text.Trim();
                if (line.StartsWith("RECORD=", StringComparison.OrdinalIgnoreCase))
                {
                    if (!int.TryParse(line[7..], out int index) || index < 1 || index > records.Count || sections.ContainsKey(index)) throw new InvalidDataException("Invalid/duplicate MTI record section: " + sidecar);
                    current = []; sections.Add(index, current);
                }
                else if (line.Length > 0 && !line.StartsWith('#'))
                {
                    if (current == null) throw new InvalidDataException("MTI metadata requires RECORD sections: " + sidecar);
                    current.Add(line);
                }
            }
            if (sections.Count == 0) throw new InvalidDataException("MTI contains no record metadata: " + sidecar);
            foreach (var section in sections)
            {
                if (!SidecarService.TryParseProfile(section.Value, out TapeProfile profile)) throw new InvalidDataException("Invalid MTI tape profile: " + sidecar);
                records[section.Key - 1].Profile = profile; records[section.Key - 1].MetadataOrigin = MetadataOrigin.LoadedFromMti;
            }
        }
        else
        {
            if (!SidecarService.TryParseProfile(lines, out TapeProfile profile)) throw new InvalidDataException("Invalid tape metadata sidecar: " + sidecar);
            records[0].Profile = profile; records[0].MetadataOrigin = SidecarService.SingleRecordOrigin(row.Input);
        }
    }

    private static TapeInput Read(string path, bool useHeuristic = false, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".mzf" or ".m12" or ".mz0" or ".mz7") return new([new MZTFileReader().ReadStandaloneMzf(path)], []);
        if (ext == ".mzt") { var input = new MZTFileReader().ReadMzt(path); return new(input.Records, input.ContainerTrailingData); }
        if (ext == ".qd") { var input = QdImageReaderWriter.ReadFile(path); return new(input.Records, [], string.Join("\n", input.Analysis.Identification.Evidence.Concat(input.Analysis.Identification.Warnings)), QuickDisk: input); }
        if (ext is ".qdf" or ".mzq") return new((ext == ".qdf" ? new QDFFileReader().ReadFile(path) : new MZQFileReader().ReadFile(path)).Select(b => TapeRecord.FromLegacy(b.Item1, b.Item2)).ToArray(), []);
        if (ext is ".wav" or ".flac")
        {
            if (useHeuristic)
            {
                var audio = WavHeuristicAnalyzer.AnalyzeFile(path, cancellationToken: cancellationToken);
                return new(audio.Records, [], $"Decoder: heuristic analysis. Audio: {audio.Statistics.SourceFormat}, {audio.Statistics.Format.SampleRate} Hz; recovered {audio.Records.Count}; failures {audio.Failures.Count}.\n" + string.Join("\n", audio.Failures), Math.Max(audio.Failures.Count, audio.Records.Count == 0 ? 1 : 0))
                { Fields = [new("Decoder", "Heuristic analysis"), new("Audio format", audio.Statistics.SourceFormat.ToString()), new("Sample rate", $"{audio.Statistics.Format.SampleRate:N0} Hz"), new("Recovered programs", audio.Records.Count.ToString()), new("Decode failures", audio.Failures.Count.ToString())] };
            }
            var standard = SharpTapeImporter.ReadFile(path, cancellationToken);
            return new(standard, [], $"Decoder: standard. Recovered {standard.Count} checksum-valid program(s); heuristic analysis disabled.", standard.Count == 0 ? 1 : 0)
            { Fields = [new("Decoder", "Standard"), new("Recovered programs", standard.Count.ToString())] };
        }
        return new(SharpTapeImporter.ReadFile(path, cancellationToken), []);
    }

    private static string Extension(string target) => target.StartsWith("QD (", StringComparison.Ordinal) ? ".qd" : "." + target.ToLowerInvariant();
    private static byte[] Encode(IReadOnlyList<TapeRecord> records, string target, int sampleRate, byte[] trailing, QuickDiskPhysicalProfile? profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (target is "MZF" or "M12") return TapeDocumentWriter.SerializeMzf(records.Single(), true);
        if (target == "MZT") return TapeDocumentWriter.SerializeMzt(records).Concat(trailing).ToArray();
        if (target == "QDF") return QDFFileReader.BuildImage(records);
        if (target.StartsWith("QD (", StringComparison.Ordinal)) return QdImageReaderWriter.Write(records, target switch { "QD (HxC)" => QdImageFormat.HxcPhysical, "QD (FlashFloppy)" => QdImageFormat.FlashFloppyPhysical, _ => QdImageFormat.SharpLegacyLogical }, profile);
        using var temporary = new TemporaryMedia();
        string path = temporary.PathFor(Extension(target));
        if (target == "MZQ")
        {
            using var stream = new FileStream(path, FileMode.CreateNew);
            var writer = new MZQFileReader(); writer.WriteMZQHeaderToFile(stream, checked((byte)(records.Count * 2)));
            foreach (var record in records) { cancellationToken.ThrowIfCancellationRequested(); writer.WriteMZQFileHeaderToFile(stream, record.Header); writer.WriteMZQFileBodyToFile(stream, record.Body); }
        }
        else if (target == "FLAC")
        {
            string wav = temporary.PathFor(".wav");
            SharpTapeExporter.Export(wav, records, SharpTapeOutputFormat.Wav, wavSampleRate: sampleRate, cancellationToken: cancellationToken);
            using var source = new SoundFileReader(wav); SoundFileWriter.CreateSoundFile(path, new CancellableAudio(source, cancellationToken));
        }
        else SharpTapeExporter.Export(path, records, SharpTapeExporter.GetFormat(Extension(target)), wavSampleRate: sampleRate, cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return File.ReadAllBytes(path);
    }

    private static void Verify(byte[] bytes, string target, IReadOnlyList<TapeRecord> expected, CancellationToken cancellationToken)
    {
        using var temporary = new TemporaryMedia();
        string path = temporary.PathFor(Extension(target)); File.WriteAllBytes(path, bytes);
        TapeInput reopened = target is "WAV" or "FLAC" ? new(SharpTapeImporter.ReadFile(path, cancellationToken), []) : Read(path, cancellationToken: cancellationToken);
        if (reopened.Errors > 0 || reopened.Records.Count != expected.Count) throw new InvalidDataException("Reopened output program count/integrity differs.");
        for (int i = 0; i < expected.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var actual = reopened.Records[i]; var original = expected[i];
            if (!original.Body.MzfBody.AsSpan().SequenceEqual(actual.Body.MzfBody) || original.Header.MzfFtype != actual.Header.MzfFtype || original.Header.MzfStart != actual.Header.MzfStart || original.Header.MzfExec != actual.Header.MzfExec || !original.Header.MzfFname.AsSpan().SequenceEqual(actual.Header.MzfFname))
                throw new InvalidDataException("Reopened output changed a program header or payload.");
            if (target is not ("QDF" or "MZQ") && !target.StartsWith("QD (", StringComparison.Ordinal) && !original.GetSerializedHeader().AsSpan().SequenceEqual(actual.GetSerializedHeader()))
                throw new InvalidDataException("Reopened output changed the original MZF header bytes.");
            if (target is "MZF" or "M12" && !(original.Body.TrailingData ?? []).AsSpan().SequenceEqual(actual.Body.TrailingData ?? [])) throw new InvalidDataException("Reopened output changed trailing program data.");
        }
    }

    private sealed class CancellableAudio(IWaveProvider source, CancellationToken cancellationToken) : IWaveProvider
    {
        public WaveFormat WaveFormat => source.WaveFormat;
        public int Read(Span<byte> buffer) { cancellationToken.ThrowIfCancellationRequested(); return source.Read(buffer); }
    }

    private sealed class TemporaryMedia : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "MZToolsBatch-" + Guid.NewGuid().ToString("N"));
        internal TemporaryMedia() => Directory.CreateDirectory(directory);
        internal string PathFor(string extension) => Path.Combine(directory, "media" + extension);
        public void Dispose()
        {
            string root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            string resolved = Path.GetFullPath(directory);
            if (!string.Equals(Path.GetDirectoryName(resolved), root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(resolved).StartsWith("MZToolsBatch-", StringComparison.Ordinal)) throw new IOException("Temporary media path escaped its directory.");
            Directory.Delete(resolved, true);
        }
    }
}
