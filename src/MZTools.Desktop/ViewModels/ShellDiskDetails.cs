using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    internal string? DiskProfileDirectory {get;set;}
    private async Task<bool> EditPhysicalAsync(DiskSession s,CancellationToken ct)
    {
        int index=s.SelectedSector?.Track??0;var track=s.Model.Image.Tracks[index]??throw new InvalidOperationException("Select a present track.");
        var a=await DiskDialogs!.DiskFormAsync("Physical track properties",[new("creator","DSK creator",s.Model.Image.Creator),new("c","Cylinder (hex)",$"{track.Cylinder:X2}"),new("h","Side (hex)",$"{track.Side:X2}"),new("gap","Gap (hex)",$"{track.Gap:X2}"),new("filler","Filler (hex)",$"{track.Filler:X2}"),new("sectors","Descriptor order: source index (decimal), R, ST1, ST2 (hex)",string.Join("\n",track.Sectors.Select((x,i)=>$"{i},{x.SectorId:X2},{x.FdcStatus1:X2},{x.FdcStatus2:X2}")),Multiline:true)],$"Physical track {index}. Every source descriptor must occur exactly once. CHRN/status edits are not proof of recovered physical data.");
        if(a is null)return false;
        var sectors=a["sectors"].Split(['\r','\n'],StringSplitOptions.RemoveEmptyEntries).Select(line=>{var x=line.Split(',');if(x.Length!=4)throw new FormatException("Each descriptor row needs index,R,ST1,ST2.");return new DskSectorProperties(int.Parse(x[0]),checked((byte)DiskSession.Hex16(x[1])),checked((byte)DiskSession.Hex16(x[2])),checked((byte)DiskSession.Hex16(x[3])));}).ToArray();
        var props=new DskTrackProperties(index,checked((byte)DiskSession.Hex16(a["c"])),checked((byte)DiskSession.Hex16(a["h"])),checked((byte)DiskSession.Hex16(a["gap"])),checked((byte)DiskSession.Hex16(a["filler"])),sectors);
        return await AcceptDiskAsync(s,await Task.Run(()=>DiskOperations.Preview(s,c=>DskPhysicalPropertyService.Preview(c,a["creator"],props).Apply(c),"Update physical descriptor properties.",true),ct),ct);
    }
    private async Task<bool> InstallBootAsync(DiskSession s,CancellationToken ct)
    {
        var bundled=BundledBootSystems.All.Where(b=>DskCapabilityService.CanInstallBootSystem(s.Model,b.Open()).IsCompatible).ToArray();
        var user=UserProfileService.BootProfiles(DiskProfileDirectory);
        var labels=new[]{"Choose source image…"}.Concat(bundled.Select(b=>b.DisplayName)).Concat(user.Select(p=>"User: "+p.Name)).ToArray();
        var a=await DiskDialogs!.DiskFormAsync("Install boot/system",[new("source","Verified source",labels[0],labels)],DskCapabilityService.BootSystemAvailabilityReason(s.Model));if(a is null)return false;
        int choice=Array.IndexOf(labels,a["source"]);DskDocument source;
        if(choice==0){var path=await InputFileAsync("Choose compatible boot/system image");if(path is null)return false;source=await Task.Run(()=>DskDocument.Open(path),ct);}
        else source=choice<=bundled.Length?bundled[choice-1].Open():UserProfileService.ResolveBootSource(user[choice-bundled.Length-1]);
        var compatibility=DskCapabilityService.CanInstallBootSystem(s.Model,source);if(!compatibility.IsCompatible)throw new InvalidDataException(compatibility.Reason);
        string report="";var preview=await Task.Run(()=>DiskOperations.Preview(s,c=>report=DskBootSystemService.InstallWithReport(c,source).ToText(),"Install compatible boot/system areas. Ordinary target files must be preserved."),ct);
        return await AcceptDiskAsync(s,preview with{Report=report+"\n"+preview.Report},ct);
    }
    private async Task<bool> EditLayoutAsync(DiskSession s,CancellationToken ct)
    {
        var profiles=UserProfileService.Layouts(DiskProfileDirectory);var labels=profiles.Select((p,i)=>$"{i+1}: {p.Name}").ToArray();
        var a=await DiskDialogs!.DiskFormAsync("CP/M layout",[new("profile","Base profile",labels[0],labels)],"Attach a validated interpretation. This does not format or rewrite the image.");if(a is null)return false;
        var profile=profiles[Array.IndexOf(labels,a["profile"])];var dpb=profile.Dpb;
        var fields=new List<DiskField>{new("name","Layout name",dpb.Name)};
        foreach(var item in new(string Key,int Value)[]{("Spt",dpb.Spt),("Bsh",dpb.Bsh),("Blm",dpb.Blm),("Exm",dpb.Exm),("Dsm",dpb.Dsm),("Drm",dpb.Drm),("Al0",dpb.Al0),("Al1",dpb.Al1),("Cks",dpb.Cks),("Off",dpb.Off),("BlockSize",dpb.BlockSize)})fields.Add(new(item.Key,item.Key+" (decimal)",item.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        fields.Add(new("inverted","Inverted bytes",dpb.Inverted.ToString(),["False","True"]));
        fields.Add(new("tracks","Physical track map (decimal, comma-separated; blank = linear)",string.Join(',',dpb.PhysicalTrackMap??[]),Multiline:true));
        fields.Add(new("sectors","Physical sector map (track:sector, comma-separated)",string.Join(',',(dpb.PhysicalSectorMap??[]).Select(p=>$"{p.AbsoluteTrack}:{p.SectorId}")),Multiline:true));
        var edit=await DiskDialogs.DiskFormAsync("CP/M DPB parameters",fields,"All fields are validated against the current disk geometry, directory and allocation before Apply.");if(edit is null)return false;
        dpb=new(checked((ushort)edit.Number("Spt")),checked((byte)edit.Number("Bsh")),checked((byte)edit.Number("Blm")),checked((byte)edit.Number("Exm")),checked((ushort)edit.Number("Dsm")),checked((ushort)edit.Number("Drm")),checked((byte)edit.Number("Al0")),checked((byte)edit.Number("Al1")),checked((ushort)edit.Number("Cks")),checked((ushort)edit.Number("Off")),checked((ushort)edit.Number("BlockSize")),edit["name"],string.IsNullOrWhiteSpace(edit["tracks"])?null:edit["tracks"].Split(',').Select(int.Parse).ToArray(),string.IsNullOrWhiteSpace(edit["sectors"])?null:edit["sectors"].Split(',').Select(x=>{var p=x.Split(':');return new CpmSectorAddress(int.Parse(p[0]),int.Parse(p[1]));}).ToArray(),bool.Parse(edit["inverted"]));
        long revision=s.Revision;var preview=await Task.Run(()=>CpmLayoutService.Preview(s.Model,dpb),ct);
        if(!preview.CanApply)throw new InvalidDataException(preview.Report);
        if(!await DiskDialogs.ConfirmDiskAsync("CP/M layout preview",preview.Report))return false;
        var candidate=s.Model.Clone();CpmLayoutService.Apply(candidate,preview);ct.ThrowIfCancellationRequested();s.Apply(candidate,revision);return true;
    }
    private async Task<bool> ManageProfilesAsync(DiskSession s,CancellationToken ct)
    {
        var a=await DiskDialogs!.DiskFormAsync("Profiles",[new("action","Action","Save current CP/M layout",["Save current CP/M layout","Delete custom layout","Save current boot source","Delete boot profile"])],"Built-in profiles are read-only. User profiles are stored in the application's profile directory; source image bytes are never modified.");if(a is null)return false;
        if(a["action"]=="Save current CP/M layout")
        {
            var dpb=s.Model.AttachedDpb??(s.Model.FileSystem as CpmFileSystem)?.Dpb??throw new InvalidOperationException("Attach a valid CP/M layout first.");
            var n=await DiskDialogs.DiskFormAsync("Save layout profile",[new("name","Name",dpb.Name),new("description","Description","")]);if(n is null)return false;
            var profile=new CpmLayoutProfile(Guid.NewGuid().ToString("N"),n["name"],n["description"],dpb,DskGeometrySignature.From(s.Model.Image));
            if(!await DiskDialogs.ConfirmDiskAsync("Save layout profile",profile.Name+"\n"+UserProfileService.DefaultDirectory))return false;UserProfileService.Save(profile,DiskProfileDirectory);return true;
        }
        if(a["action"]=="Save current boot source")
        {
            var n=await DiskDialogs.DiskFormAsync("Save boot profile",[new("name","Name","My boot system"),new("description","Description","")]);if(n is null)return false;
            var profile=UserProfileService.CaptureBoot(s.Model,n["name"],n["description"]);
            if(!await DiskDialogs.ConfirmDiskAsync("Save boot profile",profile.Name+"\nSource path and SHA-256 are stored; no disk contents are copied."))return false;UserProfileService.Save(profile,DiskProfileDirectory);return true;
        }
        var layouts=UserProfileService.Layouts(DiskProfileDirectory).Where(p=>!p.BuiltIn).ToArray();var boots=UserProfileService.BootProfiles(DiskProfileDirectory);
        var labels=a["action"]=="Delete custom layout"?layouts.Select(p=>p.Name).ToArray():boots.Select(p=>p.Name).ToArray();
        if(labels.Length==0)throw new InvalidOperationException("No user profiles exist in this category.");
        var choice=await DiskDialogs.DiskFormAsync("Delete profile",[new("profile","Profile",labels[0],labels)]);if(choice is null)return false;
        if(!await DiskDialogs.ConfirmDiskAsync("Delete profile?",choice["profile"]+"\nOnly this profile registration will be removed; disk images stay unchanged."))return false;
        ct.ThrowIfCancellationRequested();int index=Array.IndexOf(labels,choice["profile"]);
        if(a["action"]=="Delete custom layout")UserProfileService.DeleteLayout(layouts[index],DiskProfileDirectory);else UserProfileService.DeleteBoot(boots[index],DiskProfileDirectory);return true;
    }
    private async Task<bool> EditIplAsync(DiskSession s,string tool,CancellationToken ct)
    {
        var inputs=DiskOperations.IplInputs(s).ToList();var directory=s.Model.FileSystem.ReadDirectory();
        var indices=DiskOperations.SelectedOrEmpty(s).Select(r=>directory.ToList().FindIndex(e=>e.Key==r.Key)).Where(i=>i>=0).Order().ToArray();
        if(tool=="IPL add programs")
        {
            var paths=await DiskDialogs!.PickDiskFilesAsync("Add IPL programs",true);if(paths.Count==0)return false;
            inputs.AddRange(await Task.Run(()=>DiskOperations.ReadIplFiles(paths,ct),ct));
        }
        else
        {
            if(indices.Length==0)throw new InvalidOperationException("Select IPL programs first.");
            if(tool=="IPL delete programs")foreach(int i in indices.Reverse())inputs.RemoveAt(i);
            else if(tool is "IPL move up" or "IPL move down")ListReorder.MoveItems(inputs,indices,tool=="IPL move up"?indices.Min()-1:indices.Max()+2);
            else
            {
                var a=await DiskDialogs!.DiskFormAsync("IPL program options",[new("name","Menu name (one selected program only)",indices.Length==1?inputs[indices[0]].DisplayName:""),new("mode","Compression","Keep",["Keep","None","Auto","Zx0","Zx7"]),new("direction","Direction","Forward",Enum.GetNames<CompressionDirection>()),new("quick","ZX0 quick","True",["False","True"]),new("embedded","ZX7 embedded loader","False",["False","True"]),new("skip","Uncompressed prefix bytes","0")],"Core validates IPL memory, loader and compression compatibility before Apply. Keep retains the current program bytes.");if(a is null)return false;
                foreach(int i in indices)
                {
                    var input=inputs[i];string name=indices.Length==1?a["name"]:input.DisplayName;
                    if(a["mode"]=="Keep")inputs[i]=input with{DisplayName=name};
                    else
                    {
                        var options=new MzfCompressionOptions(Enum.Parse<MzfCompressionAlgorithm>(a["mode"]),Enum.Parse<CompressionDirection>(a["direction"]),a["mode"]=="Zx0"&&bool.Parse(a["quick"]),a["mode"]=="Zx7"&&bool.Parse(a["embedded"]),a.Number("skip"));
                        var result=await MzfCompressionService.CompressAsync(input.Record,options,CompressionTarget.IplDsk,ct);
                        inputs[i]=new(result.Record,name,result.AppliedOptions,result.OriginalSize);
                    }
                }
            }
        }
        return await AcceptDiskAsync(s,await Task.Run(()=>DiskOperations.RebuildIpl(s,inputs),ct),ct);
    }
    private async Task<bool> ImportDiskComAsync(DiskSession s,CancellationToken ct)
    {
        if(NativeComImportService.UnavailableReason(s.Model) is { } reason)throw new InvalidOperationException(reason);
        var path=await InputFileAsync("Choose a native tape program for COM import");if(path is null)return false;
        var records=TapeDocumentIO.Read(path).Records;if(records.Count!=1)throw new InvalidDataException("Select a standalone single-record program.");
        var labels=CpmTargetProfile.BuiltIns.Select(p=>p.Name).ToArray();
        var a=await DiskDialogs!.DiskFormAsync("Native COM options",[new("target","CP/M target",labels[0],labels),new("mode","Machine mode",Enum.GetNames<NativeMachineMode>()[0],Enum.GetNames<NativeMachineMode>()),new("compression","Compression",NativeComCompressionChoice.Choices[0].Label,NativeComCompressionChoice.Choices.Select(c=>c.Label).ToArray()),new("name","COM filename",NativeComConversionService.SuggestName(SharpMzEncoding.ConvertMzfNameToASCIIString(records[0].Header.MzfFname))),new("collision","Collision policy","Cancel",Enum.GetNames<DiskCollisionPolicy>())]);if(a is null)return false;
        var record=records[0];var source=MzfNativeImageParser.Parse(record);
        var result=await Task.Run(()=>NativeComConversionService.Build(source,CpmTargetProfile.BuiltIns[Array.IndexOf(labels,a["target"])],Enum.Parse<NativeMachineMode>(a["mode"]),NativeComCompressionChoice.Choices.Single(c=>c.Label==a["compression"]).Options,cancellationToken:ct),ct);
        var preview=NativeComImportService.Preview(s.Model,a["name"],result,Enum.Parse<DiskCollisionPolicy>(a["collision"]));
        return await AcceptDiskAsync(s,DiskOperations.Preview(s,c=>preview.Operation.Apply(c),preview.Operation.Report),ct);
    }
}
