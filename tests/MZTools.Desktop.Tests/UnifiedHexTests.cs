using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.ViewModels;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class UnifiedHexTests
{
    private static void Pump(Window window){Dispatcher.UIThread.RunJobs();window.UpdateLayout();}
    private static TableView Table(HexViewWindow window)=>window.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="HexDumpTable");
    private static void Capture(HexViewWindow window,string name)
    {
        if(Environment.GetEnvironmentVariable("MZTOOLS_UNIFIED_HEX_OUTPUT") is not {} output)return;Directory.CreateDirectory(output);Pump(window);
        using var bitmap=new Avalonia.Media.Imaging.RenderTargetBitmap(new Avalonia.PixelSize((int)window.Bounds.Width,(int)window.Bounds.Height),new Avalonia.Vector(96,96));bitmap.Render(window);bitmap.Save(Path.Combine(output,name),Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }
    [AvaloniaFact]public async Task ViewEditApplyAndCancelUseTheSameColoredTableAndNoSecondWindow()
    {
        using var files=new MediaDocumentTests();byte[] source=MzfDescriptionEditingTests.FileBytes();File.WriteAllBytes(files.FilePath("original.mzf"),source);var session=await files.Open();
        var owner=new Window();owner.Show();var viewer=new HexViewWindow(session.Rows[0],session);viewer.Show(owner);Pump(viewer);
        try
        {
            var table=Table(viewer);Capture(viewer,"view.png");Assert.False(viewer.IsEditing);Assert.False(viewer.TypeHex("C3"));
            var header=table.ItemsSource!.Cast<HexViewWindow.Row>().First();
            var colors=new[]{0,1,17,18,20,22,24}.Select(i=>Assert.IsAssignableFrom<ISolidColorBrush>(header.ByteBackground(i)).Color).ToArray();Assert.Equal(7,colors.Distinct().Count());
            Assert.Contains(viewer.GetVisualDescendants().OfType<TextBlock>(),t=>t.Text?.Trim()=="Description");
            viewer.GetVisualDescendants().OfType<Button>().Single(b=>Equals(b.Content,"Edit")).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));Pump(viewer);
            Assert.True(viewer.IsEditing);Assert.Same(table,Table(viewer));Assert.Single(owner.OwnedWindows);Assert.Empty(viewer.OwnedWindows);
            viewer.SelectOffset(24);viewer.KeyTextInput("C3");Pump(viewer);Capture(viewer,"edit.png");Assert.Equal(0xC3,viewer.EditedBytes[24]);Assert.Equal(source,TapeDocumentWriter.SerializeMzf(session.Model.Records[0],true));
            viewer.CancelEdit();Pump(viewer);Assert.False(viewer.IsEditing);Assert.Equal(source,viewer.EditedBytes);Assert.Same(table,Table(viewer));Assert.False(session.IsModified);
            viewer.BeginEdit();viewer.SelectOffset(128);viewer.KeyTextInput("76");viewer.Apply();Pump(viewer);
            Assert.True(viewer.IsVisible);Assert.False(viewer.IsEditing);Assert.Same(table,Table(viewer));Assert.Equal(0x76,session.Model.Records[0].Body.MzfBody[0]);
            viewer.BeginEdit();viewer.SelectOffset(129);Assert.True(viewer.TypeHex("F3"));viewer.Apply();Assert.Equal(0xF3,session.Model.Records[0].Body.MzfBody[1]);
            Assert.Equal(source,File.ReadAllBytes(session.Path!));Assert.Empty(viewer.OwnedWindows);
        }
        finally{viewer.Close();owner.Close();Pump(owner);}
    }
    [AvaloniaFact]public async Task GeometryLabelsTrackDraftHeaderAndInvalidPasteCannotDamageDocument()
    {
        using var files=new MediaDocumentTests();byte[] source=MzfDescriptionEditingTests.FileBytes();File.WriteAllBytes(files.FilePath("original.mzf"),source);var session=await files.Open();var viewer=new HexViewWindow(session.Rows[0],session);viewer.Show();Pump(viewer);
        try
        {
            var table=Table(viewer);viewer.BeginEdit();viewer.SelectOffset(20);Assert.True(viewer.PasteHex("00 50"));Pump(viewer);
            Assert.Equal("0x5000",table.ItemsSource!.Cast<HexViewWindow.Row>().First(r=>r.Section=="Data").RamAddress);viewer.CancelEdit();Pump(viewer);
            Assert.Equal("0x4000",table.ItemsSource!.Cast<HexViewWindow.Row>().First(r=>r.Section=="Data").RamAddress);
            viewer.BeginEdit();viewer.SelectOffset(18);Assert.True(viewer.PasteHex("FF FF"));viewer.Apply();Assert.True(viewer.IsEditing);Assert.False(viewer.Applied);Assert.NotEmpty(viewer.Status.Text!);Assert.False(session.IsModified);
            viewer.CancelEdit();var rows=table.ItemsSource!.Cast<HexViewWindow.Row>().ToArray();Assert.Equal(128,rows.Where(r=>r.Section=="MZF header").Sum(r=>r.Count));Assert.Equal(64,rows.Where(r=>r.Section=="Data").Sum(r=>r.Count));Assert.Equal(3,rows.Where(r=>r.Section=="Trailing").Sum(r=>r.Count));
            Assert.Equal("Description",MzfHexPresentation.Label(MzfHexPresentation.Field(127,64)));Assert.Equal("Data",MzfHexPresentation.Label(MzfHexPresentation.Field(128,64)));Assert.Equal("Trailing",MzfHexPresentation.Label(MzfHexPresentation.Field(192,64)));
            viewer.BeginEdit();viewer.SelectOffset(source.Length-1);Assert.False(viewer.PasteHex("AA BB"));Assert.Equal(source,viewer.EditedBytes);
        }
        finally{viewer.Close();Pump(viewer);}
    }
    [AvaloniaFact]public async Task RecordContextMenuPropertiesOpensTheSameInspectorAsDoubleClick()
    {
        using var files=new MediaDocumentTests();var session=await files.Open();var model=new ShellViewModel(files.Coordinator,_=>Task.CompletedTask,files.Dialogs);var owner=new MainWindow(model);owner.Show();Pump(owner);
        try
        {
            session.SelectedRow=session.Rows[0];var workspace=owner.GetVisualDescendants().OfType<RecordWorkspace>().Single();var records=workspace.FindControl<TableView>("RecordsTable")!;
            var properties=records.ContextMenu!.Items.OfType<MenuItem>().Single(m=>Equals(m.Header,"Properties..."));properties.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));Pump(owner);
            var dialog=Assert.Single(owner.OwnedWindows);Assert.Equal("Properties",dialog.Title);Assert.Single(dialog.GetVisualDescendants().OfType<RecordInspector>());dialog.Close();Pump(owner);
        }
        finally{owner.Close();Pump(owner);}
    }
}
