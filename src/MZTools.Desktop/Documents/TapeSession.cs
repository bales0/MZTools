using System.Globalization;
using System.Security.Cryptography;

namespace MZTools.Desktop.Documents;

public sealed record FormatChoice(string Key, string Label, string Extension)
{
    public override string ToString() => Label;
    public static IReadOnlyList<FormatChoice> All { get; } = new[]
    {
        new FormatChoice("Mzf", "MZF · one record", ".mzf"), new("M12", "M12 · one record", ".m12"),
        new("Mzt", "MZT · tape", ".mzt"), new("Mzq", "MZQ · QuickDisk", ".mzq"), new("Qdf", "QDF · QuickDisk", ".qdf"),
        new("QdSharpLegacy", "QD · SHARP legacy logical", ".qd"), new("QdHxc", "QD · HxC physical", ".qd"), new("QdFlashFloppy", "QD · FlashFloppy physical", ".qd")
    };
}

public sealed class RecordRow
{
    internal TapeRecord Record { get; }
    internal RecordRow(TapeRecord record, int index) { Record = record; Index = index; }
    public int Index { get; }
    public int Number => Index + 1;
    public string Name => SharpMzEncoding.ConvertMzfNameToASCIIString(Record.Header.MzfFname);
    public string Type => $"{Record.Header.MzfFtype:X2}";
    public string Load => $"{Record.Header.MzfStart:X4}";
    public string Execute => $"{Record.Header.MzfExec:X4}";
    public string Size => Record.Body.MzfBody.Length.ToString("N0", CultureInfo.CurrentCulture) + " B";
    public string Profile => TapeProfileNames.ToDisplayName(Record.Profile);
    public string Origin => Record.MetadataOrigin.ToString();
    public string Compression => MzfLoaderBuilder.TryGetCompressionInfo(Record, out var info) && info is not null
        ? info.DisplayName : "Not detected";
    public string Hex => Convert.ToHexString(Record.GetSerializedHeader()).Chunk(32).Select(x => new string(x))
        .Aggregate("Header (128 bytes)\n", (a, b) => a + b + "\n") + "\nPayload (first 4096 bytes)\n" +
        string.Join("\n", Convert.ToHexString(Record.Body.MzfBody.AsSpan(0, Math.Min(4096, Record.Body.MzfBody.Length))).Chunk(32).Select(x => new string(x)));
}

public sealed class TapeSession : DocumentSession, IFileSetDocument
{
    internal TapeDocument Model { get; }
    private Dictionary<string, string?> sourceHashes = new(AtomicFileTransaction.PathComparer);
    IReadOnlyDictionary<string, string?> IFileSetDocument.SourceHashes => sourceHashes;
    SaveChoice IFileSetDocument.CurrentSaveChoice(string path) => new(path, Format, GenerateSidecar, PreserveTrailing);
    private IReadOnlyList<RecordRow> rows = [];
    private RecordRow? selected;
    private string filter = "";
    private bool refreshing;
    private bool snapshotDirty = true;
    internal List<int> Selection { get; } = [];
    public IReadOnlyList<RecordRow> Rows => rows;
    public bool HasRows => rows.Count != 0;
    public string EmptyMessage => Model.Records.Count == 0 ? "No records. Use Import to add files or transfer records from another document." : "No records match this filter.";
    public RecordRow? SelectedRow
    {
        get => selected;
        set
        {
            if (refreshing) return;
            if (Set(ref selected, value)) { LoadFields(); Changed(nameof(Hex)); Changed(nameof(CanFitSelection)); }
        }
    }
    public string Filter { get => filter; set { if (Set(ref filter, value)) Refresh(); } }
    public bool IsQuickDisk => Model.IsQuickDisk;
    protected override string UntitledName => IsQuickDisk ? "Untitled QuickDisk" : "Untitled tape";
    private int viewIndex;
    public int ViewIndex { get => viewIndex; set => Set(ref viewIndex,value); }
    internal int MapAnchor { get; set; }
    public bool CanEdit => !Model.IsReadOnlyQuickDisk && !IsReadOnly;
    public override bool CanSave => CanSaveAs && (Path is null || System.IO.Path.GetExtension(Path).Equals(TapeSavePolicy.Extension(Model.Format),StringComparison.OrdinalIgnoreCase))
        && (Model.Format is not (TapeDocumentFormat.Mzf or TapeDocumentFormat.M12) || Model.Records.Count == 1);
    public override bool CanSaveAs => !Model.IsReadOnlyQuickDisk;
    public override string SaveDisabledReason => !CanSaveAs ? "Non-SHARP/unknown QuickDisk: SHARP writing and editing are disabled."
        : HasDraft ? "Apply or Cancel the unapplied metadata edits before another operation."
        : !CanSave ? "The current path/record count is not bindable in this format. Use Save As or Export separate files."
        : IsReadOnly ? "Source is read-only. Save As creates an editable copy." : !IsModified ? "No unsaved changes." : "Changes remain in memory until Save.";
    public string Format => Model.Format.ToString();
    public string Summary => $"{Format} · {Model.Records.Count} records · {Model.Records.Sum(r => (long)r.Body.DataSize):N0} B payload";
    public string Hex => SelectedRow?.Hex ?? "Select a record to inspect its bytes.";
    public bool SelectedHidden => SelectedRow is not null && !Rows.Any(r => r.Index == SelectedRow.Index);
    private string name = "", type = "", load = "", exec = "", profile = "";
    public string Name { get => name; set { if (Set(ref name, value)) DraftChanged(); } }
    public string FileType { get => type; set { if (Set(ref type, value)) DraftChanged(); } }
    public string LoadAddress { get => load; set { if (Set(ref load, value)) DraftChanged(); } }
    public string ExecuteAddress { get => exec; set { if (Set(ref exec, value)) DraftChanged(); } }
    public IReadOnlyList<string> Profiles { get; } = Enum.GetValues<TapeProfile>().Select(TapeProfileNames.ToDisplayName).ToArray();
    public string Profile { get => profile; set { if (Set(ref profile, value)) DraftChanged(); } }
    public bool HasDraft => SelectedRow is not null && (Name != SelectedRow.Name || FileType != SelectedRow.Type || LoadAddress != SelectedRow.Load || ExecuteAddress != SelectedRow.Execute || Profile != SelectedRow.Profile);
    public override bool HasUnsavedChanges => IsModified || HasDraft;
    private void DraftChanged() { Changed(nameof(HasDraft)); Changed(nameof(HasUnsavedChanges)); Changed(nameof(Title)); }
    public string Validation { get; private set; } = "";
    public bool GenerateSidecar { get; internal set; } = true;
    public bool PreserveTrailing { get; internal set; } = true;
    public double MapStart { get; internal set; }
    public double MapLength { get; internal set; }
    internal QuickDiskVisualSnapshot? Snapshot { get; private set; }
    internal QuickDiskRegion? SelectedRegion { get; private set; }
    public bool HasRegion => SelectedRegion is not null;
    public bool CanFitSelection => SelectedRow is not null || HasRegion;
    public string MapDetail => SelectedRegion is not null && Snapshot is not null ? Snapshot.Layout.Detail(SelectedRegion) : "Select a mapped region for its exact range and units.";
    public string MapSummary => Snapshot is null ? "Tape record sequence · positions are record indices, not timing measurements."
        : "Map view only · edit records in the Records tab.\nHost: " + Snapshot.Host + "\n" + Snapshot.Layout.Summary;
    public string MediaEvidence => Snapshot is null ? Summary : string.Join("\n",Snapshot.Statistics.Select(r=>string.Join(": ",r))) +
        "\n\n" + string.Join("\n",Snapshot.Findings.Select(f=>$"{f.Severity}: {f.Description}"));
    public string MediaReport => Summary + "\n" + (Path ?? "Untitled") + "\n\n" +
        string.Join("\n",Model.Records.Select((r,i)=>new RecordRow(r,i)).Select(r=>$"#{r.Number} {r.Name} · type {r.Type} · LOAD {r.Load} · EXEC {r.Execute} · {r.Size} · {r.Profile} · {r.Compression}")) +
        "\n\n" + MediaEvidence + "\n\nUnapplied metadata fields are not included. No hardware capture or test certification is implied.";
    internal void SelectRegion(QuickDiskRegion region) { SelectedRegion = region; Changed(nameof(MapDetail)); Changed(nameof(HasRegion)); Changed(nameof(CanFitSelection)); }

    internal TapeSession(TapeDocument document, Dictionary<string, string?>? hashes = null)
    {
        Model = document; if (hashes is not null) sourceHashes = hashes;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsReadOnly)) Changed(nameof(CanEdit)); };
        Refresh();
    }
    internal static TapeSession New(bool quickDisk)
    {
        var session = new TapeSession(new() { Format = quickDisk ? TapeDocumentFormat.Mzq : TapeDocumentFormat.Mzt, IsModified = true });
        session.MarkModified(); return session;
    }
    internal void SetSelection(IEnumerable<RecordRow> selectedRows)
    {
        if (refreshing) return;
        Selection.Clear(); Selection.AddRange(selectedRows.Select(r => r.Index).Distinct().Order());
        SelectedRow = selectedRows.FirstOrDefault();
    }
    private void LoadFields()
    {
        name = selected?.Name ?? ""; type = selected?.Type ?? ""; load = selected?.Load ?? "";
        exec = selected?.Execute ?? ""; profile = selected?.Profile ?? Profiles[0];
        foreach (string field in new[] { nameof(Name), nameof(FileType), nameof(LoadAddress), nameof(ExecuteAddress), nameof(Profile) }) Changed(field);
        DraftChanged();
    }
    internal void ResetMetadata() { LoadFields(); Validation = ""; Changed(nameof(Validation)); }
    internal void Refresh()
    {
        int previous = SelectedRow?.Index ?? 0;
        var previousRecord = selected?.Record; bool hadDraft = HasDraft;
        refreshing = true;
        rows = Model.Records.Select((r, i) => new RecordRow(r, i)).Where(r => (r.Name + " " + r.Profile).Contains(Filter, StringComparison.OrdinalIgnoreCase)).ToArray();
        selected = rows.FirstOrDefault(r => r.Index == previous) ?? (previous >= 0 && previous < Model.Records.Count ? new RecordRow(Model.Records[previous],previous) : rows.FirstOrDefault());
        if (snapshotDirty)
        {
            try { Snapshot = IsQuickDisk ? QuickDiskVisualSnapshot.Capture(Model) : null; }
            catch (Exception e) when (e is InvalidDataException or InvalidOperationException) { Snapshot = null; }
            snapshotDirty = false; SelectedRegion = null;
        }
        Changed(nameof(Rows)); Changed(nameof(HasRows)); Changed(nameof(EmptyMessage)); Changed(nameof(SelectedRow)); Changed(nameof(Summary)); Changed(nameof(Format)); Changed(nameof(CanEdit)); Changed(nameof(IsQuickDisk));
        Changed(nameof(MapSummary)); Changed(nameof(Hex)); Changed(nameof(SaveDisabledReason)); Changed(nameof(SelectedHidden)); Changed(nameof(MapDetail)); Changed(nameof(HasRegion)); Changed(nameof(CanFitSelection)); Changed(nameof(MediaEvidence)); Changed(nameof(MediaReport));
        refreshing = false; if (!hadDraft || previousRecord != selected?.Record) LoadFields();
    }
    private void EnsureEditable() { if (!CanEdit) throw new InvalidOperationException(SaveDisabledReason); }
    internal void ApplyMetadata()
    {
        EnsureEditable(); if (SelectedRow is null) return;
        try
        {
            var record = SelectedRow.Record.DeepClone(); var header = record.Header;
            header.MzfFname = TapeFileNameEncoding.Encode(Name);
            header.MzfFtype = byte.Parse(FileType, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            if (header.MzfFtype == 0) throw new InvalidDataException("File type zero is not supported.");
            header.MzfStart = ushort.Parse(LoadAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            header.MzfExec = ushort.Parse(ExecuteAddress, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            record.Header = header;
            if (Profile != SelectedRow.Profile) record.Profile = Enum.GetValues<TapeProfile>().First(p => TapeProfileNames.ToDisplayName(p) == Profile);
            record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
            var proposed = Model.Records.Select(r => r.DeepClone()).ToList(); proposed[SelectedRow.Index] = record;
            ApplyRecords(proposed); Validation = "";
        }
        catch (Exception e) when (e is FormatException or OverflowException or InvalidOperationException or InvalidDataException)
        { Validation = e.Message; }
        Changed(nameof(Validation));
    }
    internal void ApplyRecords(IReadOnlyList<TapeRecord> proposed)
    {
        EnsureEditable();
        if (IsQuickDisk)
        {
            var candidate = new TapeDocument { Format = Model.Format, IsModified = true, QuickDiskProfile = Model.QuickDiskProfile };
            candidate.Records.AddRange(proposed);
            _ = TapeDocumentIO.Serialize(candidate, Model.Format, true);
        }
        var applied = proposed.ToArray();
        Model.Records.Clear(); Model.Records.AddRange(applied); Model.IsModified = true; snapshotDirty = true; MarkModified(); Refresh();
    }
    internal void Move(int offset)
    {
        EnsureEditable(); var indices = Selection.Count == 0 && SelectedRow is not null ? new[] { SelectedRow.Index } : Selection.ToArray();
        if (indices.Length == 0) return;
        int insertion = offset < 0 ? indices.Min() - 1 : indices.Max() + 2;
        Reorder(indices,insertion);
    }
    internal void Reorder(int[] indices,int insertion)
    {
        EnsureEditable();if(HasDraft)throw new InvalidOperationException("Apply or Cancel metadata before changing order.");
        var list = Model.Records.ToList();
        var selectedRecords = indices.Select(i => Model.Records[i]).ToArray();
        if (ListReorder.MoveItems(list, indices, insertion))
        {
            Selection.Clear(); Selection.AddRange(selectedRecords.Select(r => list.IndexOf(r)));
            ApplyRecords(list); SelectedRow = rows.FirstOrDefault(r => Selection.Contains(r.Index));
        }
    }
    internal IReadOnlyList<TapeRecord> SelectedRecords() => (Selection.Count != 0 ? Selection : SelectedRow is null ? [] : new List<int> { SelectedRow.Index })
        .Where(i => i >= 0 && i < Model.Records.Count).Select(i => Model.Records[i].DeepClone()).ToArray();

    async Task<DocumentSavePlan> IFileSetDocument.PrepareSaveAsync(SaveChoice choice, CancellationToken cancellation)
    {
        var format = choice.Format is null ? Model.Format : Enum.Parse<TapeDocumentFormat>(choice.Format);
        if (!System.IO.Path.GetExtension(choice.Path).Equals(TapeSavePolicy.Extension(format), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The destination extension does not match the explicitly selected format.");
        var clone = new TapeDocument { Format = Model.Format, IsModified = Model.IsModified, QuickDiskProfile = Model.QuickDiskProfile };
        clone.Records.AddRange(Model.Records.Select(r => r.DeepClone()));
        byte[] main = await Task.Run(() => TapeDocumentIO.Serialize(clone, format, choice.PreserveTrailing), cancellation);
        var outputs = new Dictionary<string, byte[]>(AtomicFileTransaction.PathComparer) { [choice.Path] = main };
        bool tape = format is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12 or TapeDocumentFormat.Mzt;
        string? sidecar = tape && choice.GenerateSidecar ? SidecarService.GetSidecarPath(choice.Path) : null;
        if (tape && sidecar is null && File.Exists(SidecarService.GetSidecarPath(choice.Path)))
            throw new IOException("The destination already has metadata. Enable sidecar output or choose a different destination; stale metadata will not be left beside new media.");
        if (sidecar is not null) outputs.Add(sidecar, format == TapeDocumentFormat.Mzt ? SidecarService.SerializeMti(clone.Records) : SidecarService.SerializeMfi(clone.Records.Single()));
        var warnings = new List<string>();
        if (Model.ContainerTrailingData.Length != 0 || clone.Records.Any(r => r.Body.TrailingData.Length != 0) && (format == TapeDocumentFormat.Mzt || !choice.PreserveTrailing || !tape))
            warnings.Add("Trailing/container data will be omitted by this output format.");
        if (sidecar is null && clone.Records.Any(r => r.Profile != TapeProfile.Normal1_1 || r.MetadataOrigin != MetadataOrigin.Implicit))
            warnings.Add("This output does not preserve tape profile metadata. Source and output metadata will be shown separately.");
        if (!tape && clone.Records.Any(r => r.GetSerializedHeader().Skip(62).Any(b => b != 0)))
            warnings.Add("QuickDisk stores 38 header description bytes; the remaining MZF header description bytes will not be retained.");
        if (Model.QuickDiskSourceImage is not null && format is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy && !main.AsSpan().SequenceEqual(Model.QuickDiskSourceImage))
            warnings.Add("The existing SHARP writer regenerates the physical layout. This is not a raw physical copy.");
        return new(outputs, string.Join("\n", warnings), unchanged =>
        {
            sourceHashes = outputs.ToDictionary(p => p.Key, p => (string?)Convert.ToHexString(SHA256.HashData(p.Value)), AtomicFileTransaction.PathComparer);
            if (Model.Format != format) { MapStart = 0; MapLength = 0; }
            Model.FilePath = choice.Path; Model.Format = format; Model.SidecarPath = sidecar;
            GenerateSidecar = choice.GenerateSidecar; PreserveTrailing = choice.PreserveTrailing;
            if (unchanged)
            {
                Model.IsModified = false; Model.ContainerTrailingData = [];
                foreach (var record in Model.Records)
                {
                    if (format == TapeDocumentFormat.Mzt || !choice.PreserveTrailing || !tape) record.RemoveTrailingData();
                    if (sidecar is null) record.ResetMetadataToImplicit();
                    else record.MetadataOrigin = format == TapeDocumentFormat.Mzt ? MetadataOrigin.LoadedFromMti : SidecarService.SingleRecordOrigin(choice.Path);
                }
            }
            if (Model.IsQuickDisk) Model.QuickDiskSourceImage = main;
            else { Model.QuickDiskSourceImage = null; Model.QuickDiskReadResult = null; Model.QuickDiskProfile = null; }
            if (format is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy or TapeDocumentFormat.QdSharpLegacy)
            {
                var qd = QdImageReaderWriter.Read(main);
                // A blank image created by this writer is editable in this session; do not fabricate host evidence.
                // Reopening external blank physical data still uses the detector's read-only classification.
                Model.QuickDiskReadResult = qd.Analysis.Identification.IsBlank && Model.Records.Count == 0 ? null : qd;
                Model.QuickDiskProfile = qd.PhysicalProfile;
            }
            snapshotDirty = true; Refresh();
        });
    }
    public override Task WriteAsync(Stream destination, CancellationToken cancellationToken) => throw new NotSupportedException("Media documents require a complete file-set save plan.");
    public override Task ValidateSavedAsync(string temporaryPath, CancellationToken cancellationToken) => throw new NotSupportedException("Validation belongs to the file-set plan.");
}

public sealed class MediaDocumentLoader(IDiskDialogs? diskDialogs = null) : IDocumentLoader,IProgressDocumentLoader
{
    private IProgress<LoadProgress>? progress;
    IProgress<LoadProgress>? IProgressDocumentLoader.Progress {get=>progress;set=>progress=value;}
    public async Task<DocumentSession> LoadAsync(string path, CancellationToken cancellationToken)
    {
        if(System.IO.Path.GetExtension(path).ToLowerInvariant() is ".wav" or ".flac" || SegmentedPcmStreamReader.IsManifest(path))
        {
            var sink=progress;var reporter=new Progress<WavAnalysisProgress>(p=>sink?.Report(new(p.Stage,p.Fraction)));
            return await AudioSession.LoadAsync(path,reporter,cancellationToken);
        }
        if (System.IO.Path.GetExtension(path).ToLowerInvariant() is ".dsk" or ".hfe")
            return await DiskLoader.LoadAsync(path,diskDialogs,cancellationToken);
        return await Task.Run<DocumentSession>(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        string sidecar = SidecarService.GetSidecarPath(path);
        bool tapeExtension = System.IO.Path.GetExtension(path).ToLowerInvariant() is ".mzf" or ".m12" or ".mzt" or ".mz0" or ".mz7";
        string? sideHash = tapeExtension ? AtomicFileTransaction.Hash(sidecar) : null;
        var document = TapeDocumentIO.Read(path);
        var hashes = new Dictionary<string, string?>(AtomicFileTransaction.PathComparer) { [path] = AtomicFileTransaction.Hash(path) };
        if (document.Format is TapeDocumentFormat.Mzf or TapeDocumentFormat.M12 or TapeDocumentFormat.Mzt)
        {
            if (AtomicFileTransaction.Hash(sidecar) != sideHash) throw new IOException("Metadata changed while opening.");
            hashes.Add(sidecar, sideHash);
        }
        cancellationToken.ThrowIfCancellationRequested(); return new TapeSession(document, hashes);
    }, cancellationToken);
    }
}
