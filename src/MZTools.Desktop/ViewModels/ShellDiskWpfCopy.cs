using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    internal async Task<bool> CopyFromDiskAsync()
    {
        if(ActiveDisk is not{} target||DiskDialogs is null||!target.CanEdit||target.HasDraft)return false;
        return await Documents.ExecuteAsync(async ct=>
        {
            var paths=await DiskDialogs.PickDiskFilesAsync("Copy from another disk...");
            if(paths.Count==0)return false;
            string path=paths[0];string? fingerprint=AtomicFileTransaction.Hash(path);
            var source=await Task.Run(()=>DskDocument.Open(path),ct);
            if(!CrossDiskTransferService.Supports(source)||!CrossDiskTransferService.Supports(target.Model))
                throw new InvalidDataException("Copy requires consistent FSMZ, CP/M or MRS disks.");
            var directory=source.FileSystem.ReadDirectory();
            if(directory.Count==0)throw new InvalidDataException("The source disk is empty.");
            var labels=directory.Select(e=>$"{e.Key} | {e.Name}{(e.Extension.Length>0?"."+e.Extension:"")} · user {e.User} · {e.Size} B").ToArray();
            var options=await DiskDialogs.DiskFormAsync("Copy from another disk...",[
                new("files","Source files",System.Text.Json.JsonSerializer.Serialize(labels),labels,Multiple:true),
                new("collision","Name collisions",nameof(DiskCollisionPolicy.Cancel),Enum.GetNames<DiskCollisionPolicy>())
            ],"Source: "+path+"\nChanges apply to the current image in memory. Save writes the destination image.");
            if(options is null)return false;
            var chosen=System.Text.Json.JsonSerializer.Deserialize<string[]>(options["files"])??[];
            var selected=chosen.Select(label=>label.Split('|')[0].Trim()).ToHashSet(StringComparer.Ordinal);
            if(selected.Count==0)return false;
            var entries=directory.Where(e=>selected.Contains(e.Key)).ToArray();
            if(entries.Length!=selected.Count)throw new InvalidDataException("Select only files from the source directory.");
            if(AtomicFileTransaction.Hash(path)!=fingerprint)throw new IOException("Source disk changed before preview.");
            byte[] packet=WorkspaceDragCodec.Encode(WorkspaceDragCodec.Capture(source,entries));
            var preview=await Task.Run(()=>DiskOperations.Paste(target,packet,Enum.Parse<DiskCollisionPolicy>(options["collision"])),ct);
            if(!await DiskDialogs.ConfirmDiskAsync("Copy files preview",preview.Report))return false;
            ct.ThrowIfCancellationRequested();
            if(AtomicFileTransaction.Hash(path)!=fingerprint)throw new IOException("Source disk changed before Apply.");
            target.Apply(preview.Candidate,preview.Revision);return true;
        });
    }
}
