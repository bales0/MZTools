using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskRepairDialog : Window
{
    internal byte[]? Result { get; private set; }
    internal bool OpenOriginalRequested { get; private set; }
    internal DskRepairDialog(Window owner, byte[] source, IReadOnlyList<DskRepairPlan>? plans = null,
        Func<DskRepairPlan, byte[]>? applyRepair = null, bool canOpenOriginal = false, string? diagnostic = null)
    {
        Owner = owner; Title = "DSK Container Repair"; Width = 820; Height = 420; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) }; Content = panel;
        var inspection = plans == null ? DskOpenCoordinator.Inspect(source) : null;
        var available = (plans ?? inspection!.ManualPlans).Where(p => p.Preview != null).ToArray();
        string status = available.Length > 0
            ? available.All(p => p.RequiresTruncateConfirmation)
                ? "Optional trailing-data removal is available. These bytes may contain unrelated data; opening the image does not require their removal."
                : "A container header repair is available. Review the exact changes before applying it."
            : inspection?.Status == DskOpenStatus.CannotSafelyRepair ? "Cannot safely repair this container."
            : inspection?.Status == DskOpenStatus.UnsupportedOrUnparseable ? "Unsupported or unparseable DSK container."
            : inspection?.Status == DskOpenStatus.Healthy ? "No container repair is needed."
            : "No applicable container repair is available.";
        panel.Children.Add(new TextBlock { Text = status, TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold });
        string details = diagnostic ?? inspection?.Diagnostic ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(details) && details != status)
            panel.Children.Add(new TextBlock { Text = details,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 8) });
        if (available.Length > 0)
            panel.Children.Add(new TextBlock { Text = "Changes are applied in memory only after preview and Apply. The original .dsk stays unchanged; Save As to a different path is required.\nFilesystem metadata repairs belong to Filesystem → Guided Repair.", TextWrapping = TextWrapping.Wrap });
        foreach (var plan in available)
        {
            panel.Children.Add(new TextBlock { Text = plan.Description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) });
            var button = new Button { Content = plan.RequiresTruncateConfirmation ? "Preview trailing-data removal..." : "Preview container repair...",
                Tag = plan.IssueCode, Margin = new Thickness(0, 4, 0, 0) };
            panel.Children.Add(button);
            button.Click += (_, _) =>
            {
                var dialog = new DskPatchPreviewWindow(this, plan.Preview!.Report, () =>
                {
                    Result = applyRepair == null ? plan.Preview.Result : applyRepair(plan);
                }, "Container Repair Preview", "Apply repair", confirmApply: () => !plan.RequiresTruncateConfirmation ||
                    MessageBox.Show(this, "Permanently discard all trailing bytes when this result is saved?", "Confirm destructive truncate", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes);
                dialog.ShowDialog(); if (dialog.Applied) Close();
            };
        }
        if (canOpenOriginal)
        {
            var original = new Button { Content = "Open without repair", Margin = new Thickness(0, 10, 0, 0) };
            panel.Children.Add(original);
            original.Click += (_, _) => { OpenOriginalRequested = true; Close(); };
        }
        var close = new Button { Content = available.Length > 0 ? "Cancel" : "Close", IsCancel = true, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(close); close.Click += (_, _) => Close();
    }
}
