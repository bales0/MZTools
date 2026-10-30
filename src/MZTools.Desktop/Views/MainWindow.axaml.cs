using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using MZTools.Desktop.Controls;
using MZTools.Desktop.Documents;
using MZTools.Desktop.Platform;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

public sealed partial class MainWindow : Window
{
    private bool closeApproved;
    private Window? inspectorDetail;
    private PropertyForm? detailForm;
    public ShellViewModel Model { get; }
    internal DesktopPreferences? Preferences {get;}

    public MainWindow() : this(null) { }
    public MainWindow(ShellViewModel? model):this(model,null){}
    internal MainWindow(ShellViewModel? model,DesktopPreferences? preferences)
    {
        InitializeComponent();Preferences=preferences;if(preferences is not null){Width=preferences.Width;Height=preferences.Height;}
        var dialogs = new DesktopDialogs(this);
        Model = model ?? new ShellViewModel(new(new MediaDocumentLoader(dialogs), dialogs, new()), async text =>
        {
            var clipboard = Clipboard ?? throw new NotSupportedException("This platform has no clipboard provider.");
            await clipboard.SetTextAsync(text);
        }, dialogs, async bytes =>
        {
            var clipboard = Clipboard ?? throw new NotSupportedException("Clipboard is unavailable.");
            await clipboard.SetValueAsync(RecordWorkspace.PacketFormat, bytes);
        }, async () => Clipboard is null ? null : await Clipboard.TryGetValueAsync(RecordWorkspace.PacketFormat));
        DataContext = Model;
        Model.RecorderRequested+=()=>{var recorder=new RecorderWindow(new(new RecorderBackendFactory()),new DesktopRecorderNavigation(this),path=>!Model.Documents.IsOpenSource(path)&&!(Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime lifetime&&lifetime.Windows.OfType<MainWindow>().Any(w=>w.Model.Documents.IsOpenSource(path))));recorder.Show();};
        Model.PropertyChanged += ModelChanged;
        RefreshInspector();
        var primary = OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control;
        KeyBindings.Add(new() { Gesture = new(Key.O, primary), Command = Model.OpenCommand });
        KeyBindings.Add(new() { Gesture = new(Key.S, primary), Command = Model.SaveCommand });
        KeyBindings.Add(new() { Gesture = new(Key.S, primary | KeyModifiers.Shift), Command = Model.SaveAsCommand });
        KeyBindings.Add(new() { Gesture = new(Key.W, primary), Command = Model.CloseCommand });
        AddHandler(KeyDownEvent, CycleFocus, RoutingStrategies.Tunnel);
        SizeChanged += (_, _) => Reflow();
        Closing += OnClosing;
    }

    private void ModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.SelectedModule)) RefreshInspector();
        if (e.PropertyName is nameof(ShellViewModel.ActiveTape) or nameof(ShellViewModel.ShowMedia)) RecordEditor.BindSession();
        if (e.PropertyName is nameof(ShellViewModel.ActiveDisk) or nameof(ShellViewModel.ShowDisk)) DiskEditor.BindSession();
        if (e.PropertyName is nameof(ShellViewModel.ActiveAudio) or nameof(ShellViewModel.ShowAudio)) AudioEditor.BindSession();
        if (e.PropertyName == nameof(ShellViewModel.InspectorVisible))
        {
            Reflow();
            if (Model.ShowMedia)
            {
                RecordEditor.BindSession();
                if (RecordEditor.Bounds.Width < 850) RecordEditor.ShowInspector(this);
            }
            else if (Model.ShowDisk) { DiskEditor.BindSession(); if(DiskEditor.Bounds.Width<900) DiskEditor.ShowInspector(this); }
            else if(Model.ShowAudio){AudioEditor.BindSession();if(AudioEditor.Bounds.Width<900)AudioEditor.ShowInspector(this);}
            else if (Bounds.Width < 920) ShowInspectorDetail();
        }
    }
    private void RefreshInspector()
    {
        var module = Model.SelectedModule;
        InspectorForm.Fields = module is null ? [] : new[]
        {
            new PropertyField("Module", module.Name, true), new PropertyField("Phase", module.Phase, true),
            new PropertyField("State", module.State, true)
        };
        if (detailForm is not null) detailForm.Fields = InspectorForm.Fields;
    }
    private void ShowInspectorDetail()
    {
        if (inspectorDetail is not null) { inspectorDetail.Activate(); return; }
        detailForm = new PropertyForm { Fields = InspectorForm.Fields, Margin = new Thickness(20) };
        inspectorDetail = new Window { Title = "Workspace inspector", Width = 360, Height = 420,
            Content = new ScrollViewer { Content = detailForm }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        inspectorDetail.Closed += (_, _) => { inspectorDetail = null; detailForm = null; OpenButton.Focus(); };
        inspectorDetail.Show(this);
    }
    private void Reflow()
    {
        bool compact = Bounds.Width < 1100;
        BatchEditor.Height = Math.Max(720, Bounds.Height - 350);
        AudioEditor.Height = Math.Max(800, Bounds.Height - 350);
        DiskEditor.Height = Math.Max(520, Bounds.Height - 350);
        RecordEditor.Height = Math.Max(460, Bounds.Height - 330);
        NavigationContext.IsVisible = Bounds.Height >= 640;
        ReadinessPanel.Height = Math.Clamp(Bounds.Height - 440, 180, 500);
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(Bounds.Width < 800 ? 132 : 184);
        WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(Model.InspectorVisible ? (compact ? 220 : 272) : 0);
        WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(Model.InspectorVisible ? 16 : 0);
        // At narrow widths inspector becomes a separately accessible detail window.
        if (Bounds.Width < 920)
        {
            InspectorPanel.IsVisible = false;
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
        else InspectorPanel.IsVisible = Model.InspectorVisible;
    }
    private void CycleFocus(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.F6) return;
        Control[] regions = [MainMenu, OpenButton, DocumentTabs, Navigation, Model.ShowBatch ? BatchEditor.FocusRegion : Model.ShowAudio ? AudioEditor.WorkspaceFocusRegion : Model.ShowDisk ? DiskEditor.WorkspaceFocusRegion : Model.ShowMedia ? RecordEditor.WorkspaceFocusRegion : ModuleFilter,
            Model.ShowAudio ? AudioEditor.InspectorFocusRegion : Model.ShowDisk ? DiskEditor.InspectorFocusRegion : Model.ShowMedia ? RecordEditor.InspectorFocusRegion : InspectorForm, CancelOperation];
        var visible = regions.Where(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled).ToArray();
        int current = Array.FindIndex(visible, c => c.IsKeyboardFocusWithin);
        int direction = e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1;
        for (int offset = 1; offset <= visible.Length; offset++)
        {
            int index = (current + direction * offset + visible.Length * 2) % visible.Length;
            var region = visible[index];
            if (region.Focus(NavigationMethod.Tab) || region.GetVisualDescendants().OfType<Control>()
                    .Any(c => c.IsEffectivelyVisible && c.IsEffectivelyEnabled && c.Focusable && c.Focus(NavigationMethod.Tab))) break;
        }
        e.Handled = true;
    }
    private async void OnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (closeApproved) return;
        e.Cancel = true;
        if (await Model.Documents.CloseAllAsync()) { Preferences?.Save(Bounds.Width,Bounds.Height);closeApproved = true; Close(); }
    }
}
