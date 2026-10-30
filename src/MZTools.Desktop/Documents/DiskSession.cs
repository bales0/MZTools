using System.Globalization;
using MZTools.Desktop.Platform;

namespace MZTools.Desktop.Documents;

public sealed class DiskFileRow
{
    internal DskFileEntry Entry { get; }
    internal DiskFileRow(DskFileEntry entry) => Entry = entry;
    public string Key => Entry.Key;
    public string Name => Entry.Name + (Entry.Extension.Length == 0 ? "" : "." + Entry.Extension);
    public long Bytes => Entry.Size;
    public string Type => $"0x{Entry.FileType:X2}";
    public string Load => $"0x{Entry.LoadAddress:X4}";
    public string Execute => $"0x{Entry.ExecuteAddress:X4}";
    public string Attributes => $"User {Entry.User}" + (Entry.ReadOnly ? " · R" : "") + (Entry.System ? " · S" : "") + (Entry.Archived ? " · A" : "") + (Entry.Locked ? " · Locked" : "");
    public string Detail => $"{Name}\n{Bytes:N0} B · {Entry.Blocks} blocks · {Entry.Extents} extents\n{Attributes}\n{Entry.Notes}";
}
public sealed class DiskSectorRow
{
    internal DskVisualSector Sector { get; }
    internal DiskSectorRow(DskVisualSector sector) => Sector = sector;
    public int Track => Sector.Track;
    public int Descriptor => Sector.Descriptor;
    public string Chrn => $"{Sector.C}/{Sector.H}/{Sector.R}/{Sector.N}";
    public string Status => $"{Sector.St1:X2}/{Sector.St2:X2}";
    public string Role => Sector.Role.ToString();
    public string Files => Sector.Ownership;
    public int Bytes => Sector.StoredBytes;
    public string Detail => $"T{Track} descriptor {Descriptor} · CHRN {Chrn}\nST1/ST2: {Status} · {Sector.Status}\n{Bytes:N0} stored B / {Sector.DeclaredBytes:N0} declared B\n{(Sector.PhysicalCells ? "Track bitcell" : "File byte")} offset 0x{Sector.DataOffset:X}\n{Role} · {Files}\nSHA-256 {Sector.DataSha256}";
}
public sealed class DiskFindingRow
{
    internal DskAnalysisIssue Issue { get; }
    internal DiskFindingRow(DskAnalysisIssue issue) => Issue = issue;
    public string Severity => Issue.Severity.ToString();
    public string Code => Issue.Code;
    public string Location => Issue.Location;
    public string Description => Issue.Description;
    public string Detail => Issue.Explanation;
}

public sealed class DiskSession : DocumentSession, IFileSetDocument
{
    private DskDocument? projection;
    private HfeImage? opaque;
    internal DskDocument Model => projection ?? throw new InvalidOperationException("No safe decoded filesystem projection is available for this physical image.");
    internal HfeImage? PhysicalImage => opaque ?? projection?.CurrentHfe();
    private DskGeneratedTrackCache? trackCache;
    internal PhysicalTrack? TrackData { get; private set; }
    public IReadOnlyList<int> TrackIndices => Snapshot.Tracks.Select(t=>t.PhysicalIndex).ToArray();
    private int trackIndex;
    public int TrackIndex { get=>trackIndex;set=>Set(ref trackIndex,value); }
    private double cellStart;
    public double CellStart { get=>cellStart;internal set=>Set(ref cellStart,value); }
    private double cellLength;
    public double CellLength { get=>cellLength;internal set=>Set(ref cellLength,value); }
    public string PhysicalReport { get; private set; } = "Select a track and Load track. DSK cells are generated on demand; HFE cells come from the stored image.";
    internal async Task LoadTrackAsync(CancellationToken ct)
    {
        var cache=trackCache;var snapshot=Snapshot;int index=TrackIndex;
        if(cache is null)throw new InvalidOperationException("No track cache is available.");
        var data=await cache.GetAsync(index,ct);
        if(cache!=trackCache || snapshot!=Snapshot || index!=TrackIndex)return;
        TrackData=data;CellStart=0;CellLength=data.BitCellCount;
        PhysicalReport=$"{(Snapshot.IsPhysical?"Stored HFE physical cells":"GeneratedFrom=DSK · synthetic MFM · measuredFlux=false")}\nC{data.Cylinder}/H{data.Side} · {data.Encoding} · {data.BitCellCount:N0} bitcells\nDeclared rate {data.BitRate} bit/s · RPM {data.Rpm}\nIndex cells: {string.Join(", ",data.IndexCells)}\nTiming changes: {data.Timing.Count} · weak-mask bytes: {data.WeakBitMask.Count(b=>b!=0)}\n"+string.Join("\n",data.Sectors.Select(s=>$"CHRN {s.C}/{s.H}/{s.R}/{s.N} · header @ cell {s.HeaderCell:N0} · data @ cell {s.DataCell:N0} · CRC header/data {s.HeaderCrcValid}/{s.DataCrcValid}"));
        Changed(nameof(PhysicalReport));Changed(nameof(TrackIndex));
    }
    public override void Dispose() {trackCache?.Dispose();trackCache=null;}
    internal void MakeNew() => MarkModified();
    internal bool HasProjection => projection is not null;
    internal DskVisualSnapshot Snapshot { get; private set; } = null!;
    internal DskLayoutModel? Layout { get; private set; }
    private Dictionary<string,string?> hashes = new(AtomicFileTransaction.PathComparer);
    IReadOnlyDictionary<string,string?> IFileSetDocument.SourceHashes => hashes;
    public bool IsHfe => opaque is not null || projection?.SourceHfe is not null;
    public string Container => opaque?.Variant ?? projection!.ContainerName;
    public string Extension => IsHfe ? ".hfe" : ".dsk";
    public bool RequiresRepairSaveAs => projection?.RequiresRepairSaveAs == true;
    public bool CanEdit => projection is not null && !projection.IsReadOnly && !IsReadOnly;
    public bool CanEditIpl => !IsReadOnly && projection?.SourceHfe?.WriteAllowed != false && (projection?.FileSystem is SingleGameIplFileSystem or MultiGameIplFileSystem {CanConfigure:true});
    public bool CanEditFileName => SelectedFile is not null && CanEdit && projection is not null && DskCapabilityService.GetFilePropertyKind(Model)!=DskFilePropertyKind.None;
    public bool CanDeleteFiles => CanEdit && projection?.FileSystem is FsmzFileSystem or CpmFileSystem or MrsFileSystem;
    public bool CanEditHex => projection is not null && projection.SourceHfe?.WriteAllowed != false && !IsReadOnly;
    public override bool CanSave => !RequiresRepairSaveAs;
    public override bool CanSaveAs => true;
    protected override string UntitledName => "Untitled " + Container;
    public override string SaveDisabledReason => HasDraft ? "Apply or Cancel disk edits before another operation."
        : RequiresRepairSaveAs ? "Container repair is in memory. Save As to a new path; the original is protected."
        : IsReadOnly ? "Source is read-only. Save As creates a separate copy."
        : projection?.IsReadOnly != false ? "Read-only filesystem/physical interpretation; inspection and Save As copy remain available."
        : IsModified ? "Changes remain in memory until Save." : "No unsaved changes.";
    public string Summary => $"{Container} · {projection?.FileSystem.DisplayName ?? "Physical image without safe filesystem projection"} · {Files.Count} files · {Snapshot.Sectors.Count():N0} sectors";
    public string Capacity => projection is null ? "Decoded filesystem capacity unavailable." : $"Used {Model.FileSystem.UsedBytes:N0} B · free {Model.FileSystem.FreeBytes:N0} B";
    private string filter = "", issueFilter = "All";
    private IReadOnlyList<DiskFileRow> files = [];
    public IReadOnlyList<DiskFileRow> Files => files.Where(f => f.Name.Contains(Filter,StringComparison.OrdinalIgnoreCase)).ToArray();
    public IReadOnlyList<DiskSectorRow> Sectors { get; private set; } = [];
    public IReadOnlyList<DiskFindingRow> Findings => (Layout?.Issues ?? []).Where(i => IssueFilter == "All" || i.Severity.ToString() == IssueFilter).Select(i => new DiskFindingRow(i)).ToArray();
    public IReadOnlyList<string> IssueFilters { get; } = ["All","Unsafe","Error","Warning","Info"];
    public string Filter { get => filter; set { if(Set(ref filter,value)) Changed(nameof(Files)); } }
    public string IssueFilter { get => issueFilter; set { if(Set(ref issueFilter,value)) Changed(nameof(Findings)); } }
    private int viewIndex;
    public int ViewIndex { get => viewIndex; set => Set(ref viewIndex,value); }
    private int mapMode;
    public int MapMode {get=>mapMode;set=>Set(ref mapMode,value);}
    internal double SurfaceZoom {get;set;}=1;
    internal double SurfacePanX {get;set;}
    internal double SurfacePanY {get;set;}
    public double MapZoom { get; internal set; } = 1;
    public double MapOffset { get; internal set; }
    private bool logicalOrder;
    public bool LogicalOrder { get => logicalOrder; set { if(Set(ref logicalOrder,value)) { SortSectors(); Changed(nameof(Sectors)); } } }
    internal HashSet<DskSectorAddress> SectorSelection { get; } = [];
    internal List<string> FileSelection { get; } = [];
    private bool linkingSelection;
    private DiskFileRow? selectedFile;
    private DiskSectorRow? selectedSector;
    private DiskFindingRow? selectedFinding;
    public DiskFileRow? SelectedFile
    {
        get => selectedFile;
        set
        {
            if (HasDraft && selectedFile?.Key != value?.Key) { Validation = "Apply or Cancel disk edits before changing selection."; Changed(nameof(Validation)); Changed(nameof(SelectedFile)); return; }
            if(Set(ref selectedFile,value))
            {
                ResetDraft(); if(!linkingSelection)SelectFileSectors(); Changed(nameof(Detail));
                foreach(var property in new[]{nameof(CanEditFileName),nameof(CanEditCpmProperties),nameof(CanEditAddresses),nameof(CanEditFsmzProperties)})Changed(property);
            }
        }
    }
    public DiskSectorRow? SelectedSector
    {
        get => selectedSector;
        set
        {
            if(HasDraft && selectedSector?.Sector.Address != value?.Sector.Address) { Validation = "Apply or Cancel the current draft before changing sector."; Changed(nameof(Validation)); Changed(nameof(SelectedSector)); return; }
            if(Set(ref selectedSector,value))
            {
                ResetHex();
                if(!linkingSelection)
                {
                    linkingSelection=true;
                    try
                    {
                        var keys=value?.Sector.Owners.Select(o=>o.FileKey).Distinct().ToArray()??[];
                        FileSelection.Clear();FileSelection.AddRange(keys);
                        SelectedFile=files.FirstOrDefault(f=>keys.Contains(f.Key));
                    }
                    finally{linkingSelection=false;}
                }
                Changed(nameof(Detail));
            }
        }
    }
    public DiskFindingRow? SelectedFinding
    {
        get => selectedFinding;
        set { if(Set(ref selectedFinding,value) && value is not null) { var i=value.Issue; if(i.Track is int t) SelectedSector=Sectors.FirstOrDefault(s=>s.Track==t&&(i.Sector is null || s.Descriptor==i.Sector)); Changed(nameof(Detail)); } }
    }
    public string Detail => SelectedFinding?.Detail ?? SelectedSector?.Detail ?? SelectedFile?.Detail ?? Capacity;
    public string Report => (Layout?.Report() ?? DskVisualSnapshot.SurfaceOrigin) + "\n\n" + Capacity + "\n\n" + SaveDisabledReason;
    public string ComparisonReport { get; private set; } = "Choose Compare image from Disk tools.";
    internal DskComparison? Comparison { get; private set; }
    public IReadOnlyList<DiskDiffRow> Differences { get; private set; } = [];
    internal void SetComparison(DskComparison? comparison,string report) { Comparison=comparison;ComparisonReport=report;Differences=comparison?.Items.Select(i=>new DiskDiffRow(i)).ToArray()??[];Changed(nameof(Differences));Changed(nameof(ComparisonReport)); }
    public string Structure => projection is not null && DskCapabilityService.CanInspectStructure(Model)
        ? string.Join("\n\n",DskStructureService.Build(Model).Items.Select(i=>$"{i.Tab}: {i.Name} = {i.Value}\n{i.Detail}")) : "Semantic structure is unavailable for this image.";
    private string name="", user="", load="", exec="", type="";
    private bool readOnly, system, archived, locked;
    public string FileName { get=>name; set {if(Set(ref name,value)) DraftChanged();} }
    public string User { get=>user; set {if(Set(ref user,value)) DraftChanged();} }
    public string LoadAddress { get=>load; set {if(Set(ref load,value)) DraftChanged();} }
    public string ExecuteAddress { get=>exec; set {if(Set(ref exec,value)) DraftChanged();} }
    public string FileType { get=>type; set {if(Set(ref type,value)) DraftChanged();} }
    public bool FileReadOnly { get=>readOnly; set {if(Set(ref readOnly,value)) DraftChanged();} }
    public bool System { get=>system; set {if(Set(ref system,value)) DraftChanged();} }
    public bool Archived { get=>archived; set {if(Set(ref archived,value)) DraftChanged();} }
    public bool Locked { get=>locked; set {if(Set(ref locked,value)) DraftChanged();} }
    public bool HasPropertiesDraft => SelectedFile is { } f && (FileName!=f.Name || User!=f.Entry.User.ToString(CultureInfo.InvariantCulture) || LoadAddress!=$"{f.Entry.LoadAddress:X4}" || ExecuteAddress!=$"{f.Entry.ExecuteAddress:X4}" || FileType!=$"{f.Entry.FileType:X2}" || FileReadOnly!=f.Entry.ReadOnly || System!=f.Entry.System || Archived!=f.Entry.Archived || Locked!=f.Entry.Locked);
    private DskHexEditSession? hexSession;
    private string hex="", originalHex="";
    public string HexText { get=>hex; set {if(Set(ref hex,value)) DraftChanged();} }
    public string HexTitle => hexSession?.Title ?? "Select a sector, or open a CP/M allocation block.";
    public bool HasHexDraft => HexText!=originalHex;
    public bool HasDraft => HasPropertiesDraft || HasHexDraft;
    public override bool HasUnsavedChanges => IsModified || HasDraft;
    public string Validation { get; private set; } = "";
    public bool CanEditCpmProperties => SelectedFile is not null && CanEdit && projection?.FileSystem is CpmFileSystem;
    public bool CanEditAddresses => SelectedFile is not null && CanEdit && projection?.FileSystem is FsmzFileSystem or MrsFileSystem;
    public bool CanEditFsmzProperties => SelectedFile is not null && CanEdit && projection?.FileSystem is FsmzFileSystem;
    private void DraftChanged() { foreach(var p in new[]{nameof(HasDraft),nameof(HasUnsavedChanges),nameof(Title),nameof(SaveDisabledReason)}) Changed(p); }
    internal DiskSession(DskDocument model, Dictionary<string,string?>? sourceHashes=null)
    { projection=model;hashes=sourceHashes??new(AtomicFileTransaction.PathComparer);Refresh(); }
    internal DiskSession(HfeImage image, Dictionary<string,string?>? sourceHashes=null)
    { opaque=image;hashes=sourceHashes??new(AtomicFileTransaction.PathComparer);Refresh(); }
    private void NotifyCapabilities()
    {foreach(var p in new[]{nameof(CanEdit),nameof(CanEditIpl),nameof(CanEditFileName),nameof(CanDeleteFiles),nameof(CanEditHex),nameof(CanEditCpmProperties),nameof(CanEditAddresses),nameof(CanEditFsmzProperties)})Changed(p);}
    protected override void OnBound()=>NotifyCapabilities();
    protected override void OnLoaded() { if(RequiresRepairSaveAs) MarkModified();NotifyCapabilities(); }
    internal static DiskSession New(DskNewOptions options)
    { var session=new DiskSession(DiskFormatService.CreateNew(options));session.MarkModified();return session; }
    internal void Apply(DskDocument candidate,long revision)
    {
        if(Revision!=revision || HasDraft) throw new InvalidOperationException("The disk changed after preview. Review it again.");
        if(projection is not null && candidate.SerializeContainer().AsSpan().SequenceEqual(projection.SerializeContainer()) && candidate.AttachedDpb==projection.AttachedDpb && candidate.RequiresRepairSaveAs==projection.RequiresRepairSaveAs) return;
        projection=candidate;opaque=null;MarkModified();Refresh();
    }
    internal void ApplyProperties(DskDocument candidate,long revision)
    { if(Revision!=revision) throw new InvalidOperationException("The disk changed after preview.");projection=candidate;MarkModified();Refresh(); }
    internal DskDocument PrepareProperties()
    {
        if(!CanEdit || SelectedFile is null) throw new InvalidOperationException("Select an editable file.");
        var candidate=Model.Clone();var e=candidate.FileSystem.ReadDirectory().Single(e=>e.Key==SelectedFile.Key);
        var props=new DskFileProperties(int.Parse(User,CultureInfo.InvariantCulture),FileReadOnly,System,Archived,Hex16(LoadAddress),Hex16(ExecuteAddress),checked((byte)Hex16(FileType)),Locked);
        if(props!=DskFileProperties.From(e)) DskFilePropertyService.Apply(candidate,e,props);
        if(FileName!=SelectedFile.Name) { candidate.FileSystem.Rename(candidate.FileSystem.ReadDirectory().Single(f=>f.Name==e.Name&&f.Extension==e.Extension&&f.User==props.User),FileName); candidate.MarkModified(); }
        return candidate;
    }
    internal DskHexEditPreview PrepareHex()
    { if(!CanEditHex || hexSession is null) throw new InvalidOperationException("No editable hex buffer.");return DskHexEditService.Preview(hexSession,DskHexEditService.ParseHex(HexText,hexSession.OriginalBuffer.Length)); }
    internal void OpenBlock(int block)
    { if(HasDraft) throw new InvalidOperationException("Apply or Cancel disk edits first.");hexSession=DskHexEditService.OpenCpmBlock(Model,block);originalHex=hex=DskHexEditService.FormatHex(hexSession.OriginalBuffer);Changed(nameof(HexText));Changed(nameof(HexTitle));ViewIndex=3; }
    public void ResetDraft()
    {
        var e=SelectedFile?.Entry;name=SelectedFile?.Name??"";user=(e?.User??0).ToString(CultureInfo.InvariantCulture);load=$"{e?.LoadAddress??0:X4}";exec=$"{e?.ExecuteAddress??0:X4}";type=$"{e?.FileType??0:X2}";
        readOnly=e?.ReadOnly??false;system=e?.System??false;archived=e?.Archived??false;locked=e?.Locked??false;
        Validation="";ResetHex();foreach(var p in new[]{nameof(FileName),nameof(User),nameof(LoadAddress),nameof(ExecuteAddress),nameof(FileType),nameof(FileReadOnly),nameof(System),nameof(Archived),nameof(Locked),nameof(Validation)})Changed(p);DraftChanged();
    }
    private void ResetHex()
    {
        hexSession=projection is not null && SelectedSector is not null ? DskHexEditService.OpenSector(Model,SelectedSector.Sector.Address) : null;
        originalHex=hex=hexSession is not null ? DskHexEditService.FormatHex(hexSession.OriginalBuffer) : SelectedSector is not null ? DskHexEditService.FormatHex(SelectedSector.Sector.Data) : "";
        Changed(nameof(HexText));Changed(nameof(HexTitle));DraftChanged();
    }
    internal void SetError(string message) {Validation=message;Changed(nameof(Validation));}
    internal void SelectFileSectors()
    {
        SectorSelection.Clear();
        if(SelectedFile is not null)foreach(var row in Sectors.Where(r=>r.Sector.Owners.Any(o=>o.FileKey==SelectedFile.Key)))SectorSelection.Add(row.Sector.Address);
        linkingSelection=true;
        try { SelectedSector=selectedSector is not null&&SectorSelection.Contains(selectedSector.Sector.Address)?selectedSector:Sectors.FirstOrDefault(r=>SectorSelection.Contains(r.Sector.Address)); }
        finally { linkingSelection=false; }
        Changed(nameof(SectorSelection));
    }
    internal void SelectSector(DiskSectorRow row,bool toggle=false,bool range=false)
    {
        if(HasDraft) throw new InvalidOperationException("Apply or Cancel edits before selecting another sector.");
        if(range && SelectedSector is { } anchor)
        {int a=Sectors.ToList().IndexOf(anchor),b=Sectors.ToList().IndexOf(row);SectorSelection.Clear();foreach(var r in Sectors.Skip(Math.Min(a,b)).Take(Math.Abs(a-b)+1))SectorSelection.Add(r.Sector.Address);}
        else {if(!toggle)SectorSelection.Clear();if(!SectorSelection.Add(row.Sector.Address))SectorSelection.Remove(row.Sector.Address);}
        selectedFinding=null;SelectedSector=SectorSelection.Count==0?null:SectorSelection.Contains(row.Sector.Address)?row:Sectors.Last(s=>SectorSelection.Contains(s.Sector.Address));Changed(nameof(SectorSelection));Changed(nameof(Detail));
    }
    internal void Refresh()
    {
        string? key=selectedFile?.Key;var address=selectedSector?.Sector.Address;
        Layout=projection is null?null:DskAnalyzer.Analyze(Model);
        Snapshot=opaque is not null?new DskVisualSnapshot(opaque):Model.CurrentHfe() is { } hfe?new DskVisualSnapshot(hfe):new DskVisualSnapshot(Model.Image,Layout!);
        trackCache?.Dispose();trackCache=new(Snapshot);TrackData=null;CellStart=CellLength=0;
        files=projection?.FileSystem.ReadDirectory().Select(e=>new DiskFileRow(e)).ToArray()??[];
        Sectors=Snapshot.Sectors.Select(s=>new DiskSectorRow(s)).ToArray();SortSectors();
        selectedFile=files.FirstOrDefault(f=>f.Key==key);selectedSector=Sectors.FirstOrDefault(s=>s.Sector.Address==address);selectedFinding=null;
        SectorSelection.RemoveWhere(a=>!Sectors.Any(s=>s.Sector.Address==a));ResetDraft();
        foreach(var p in new[]{nameof(Files),nameof(Sectors),nameof(Findings),nameof(SelectedFile),nameof(SelectedSector),nameof(SelectedFinding),nameof(Summary),nameof(Capacity),nameof(TrackIndices),nameof(Detail),nameof(Report),nameof(Structure),nameof(CanEdit),nameof(CanEditHex),nameof(CanEditIpl),nameof(CanEditFileName),nameof(CanDeleteFiles),nameof(CanEditCpmProperties),nameof(CanEditAddresses),nameof(CanEditFsmzProperties),nameof(CanSave),nameof(SaveDisabledReason)})Changed(p);
    }
    private void SortSectors() => Sectors=(LogicalOrder?Sectors.OrderBy(s=>s.Track).ThenBy(s=>s.Sector.R):Sectors.OrderBy(s=>s.Track).ThenBy(s=>s.Descriptor)).ToArray();
    internal static ushort Hex16(string value) => ushort.Parse(value.Trim().StartsWith("0x",StringComparison.OrdinalIgnoreCase)?value.Trim()[2..]:value.Trim(),NumberStyles.HexNumber,CultureInfo.InvariantCulture);
    SaveChoice IFileSetDocument.CurrentSaveChoice(string path)=>new(path,IsHfe?"Hfe":"Dsk",false);
    async Task<DocumentSavePlan> IFileSetDocument.PrepareSaveAsync(SaveChoice choice,CancellationToken cancellation)
    {
        if(HasDraft)throw new InvalidOperationException("Apply or Cancel disk edits first.");
        if(!global::System.IO.Path.GetExtension(choice.Path).Equals(Extension,StringComparison.OrdinalIgnoreCase) || choice.Format is not null && choice.Format!=(IsHfe?"Hfe":"Dsk"))
            throw new InvalidDataException("Save preserves the current container. Use Convert/Export for a different format.");
        var bytes=await Task.Run(()=>SerializeContainer(),cancellation);
        if(IsHfe)HfeImage.Parse(bytes);else DskImage.Parse(bytes);
        foreach(var source in hashes)if(AtomicFileTransaction.Hash(source.Key)!=source.Value)throw new IOException("The disk source changed. Open it again before saving.");
        bool repair=RequiresRepairSaveAs;var candidate=projection?.Clone();
        void VerifySources()
        {
            foreach(var source in hashes)if(AtomicFileTransaction.Hash(source.Key)!=source.Value)throw new IOException("The disk source changed during save preparation.");
            if(repair){using var guard=Model.ProtectRepairDestination(choice.Path);}
        }
        return new(new Dictionary<string,byte[]> { [choice.Path]=bytes },"",unchanged=>
        {
            if(!unchanged)throw new InvalidOperationException("The disk changed while saving.");
            if(candidate is not null){candidate.CommitSaved(choice.Path,bytes);projection=candidate;}else opaque=HfeImage.Parse(bytes);
            hashes=new(AtomicFileTransaction.PathComparer){[choice.Path]=ImageVerificationService.Hash(bytes)};Refresh();
        },repair,repair?()=>Model.ProtectRepairDestination(choice.Path)??(Model.FilePath is null ? new EmptyLease() : throw new IOException("Repair source protection is unavailable.")):null,VerifySources);
    }
    private sealed class EmptyLease : IDisposable { public void Dispose() { } }
    internal byte[] SerializeContainer()=>opaque?.Serialize(opaque.Version)??Model.SerializeContainer();
    public override Task WriteAsync(Stream destination,CancellationToken cancellationToken)=>throw new NotSupportedException("Disk documents require a verified file-set save plan.");
    public override Task ValidateSavedAsync(string path,CancellationToken cancellationToken)=>throw new NotSupportedException("Validation belongs to the disk save plan.");
}

public sealed class DiskDiffRow
{
    internal DskDiffItem Item { get; }
    internal DiskDiffRow(DskDiffItem item)=>Item=item;
    public string Level=>Item.Level.ToString();
    public string Name=>Item.Name;
    public string State=>Item.State.ToString();
    public string Detail=>Item.Detail;
}
