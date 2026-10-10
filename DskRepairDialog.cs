using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MZTools;

internal sealed class DskRepairDialog : Window
{
    internal byte[]? Result { get; private set; }
    internal DskRepairDialog(Window owner, byte[] source)
    {
        Owner = owner; Title = "Guided DSK Container Repair"; Width = 820; Height = 360; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(12) }; Content = panel;
        panel.Children.Add(new TextBlock { Text = "Repairs are explicit. Nothing changes until Apply. Save remains a separate action.\nAmbiguous C/H, sector IDs, FDC status, missing sectors and filesystem conflicts are never repaired automatically.", TextWrapping = TextWrapping.Wrap });
        foreach (var plan in DskContainerRepairService.Inspect(source))
        {
            var button = new Button { Content = $"{plan.Risk}: {plan.Description}", Margin = new Thickness(0, 10, 0, 0), IsEnabled = plan.Preview != null };
            panel.Children.Add(button);
            button.Click += (_, _) =>
            {
                var dialog = new DskPatchPreviewWindow(this, plan.Preview!.Report, () =>
                {
                    if (plan.RequiresTruncateConfirmation && MessageBox.Show(this, "Permanently discard all trailing bytes when this result is saved?", "Confirm destructive truncate", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                        throw new InvalidOperationException("Truncate was not confirmed; no changes were applied.");
                    Result = plan.Preview.Result;
                }, "Container Repair Preview", "Apply repair");
                dialog.ShowDialog(); if (dialog.Applied) Close();
            };
        }
        var close = new Button { Content = "Cancel", IsCancel = true, Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right }; panel.Children.Add(close); close.Click += (_, _) => Close();
    }
}
