using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MZTools.Desktop.ViewModels;

namespace MZTools.Desktop.Views;

// Remove migration/readiness dashboard from the ordinary application UI.
// Keep file operations, document tabs and the error/status bar; document type determines the module.
public sealed partial class MainWindow
{
    private bool compactShellApplied;
    private StackPanel? workspaceHeader;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (compactShellApplied) return;
        compactShellApplied = true;

        // All creation commands live under File; tool launchers live under Tools.
        // Leave only the frequently used Open / Save / Save As toolbar actions.
        string[] redundant = ["New Tape", "New QuickDisk", "New disk…", "Close", "Inspector", "Batch…", "Recorder…", "Copy report"];
        foreach (var button in CommandBar.Children.OfType<Button>().Where(b =>
            b.Content is string label && redundant.Contains(label)).ToArray())
            CommandBar.Children.Remove(button);

        // Reports/phase readiness are developer diagnostics, not normal navigation.
        Navigation.ItemsSource = Model.Modules.Where(m => m.Name != "Reports").ToArray();
        NavigationContext.IsVisible = false;
        Title = "MZTools";
        var marketingTagline = this.GetVisualDescendants().OfType<TextBlock>()
            .FirstOrDefault(t => t.Text == "Tape / QuickDisk / Disks / Audio");
        if (marketingTagline is not null) marketingTagline.IsVisible = false;
        ConfigureMainMenus();
        Model.EnableNativeComDiskImport();

        // Document tabs already select the appropriate module. The extra
        // WORKSPACES column repeats the same navigation and wastes space.
        Navigation.IsVisible = false;
        var sidebar = BodyGrid.Children.OfType<Border>().FirstOrDefault();
        if (sidebar is not null) sidebar.IsVisible = false;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(0);

        OverviewScroll.Content = new StackPanel
        {
            Spacing = 8,
            Margin = new Thickness(6, 14),
            Children =
            {
                new TextBlock { Text = "Open a file or create a new image", FontWeight = Avalonia.Media.FontWeight.SemiBold, FontSize = 16 },
                new TextBlock { Text = "MZF / M12 / MZT · QuickDisk · DSK / HFE · WAV / FLAC", Opacity = .75 }
            }
        };
        // Developer readiness is not a user-facing workspace.
        if (WorkspaceTabs.Items.Count > 1 && WorkspaceTabs.Items[1] is TabItem reportTab)
            reportTab.IsVisible = false;

        workspaceHeader = (WorkspaceGrid.Parent as Grid)?.Children.OfType<StackPanel>().FirstOrDefault();
        if (workspaceHeader is not null) workspaceHeader.IsVisible = false;
        if (WorkspaceGrid.Parent is Grid workspaceBody)
        {
            workspaceBody.Margin = new Thickness(8, 6, 8, 4);
            workspaceBody.RowSpacing = 4;
        }
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(0);

        Model.PropertyChanged += CompactModelChanged;
        Model.Documents.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == "IsBusy") Dispatcher.UIThread.Post(UpdateCompactShell);
            if (e.PropertyName == "Status") Dispatcher.UIThread.Post(UpdateCompactStatus);
        };
        UpdateCompactStatus();
        SizeChanged += (_, _) => Dispatcher.UIThread.Post(UpdateCompactShell);
        UpdateCompactShell();
        ApplyWpfLayout();
    }
    private void CompactModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.HasDocument) or nameof(ShellViewModel.ShowReadiness)
            or nameof(ShellViewModel.InspectorVisible) or nameof(ShellViewModel.SelectedModule))
            Dispatcher.UIThread.Post(UpdateCompactShell);
    }
    private void UpdateCompactStatus()
    {
        // The old startup status describes a future migration phase, not application state.
        StatusText.IsVisible = Model.HasDocument || Model.Documents.IsBusy;
    }

    private void UpdateCompactShell()
    {
        if (!compactShellApplied) return;
        NavigationContext.IsVisible = false;
        BodyGrid.ColumnDefinitions[0].Width = new GridLength(0);
        UpdateComImportMenu();
        // Avoid an empty phase/status inspector taking up a whole right-hand column.
        bool showReadiness = Model.ShowReadiness;
        if (workspaceHeader is not null) workspaceHeader.IsVisible = false;
        if (showReadiness)
        {
            InspectorPanel.IsVisible = false;
            WorkspaceGrid.ColumnDefinitions[1].Width = new GridLength(0);
            WorkspaceGrid.ColumnDefinitions[2].Width = new GridLength(0);
        }
        UpdateWpfLayout();
    }
}
