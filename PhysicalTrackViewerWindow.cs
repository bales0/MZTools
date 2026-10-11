using Microsoft.Win32;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

// HFE uses the same surface, multi-selection, floating details and lower cell view as DSK.
internal sealed class PhysicalTrackViewerWindow
{
    private readonly DskVisualInspectorWindow inspector;
    private readonly HfeDocumentSession session;
    internal bool? ShowDialog()=>inspector.ShowDialog();
    internal void Close()=>inspector.Close();
    internal void SaveAs(string destination)
    {
        session.SaveAs(destination);
        ShowActivePath();
    }
    private void ShowActivePath() => inspector.Title=$"Visual {session.Image.Variant} Inspector — read-only — {session.FilePath ?? "unsaved image"}";
    internal PhysicalTrackViewerWindow(Window owner,HfeImage image,string? path=null,Action<DskDocument>? openFormatted=null)
    {
        session=new HfeDocumentSession(image,path);
        var snapshot=new DskVisualSnapshot(image);
        inspector=new(owner,snapshot,(_,_)=>true,address=> {
            var sector=snapshot.Sectors.First(s=>s.Address==address);
            var browser=new HexBrowser {Owner=inspector}; browser.ShowRawData($"{image.Variant} R{sector.R} — decoded sector",sector.Data); browser.Show();
        },_=>{});
        inspector.AddFileList();
        ShowActivePath();
        var menu=new ContextMenu(); inspector.AddCommands(menu);
        var saveAs=new MenuItem {Header="Save As…"};menu.Items.Add(saveAs);
        saveAs.Click+=(_,_)=> {
            var save=new SaveFileDialog {Title="Save As — switch active HFE image",Filter="HFE physical image|*.hfe",DefaultExt=".hfe",AddExtension=true,
                FileName=Path.GetFileName(session.FilePath)??"disk.hfe",InitialDirectory=session.FilePath==null?"":Path.GetDirectoryName(Path.GetFullPath(session.FilePath))};
            if(save.ShowDialog(inspector)!=true) return;
            try {SaveAs(save.FileName);}
            catch(Exception ex) {MessageBox.Show(inspector,ex.Message,"Save As",MessageBoxButton.OK,MessageBoxImage.Error);}
        };
        var formatDisk=new MenuItem {Header="Format disk…",IsEnabled=openFormatted!=null};menu.Items.Add(formatDisk);
        formatDisk.Click+=(_,_)=>DiskFormatting.Show(inspector,null,session.Image,session.FilePath,formatted=> {inspector.Close();openFormatted!(formatted);});
        var compare=new MenuItem {Header="Compare with…"}; menu.Items.Add(compare);
        compare.Click+=(_,_)=> {
            var picker=new OpenFileDialog {Filter="Disk images|*.hfe;*.dsk"}; if(picker.ShowDialog(inspector)!=true) return;
            try {new DskPatchPreviewWindow(inspector,DskCompareService.CompareMedia(session.Image.Serialize(session.Image.Version),File.ReadAllBytes(picker.FileName)),title:"Physical / decoded compare").ShowDialog();}
            catch(Exception e) {MessageBox.Show(inspector,e.Message,inspector.Title);}
        };
        foreach(string format in new[] {"HFE","HFEv2","HFEv3","DSK"}) {
            var command=new MenuItem {Header=$"Export converted image — {format}…"}; menu.Items.Add(command);
            command.Click+=(_,_)=> {
                try {
                    var version=format=="HFEv3"?HfeVersion.V3:format=="HFEv2"?HfeVersion.V2:HfeVersion.V1;
                    var preview=format=="DSK"?MediaConversionService.HfeToDsk(session.Image):new MediaConversionPreview(session.Image.Serialize(version),"Stored cells, weak masks, timing and index verified. Original variant preserves all container bytes. Unsupported physical changes are refused.",false);
                    new DskPatchPreviewWindow(inspector,preview.Report,()=> {
                        if(preview.IsLossy && MessageBox.Show(inspector,"Accept the physical information losses listed in this preview?","Lossy conversion",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes) throw new InvalidOperationException("Conversion cancelled.");
                        var save=new SaveFileDialog {Title="Export converted image — active image remains unchanged",Filter=format=="DSK"?"Extended DSK|*.dsk":"HFE|*.hfe",FileName="converted"};
                        if(save.ShowDialog(inspector)!=true) return;
                        MediaConversionService.WriteVerified(save.FileName,preview.Output,bytes=> {if(format=="DSK") DskImage.Parse(bytes);else HfeImage.Parse(bytes);},session.FilePath);
                    },"Export converted image — current image remains open","Export converted image…").ShowDialog();
                } catch(Exception e) {MessageBox.Show(inspector,e.Message,inspector.Title,MessageBoxButton.OK,MessageBoxImage.Error);}
            };
        }
    }
}
