using Microsoft.Win32;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class CpmSystemBuilderDialog : Window
{
    private readonly DskDocument target;
    private readonly TextBox sourcePath = new() { IsReadOnly = true, MinWidth = 430 };
    private readonly TextBlock profileText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox report = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.NoWrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        FontFamily = new System.Windows.Media.FontFamily("Consolas") };
    private readonly Button build = new() { Content = "Build and Save As...", MinWidth = 145, IsEnabled = false };
    private readonly Button install = new() { Content = "Install into current disk", MinWidth = 145, IsEnabled = false };
    private readonly ComboBox sourceMode = new() { Margin = new Thickness(0, 0, 0, 6) };
    private readonly Button copyReport = new() { Content = "Copy report", MinWidth = 95, IsEnabled = false };
    private readonly Button saveReport = new() { Content = "Save report...", MinWidth = 95, IsEnabled = false };
    private DskDocument? source;
    private CpmSystemProfile? profile;
    private CpmSystemBuildReport? completedReport;

    internal CpmSystemBuilderDialog(Window? owner, DskDocument target)
    {
        Owner = owner;
        this.target = target;
        Title = "Make Bootable / Install CP/M System";
        Width = 820; Height = 680; MinWidth = 680; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Content = root;

        var targetPanel = Section("Target layout");
        targetPanel.Children.Add(new TextBlock { Text = TargetDescription(target), TextWrapping = TextWrapping.Wrap });
        root.Children.Add(targetPanel);

        var sourcePanel = Section("System source");
        sourceMode.Items.Add("Verified template — exact registered SHA-256");
        sourceMode.Items.Add("Compatible source DSK — system/version unverified");
        sourceMode.SelectedIndex = CpmSystemProfileRegistry.SupportsTarget(target) ? 0 : 1;
        sourcePanel.Children.Add(sourceMode);
        var sourceRow = new DockPanel();
        var browse = new Button { Content = "Browse...", MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); sourceRow.Children.Add(browse); sourceRow.Children.Add(sourcePath);
        sourcePanel.Children.Add(sourceRow);
        profileText.Margin = new Thickness(0, 6, 0, 0); sourcePanel.Children.Add(profileText);
        Grid.SetRow(sourcePanel, 1); root.Children.Add(sourcePanel);

        var policyPanel = Section("Build policy");
        policyPanel.Children.Add(new TextBlock { Text = "Drive configuration: Profile default (no unreviewed patch)", TextWrapping = TextWrapping.Wrap });
        var checks = new WrapPanel { Margin = new Thickness(0, 5, 0, 0) };
        checks.Children.Add(PolicyCheck("Preserve source boot name"));
        checks.Children.Add(PolicyCheck("Preserve source system logo"));
        checks.Children.Add(PolicyCheck("Preserve source version strings"));
        policyPanel.Children.Add(checks);
        policyPanel.Children.Add(new CheckBox { Content = "Explicit branding replacement (not configured)", IsEnabled = false, Margin = new Thickness(0, 4, 0, 0) });
        Grid.SetRow(policyPanel, 2); root.Children.Add(policyPanel);

        var reportPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        reportPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        reportPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        reportPanel.Children.Add(new TextBlock { Text = "Preflight / build report", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        Grid.SetRow(report, 1); reportPanel.Children.Add(report);
        Grid.SetRow(reportPanel, 3); root.Children.Add(reportPanel);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        buttons.Children.Add(copyReport); saveReport.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(saveReport);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right); buttons.Children.Add(close);
        build.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(build, Dock.Right); buttons.Children.Add(build);
        install.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(install, Dock.Right); buttons.Children.Add(install);
        Grid.SetRow(buttons, 4); root.Children.Add(buttons);

        browse.Click += Browse_Click;
        build.Click += Build_Click;
        install.Click += Install_Click;
        sourceMode.SelectionChanged += (_, _) =>
        {
            if (source == null) return;
            try { LoadSource(source, CpmSystemProfileRegistry.ResolveVerifiedSource, sourcePath.Text); }
            catch (Exception exception) { RejectSource(sourcePath.Text, exception.Message); }
        };
        copyReport.Click += (_, _) => CopyCurrentReport();
        saveReport.Click += (_, _) => SaveCurrentReport();
        close.Click += (_, _) => Close();
        report.Text = "Select one of the exact registered CP/M system DSK templates. No system bytes are generated or guessed.";
    }

    internal string? SavedImagePath { get; private set; }
    internal bool Installed { get; private set; }
    internal bool CanBuild => build.IsEnabled;
    internal bool CanInstall => install.IsEnabled;
    internal string ReportText => report.Text;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "DSK image|*.dsk", Title = "Select a CP/M system source DSK" };
        if (picker.ShowDialog(this) != true) return;
        try { LoadSource(DskDocument.Open(picker.FileName), CpmSystemProfileRegistry.ResolveVerifiedSource, picker.FileName); }
        catch (Exception exception) { RejectSource(picker.FileName, exception.Message); }
    }

    internal void SetSourceForTesting(DskDocument value, CpmSystemProfile valueProfile, string displayPath = "test.dsk")
        => LoadSource(value, _ => valueProfile, displayPath);

    internal void SetCompatibleSourceForTesting(DskDocument value)
    {
        sourceMode.SelectedIndex = 1;
        LoadSource(value, CpmSystemProfileRegistry.ResolveVerifiedSource, "compatible-test.dsk");
    }

    private void LoadSource(DskDocument value, Func<DskDocument, CpmSystemProfile> resolver, string displayPath)
    {
        if (sourceMode.SelectedIndex == 1)
        {
            var compatibility = DskCapabilityService.CanInstallBootSystem(target, value);
            source = value; profile = null; completedReport = null; SavedImagePath = null;
            sourcePath.Text = displayPath;
            profileText.Text = "Compatible source DSK. Layout is checked; OS version and bootability are unverified.";
            report.Text = compatibility.IsCompatible ? "Ready to install: " + compatibility.Reason : "Install rejected:\n" + compatibility.Reason;
            install.IsEnabled = compatibility.IsCompatible; build.IsEnabled = false;
            copyReport.IsEnabled = saveReport.IsEnabled = true;
            return;
        }
        CpmSystemProfile resolved = resolver(value);
        CpmSystemBuildPreflight preflight = CpmSystemBuilder.Preflight(target, value, resolved);
        source = value; profile = resolved; completedReport = null; SavedImagePath = null;
        sourcePath.Text = displayPath;
        profileText.Text = $"{resolved.DisplayName}\nLayout: {resolved.Layout} | Transfer: {resolved.TransferMode} | Loader: {resolved.BootLoader}\nSystem tracks: {string.Join(", ", resolved.SystemPhysicalTracks)}\nTemplate SHA-256: {resolved.VerifiedTemplateSha256}";
        report.Text = preflight.Report;
        build.IsEnabled = preflight.CanBuild;
        install.IsEnabled = preflight.CanBuild;
        copyReport.IsEnabled = saveReport.IsEnabled = true;
    }

    private void RejectSource(string path, string reason)
    {
        source = null; profile = null; completedReport = null; SavedImagePath = null;
        sourcePath.Text = path; profileText.Text = "Source is not an exact registered template.";
        report.Text = "Build rejected:\n- " + reason;
        build.IsEnabled = false; copyReport.IsEnabled = saveReport.IsEnabled = true;
        install.IsEnabled = false;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (source == null || !install.IsEnabled) return;
        if (MessageBox.Show(this, "Replace the current disk's boot/system area with the selected source? File data will be validated and retained. Save is a separate operation.",
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            if (profile != null)
            {
                completedReport = CpmSystemBuilder.Apply(target, source, profile);
                report.Text = completedReport.ToText();
            }
            else
            {
                DskBootSystemService.Install(target, source);
                report.Text = "Compatible boot/system area installed; source system/version remains unverified. Save the current disk to persist changes.";
            }
            Installed = true; SavedImagePath = null;
        }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void Build_Click(object sender, RoutedEventArgs e)
    {
        if (source == null || profile == null) return;
        var save = new SaveFileDialog { Filter = "Extended CPC DSK|*.dsk", DefaultExt = ".dsk", AddExtension = true,
            FileName = SuggestedName(profile), Title = "Build and Save Bootable CP/M System" };
        if (save.ShowDialog(this) != true) return;
        try
        {
            string destination = Path.GetFullPath(save.FileName);
            if ((target.FilePath != null && destination.Equals(Path.GetFullPath(target.FilePath), StringComparison.OrdinalIgnoreCase)) ||
                (source.FilePath != null && destination.Equals(Path.GetFullPath(source.FilePath), StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Build and Save As requires a separate path from the current disk and system source.");
            CpmSystemBuildResult result = CpmSystemBuilder.Build(target, source, profile);
            CpmSystemBuilder.SaveValidatedResult(result, save.FileName);
            completedReport = result.Report; SavedImagePath = Path.GetFullPath(save.FileName);
            report.Text = result.Report.ToText(); copyReport.IsEnabled = saveReport.IsEnabled = true;
            build.Content = "Build again...";
            MessageBox.Show(this, $"Validated system image saved to:\n{SavedImagePath}", Title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            report.Text = "Build failed; the open document was not changed.\n\n" + exception.Message;
            MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void CopyCurrentReport()
    {
        try { Clipboard.SetText(CurrentReport()); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveCurrentReport()
    {
        var save = new SaveFileDialog { Filter = "Text report|*.txt", DefaultExt = ".txt", AddExtension = true, FileName = "cpm-system-build.txt" };
        if (save.ShowDialog(this) != true) return;
        try { File.WriteAllText(save.FileName, CurrentReport()); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private string CurrentReport() => completedReport?.ToText() ?? report.Text;

    private static StackPanel Section(string heading) => new()
    {
        Margin = new Thickness(0, 0, 0, 10),
        Children = { new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) } }
    };

    private static CheckBox PolicyCheck(string text) => new()
    {
        Content = text, IsChecked = true, IsEnabled = false, Margin = new Thickness(0, 0, 18, 0)
    };

    private static string TargetDescription(DskDocument document)
    {
        if (document.FileSystem is not CpmFileSystem cpm) return document.FileSystem.DisplayName;
        return $"{document.FileSystem.DisplayName} | {document.Image.TrackCount} cylinders × {document.Image.SideCount} sides | DPB OFF={cpm.Dpb.Off}, block={cpm.Dpb.BlockSize} B";
    }

    private static string SuggestedName(CpmSystemProfile value) => value.Id + "-system.dsk";
}
