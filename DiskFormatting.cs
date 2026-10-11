using System;
using System.Linq;
using System.Windows;

namespace MZTools;

internal static class DiskFormatting
{
    internal static DskDocument CreateNew(DskNewOptions options) => SetNewContainer(Create(options),options.HfeVersion);
    internal static DskDocument SetNewContainer(DskDocument document,HfeVersion? version)
    {
        if(version==null) return document;
        var image=HfeImage.FromDsk(document,uniformTiming:version!=HfeVersion.V3);
        return DskDocument.FromFormattedHfe(document,HfeImage.Parse(image.Serialize(version.Value)),null);
    }
    internal static DskDocument Create(DskNewOptions options)=>options.Format switch {
        DskNewFormat.MzBasic=>DskDocumentFactory.CreateFsmz(false,options.Tracks,options.Sides),
        DskNewFormat.IplDisk=>DskDocumentFactory.CreateFsmz(true,options.Tracks,options.Sides),
        DskNewFormat.PersonalCpm=>DskDocumentFactory.CreatePersonalCpm80(false),
        DskNewFormat.Sds400=>DskDocumentFactory.CreatePersonalCpm80(true),
        DskNewFormat.LecCpmDd=>DskDocumentFactory.CreateCpm(false,options.Tracks,options.Sides),
        DskNewFormat.LecCpmHd=>DskDocumentFactory.CreateCpm(true,options.Tracks,options.Sides),
        DskNewFormat.Mrs=>DskDocumentFactory.CreateMrs(options.Tracks,options.Sides),
        DskNewFormat.Lemmings=>DskDocumentFactory.CreateLemmings(options.Tracks,options.Sides),
        DskNewFormat.CustomRaw=>DskDocumentFactory.CreateRaw(options.Tracks,options.Sides,options.Sectors,options.SectorSize,1,options.SectorSize==256?(byte)0x2A:(byte)0x4E,options.Filler,"MZTools",options.SectorOrder,options.SectorIds),
        _=>throw new InvalidOperationException("Choose a filesystem or raw geometry for formatting.")
    };
    internal static void Show(Window owner,DskDocument? current,HfeImage? physical,string? path,Action<DskDocument> apply)
    {
        var options=DskNewDialog.ShowFormatting(owner,physical?.Cylinders??current!.Image.TrackCount,physical?.Sides??current!.Image.SideCount);
        if(options==null) return;
        try {
            var fresh=Create(options);
            if(current!=null) fresh=current.Reformatted(fresh);
            else if(physical!=null) {
                var encoded=HfeImage.FromDsk(fresh,physical.Version!=HfeVersion.V3).Serialize(physical.Version);
                fresh=DskDocument.FromFormattedHfe(fresh,HfeImage.Parse(encoded),path);
            }
            // Validate the complete intended output before the user approves replacement.
            byte[] output=fresh.SerializeContainer();
            string report=$"Format disk — replace entire contents\nContainer: {fresh.ContainerName}\nFilesystem: {fresh.FileSystem.DisplayName}\nGeometry: {fresh.Image.TrackCount} cylinders × {fresh.Image.SideCount} sides\nSector bytes: {fresh.Image.TotalSectorDataBytes}\nOutput bytes: {output.Length}\nExisting files, boot/system and physical layout will be erased.\n"+
                (fresh.SourceHfe is {} hfe?$"Generated timing: {string.Join(", ",hfe.Tracks.Select(t=>t.BitRate).Distinct())} bit/s; 300 RPM\nHFE cells will be newly generated MFM with valid CRC; original gaps, weak cells, index and timing are replaced.\n":"")+
                "Apply changes the in-memory document only. Save / Save As writes the image. Filesystem formatting does not install an operating system; use Boot / System afterward.";
            new DskPatchPreviewWindow(owner,report,()=> {
                if(MessageBox.Show(owner,"Erase all existing files, boot/system and disk layout in the current document?","Confirm formatting",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) return;
                apply(fresh);
            },"Format disk preview","Format current document").ShowDialog();
        } catch(Exception ex) {MessageBox.Show(owner,ex.Message,"Format disk",MessageBoxButton.OK,MessageBoxImage.Error);}
    }
}
