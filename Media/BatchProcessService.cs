using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace MZTools;

internal enum BatchOperation { Analyze, ConvertFormat, InstallBootSystem, ApplyPatch, ExtractAll, Defragment, SafeRepair, AnalyzeMzfCompatibility, TestIntegrity, CompressPrograms, DecompressPrograms }
internal sealed record BatchOptions(BatchOperation Operation, string OutputDirectory, string TargetFormat = "HFEv3",
    string NamingTemplate = "{name}_processed{ext}", string? AuxiliaryPath = null, bool StrictPreflight = false,
    bool ReplaceOriginals = false, bool BackupOriginals = true, string? InputRoot = null,
    MzfCompressionOptions? Compression = null, int WavSampleRate = SharpTapeExporter.WavSampleRate,
    bool UseHeuristicAnalysis = false, string ExtractFormat = "Native");
internal sealed record BatchProgress(int Completed, int Total, string Input, string Stage);
internal sealed record BatchDetailField(string Property, string Value);
internal sealed record BatchIntegrityCheck(string Subject, string Check, string Result, string Details);
internal sealed record BatchContentItem(int Number, string Name, long Size, string Type,
    ushort? Load, ushort? Exec, int? User = null, string Profile = "", string Compression = "", long TrailingBytes = 0, string Attributes = "")
{
    internal static BatchContentItem FromDisk(DskFileEntry file, int number) => new(number,
        file.Name + (file.Extension.Length == 0 ? "" : "." + file.Extension), file.Size,
        file.FileType.ToString("X2"), file.LoadAddress, file.ExecuteAddress, file.User,
        Attributes: string.Join(", ", new[] { file.Locked ? "Locked" : "", file.ReadOnly ? "Read-only" : "", file.System ? "System" : "", file.Archived ? "Archived" : "" }.Where(s => s.Length > 0)));
}
internal sealed record BatchCatalog(string Filename, string Sha256, string Container, string Geometry, long Capacity,
    string Filesystem, string Variant, string BootType, string BootProfile, string BootableState, int FileCount,
    long UsedBytes, long FreeBytes, int AnalyzerErrors, int AnalyzerWarnings, int TrackCount = 0, int Sides = 0,
    string BitrateRpm = "", bool WeakBits = false, int CrcErrorSectors = 0, int AnalyzerUnsafe = 0, int SectorCount = 0);

internal sealed class BatchRow
{
    public string Input { get; init; } = "";
    public string Output { get; internal set; } = "";
    public string Operation { get; init; } = "";
    public string DetectedFormat { get; internal set; } = "";
    public string Filesystem { get; internal set; } = "";
    public string Result { get; internal set; } = "";
    public string Reason { get; internal set; } = "";
    public string Warnings { get; internal set; } = "";
    public string Error { get; internal set; } = "";
    public string OldFingerprint { get; internal set; } = "";
    public string NewFingerprint { get; internal set; } = "";
    public string OldBootSystemFingerprint { get; internal set; } = "";
    public string NewBootSystemFingerprint { get; internal set; } = "";
    public string NewBootProfile { get; internal set; } = "";
    public BatchCatalog? Catalog { get; internal set; }
    public DskIssueSeverity Severity => Catalog?.AnalyzerUnsafe > 0 ? DskIssueSeverity.Unsafe :
        Error.Length > 0 || Result == "Error" || Catalog?.AnalyzerErrors > 0 ? DskIssueSeverity.Error :
        Warnings.Length > 0 ? DskIssueSeverity.Warning : DskIssueSeverity.Info;
    public string Validation => Result == "Completed" ? "Output validated and saved" : Result is "Will modify" or "Already matching" ? "Preflight validated" : Reason;
    public string Geometry => Catalog?.Geometry ?? "";
    public ushort? Load { get; internal set; }
    public ushort? Exec { get; internal set; }
    public string ConversionMode { get; internal set; } = "";
    public string Profile { get; internal set; } = "";
    public string Evidence { get; internal set; } = "";
    public string Integrity { get; internal set; } = "Not tested";
    public IReadOnlyList<BatchContentItem> Contents { get; internal set; } = [];
    public IReadOnlyList<BatchDetailField> DetailFields { get; internal set; } = [];
    public List<BatchIntegrityCheck> IntegrityChecks { get; } = [];
    internal string SourceHash { get; set; } = "";
    internal byte[]? ImageBytes { get; set; }
    internal IReadOnlyDictionary<string, byte[]>? ExtractedFiles { get; set; }
    internal bool RequiresLossConfirmation { get; set; }
    internal string? OutputExtension { get; set; }
    internal Action<byte[], CancellationToken>? VerifyOutput { get; set; }
    internal byte[]? SidecarBytes { get; set; }
    internal Dictionary<string, string?> Dependencies { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string DetailedReport { get; internal set; } = "";
}
internal sealed class BatchPreview
{
    internal BatchPreview(BatchOptions options, IReadOnlyList<BatchRow> rows) { Options = options; Rows = rows; }
    internal BatchOptions Options { get; }
    internal IReadOnlyList<BatchRow> Rows { get; }
    internal bool Cancelled { get; set; }
    internal bool BlocksStrict => Rows.Any(r => r.Result is "Error" or "Skipped incompatible");
    internal string Summary => $"Files scanned: {Rows.Count}; will analyze: {Rows.Count(r => r.Result == "Will analyze")}; will modify/output: {Rows.Count(r => r.Result == "Will modify")}; already matching: {Rows.Count(r => r.Result == "Already matching")}; skipped incompatible: {Rows.Count(r => r.Result == "Skipped incompatible")}; cancelled: {Rows.Count(r => r.Result == "Cancelled")}; warnings: {Rows.Count(r => r.Warnings.Length > 0)}; errors: {Rows.Count(r => r.Result == "Error")}.";
}

internal static class BatchProcessService
{
    internal static string[] Scan(string folder, bool recursive, string extensions, CancellationToken cancellationToken = default)
    {
        var filter = extensions.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => e.StartsWith('.') ? e : "." + e.TrimStart('*')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (filter.Count == 0) throw new ArgumentException("Specify at least one extension.");
        return Directory.EnumerateFiles(folder, "*", new EnumerationOptions { RecurseSubdirectories = recursive, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false })
            .Where(p => { cancellationToken.ThrowIfCancellationRequested(); return filter.Contains(Path.GetExtension(p)); }).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static BatchPreview Preview(IEnumerable<string> inputs, BatchOptions options, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        string[] paths = inputs.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var rows = new List<BatchRow>();
        byte[]? auxiliary = null; Exception? auxiliaryError = null;
        if (options.Operation is BatchOperation.InstallBootSystem or BatchOperation.ApplyPatch)
            try
            {
                auxiliary = File.ReadAllBytes(options.AuxiliaryPath ?? throw new ArgumentException("Select a boot/system source or patch."));
                if (options.Operation == BatchOperation.InstallBootSystem && Path.GetExtension(options.AuxiliaryPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
                {
                    var profile = JsonSerializer.Deserialize<BootSystemUserProfile>(auxiliary) ?? throw new InvalidDataException("Invalid boot profile.");
                    auxiliary = UserProfileService.ResolveBootSource(profile).Serialize();
                }
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { auxiliaryError = e; }
        foreach (string path in paths)
        {
            var row = new BatchRow { Input = path, Operation = options.Operation.ToString() }; rows.Add(row);
            progress?.Report(new(rows.Count - 1, paths.Length, path, "Preparing"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] source = File.ReadAllBytes(path); row.SourceHash = Hash(source); row.OldFingerprint = row.SourceHash;
                if (!BatchMediaPolicy.Supports(options.Operation, path, options.TargetFormat))
                    throw new InvalidDataException($"{options.Operation} ({options.TargetFormat}) does not support {Path.GetExtension(path)} inputs. Supported extensions: {BatchMediaPolicy.Extensions(options.Operation, options.TargetFormat)}.");
                if (options.Operation == BatchOperation.AnalyzeMzfCompatibility)
                {
                    var record = new MZTFileReader().ReadStandaloneMzf(path);
                    var analysis = MzfCpmCompatibilityAnalyzer.Analyze(record);
                    row.DetectedFormat = Path.GetExtension(path).Equals(".m12", StringComparison.OrdinalIgnoreCase) ? "M12" : "MZF";
                    row.Load = analysis.Load; row.Exec = analysis.Exec; row.ConversionMode = analysis.ConversionMode;
                    row.Profile = analysis.Profile; row.Evidence = analysis.Evidence.ToString(); row.DetailedReport = analysis.Report;
                    row.Warnings = string.Join("\n", analysis.Warnings); row.Result = "Will analyze";
                    row.Reason = "Read-only compatibility analysis; conversion not proven safe, no COM output.";
                    continue;
                }
                if (BatchTapeService.IsTape(path))
                {
                    BatchTapeService.Prepare(row, source, options, cancellationToken);
                    if (row.Result is "Will analyze" or "Error") continue;
                }
                else
                {
                row.DetectedFormat = MediaFormats.All.FirstOrDefault(format => format.Identify(source).Matches)?.DisplayName ?? "Unknown";
                byte[] working = source;
                if (options.Operation == BatchOperation.SafeRepair)
                {
                    var container = DskContainerRepairService.Inspect(source).FirstOrDefault(p => p.Risk == DskRepairRisk.SafeDeterministic);
                    if (container?.Preview != null) { working = container.Preview.Result; row.ImageBytes = working; row.DetailedReport = container.Preview.Report; }
                }
                HfeImage? physical = new HfeImage().Identify(working).Matches ? HfeImage.Parse(working) : null;
                DskDocument? document = physical == null ? DskDocument.Open(working, path) : null;
                row.DetectedFormat = physical == null ? "Extended CPC DSK" : physical.IsV3 ? "HFEv3" : "HFE";
                if (document != null)
                {
                    var analysis = DskAnalyzer.Analyze(document); var boot = DskBootInfo.Inspect(document);
                    row.Filesystem = document.FileSystem.DisplayName;
                    row.Contents = document.FileSystem.ReadDirectory().Select((file, index) => BatchContentItem.FromDisk(file, index + 1)).ToArray();
                    row.Catalog = new(Path.GetFileName(path), row.SourceHash, row.DetectedFormat, $"{document.Image.TrackCount} × {document.Image.SideCount}", document.Image.TotalSectorDataBytes,
                        document.FileSystem.Type.ToString(), row.Filesystem, boot.System, CpmSystemProfileRegistry.TryResolveVerifiedSource(document)?.DisplayName ?? "", boot.Bootable,
                        document.FileSystem.ReadDirectory().Count, document.FileSystem.UsedBytes, document.FileSystem.FreeBytes, analysis.Errors, analysis.Warnings, AnalyzerUnsafe: analysis.Unsafe, SectorCount: analysis.Sectors.Count());
                    row.Warnings = string.Join("\n", analysis.Issues.Where(i => i.Severity != DskIssueSeverity.Info).Select(i => i.Code + ": " + i.Description));
                    row.DetailedReport = analysis.Report();
                }
                else
                {
                    var semantic = HfeSemanticInspectionService.Inspect(physical!);
                    row.Contents = semantic.Contents;
                    row.Filesystem = semantic.Available ? semantic.Layout + " (read-only projection)" : "Physical tracks (filesystem not attached)";
                    row.DetailedReport = semantic.Report;
                    row.Catalog = new(Path.GetFileName(path), row.SourceHash, row.DetectedFormat, $"{physical!.Cylinders} × {physical.Sides}", physical.Tracks.Sum(t => t.Sectors.Sum(s => (long)s.Data.Length)), semantic.Filesystem, semantic.Layout, semantic.BootType, semantic.BootProfile, semantic.BootableState, semantic.FileCount, semantic.UsedBytes, semantic.FreeBytes, 0, 0,
                        physical.Cylinders, physical.Sides, string.Join("; ", physical.Tracks.SelectMany(t => t.Timing.Select(p => $"{p.BitRate:0.###} bit/s, {t.Rpm?.ToString() ?? "unknown"} RPM").Append($"{t.BitRate?.ToString() ?? "unknown"} bit/s, {t.Rpm?.ToString() ?? "unknown"} RPM")).Distinct()), physical.Tracks.Any(t => t.WeakBitMask.Any(b => b != 0)), physical.Tracks.Sum(t => t.Sectors.Count(s => !s.HeaderCrcValid || !s.DataCrcValid)), SectorCount: physical.Tracks.Sum(t => t.Sectors.Count));
                }
                if (options.Operation is BatchOperation.Analyze or BatchOperation.TestIntegrity)
                {
                    bool valid = row.Catalog.AnalyzerErrors == 0 && row.Catalog.AnalyzerUnsafe == 0 && row.Catalog.CrcErrorSectors == 0;
                    bool test = options.Operation == BatchOperation.TestIntegrity;
                    if (test)
                    {
                        row.IntegrityChecks.Add(new("Disk", "Container / filesystem", row.Catalog.AnalyzerErrors == 0 && row.Catalog.AnalyzerUnsafe == 0 ? "Passed" : "Failed",
                            $"Errors: {row.Catalog.AnalyzerErrors}; unsafe findings: {row.Catalog.AnalyzerUnsafe}; warnings: {row.Catalog.AnalyzerWarnings}."));
                        row.IntegrityChecks.Add(new("Disk", "Sector CRC", physical == null ? "Not available" : row.Catalog.CrcErrorSectors == 0 ? "Passed" : "Failed",
                            physical == null ? "DSK stores sector status, but has no original sector CRC bytes to recalculate." : $"Decoded sectors: {row.Catalog.SectorCount}; CRC errors: {row.Catalog.CrcErrorSectors}."));
                    }
                    row.Integrity = test ? valid ? "Passed" : "Failed" : "Not tested";
                    row.Result = !test || valid ? "Will analyze" : "Error";
                    row.Reason = !test ? "Catalog collected. This lists contents and inspection findings. Test integrity provides separate check results and format limits." : valid ? "Integrity checks passed. See Integrity checks for tested items and format limits." : "Integrity checks report errors. See Integrity checks for results.";
                    continue;
                }
                if (auxiliaryError != null) throw new ArgumentException(auxiliaryError.Message);
                switch (options.Operation)
                {
                    case BatchOperation.ConvertFormat:
                        if (document != null && options.TargetFormat == "DSK") { row.Result = "Already matching"; row.Reason = "Source is already Extended CPC DSK."; continue; }
                        if (options.TargetFormat is "HFEv3" or "HFE")
                        {
                            var conversion = physical == null ? MediaConversionService.DskToHfe(document!, options.TargetFormat == "HFEv3") :
                                new MediaConversionPreview(physical.Serialize(options.TargetFormat == "HFEv3"), "Physical bitcells, weak bits and timing verified.", false);
                            row.ImageBytes = conversion.Output; row.RequiresLossConfirmation = conversion.IsLossy; row.DetailedReport = conversion.Report;
                        }
                        else if (physical != null && options.TargetFormat == "DSK")
                        {
                            var conversion = MediaConversionService.HfeToDsk(physical); row.ImageBytes = conversion.Output; row.RequiresLossConfirmation = conversion.IsLossy; row.DetailedReport = conversion.Report;
                        }
                        else if (document != null && Enum.TryParse<DskConversionTarget>(options.TargetFormat, out var target))
                        {
                            var profile = DskCapabilityService.GetAvailableConversions(document).FirstOrDefault(p => p.Target == target);
                            if (profile == null)
                            {
                                if (DskCapabilityService.CompatibilityKey(document) == DskCapabilityService.CompatibilityKey(DskFileTransferService.CreateTarget(target))) { row.Result = "Already matching"; row.Reason = "Detected layout already matches the requested target."; continue; }
                                throw new InvalidDataException("The conversion profile is not compatible with the detected layout.");
                            }
                            var conversion = DskFileTransferService.Preflight(document, profile); if (!conversion.CanConvert) throw new InvalidDataException(conversion.Report);
                            row.ImageBytes = conversion.OutputImage; row.DetailedReport = conversion.Report; row.RequiresLossConfirmation = conversion.MetadataChanges.Count != 0;
                        }
                        else throw new InvalidDataException("Select a supported target conversion.");
                        break;
                    case BatchOperation.InstallBootSystem:
                        if (document == null) throw new InvalidDataException("Boot/system installation requires a compatible DSK filesystem.");
                        var bootSource = DskDocument.Open(auxiliary!); var bootProfile = CpmSystemProfileRegistry.TryResolveVerifiedSource(bootSource);
                        if (bootProfile == null) throw new InvalidDataException("The boot/system source must match a registered verified profile.");
                        var build = CpmSystemBuilder.Build(document, bootSource, bootProfile); row.ImageBytes = build.ImageBytes; row.DetailedReport = build.Report.ToText();
                        row.OldBootSystemFingerprint = BootSystemFingerprint(document);
                        row.NewBootSystemFingerprint = BootSystemFingerprint(document.Reopen(build.ImageBytes));
                        row.NewBootProfile = bootProfile.DisplayName;
                        break;
                    case BatchOperation.ApplyPatch:
                        if (document == null) throw new InvalidDataException("Sector patch requires a DSK image.");
                        var patch = DskPatchService.FromJson(Encoding.UTF8.GetString(auxiliary!));
                        var patchPreview = DskPatchService.Preview(document, patch); row.ImageBytes = patchPreview.ResultBytes; row.DetailedReport = patchPreview.Report;
                        break;
                    case BatchOperation.ExtractAll:
                        if (document == null || document.IsReadOnly || document.FileSystem is RawDskFileSystem || DskAnalyzer.Analyze(document).Errors != 0) throw new InvalidDataException("Extract All requires a recognized, validated filesystem.");
                        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
                        foreach (var entry in document.FileSystem.ReadDirectory())
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            string name = entry.Name + (entry.Extension.Length == 0 ? "" : "." + entry.Extension);
                            ValidateName(name);
                            string relative = document.FileSystem is CpmFileSystem ? Path.Combine("user" + entry.User, name) : name;
                            byte[] payload = document.FileSystem.Extract(entry);
                            if (options.ExtractFormat == "Native")
                            {
                                if (!files.TryAdd(relative, payload)) throw new InvalidDataException("Extracted filenames collide.");
                            }
                            else if (options.ExtractFormat == "BIN")
                            {
                                if (!files.TryAdd(Path.ChangeExtension(relative, ".bin"), payload)) throw new InvalidDataException("Extracted filenames collide.");
                            }
                            else
                            {
                                if (!DskExportSupport.CanExportMzf(document.FileSystem.Type, entry)) throw new InvalidDataException("This filesystem does not store the MZF type/LOAD/EXEC metadata required for this output. Choose Native or BIN; no tape header is guessed.");
                                using var stream = new MemoryStream(DskMzfConverter.Serialize(entry, payload)); using var reader = new BinaryReader(stream);
                                BatchTapeService.ExtractRecord(files, Path.ChangeExtension(relative, null), new MZTFileReader().ReadMzfRecord(reader), options, cancellationToken);
                            }
                        }
                        row.ExtractedFiles = files; break;
                    case BatchOperation.Defragment:
                        if (document == null) throw new InvalidDataException("Defragmentation requires DSK.");
                        var defrag = DskDefragmentService.Preview(document); row.ImageBytes = defrag.Result; row.DetailedReport = defrag.Report; break;
                    case BatchOperation.SafeRepair:
                        if (document == null) throw new InvalidDataException("Safe repair requires DSK.");
                        if (row.ImageBytes == null) { var repair = DskFilesystemRepairService.For(document).Preview(document); row.ImageBytes = repair.Result; row.DetailedReport = repair.Report; }
                        break;
                    default: throw new InvalidDataException("This operation is not available for disk images.");
                }
                }
                if (row.ImageBytes != null)
                {
                    (row.VerifyOutput ?? VerifyImage)(row.ImageBytes, cancellationToken); row.NewFingerprint = Hash(row.ImageBytes);
                    if (row.OutputExtension == null && source.AsSpan().SequenceEqual(row.ImageBytes)) { row.Result = "Already matching"; row.Reason = "The service output is byte-identical."; continue; }
                }
                if (row.RequiresLossConfirmation) row.Warnings += "\nExplicit confirmation required: " + row.DetailedReport;
                string ext = row.ExtractedFiles != null ? "" : row.OutputExtension ?? (new HfeImage().Identify(row.ImageBytes!).Matches ? ".hfe" : ".dsk");
                string outputName = options.NamingTemplate.Replace("{name}", Path.GetFileNameWithoutExtension(path), StringComparison.Ordinal).Replace("{ext}", ext, StringComparison.Ordinal);
                ValidateName(outputName);
                if (row.OutputExtension != null && row.ExtractedFiles == null && !Path.GetExtension(outputName).Equals(ext, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Naming template must retain {ext} for the selected media format.");
                if (options.ReplaceOriginals && (row.ExtractedFiles != null || ext != Path.GetExtension(path).ToLowerInvariant())) throw new InvalidDataException("Replacing originals requires the same container extension and cannot extract directories.");
                string relativeFolder = "";
                if (options.InputRoot != null)
                {
                    string relative = Path.GetRelativePath(Path.GetFullPath(options.InputRoot), path);
                    if (!Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        relativeFolder = Path.GetDirectoryName(relative) ?? "";
                }
                row.Output = options.ReplaceOriginals ? path : Path.GetFullPath(Path.Combine(options.OutputDirectory, relativeFolder, outputName));
                if (row.SidecarBytes != null && (File.Exists(SidecarService.GetSidecarPath(row.Output)) || Directory.Exists(SidecarService.GetSidecarPath(row.Output)))) throw new IOException("Output sidecar already exists; select a different name or folder.");
                if (!options.ReplaceOriginals && paths.Contains(row.Output, StringComparer.OrdinalIgnoreCase)) throw new InvalidDataException("Output would overwrite a selected source.");
                if (!options.ReplaceOriginals && (File.Exists(row.Output) || Directory.Exists(row.Output))) throw new IOException("Output already exists; select a different naming template or folder.");
                if (options.ReplaceOriginals && options.BackupOriginals && File.Exists(path + ".bak")) throw new IOException("Backup already exists; it will not be overwritten.");
                cancellationToken.ThrowIfCancellationRequested();
                row.Result = "Will modify"; row.Reason = "Per-file service preflight and reopen verification passed.";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { row.Result = "Cancelled"; row.Reason = "Cancelled; no output was written."; row.ImageBytes = null; row.ExtractedFiles = null; }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or OverflowException or UnauthorizedAccessException or JsonException)
            {
                row.Result = e is InvalidDataException && options.Operation is not (BatchOperation.Analyze or BatchOperation.TestIntegrity) ? "Skipped incompatible" : "Error"; row.Error = e.Message; row.Reason = e.Message;
                if (options.Operation == BatchOperation.TestIntegrity)
                {
                    row.Integrity = "Failed";
                    row.IntegrityChecks.Add(new(Path.GetFileName(path), "Read / validation", "Failed", e.Message));
                }
                if (row.Catalog == null && options.Operation == BatchOperation.Analyze)
                    row.Catalog = new(Path.GetFileName(path), row.SourceHash, row.DetectedFormat, "Unknown", 0, "Unknown", "", "Unknown", "", "Unverified", 0, 0, 0, 1, 0);
            }
        }
        foreach (var collision in rows.Where(r => r.Output.Length != 0)
            .SelectMany(r => r.SidecarBytes == null ? new[] { (Row: r, Path: r.Output) } : new[] { (Row: r, Path: r.Output), (Row: r, Path: SidecarService.GetSidecarPath(r.Output)) })
            .GroupBy(r => r.Path, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            foreach (var item in collision) { item.Row.Result = "Error"; item.Row.Error = item.Row.Reason = "Multiple inputs map to the same output or sidecar path."; }
        progress?.Report(new(paths.Length, paths.Length, "", "Preview complete"));
        return new(options, rows) { Cancelled = cancellationToken.IsCancellationRequested };
    }

    internal static void Execute(BatchPreview preview, bool confirmLoss = false, bool confirmReplace = false, IProgress<BatchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (preview.Cancelled) throw new InvalidOperationException("This batch was cancelled. Run Preview again before execution.");
        if (preview.Options.StrictPreflight && preview.BlocksStrict) throw new InvalidOperationException("Strict preflight blocks all execution because at least one input failed.");
        if (preview.Options.ReplaceOriginals && !confirmReplace) throw new InvalidOperationException("Replacing originals requires explicit confirmation.");
        if (!confirmLoss && preview.Rows.Any(r => r.Result == "Will modify" && r.RequiresLossConfirmation)) throw new InvalidOperationException("Lossy operations require explicit confirmation of the preview warnings.");
        var pending = preview.Rows.Where(r => r.Result is "Will modify" or "Will analyze").ToArray();
        int completed = 0;
        foreach (var row in pending)
        {
            progress?.Report(new(completed, pending.Length, row.Input, "Processing"));
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Hash(File.ReadAllBytes(row.Input)) != row.SourceHash) throw new IOException("Input changed after preview; this file was not processed.");
                foreach (var dependency in row.Dependencies)
                    if ((File.Exists(dependency.Key) ? Hash(File.ReadAllBytes(dependency.Key)) : null) != dependency.Value)
                        throw new IOException("Input sidecar changed after preview; this file was not processed.");
                if (row.Result == "Will analyze") { row.Result = "Analyzed"; continue; }
                Directory.CreateDirectory(Path.GetDirectoryName(row.Output)!);
                if (row.ExtractedFiles != null) WriteExtracted(row, cancellationToken);
                else if (row.SidecarBytes != null) WriteTapePair(row, cancellationToken);
                else WriteImage(row, preview.Options, cancellationToken);
                row.Result = "Completed";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            { row.Result = "Cancelled"; row.Reason = "Cancelled before output commit; no output was saved for this input."; }
            catch (Exception e) when (e is IOException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            { row.Result = "Error"; row.Error = row.Reason = e.Message; }
            finally { completed++; progress?.Report(new(completed, pending.Length, row.Input, "Processed")); }
        }
        preview.Cancelled = cancellationToken.IsCancellationRequested;
    }

    private static void WriteImage(BatchRow row, BatchOptions options, CancellationToken cancellationToken)
    {
        byte[] bytes = row.ImageBytes ?? throw new InvalidOperationException("Missing prepared image.");
        if (Hash(bytes) != row.NewFingerprint) throw new IOException("Prepared output changed after preflight.");
        string temporary = row.Output + "." + Guid.NewGuid().ToString("N") + ".tmp"; bool owned = false;
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; output.Write(bytes); output.Flush(true); }
            byte[] stored = File.ReadAllBytes(temporary); if (Hash(stored) != row.NewFingerprint) throw new IOException("Temporary output fingerprint differs."); (row.VerifyOutput ?? VerifyImage)(stored, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (options.ReplaceOriginals)
            {
                if (Hash(File.ReadAllBytes(row.Input)) != row.SourceHash) throw new IOException("Original changed before atomic replace.");
                if (options.BackupOriginals)
                    CreateBackup(row);
                if (Hash(File.ReadAllBytes(row.Input)) != row.SourceHash) throw new IOException("Original changed before atomic replace.");
                File.Replace(temporary, row.Output, null);
            }
            else File.Move(temporary, row.Output, false);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteTapePair(BatchRow row, CancellationToken cancellationToken)
    {
        string sidecar = SidecarService.GetSidecarPath(row.Output);
        string temporary = row.Output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string sidecarTemporary = temporary + ".sidecar";
        bool wroteMain = false;
        try
        {
            if (Hash(row.ImageBytes!) != row.NewFingerprint) throw new IOException("Prepared output changed after preflight.");
            File.WriteAllBytes(temporary, row.ImageBytes!);
            File.WriteAllBytes(sidecarTemporary, row.SidecarBytes!);
            byte[] stored = File.ReadAllBytes(temporary);
            if (Hash(stored) != row.NewFingerprint || !File.ReadAllBytes(sidecarTemporary).AsSpan().SequenceEqual(row.SidecarBytes)) throw new IOException("Temporary tape/sidecar verification failed.");
            row.VerifyOutput!(stored, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, row.Output, false); wroteMain = true;
            File.Move(sidecarTemporary, sidecar, false);
        }
        catch
        {
            if (wroteMain && File.Exists(row.Output) && Hash(File.ReadAllBytes(row.Output)) == row.NewFingerprint) File.Delete(row.Output);
            throw;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
            if (File.Exists(sidecarTemporary)) File.Delete(sidecarTemporary);
        }
    }
    private static void CreateBackup(BatchRow row)
    {
        byte[] original = File.ReadAllBytes(row.Input);
        if (Hash(original) != row.SourceHash) throw new IOException("Original changed before backup creation.");
        string temporary = row.Output + ".bak." + Guid.NewGuid().ToString("N") + ".tmp"; bool owned = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { owned = true; stream.Write(original); stream.Flush(true); }
            if (Hash(File.ReadAllBytes(temporary)) != row.SourceHash) throw new IOException("Backup verification failed.");
            File.Move(temporary, row.Output + ".bak", false);
        }
        finally { if (owned && File.Exists(temporary)) File.Delete(temporary); }
    }
    private static void WriteExtracted(BatchRow row, CancellationToken cancellationToken)
    {
        string temporary = row.Output + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(temporary);
        try
        {
            foreach (var pair in row.ExtractedFiles!)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination = Path.GetFullPath(Path.Combine(temporary, pair.Key));
                if (!destination.StartsWith(Path.GetFullPath(temporary) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Extraction path escapes its output directory.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.WriteAllBytes(destination, pair.Value);
                if (!File.ReadAllBytes(destination).AsSpan().SequenceEqual(pair.Value)) throw new IOException("Extracted payload verification failed.");
            }
            cancellationToken.ThrowIfCancellationRequested(); Directory.Move(temporary, row.Output);
        }
        finally { if (Directory.Exists(temporary)) Directory.Delete(temporary, true); }
    }
    private static void VerifyImage(byte[] bytes, CancellationToken cancellationToken) { cancellationToken.ThrowIfCancellationRequested(); if (new HfeImage().Identify(bytes).Matches) HfeImage.Parse(bytes); else DskImage.Parse(bytes); cancellationToken.ThrowIfCancellationRequested(); }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string BootSystemFingerprint(DskDocument document)
    {
        if (PersonalCpmSystemInstaller.IsPersonalLayout(document))
        {
            string boot = CpmSystemFingerprint.HashSystemArea(document.Image, [1]);
            if (PersonalCpmSystemInstaller.FindSystemFile(document) == null) return $"IPL={boot}; PCPM.SYS=missing";
            return JsonSerializer.Serialize(PersonalCpmSystemInstaller.Fingerprint(document));
        }
        return document.FileSystem is CpmFileSystem cpm
            ? CpmSystemFingerprint.HashSystemArea(document.Image, DskDocumentFactory.GetSystemPhysicalTracks(cpm.Dpb, document.Image).ToArray()) : "Unavailable";
    }

    private static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".." || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || value.Contains('/') || value.Contains('\\') || value.EndsWith('.') || value.EndsWith(' ')) throw new InvalidDataException("Output filename is not safe/representable; no automatic renaming is performed.");
        string stem = value.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')) throw new InvalidDataException("Output filename is reserved by Windows.");
    }
    internal static string Report(BatchPreview preview, string format)
    {
        if (format == "json") return JsonSerializer.Serialize(preview.Rows, new JsonSerializerOptions { WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } });
        if (format == "txt") return preview.Summary + "\n\n" + string.Join("\n\n", preview.Rows.Select(r =>
            $"{r.Input}\nOutput: {r.Output}\n{r.Operation}: {r.Result}\n{r.DetectedFormat}; {r.Filesystem}\nInput SHA-256: {r.OldFingerprint}\nOutput SHA-256: {r.NewFingerprint}\n" +
            (r.Catalog is { } c ? $"Geometry: {c.Geometry}; capacity: {c.Capacity} B; layout / DPB: {c.Variant}\nBoot/system: {c.BootType}; profile: {c.BootProfile}; bootable state: {c.BootableState}\nFiles: {c.FileCount}; used: {c.UsedBytes} B; free: {c.FreeBytes} B\nAnalyzer errors/unsafe: {c.AnalyzerErrors}; unsafe: {c.AnalyzerUnsafe}; warnings: {c.AnalyzerWarnings}\nDecoded sectors: {c.SectorCount}; bitrate/RPM: {c.BitrateRpm}; weak bits: {c.WeakBits}; CRC-error sectors: {c.CrcErrorSectors}\n" : "") +
            (r.OldBootSystemFingerprint.Length > 0 ? $"Old boot/system fingerprint: {r.OldBootSystemFingerprint}\nNew boot/system fingerprint: {r.NewBootSystemFingerprint}\nNew boot profile: {r.NewBootProfile}\n" : "") +
            $"Severity: {r.Severity}; validation: {r.Validation}; integrity: {r.Integrity}\nLOAD={r.Load:X4}; EXEC={r.Exec:X4}; conversion={r.ConversionMode}; profile={r.Profile}; evidence={r.Evidence}\n{r.Reason}\n{r.Warnings}\n{r.Error}\n{r.DetailedReport}"));
        string Escape(object? value) => "\"" + (value?.ToString() ?? "").Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        var report = new StringBuilder("input,output,operation,format,filesystem,oldFingerprint,newFingerprint,result,reason,warnings,error,sha256,geometry,capacity,variant,bootType,bootProfile,bootable,fileCount,usedBytes,freeBytes,analyzerErrors,analyzerWarnings,trackCount,sides,bitrateRpm,weakBits,crcErrorSectors,analyzerUnsafe,sectorCount,oldBootSystemFingerprint,newBootSystemFingerprint,newBootProfile,severity,validation,load,exec,conversionMode,profile,evidence,integrity\n");
        foreach (var r in preview.Rows)
        {
            var c = r.Catalog;
            report.AppendLine(string.Join(",", new object?[] { r.Input, r.Output, r.Operation, r.DetectedFormat, r.Filesystem, r.OldFingerprint, r.NewFingerprint, r.Result, r.Reason, r.Warnings, r.Error, c?.Sha256, c?.Geometry, c?.Capacity, c?.Variant, c?.BootType, c?.BootProfile, c?.BootableState, c?.FileCount, c?.UsedBytes, c?.FreeBytes, c?.AnalyzerErrors, c?.AnalyzerWarnings, c?.TrackCount, c?.Sides, c?.BitrateRpm, c?.WeakBits, c?.CrcErrorSectors, c?.AnalyzerUnsafe, c?.SectorCount, r.OldBootSystemFingerprint, r.NewBootSystemFingerprint, r.NewBootProfile, r.Severity, r.Validation, r.Load, r.Exec, r.ConversionMode, r.Profile, r.Evidence, r.Integrity }.Select(Escape)));
        }
        return report.ToString();
    }
}
