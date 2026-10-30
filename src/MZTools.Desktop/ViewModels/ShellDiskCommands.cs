using System.Text.Json;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public DiskSession? ActiveDisk => Documents.Active as DiskSession;
    public bool ShowDisk => ActiveDisk is not null && SelectedModule?.Name=="Disks / HFE";
    public bool ShowReadiness => !ShowDisk && !ShowMedia && !ShowAudio && !ShowBatch;
    public bool CanUseDisk => ActiveDisk is not null && !Documents.IsBusy;
    public AsyncCommand NewDiskCommand { get; private set; }=null!;
    public AsyncCommand DiskImportCommand { get; private set; }=null!;
    public AsyncCommand DiskExportCommand { get; private set; }=null!;
    public AsyncCommand DiskDeleteCommand { get; private set; }=null!;
    public AsyncCommand DiskApplyPropertiesCommand { get; private set; }=null!;
    public AsyncCommand DiskApplyHexCommand { get; private set; }=null!;
    public AsyncCommand DiskCancelDraftCommand { get; private set; }=null!;
    public AsyncCommand DiskCopyCommand { get; private set; }=null!;
    public AsyncCommand DiskPasteCommand { get; private set; }=null!;
    public AsyncCommand DiskLoadTrackCommand { get; private set; }=null!;
    public AsyncCommand DiskToolCommand { get; private set; }=null!;
    public IReadOnlyList<string> DiskTools { get; } = ["Container repair","Normalize container","Filesystem repair","Defragment","Format disk","Compare image","Convert filesystem","Export converted container","Physical properties","Open CP/M block","Raw export","Raw import","Export patch","Apply patch","Bootstrap export","Bootstrap import","Bootstrap header export","Bootstrap header import","Bootstrap clear","Bootstrap metadata","Install boot/system","CP/M layout","Manage profiles","IPL add programs","IPL rename / compression","IPL move up","IPL move down","IPL delete programs","Import Native COM","Copy to Tape"];
    private string diskTool="Container repair";
    public string DiskTool { get=>diskTool;set=>Set(ref diskTool,value); }
    internal IDiskDialogs? DiskDialogs { get; private set; }
    private Func<byte[],Task>? diskCopy;
    private Func<Task<byte[]?>>? diskRead;
    private void InitializeDisk(IDiskDialogs? dialogs,Func<byte[],Task>? copy,Func<Task<byte[]?>>? read)
    {
        DiskDialogs=dialogs;diskCopy=copy;diskRead=read;RepairProtectionFactory.EnsureConfigured();
        AsyncCommand Op(Func<DiskSession,CancellationToken,Task<bool>> work,Func<bool>? allowed=null)=>new(async()=>{var disk=ActiveDisk;if(disk is not null)await Documents.ExecuteAsync(ct=>work(disk,ct));},()=>CanUseDisk&&(allowed?.Invoke()??true));
        NewDiskCommand=new(async()=>{if(dialogs is null)return;await Documents.ExecuteAsync(async ct=>
        {
            var options=await ChooseDiskOptionsAsync();if(options is null)return false;
            DiskSession session;
            if(options.Format is DskNewFormat.IplSingle or DskNewFormat.IplMulti)
            {
                var paths=await dialogs.PickDiskFilesAsync("Choose IPL programs (MZF/M12/MZT)",true);if(paths.Count==0)return false;
                var inputs=await Task.Run(()=>DiskOperations.ReadIplFiles(paths,ct),ct);
                var model=await Task.Run(()=>DiskFormatService.SetNewContainer(DiskOperations.BuildIpl(inputs,options.Format==DskNewFormat.IplSingle),options.HfeVersion),ct);
                if(!await dialogs.ConfirmDiskAsync("New IPL preview",$"Build {inputs.Length} programs in {options.Format}. Core validates memory ranges and disk capacity."))return false;
                session=new DiskSession(model);session.MakeNew();
            }
            else session=await Task.Run(()=>DiskSession.New(options),ct);
            Documents.Add(session);return true;
        });},()=>!Documents.IsBusy&&dialogs is not null);
        DiskImportCommand=Op(async(s,ct)=>{if(s.CanEditIpl)return await EditIplAsync(s,"IPL add programs",ct);var paths=await dialogs!.PickDiskFilesAsync("Import MZF or binary files",true);if(paths.Count==0)return false;return await AcceptDiskAsync(s,await Task.Run(()=>DiskOperations.Import(s,paths,ct),ct),ct);},()=>dialogs is not null&&(ActiveDisk?.CanEdit==true||ActiveDisk?.CanEditIpl==true)&&ActiveDisk.HasDraft==false);
        DiskDeleteCommand=Op(async(s,ct)=>s.CanEditIpl?await EditIplAsync(s,"IPL delete programs",ct):await AcceptDiskAsync(s,await Task.Run(()=>DiskOperations.Delete(s),ct),ct),()=>dialogs is not null&&(ActiveDisk?.CanDeleteFiles==true||ActiveDisk?.CanEditIpl==true)&&ActiveDisk.SelectedFile is not null&&!ActiveDisk.HasDraft);
        DiskExportCommand=Op(async(s,ct)=>
        {
            var a=await dialogs!.DiskFormAsync("Export files",[new("format","Format","Raw",["Raw","MZF"]),new("scope","Files","Selected",["Selected","All"])],"Multiple files use the chosen destination directory and Core-generated collision-safe names. MRS and CP/M exports may include final-block/record padding. The source binding stays unchanged.");if(a is null)return false;
            string? path=await dialogs.PickDiskOutputAsync("Export disk files",a["format"]=="MZF"?"program.mzf":"export.bin");if(path is null)return false;
            var outputs=await Task.Run(()=>DiskOperations.ExportFiles(s,path,a["format"]=="MZF",a["scope"]=="All"),ct);
            if(!await dialogs.ConfirmDiskAsync("Export preview",string.Join("\n",outputs.Select(p=>$"{p.Key}: {p.Value.Length:N0} B"))))return false;
            await Documents.WriteExportAsync(outputs,ct);return true;
        },()=>dialogs is not null&&ActiveDisk?.HasProjection==true&&!ActiveDisk.HasDraft);
        DiskApplyPropertiesCommand=Op(async(s,ct)=>
        {
            try{long revision=s.Revision;var candidate=await Task.Run(s.PrepareProperties,ct);if(!await dialogs!.ConfirmDiskAsync("File properties preview",new DskOperationPreview(s.Model.Serialize(),candidate.Serialize(),"Apply the edited filename and supported attributes.",s.Model).Report))return false;ct.ThrowIfCancellationRequested();s.ApplyProperties(candidate,revision);return true;}
            catch(Exception e) when(e is InvalidDataException or ArgumentException or FormatException or OverflowException){s.SetError(e.Message);return false;}
        },()=>dialogs is not null&&ActiveDisk?.CanEdit==true&&ActiveDisk.HasPropertiesDraft&&!ActiveDisk.HasHexDraft);
        DiskApplyHexCommand=Op(async(s,ct)=>
        {
            var preview=await Task.Run(s.PrepareHex,ct);long revision=s.Revision;
            if(!await dialogs!.ConfirmDiskAsync("Hex preview",preview.Report))return false;
            var candidate=s.Model.Clone();DskHexEditService.Apply(candidate,preview,true);ct.ThrowIfCancellationRequested();s.ApplyProperties(candidate,revision);return true;
        },()=>dialogs is not null&&ActiveDisk?.CanEditHex==true&&ActiveDisk.HasHexDraft&&!ActiveDisk.HasPropertiesDraft);
        DiskCancelDraftCommand=Op((s,_)=>{s.ResetDraft();return Task.FromResult(true);},()=>ActiveDisk?.HasDraft==true);
        DiskCopyCommand=Op(async(s,_)=>{await diskCopy!(WorkspaceDragCodec.Encode(DiskOperations.CapturePacket(s)));return true;},()=>copy is not null&&ActiveDisk?.HasProjection==true&&DiskOperations.CanCopy(ActiveDisk)&&ActiveDisk.SelectedFile is not null);
        DiskPasteCommand=Op(async(s,ct)=>await PasteDiskAsync(s,await diskRead!()??throw new InvalidDataException("Clipboard contains no MZTools files/records."),ct),()=>read is not null&&dialogs is not null&&ActiveDisk?.CanEdit==true&&!ActiveDisk.HasDraft);
        DiskLoadTrackCommand=Op(async(s,ct)=>{await s.LoadTrackAsync(ct);return true;});
        DiskToolCommand=Op((s,ct)=>RunDiskToolAsync(s,DiskTool,ct),()=>dialogs is not null&&ActiveDisk?.HasDraft==false);
    }
    internal async Task<bool> AcceptDiskAsync(DiskSession session,DiskPreview preview,CancellationToken ct)
    {
        if(!await DiskDialogs!.ConfirmDiskAsync("Disk operation preview",preview.Report))return false;
        ct.ThrowIfCancellationRequested();session.Apply(preview.Candidate,preview.Revision);return true;
    }
    internal async Task<bool> PasteDiskAsync(DiskSession session,byte[] packet,CancellationToken ct)
    {
        var transfer=WorkspaceDragCodec.Decode(packet);
        var source=transfer.Disk is null?null:WorkspaceDragCodec.OpenDisk(transfer);
        var keys=transfer.Disk is null?Enumerable.Range(0,transfer.Records.Length).Select(i=>i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToArray():transfer.Keys;
        var sourceNames=source is null?WorkspaceDragCodec.OpenRecords(transfer).Select(r=>SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname).Trim()).ToArray():keys.Select(k=>{var e=source.FileSystem.ReadDirectory().Single(e=>e.Key==k);return e.Name+(e.Extension.Length==0?"":"."+e.Extension);}).ToArray();
        var fields=new List<DiskField>{new("collision","Disk file collision policy","Cancel",Enum.GetNames<DiskCollisionPolicy>())};
        for(int i=0;i<keys.Length;i++)fields.Add(new("file"+i,$"Target name for {sourceNames[i]}",sourceNames[i]));
        var a=await DiskDialogs!.DiskFormAsync("Copy to disk",fields,"Review target names for the detected filesystem. Tape record duplicates are refused. Core previews metadata changes before Apply.");if(a is null)return false;
        var names=keys.Select((key,i)=>(key,value:a["file"+i])).ToDictionary(p=>p.key,p=>p.value);
        return await AcceptDiskAsync(session,await Task.Run(()=>DiskOperations.Paste(session,packet,Enum.Parse<DiskCollisionPolicy>(a["collision"]),names),ct),ct);
    }
    private void RefreshDiskCommands()
    {
        foreach(var c in new[]{NewDiskCommand,DiskImportCommand,DiskExportCommand,DiskDeleteCommand,DiskApplyPropertiesCommand,DiskApplyHexCommand,DiskCancelDraftCommand,DiskCopyCommand,DiskPasteCommand,DiskLoadTrackCommand,DiskToolCommand})c.Refresh();
        foreach(var p in new[]{nameof(ActiveDisk),nameof(ShowDisk),nameof(ShowReadiness),nameof(CanUseDisk)})Changed(p);
    }
    private async Task<DskNewOptions?> ChooseDiskOptionsAsync(DiskSession? target=null)
    {
        string? fixedContainer=target is null?null:target.IsHfe?"HFE "+target.PhysicalImage!.Version:"DSK";
        var a=await DiskDialogs!.DiskFormAsync("Disk format",[new("format","Filesystem/profile","MzBasic",Enum.GetNames<DskNewFormat>()),new("container","Container",fixedContainer??"DSK",fixedContainer is null?["DSK","HFE V1","HFE V2","HFE V3"]:[fixedContainer]),new("tracks","Cylinders (profile-dependent)","40"),new("sides","Sides","2"),new("sectors","Raw sectors per track","16"),new("size","Raw sector size in bytes","256"),new("filler","Raw filler (hex)","FF"),new("order","Raw sector order","Normal",Enum.GetNames<DskDocumentFactory.RawSectorOrder>()),new("ids","Custom raw sector IDs (decimal, comma-separated)","")],"Personal CP/M and SDS profiles have fixed geometry. Other profiles use the entered geometry and Core validation. Typical LEC/MRS: 80 cylinders, 2 sides. IPL creation asks for programs; IPL cannot be used to format an existing disk.");
        if(a is null)return null;
        return new(Enum.Parse<DskNewFormat>(a["format"]),a.Number("tracks"),a.Number("sides"),a.Number("sectors"),a.Number("size"),checked((byte)DiskSession.Hex16(a["filler"])),Enum.Parse<DskDocumentFactory.RawSectorOrder>(a["order"]),string.IsNullOrWhiteSpace(a["ids"])?null:a["ids"].Split(',').Select(int.Parse).ToArray(),a["container"]=="DSK"?null:Enum.Parse<HfeVersion>(a["container"].Replace("HFE ","")));
    }
    private async Task<bool> WriteDiskExportAsync(string title,string name,byte[] bytes,string report,CancellationToken ct)
    {
        if(!await DiskDialogs!.ConfirmDiskAsync(title,report+$"\nOutput: {bytes.Length:N0} B. Export does not change document binding."))return false;
        var path=await DiskDialogs.PickDiskOutputAsync(title,name);if(path is null)return false;
        await Documents.WriteExportAsync(new Dictionary<string,byte[]>{[path]=bytes},ct);return true;
    }
    private async Task<string?> InputFileAsync(string title)
    {var paths=await DiskDialogs!.PickDiskFilesAsync(title);return paths.FirstOrDefault();}
    internal async Task<bool> RunDiskToolAsync(DiskSession s,string tool,CancellationToken ct)
    {
        var d=DiskDialogs??throw new InvalidOperationException("Disk dialogs are unavailable.");
        if(s.HasDraft)throw new InvalidOperationException("Apply or Cancel disk edits before another operation.");
        switch(tool)
        {
            case "Container repair":
            {
                if(s.IsHfe)throw new InvalidOperationException("DSK container repair does not apply to HFE.");
                var plans=DskOpenCoordinator.Inspect(s.Model.Serialize(),s.Path).ManualPlans.Where(p=>p.Preview is not null).ToArray();
                if(plans.Length==0)throw new InvalidOperationException("No deterministic container repair is available.");
                var labels=plans.Select((p,i)=>$"{i+1}: {p.Description}").ToArray();
                var a=await d.DiskFormAsync(tool,[new("plan","Repair plan",labels[0],labels)],"Container repair is separate from filesystem repair. The original is protected and a new Save As destination is required.");if(a is null)return false;
                var plan=plans[Array.IndexOf(labels,a["plan"])];
                if(plan.RequiresTruncateConfirmation&&!await d.ConfirmDiskAsync("Destructive trailing-data removal",plan.Description+"\nConfirm permanent removal of the identified trailing data from the copy."))return false;
                var preview=DiskOperations.Preview(s,c=>c.ApplyContainerRepair(plan,plan.RequiresTruncateConfirmation),plan.Preview!.Report,true);
                return await AcceptDiskAsync(s,preview,ct);
            }
            case "Normalize container":
                if(s.IsHfe)throw new InvalidOperationException("DSK creator normalization does not apply to HFE.");
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>ContainerNormalizeService.Preview(c).Apply(c),ContainerNormalizeService.Preview(s.Model).Report,true),ct);
            case "Filesystem repair":
            {
                var action=DskFilesystemRepairService.For(s.Model);var repair=await Task.Run(()=>action.Preview(s.Model),ct);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>action.Apply(c,repair),repair.Report,true),ct);
            }
            case "Defragment":
            {
                var preview=await Task.Run(()=>DskDefragmentService.Preview(s.Model),ct);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>preview.Apply(c),preview.Report),ct);
            }
            case "Format disk":
            {
                var options=await ChooseDiskOptionsAsync(s);if(options is null)return false;
                if(options.Format is DskNewFormat.IplSingle or DskNewFormat.IplMulti)throw new InvalidDataException("Create a separate IPL document with New disk.");
                if(s.IsReadOnly || s.Model.SourceHfe?.WriteAllowed==false)throw new InvalidOperationException("This image is write-protected.");
                long revision=s.Revision;
                var replacement=await Task.Run(()=>s.Model.Reformatted(DiskFormatService.Create(options)),ct);
                var report=new DskOperationPreview(s.Model.Serialize(),replacement.Serialize(),"FORMAT replaces every track and filesystem. Existing files are removed. HFE tracks are regenerated.",s.Model).Report;
                return await AcceptDiskAsync(s,new(replacement,revision,report),ct);
            }
            case "Compare image":
            {
                var path=await InputFileAsync(tool);if(path is null)return false;byte[] right=await File.ReadAllBytesAsync(path,ct);
                var report=await Task.Run(()=>DskCompareService.CompareMedia(s.SerializeContainer(),right),ct);
                DskComparison? comparison=null;
                if(s.HasProjection){try{comparison=DskCompareService.Compare(s.Model,DskDocument.Open(right));}catch(InvalidDataException){/* Physical comparison report remains available without a projection. */}}
                s.SetComparison(comparison,report);s.ViewIndex=5;return true;
            }
            case "Convert filesystem":
            {
                var profiles=DskCapabilityService.GetAvailableConversions(s.Model);if(profiles.Count==0)throw new InvalidDataException("No compatible filesystem conversion is available.");
                var labels=profiles.Select(p=>p.ToString()).ToArray();var a=await d.DiskFormAsync(tool,[new("profile","Target",labels[0],labels)]);if(a is null)return false;
                var result=await Task.Run(()=>DskFileTransferService.Preflight(s.Model,profiles[Array.IndexOf(labels,a["profile"])]),ct);if(!result.CanConvert)throw new InvalidDataException(result.Report);
                return await WriteDiskExportAsync(tool,"converted.dsk",result.OutputImage!,result.Report,ct);
            }
            case "Export converted container":
            {
                var labels=s.IsHfe?new[]{"DSK","HFE V1","HFE V2","HFE V3"}:new[]{"HFE V1","HFE V2","HFE V3"};
                var a=await d.DiskFormAsync(tool,[new("format","Target container",labels[0],labels)]);if(a is null)return false;
                MediaConversionPreview result=await Task.Run(()=>DiskOperations.ConvertContainer(s,a["format"]),ct);
                return await WriteDiskExportAsync(tool,a["format"]=="DSK"?"converted.dsk":"converted.hfe",result.Output,result.Report,ct);
            }
            case "Open CP/M block":
            {
                var a=await d.DiskFormAsync(tool,[new("block","Allocation block (decimal)","0")]);if(a is null)return false;s.OpenBlock(a.Number("block"));return true;
            }
            case "Raw export":case "Raw import":
            {
                if(s.SectorSelection.Count==0&&s.SelectedSector is null)throw new InvalidOperationException("Select sectors in the map first.");
                var addresses=s.SectorSelection.Count==0?new[]{s.SelectedSector!.Sector.Address}:s.SectorSelection.ToArray();
                var a=await d.DiskFormAsync(tool,[new("order","Range order","Physical",Enum.GetNames<DskRawOrder>())],$"{addresses.Length} selected sectors. Raw data uses exact Core ranges and order.");if(a is null)return false;
                var order=Enum.Parse<DskRawOrder>(a["order"]);
                if(tool=="Raw export")return await WriteDiskExportAsync(tool,"sectors.bin",DskRawRangeService.Export(s.Model,addresses,order),"Export selected decoded/stored sector bytes.",ct);
                var path=await InputFileAsync(tool);if(path is null)return false;byte[] data=await File.ReadAllBytesAsync(path,ct);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskHexEditService.Apply(c,DskRawRangeService.PreviewImport(c,addresses,order,data),true),"Replace the selected raw sector range. Core validates exact length and filesystem impact.",true),ct);
            }
            case "Apply patch":
            {
                var path=await InputFileAsync(tool);if(path is null)return false;var patch=DskPatchService.Read(path);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskPatchService.Apply(c,DskPatchService.Preview(c,patch)),"Apply the source-hash-bound patch.",true),ct);
            }
            case "Export patch":
            {
                var path=await InputFileAsync("Choose baseline DSK for patch");if(path is null)return false;var patch=DskPatchService.Export(await File.ReadAllBytesAsync(path,ct),s.Model.Serialize());
                return await WriteDiskExportAsync(tool,"changes.json",System.Text.Encoding.UTF8.GetBytes(DskPatchService.ToJson(patch)),"Export baseline-to-current patch, including source and target hashes.",ct);
            }
            case "Physical properties":return await EditPhysicalAsync(s,ct);
            case "Bootstrap header export":return await WriteDiskExportAsync(tool,"bootstrap-header.bin",BootstrapService.Export(s.Model),"Export the 256 decoded native bootstrap header bytes.",ct);
            case "Bootstrap header import":
            {
                var path=await InputFileAsync(tool);if(path is null)return false;var data=await File.ReadAllBytesAsync(path,ct);
                var preview=BootstrapService.PreviewHeader(s.Model,data);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskHexEditService.Apply(c,preview,true),preview.Report,true),ct);
            }
            case "Bootstrap clear":
            {
                var preview=BootstrapService.PreviewBootstrap(s.Model,new byte[4096],true);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskHexEditService.Apply(c,preview,true),preview.Report,true),ct);
            }
            case "Bootstrap export":return await WriteDiskExportAsync(tool,"bootstrap.bin",BootstrapService.ExportBootstrap(s.Model),"Export the decoded native bootstrap track.",ct);
            case "Bootstrap import":
            {
                var path=await InputFileAsync(tool);if(path is null)return false;var data=await File.ReadAllBytesAsync(path,ct);
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskHexEditService.Apply(c,BootstrapService.PreviewBootstrap(c,data),true),"Replace the validated native bootstrap track.",true),ct);
            }
            case "Bootstrap metadata":
            {
                var m=BootstrapService.Inspect(s.Model);var a=await d.DiskFormAsync(tool,[new("name","Name",m.Name),new("type","Type (hex)",$"{m.FileType:X2}"),new("size","Size (hex)",$"{m.Size:X4}"),new("load","Load (hex)",$"{m.Load:X4}"),new("exec","Execute (hex)",$"{m.Execute:X4}")]);if(a is null)return false;
                var metadata=new BootstrapMetadata(a["name"],checked((byte)DiskSession.Hex16(a["type"])),DiskSession.Hex16(a["size"]),DiskSession.Hex16(a["load"]),DiskSession.Hex16(a["exec"]));
                return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>DskHexEditService.Apply(c,BootstrapService.PreviewMetadata(c,metadata),true),"Update native bootstrap metadata.",true),ct);
            }
            case "Install boot/system":return await InstallBootAsync(s,ct);
            case "CP/M layout":return await EditLayoutAsync(s,ct);
            case "Manage profiles":return await ManageProfilesAsync(s,ct);
            case "IPL add programs":case "IPL rename / compression":case "IPL move up":case "IPL move down":case "IPL delete programs":return await EditIplAsync(s,tool,ct);
            case "Import Native COM":return await ImportDiskComAsync(s,ct);
            case "Copy to Tape":
            {
                if(s.Model.FileSystem is CpmFileSystem or MrsFileSystem or RawDskFileSystem)throw new InvalidDataException("Native tape metadata is unavailable for these files. Export raw payloads instead.");
                var rows=DiskOperations.Selected(s);var records=new List<TapeRecord>();
                foreach(var row in rows)
                {var output=DiskOperations.ExportFilesForRecord(s,row);using var reader=new BinaryReader(new MemoryStream(output));records.Add(new MZTFileReader().ReadMzfRecord(reader));}
                if(!await d.ConfirmDiskAsync(tool,$"Create a new tape with {records.Count} selected native records. Source disk remains unchanged."))return false;
                var tape=TapeSession.New(false);tape.ApplyRecords(records);Documents.Add(tape);return true;
            }
            default:throw new InvalidOperationException("Unknown disk action.");
        }
    }
}
