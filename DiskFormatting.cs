using System;
using System.Linq;
using System.Windows;

namespace MZTools;

internal static class DiskFormatting
{
    internal static DskDocument CreateNew(DskNewOptions options) => DiskFormatService.CreateNew(options);
    internal static DskDocument SetNewContainer(DskDocument document, HfeVersion? version) => DiskFormatService.SetNewContainer(document, version);
    internal static DskDocument Create(DskNewOptions options) => DiskFormatService.Create(options);
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
