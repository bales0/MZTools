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
    private readonly Button install = new() { Content = "Install into current disk", MinWidth = 145, IsEnabled = false };
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
        var sourceRow = new DockPanel();
        var browse = new Button { Content = "Browse...", MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right); sourceRow.Children.Add(browse); sourceRow.Children.Add(sourcePath);
        sourcePanel.Children.Add(sourceRow);
        profileText.Margin = new Thickness(0, 6, 0, 0); sourcePanel.Children.Add(profileText);
        Grid.SetRow(sourcePanel, 1); root.Children.Add(sourcePanel);

        var policyPanel = Section("Installation policy");
        policyPanel.Children.Add(new TextBlock { Text = PersonalCpmSystemInstaller.IsPersonalLayout(target)
            ? "Native SHARP IPL and PCPM.SYS (user 0, SYS) will be installed at directory entry #0 using free allocation blocks.\nThe previous first file extent will be relocated without moving its data. Other target files will be preserved; existing user 0 PCPM.SYS will be replaced.\nNothing is saved automatically: use Save / Save As in the editor."
            : "Source boot/system bytes and branding will be preserved exactly.\nOnly boot/system area will be installed. Target files will be preserved.\nNothing is saved automatically: use Save / Save As in the editor.", TextWrapping = TextWrapping.Wrap });
        Grid.SetRow(policyPanel, 2); root.Children.Add(policyPanel);

        var reportPanel = new Grid { Margin = new Thickness(0, 0, 0, 10) };
        reportPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        reportPanel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        reportPanel.Children.Add(new TextBlock { Text = "Preflight / installation report", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        Grid.SetRow(report, 1); reportPanel.Children.Add(report);
        Grid.SetRow(reportPanel, 3); root.Children.Add(reportPanel);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0), LastChildFill = false };
        buttons.Children.Add(copyReport); saveReport.Margin = new Thickness(8, 0, 0, 0); buttons.Children.Add(saveReport);
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 85, Margin = new Thickness(8, 0, 0, 0) };
        DockPanel.SetDock(close, Dock.Right); buttons.Children.Add(close);
        install.Margin = new Thickness(8, 0, 0, 0); DockPanel.SetDock(install, Dock.Right); buttons.Children.Add(install);
        Grid.SetRow(buttons, 4); root.Children.Add(buttons);

        browse.Click += Browse_Click;
        install.Click += Install_Click;
        copyReport.Click += (_, _) => CopyCurrentReport();
        saveReport.Click += (_, _) => SaveCurrentReport();
        close.Click += (_, _) => Close();
        report.Text = "Browse for a trusted CP/M source. Registered system areas are identified automatically; compatible unknown systems remain unverified. No system bytes are generated or guessed.";
    }

    internal bool Installed { get; private set; }
    internal bool CanInstall => install.IsEnabled;
    internal string ReportText => report.Text;
    internal string IdentificationText => profileText.Text;
    internal event EventHandler? SystemInstalled;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog { Filter = "DSK image|*.dsk", Title = "Select a CP/M system source DSK" };
        if (picker.ShowDialog(this) != true) return;
        ResetSource(picker.FileName);
        try { LoadSource(DskDocument.Open(picker.FileName), CpmSystemProfileRegistry.TryResolveVerifiedSource, picker.FileName); }
        catch (Exception exception) { RejectSource(picker.FileName, exception.Message); }
    }

    internal void SetSourceForTesting(DskDocument value, CpmSystemProfile valueProfile, string displayPath = "test.dsk")
        => LoadSource(value, _ => valueProfile, displayPath);

    internal void SetCompatibleSourceForTesting(DskDocument value)
    {
        LoadSource(value, CpmSystemProfileRegistry.TryResolveVerifiedSource, "compatible-test.dsk");
    }

    internal void LoadSource(DskDocument value, Func<DskDocument, CpmSystemProfile?>? resolver = null, string displayPath = "source.dsk")
    {
        ResetSource(displayPath);
        try
        {
            CpmSystemProfile? resolved = (resolver ?? CpmSystemProfileRegistry.TryResolveVerifiedSource)(value);
            if (resolved == null)
            {
                var compatibility = DskCapabilityService.CanInstallBootSystem(target, value);
                source = value; profile = null;
                profileText.Text = "Identification: " + (compatibility.IsCompatible ? "Compatible source DSK" : "Unregistered source — incompatible") +
                    "\nOS/version: Unverified | Transfer mode: Unverified | Loader: Unverified\nBootability is unverified.\nCompatibility: " + (compatibility.IsCompatible ? "OK" : "Rejected — see report");
                report.Text = compatibility.IsCompatible
                    ? "Ready to install: Compatible source DSK — OS/version/transfer unverified.\n" + compatibility.Reason +
                        "\nSystem physical tracks: " + string.Join(", ", DskDocumentFactory.GetSystemPhysicalTracks(((CpmFileSystem)value.FileSystem).Dpb, value.Image))
                    : "Install rejected:\n" + compatibility.Reason;
                install.IsEnabled = compatibility.IsCompatible;
                if (PersonalCpmSystemInstaller.IsPersonalLayout(value))
                    profileText.Text += "\nSource kind: P-CP/M80-style source — version unverified\nSystem storage: PCPM.SYS (user 0)\nNative SHARP IPL: " + (PersonalCpmSystemInstaller.HasNativeLoader(value) ? "Yes" : "No") +
                        "\nSystem file: " + (PersonalCpmSystemInstaller.FindSystemFile(value)?.Size.ToString() ?? "missing") + " B" +
                        "\nPCPM.SYS directory entry #0: " + (PersonalCpmSystemInstaller.IsSystemFirst(value) ? "Yes" : "No — source is not currently bootable");
                return;
            }
            CpmSystemBuildPreflight preflight = CpmSystemBuilder.Preflight(target, value, resolved);
            source = value; profile = resolved;
            profileText.Text = $"Identification: Registered exact {(resolved.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile ? "IPL + system-file" : "system-area")} match\n{resolved.DisplayName}\nLayout: {resolved.Layout} | Transfer: {resolved.TransferMode?.ToString() ?? "Unverified"} | Loader: {resolved.BootLoader}\nVerification: {resolved.Verification} (not runtime boot certification)\nSystem tracks: {string.Join(", ", resolved.SystemPhysicalTracks)}\nSystem-area SHA-256: {resolved.SystemAreaSha256}\nCompatibility: {(preflight.CanBuild ? "OK" : "Rejected — see report")}";
            report.Text = preflight.Report;
            if (resolved.Storage == CpmSystemStorageKind.BootTrackPlusSystemFile)
                profileText.Text += $"\nSystem storage: PCPM.SYS | System file: {resolved.FileBasedFingerprint!.SystemFileSize} B\nNative SHARP IPL: Yes | Transfer mode: Unverified\nPCPM.SYS directory slot: 0";
            install.IsEnabled = preflight.CanBuild;
        }
        catch (Exception exception) { RejectSource(displayPath, exception.Message); }
    }

    private void ResetSource(string path)
    {
        source = null; profile = null; completedReport = null; install.IsEnabled = false;
        sourcePath.Text = path; profileText.Text = "No validated source selected."; report.Clear();
        copyReport.IsEnabled = saveReport.IsEnabled = true;
    }

    internal void RejectSource(string path, string reason)
    {
        ResetSource(path); profileText.Text = "Source rejected."; report.Text = "Install rejected:\n- " + reason;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (source == null || !install.IsEnabled) return;
        string confirmation = PersonalCpmSystemInstaller.IsPersonalLayout(target)
            ? "Install native SHARP IPL and user 0 PCPM.SYS at directory entry #0? Its previous file extent will be relocated without moving its data. An existing user 0 PCPM.SYS will be replaced; all other files and metadata will be retained. Save is a separate operation."
            : "Replace the current disk's boot/system area with the selected source? File data will be validated and retained. Save is a separate operation.";
        if (MessageBox.Show(this, confirmation,
            Title, MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        try
        {
            InstallCurrentSource();
        }
        catch (Exception exception) { RejectSource(sourcePath.Text, exception.Message); MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    internal void InstallCurrentSource()
    {
        if (source == null || !install.IsEnabled) throw new InvalidOperationException("Select a compatible source first.");
        completedReport = profile == null ? DskBootSystemService.InstallWithReport(target, source) : CpmSystemBuilder.Apply(target, source, profile);
        report.Text = completedReport.ToText() + "\nDocument modified. Use Save / Save As in the editor; nothing was saved automatically.";
        Installed = true; install.IsEnabled = false; SystemInstalled?.Invoke(this, EventArgs.Empty);
    }

    private void CopyCurrentReport()
    {
        try { Clipboard.SetText(CurrentReport()); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private void SaveCurrentReport()
    {
        var save = new SaveFileDialog { Filter = "Text report|*.txt", DefaultExt = ".txt", AddExtension = true, FileName = "cpm-system-install.txt" };
        if (save.ShowDialog(this) != true) return;
        try { File.WriteAllText(save.FileName, CurrentReport()); }
        catch (Exception exception) { MessageBox.Show(this, exception.Message, Title, MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private string CurrentReport() => report.Text;

    private static StackPanel Section(string heading) => new()
    {
        Margin = new Thickness(0, 0, 0, 10),
        Children = { new TextBlock { Text = heading, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) } }
    };


    private static string TargetDescription(DskDocument document)
    {
        if (document.FileSystem is not CpmFileSystem cpm) return document.FileSystem.DisplayName;
        return $"{document.FileSystem.DisplayName} | {document.Image.TrackCount} cylinders × {document.Image.SideCount} sides | DPB OFF={cpm.Dpb.Off}, block={cpm.Dpb.BlockSize} B";
    }

}
