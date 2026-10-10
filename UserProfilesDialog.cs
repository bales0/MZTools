using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class UserProfilesDialog : Window
{
    internal bool Applied { get; private set; }
    internal UserProfilesDialog(Window owner, DskDocument document, bool boot = false, CpmDpb? currentLayout = null)
    {
        Owner = owner; Title = boot ? "Boot / System profiles" : "CP/M layout profiles"; Width = 820; Height = 580;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(12) }; Content = root;
        var buttons = new WrapPanel(); DockPanel.SetDock(buttons, Dock.Bottom); root.Children.Add(buttons);
        var top = new StackPanel(); DockPanel.SetDock(top, Dock.Top); root.Children.Add(top);
        top.Children.Add(new TextBlock { Text = boot ? "Profiles reference an exact saved system source. P-CP/M80 uses native IPL + PCPM.SYS at slot #0. No system binary is stored in a profile." : "Built-in Sharp profiles are read-only. Load validates geometry/allocation and attaches interpretation only; image bytes stay unchanged.", TextWrapping = TextWrapping.Wrap });
        var name = new TextBox { Margin = new Thickness(0, 6, 0, 2) }; var description = new TextBox { Margin = new Thickness(0, 2, 0, 6) };
        top.Children.Add(new TextBlock { Text = "Profile name:" }); top.Children.Add(name); top.Children.Add(new TextBlock { Text = "Description:" }); top.Children.Add(description);
        var list = new ListBox(); root.Children.Add(list);
        void Refresh() { list.ItemsSource = boot ? UserProfileService.BootProfiles().Cast<object>().ToArray() : UserProfileService.Layouts().Cast<object>().ToArray(); }
        Button Action(string label, Action action)
        {
            var button = new Button { Content = label, Margin = new Thickness(4), Padding = new Thickness(8, 4, 8, 4) }; buttons.Children.Add(button);
            button.Click += (_, _) => { try { action(); } catch (Exception e) { MessageBox.Show(this, e.Message, Title); } };
            return button;
        }
        Button? load = null, duplicate = null, rename = null, delete = null;
        list.SelectionChanged += (_, _) =>
        {
            if (load != null) load.IsEnabled = list.SelectedItem != null;
            if (duplicate != null) duplicate.IsEnabled = list.SelectedItem != null;
            bool editable = list.SelectedItem is BootSystemUserProfile or CpmLayoutProfile { BuiltIn: false };
            if (rename != null) rename.IsEnabled = editable;
            if (delete != null) delete.IsEnabled = editable;
            if (list.SelectedItem is CpmLayoutProfile layout) { name.Text = layout.Name; description.Text = layout.Description; }
            if (list.SelectedItem is BootSystemUserProfile system) { name.Text = system.Name; description.Text = system.Description; }
        };
        Action("Save current as new...", () =>
        {
            if (boot) UserProfileService.Save(UserProfileService.CaptureBoot(document, name.Text, description.Text));
            else if ((currentLayout ?? (document.FileSystem as CpmFileSystem)?.Dpb) is { } dpb)
                UserProfileService.Save(new CpmLayoutProfile(Guid.NewGuid().ToString("N"), name.Text, description.Text, dpb, DskGeometrySignature.From(document.Image)));
            else throw new InvalidOperationException("Attach or enter a CP/M layout first.");
            Refresh();
        });
        load = Action("Load / Preview...", () =>
        {
            if (list.SelectedItem is CpmLayoutProfile layout)
            {
                var result = UserProfileService.Preview(document, layout);
                var preview = new DskPatchPreviewWindow(this, result.Report, result.CanApply ? () => CpmLayoutService.Apply(document, result) : null, "Profile layout preview", "Attach profile");
                preview.ShowDialog(); if (preview.Applied) { Applied = true; Close(); }
            }
            else if (list.SelectedItem is BootSystemUserProfile system)
            {
                var source = UserProfileService.ResolveBootSource(system);
                var dialog = new CpmSystemBuilderDialog(this, document); dialog.LoadProfileSource(source); dialog.ShowDialog();
                if (dialog.Installed) { Applied = true; Close(); }
            }
        });
        duplicate = Action("Duplicate...", () =>
        {
            if (list.SelectedItem is CpmLayoutProfile layout) UserProfileService.Save(layout with { Id = Guid.NewGuid().ToString("N"), Name = name.Text + " copy", Description = description.Text, BuiltIn = false });
            if (list.SelectedItem is BootSystemUserProfile system) UserProfileService.Save(system with { Id = Guid.NewGuid().ToString("N"), Name = name.Text + " copy", Description = description.Text });
            Refresh();
        });
        rename = Action("Rename / Update description", () =>
        {
            if (list.SelectedItem is CpmLayoutProfile layout) UserProfileService.Save(layout with { Name = name.Text, Description = description.Text });
            if (list.SelectedItem is BootSystemUserProfile system) UserProfileService.Save(system with { Name = name.Text, Description = description.Text });
            Refresh();
        });
        delete = Action("Delete user profile", () =>
        {
            if (list.SelectedItem is CpmLayoutProfile layout) UserProfileService.DeleteLayout(layout);
            if (list.SelectedItem is BootSystemUserProfile system) UserProfileService.DeleteBoot(system);
            Refresh();
        });
        load.IsEnabled = duplicate.IsEnabled = rename.IsEnabled = delete.IsEnabled = false;
        load.ToolTip = duplicate.ToolTip = "Select a profile first.";
        ToolTipService.SetShowOnDisabled(load, true); ToolTipService.SetShowOnDisabled(duplicate, true);
        rename.ToolTip = delete.ToolTip = "Select a user profile; built-in Sharp profiles are read-only.";
        ToolTipService.SetShowOnDisabled(rename, true); ToolTipService.SetShowOnDisabled(delete, true);
        Refresh();
    }
}
