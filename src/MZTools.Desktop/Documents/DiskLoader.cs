using MZTools.Desktop.Platform;

namespace MZTools.Desktop.Documents;

internal static class DiskLoader
{
    internal static async Task<DocumentSession> LoadAsync(string path,IDiskDialogs? dialogs,CancellationToken ct)
    {
        RepairProtectionFactory.EnsureConfigured();
        byte[] source=await File.ReadAllBytesAsync(path,ct);
        var hashes=new Dictionary<string,string?>(AtomicFileTransaction.PathComparer){[path]=ImageVerificationService.Hash(source)};
        if(new HfeImage().Identify(source).Matches)
        {
            var image=await Task.Run(()=>HfeImage.Parse(source),ct);
            try {return await Task.Run<DocumentSession>(()=>new DiskSession(DskDocument.OpenHfe(image,path),hashes),ct);}
            catch(InvalidDataException){return new DiskSession(image,hashes);}
        }
        var inspection=await Task.Run(()=>DskOpenCoordinator.Inspect(source,path),ct);
        if(inspection.NeedsRepairPrompt)
        {
            if(dialogs is null)throw new InvalidDataException("Container repair is available. An explicit repair decision is required.");
            var labels=inspection.Plans.Select((p,i)=>$"{i+1}: {p.Description}").ToList();
            if(inspection.Original is not null)labels.Add("Open original unchanged");
            var answer=await dialogs.DiskFormAsync("DSK container inspection",[new("plan","Open decision",labels[0],labels)],inspection.Diagnostic+"\nRepair modifies a memory copy. Save As to a new path is required. Cancel keeps existing tabs.");
            if(answer is null)throw new OperationCanceledException();
            if(answer["plan"]!="Open original unchanged")
            {
                var plan=inspection.Plans[labels.IndexOf(answer["plan"])];
                byte[] current=await File.ReadAllBytesAsync(path,ct);
                var repaired=await Task.Run(()=>inspection.Apply(plan,current),ct);
                return new DiskSession(repaired,hashes);
            }
        }
        if(inspection.Original is null)throw new InvalidDataException(inspection.Diagnostic);
        return new DiskSession(inspection.Original,hashes);
    }
}
