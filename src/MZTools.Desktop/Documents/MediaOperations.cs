namespace MZTools.Desktop.Documents;

public sealed record CompressionChoice(string Mode, string Direction = "Forward", bool Quick = false, bool Embedded = false, int Skip = 0);
public sealed record ExportChoice(string Path, string Format, bool SelectedOnly = true, bool Separate = false,
    bool Sidecar = true, bool PreserveTrailing = true, string Machine = "Mz800", int CpmProfile = 0,
    string NativeMode = "Mz800Monitor", int ComCompression = 0, bool Multipart = false);
public sealed record MediaPreview(string Report, long Revision, IReadOnlyList<string> Files);
internal sealed record RecordPreview(string Report, long Revision, IReadOnlyList<TapeRecord> Records);
internal sealed record ExportPreview(string Report, IReadOnlyDictionary<string, byte[]> Outputs,
    IReadOnlyDictionary<string,string> SourceFiles) : IDisposable
{
    public void Dispose() { foreach(string source in SourceFiles.Values.Distinct()) if(File.Exists(source)) File.Delete(source); }
}

public interface IMediaDialogs
{
    Task<IReadOnlyList<string>> PickImportAsync();
    Task<CompressionChoice?> ChooseCompressionAsync();
    Task<ExportChoice?> ChooseExportAsync(TapeSession session);
    Task<bool> ConfirmPreviewAsync(string title, string report);
}

internal static class MediaOperations
{
    internal static async Task<RecordPreview> ImportAsync(TapeSession session, IEnumerable<string> paths, CancellationToken cancellation)
    {
        long revision = session.Revision; var original = session.Model.Records.Select(r => r.DeepClone()).ToList();
        var imported = await Task.Run(() =>
        {
            var result = new List<TapeRecord>();
            foreach (string path in paths)
            {
                cancellation.ThrowIfCancellationRequested(); var hash = AtomicFileTransaction.Hash(path);
                var extension = System.IO.Path.GetExtension(path).ToLowerInvariant();
                if (extension is ".wav" or ".lep" or ".l16") result.AddRange(SharpTapeImporter.ReadFile(path, cancellation));
                else
                {
                    var document = TapeDocumentIO.Read(path);
                    if (document.IsReadOnlyQuickDisk) throw new InvalidDataException("Non-SHARP/unknown QuickDisk cannot be imported as SHARP records.");
                    result.AddRange(document.Records);
                }
                if (AtomicFileTransaction.Hash(path) != hash) throw new IOException("Import source changed while reading: " + path);
            }
            return result;
        }, cancellation);
        original.AddRange(imported.Select(r => r.DeepClone()));
        return new($"Import {imported.Count} records ({imported.Sum(r => (long)r.Body.DataSize):N0} B) at the end of this document.\nSource files are unchanged. Existing order is retained.\n" +
            string.Join("\n", imported.Select(r => SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname))), revision, original);
    }

    internal static async Task<RecordPreview> CompressAsync(TapeSession session, CompressionChoice choice, CancellationToken cancellation)
    {
        long revision = session.Revision;
        var indices = session.Selection.Count != 0 ? session.Selection.ToArray() : session.SelectedRow is { } row ? new[] { row.Index } : [];
        if (indices.Length == 0) throw new InvalidOperationException("Select at least one record.");
        var records = session.Model.Records.Select(r => r.DeepClone()).ToArray();
        var target = session.IsQuickDisk ? CompressionTarget.QuickDisk : session.Model.Format == TapeDocumentFormat.Mzt ? CompressionTarget.MztTape : CompressionTarget.MzfTape;
        string report = await Task.Run(() =>
        {
            var lines = new List<string>();
            foreach (int index in indices)
            {
                cancellation.ThrowIfCancellationRequested(); int before = records[index].Body.DataSize;
                if (choice.Mode == "None" && MzfLoaderBuilder.TryGetCompressionInfo(records[index], out _))
                {
                    var unpacked = MzfDecompressionService.Decompress(records[index]); records[index] = unpacked.Record;
                    if (!unpacked.HeaderDescriptionFullyRestored) lines.Add("Header description was used by the old loader and cannot be fully restored.");
                }
                else if (choice.Mode is not ("Keep" or "None"))
                {
                    var options = new MzfCompressionOptions(Enum.Parse<MzfCompressionAlgorithm>(choice.Mode),
                        Enum.Parse<CompressionDirection>(choice.Direction), choice.Quick, choice.Embedded, choice.Skip);
                    var compressed = MzfCompressionService.Compress(records[index], options, target, cancellation);
                    records[index] = compressed.Record; lines.Add(CompressionDescription.Describe(compressed.AppliedOptions));
                }
                lines.Add($"#{index + 1}: {before:N0} → {records[index].Body.DataSize:N0} B");
            }
            return string.Join("\n", lines);
        }, cancellation);
        return new(report + "\nApply changes memory only. Save is a separate command.", revision, records);
    }

    internal static async Task<ExportPreview> ExportAsync(TapeSession session, ExportChoice choice, CancellationToken cancellation)
    {
        var records = choice.SelectedOnly ? session.SelectedRecords() : session.Model.Records.Select(r => r.DeepClone()).ToArray();
        if (records.Count == 0) throw new InvalidOperationException("There are no records to export.");
        if (session.Model.IsReadOnlyQuickDisk) throw new InvalidOperationException("Use decoded/raw region export for a non-SHARP/unknown host.");
        string extension = choice.Format switch { "Wav" => ".wav", "Lep" => ".lep", "L16" => ".l16", "Com" => ".com", _ => TapeSavePolicy.Extension(Enum.Parse<TapeDocumentFormat>(choice.Format)) };
        if (!System.IO.Path.GetExtension(choice.Path).Equals(extension, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Export extension does not match the selected format.");
        var paths = choice.Separate ? SharpTapeExporter.GetSeparateOutputPaths(choice.Path, records) : new[] { choice.Path };
        var outputs = new Dictionary<string, byte[]>(AtomicFileTransaction.PathComparer); var reports = new List<string>();
        var sourceFiles = new Dictionary<string,string>(AtomicFileTransaction.PathComparer);
        try
        {
        for (int i = 0; i < paths.Count; i++)
        {
            var subset = choice.Separate ? new[] { records[i] } : records;
            string path = PathIdentity.Normalize(paths[i]); cancellation.ThrowIfCancellationRequested();
            if (choice.Format is "Wav" or "Lep" or "L16")
            {
                string temporary = System.IO.Path.Combine(System.IO.Path.GetTempPath(),"mztools-export-"+Guid.NewGuid().ToString("N")+extension);
                sourceFiles.Add(path,temporary);outputs.Add(path,[]);
                await Task.Run(()=>SharpTapeExporter.Export(temporary,subset,Enum.Parse<SharpTapeOutputFormat>(choice.Format),
                    Enum.Parse<SharpTapeMachine>(choice.Machine),cancellationToken:cancellation),cancellation);
            }
            else if (choice.Format == "Com")
            {
                byte[] bytes = await Task.Run(() =>
                {
                        if (subset.Count != 1) throw new InvalidDataException("COM requires one record or Separate files.");
                        var result = NativeComConversionService.Build(MzfNativeImageParser.Parse(subset[0]),
                            CpmTargetProfile.BuiltIns[choice.CpmProfile], Enum.Parse<NativeMachineMode>(choice.NativeMode),
                            NativeComCompressionChoice.Choices[choice.ComCompression].Options, choice.Multipart, cancellation);
                        reports.Add(result.Report); return result.Bytes;
                }, cancellation);
                outputs.Add(path, bytes);
            }
            else
            {
                var model = new TapeDocument { Format = session.Model.Format, IsModified = true, QuickDiskProfile = session.Model.QuickDiskProfile };
                model.Records.AddRange(subset.Select(r => r.DeepClone()));
                var copy = new TapeSession(model);
                var plan = await ((IFileSetDocument)copy).PrepareSaveAsync(new(path, choice.Format, choice.Sidecar, choice.PreserveTrailing), cancellation);
                foreach (var output in plan.Outputs) outputs.Add(output.Key, output.Value);
                if (plan.LossWarning.Length != 0) reports.Add(plan.LossWarning);
            }
        }
        reports.Insert(0, string.Join("\n", outputs.Select(p => $"{p.Key} · {(sourceFiles.TryGetValue(p.Key,out var source) ? new FileInfo(source).Length : p.Value.LongLength):N0} B")));
        reports.Add("Export creates separate artifacts. The active path, modified state and source are unchanged.");
        return new(string.Join("\n\n", reports), outputs, sourceFiles);
        }
        catch { foreach(string source in sourceFiles.Values) if(File.Exists(source))File.Delete(source);throw; }
    }
}
