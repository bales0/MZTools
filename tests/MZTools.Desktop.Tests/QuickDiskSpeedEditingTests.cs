using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Views;

namespace MZTools.Desktop.Tests;

public sealed class QuickDiskSpeedEditingTests
{
    private static void Pump(Window w){Dispatcher.UIThread.RunJobs();w.UpdateLayout();}
    private static async Task<TapeSession> Open(DiskDocumentTests files)
    {
        string mzf=files.FilePath("three.mzt");File.WriteAllBytes(mzf,MediaDocumentTests.Mzf("FIRST",16).Concat(MediaDocumentTests.Mzf("SECOND",16)).Concat(MediaDocumentTests.Mzf("THIRD",16)).ToArray());
        var model=TapeDocumentIO.Read(mzf);string path=files.FilePath("original.qdf");File.WriteAllBytes(path,TapeDocumentIO.Serialize(model,TapeDocumentFormat.Qdf,true));
        files.Dialogs.OpenPath=path;Assert.True(await files.Coordinator.OpenAsync(),files.Coordinator.Error);return Assert.IsType<TapeSession>(files.Coordinator.Active);
    }
    private static TableView Table(Window w)=>w.GetVisualDescendants().OfType<TableView>().Single(t=>t.Name=="RecordsTable");
    private static void Select(Window w,TapeSession tape,params int[] indices)
    {var table=Table(w);table.SelectedItems!.Clear();foreach(int i in indices)table.SelectedItems.Add(tape.Rows[i]);Pump(w);Assert.Equal(indices,tape.Selection);}
    private static void Speed(Window w,int row,string value)
    {var box=Table(w).GetVisualDescendants().OfType<ComboBox>().Single(c=>c.DataContext is RecordRow r&&r.Index==row);Assert.Equal("Tape speed",AutomationProperties.GetName(box));
        var selected=Table(w).SelectedItems!.OfType<RecordRow>().Select(r=>r.Index).ToArray();
        var point=box.TranslatePoint(new Point(box.Bounds.Width-8,box.Bounds.Height/2),w)!.Value;w.MouseDown(point,MouseButton.Left);w.MouseUp(point,MouseButton.Left);Pump(w);
        Assert.True(box.IsDropDownOpen);Assert.Equal(selected,Table(w).SelectedItems!.OfType<RecordRow>().Select(r=>r.Index));
        box.SelectedItem=value;box.IsDropDownOpen=false;Pump(w);}
    [AvaloniaFact]public async Task RepeatedMultiSpeedChangesKeepSelectionHeadersAndSourceAndCanBeSaved()
    {
        using var files=new DiskDocumentTests();var tape=await Open(files);byte[] original=File.ReadAllBytes(tape.Path!);var headers=tape.Model.Records.Select(r=>r.GetSerializedHeader()).ToArray();
        var records=tape.Model.Records.Select(r=>r.DeepClone()).ToArray();records[1].Profile=TapeProfileComponents.Combine("IC","1:1");tape.ApplyRecords(records);
        var window=new MainWindow(files.Shell){Width=1500};window.Show();
        try
        {
            Pump(window);Assert.All(Table(window).GetVisualDescendants().OfType<TextBox>().Where(t=>!t.GetVisualAncestors().Any(v=>v is ComboBox)),t=>Assert.True(t.IsReadOnly,$"Editable: {t.Text} ({t.Name})"));Assert.All(Table(window).GetVisualDescendants().OfType<ComboBox>(),c=>Assert.Equal("Tape speed",AutomationProperties.GetName(c)));
            bool transientDraft=false;tape.PropertyChanged+=(_,e)=>{if(e.PropertyName==nameof(TapeSession.Rows)&&tape.HasDraft)transientDraft=true;};
            Select(window,tape,0,1);Speed(window,0,"1:3");Assert.Equal(TapeProfileComponents.Combine("NORMAL","1:3"),tape.Model.Records[0].Profile);Assert.Equal(TapeProfileComponents.Combine("IC","1:3"),tape.Model.Records[1].Profile);
            Select(window,tape,1,2);Speed(window,1,"1:2");Assert.Equal(TapeProfileComponents.Combine("IC","1:2"),tape.Model.Records[1].Profile);Assert.Equal(TapeProfileComponents.Combine("NORMAL","1:2"),tape.Model.Records[2].Profile);
            Assert.Equal(new[]{1,2},tape.Selection);Assert.False(tape.HasDraft);Assert.False(transientDraft);Assert.Empty(files.Dialogs.Confirmations);Assert.Empty(window.OwnedWindows);Assert.Null(files.Coordinator.Error);
            for(int i=0;i<3;i++)Assert.Equal(headers[i],tape.Model.Records[i].GetSerializedHeader());Assert.Equal(original,File.ReadAllBytes(tape.Path!));
            var expected=tape.Model.Records.Select(r=>r.Profile).ToArray();var qdf=await ((IFileSetDocument)tape).PrepareSaveAsync(new(files.FilePath("not-written.qdf"),"Qdf",true),TestContext.Current.CancellationToken);Assert.DoesNotContain("tape profile metadata",qdf.LossWarning);Assert.Contains(files.FilePath("not-written.mqi"),qdf.Outputs.Keys);files.Dialogs.Save=new(files.FilePath("copy.qdf"),"Qdf",true);Assert.True(await files.Coordinator.SaveAsync(true),files.Coordinator.Error);Assert.Equal(original,File.ReadAllBytes(files.FilePath("original.qdf")));
            Assert.True(await files.Coordinator.CloseAsync());Assert.True(await files.Coordinator.OpenPathAsync(files.FilePath("copy.qdf")));Assert.Equal(expected,Assert.IsType<TapeSession>(files.Coordinator.Active).Model.Records.Select(r=>r.Profile));
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public async Task ChangingRowsDoesNotAskToDiscardMetadataAndSpeedRemainsEditable()
    {
        using var files=new DiskDocumentTests();var tape=await Open(files);var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Pump(window);tape.Name="NOT APPLIED";Assert.True(tape.HasDraft);files.Dialogs.Accept=false;Table(window).SelectedItem=tape.Rows[1];Pump(window);
            Assert.Equal("SECOND",tape.Name);Assert.False(tape.HasDraft);Assert.Empty(files.Dialogs.Confirmations);Assert.Empty(window.OwnedWindows);
            Select(window,tape,0,2);Speed(window,0,"1:3");Assert.False(tape.HasDraft);Assert.Equal(TapeProfileComponents.Combine("NORMAL","1:3"),tape.Model.Records[2].Profile);Assert.Null(files.Coordinator.Error);
        }
        finally{window.Close();Pump(window);}
    }
    [AvaloniaFact]public async Task DoubleClickOpensPropertiesAndClosingUnappliedFieldsDoesNotBlockMultiSpeed()
    {
        using var files=new DiskDocumentTests();var tape=await Open(files);var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Pump(window);var row=Assert.IsAssignableFrom<Control>(Table(window).ContainerFromIndex(0));var point=row.TranslatePoint(new Point(90,10),window)!.Value;
            window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);Pump(window);
            var properties=Assert.Single(window.OwnedWindows);Assert.Equal("Properties",properties.Title);properties.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Record name").Text="UNAPPLIED";Assert.True(tape.HasDraft);properties.Close();Pump(window);
            Assert.False(tape.HasDraft);Assert.Equal("FIRST",tape.Rows[0].Name);Select(window,tape,0,1);Speed(window,0,"1:2");Assert.Empty(window.OwnedWindows);Assert.Empty(files.Dialogs.Confirmations);Assert.Null(files.Coordinator.Error);
        }
        finally{foreach(var child in window.OwnedWindows.ToArray())child.Close();window.Close();Pump(window);}
    }
    [AvaloniaTheory][InlineData(false)][InlineData(true)]
    public async Task PropertiesRoundTripCannotReorderQdWhenMouseReleaseGoesToTheDialog(bool apply)
    {
        using var files=new DiskDocumentTests();var source=await Open(files);string path=files.FilePath("order.qd");File.WriteAllBytes(path,TapeDocumentIO.Serialize(source.Model,TapeDocumentFormat.QdSharpLegacy,true));
        Assert.True(await files.Coordinator.OpenPathAsync(path));var tape=Assert.IsType<TapeSession>(files.Coordinator.Active);byte[] before=File.ReadAllBytes(path);long revision=tape.Revision;
        var names=tape.Rows.Select(r=>r.Name).ToArray();var bodies=tape.Model.Records.Select(r=>r.Body.MzfBody.ToArray()).ToArray();
        var window=new MainWindow(files.Shell);window.Show();
        try
        {
            Pump(window);var row=Assert.IsAssignableFrom<Control>(Table(window).ContainerFromIndex(0));var point=row.TranslatePoint(new Point(90,10),window)!.Value;
            window.MouseDown(point,MouseButton.Left);window.MouseUp(point,MouseButton.Left);window.MouseDown(point,MouseButton.Left);Pump(window);
            var properties=Assert.Single(window.OwnedWindows);Assert.Equal("Properties",properties.Title);
            // The owner never receives the second release after the modal opens.
            properties.MouseUp(new Point(20,20),MouseButton.Left);Pump(properties);
            properties.GetVisualDescendants().OfType<TextBox>().Single(t=>AutomationProperties.GetName(t)=="Record name").Text=apply?"RENAMED":"DISCARDED";
            if(apply){files.Shell.ApplyMetadataCommand.Execute(null);Pump(properties);Assert.Empty(tape.Validation);names[0]="RENAMED";}
            properties.Close();Pump(window);var table=Table(window);var below=table.TranslatePoint(new Point(90,table.Bounds.Height-5),window)!.Value;
            window.MouseMove(below,RawInputModifiers.None);Pump(window);
            Assert.Null(files.Coordinator.Error);Assert.False(files.Coordinator.IsBusy);Assert.Empty(window.OwnedWindows);Assert.Equal(names,tape.Rows.Select(r=>r.Name));
            Assert.Equal(revision+(apply?1:0),tape.Revision);Assert.Equal(0,tape.SelectedRow!.Index);Assert.False(tape.HasDraft);for(int i=0;i<bodies.Length;i++)Assert.Equal(bodies[i],tape.Model.Records[i].Body.MzfBody);
            Assert.Equal(before,File.ReadAllBytes(path));
            // Explicit movement is still available and remains the only reason for reordering.
            tape.Move(1);Pump(window);Assert.Equal(new[]{names[1],names[0],names[2]},tape.Rows.Select(r=>r.Name));
        }
        finally{foreach(var child in window.OwnedWindows.ToArray())child.Close();window.Close();Pump(window);}
    }

}
