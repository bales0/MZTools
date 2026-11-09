using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

// Separate, non-destructive WPF-parity controls. Existing AudioPreviewCommand
// remains available for callers explicitly expecting selection-fit semantics.
public sealed partial class ShellViewModel
{
    private AsyncCommand? preserveAudioViewport;
    private AsyncCommand? shiftRightMinusOne;
    private AsyncCommand? shiftRightPlusOne;
    private AsyncCommand? shiftRightMinusTen;
    private AsyncCommand? shiftRightPlusTen;
    private bool audioParityCommandsSubscribed;

    private void EnsureAudioParityCommands()
    {
        if (preserveAudioViewport is not null) return;
        preserveAudioViewport = new(async () =>
        {
            var source = ActiveAudio;
            if (source is not null)
                await Documents.ExecuteAsync(ct => source.ApplyTransformKeepingViewportAsync(ct));
        }, () => ActiveAudio is not null && !Documents.IsBusy);
        shiftRightMinusOne = ShiftRightCommand(-1);
        shiftRightPlusOne = ShiftRightCommand(+1);
        shiftRightMinusTen = ShiftRightCommand(-10);
        shiftRightPlusTen = ShiftRightCommand(+10);
        if (!audioParityCommandsSubscribed)
        {
            audioParityCommandsSubscribed = true;
            Documents.PropertyChanged += (_, _) => RefreshAudioParityCommands();
        }
    }

    private AsyncCommand ShiftRightCommand(int count) => new(async () =>
    {
        var source = ActiveAudio;
        if (source is null) return;
        await Documents.ExecuteAsync(async ct =>
        {
            source.StepRightChannel(count);
            return await source.ApplyTransformKeepingViewportAsync(ct);
        });
    }, () => ActiveAudio?.CanAdjustRight == true && !Documents.IsBusy);

    private void RefreshAudioParityCommands()
    {
        preserveAudioViewport?.Refresh();
        shiftRightMinusOne?.Refresh();
        shiftRightPlusOne?.Refresh();
        shiftRightMinusTen?.Refresh();
        shiftRightPlusTen?.Refresh();
    }

    public AsyncCommand AudioPreviewKeepViewportCommand { get { EnsureAudioParityCommands(); return preserveAudioViewport!; } }
    public AsyncCommand AudioShiftRightMinusOneCommand { get { EnsureAudioParityCommands(); return shiftRightMinusOne!; } }
    public AsyncCommand AudioShiftRightPlusOneCommand { get { EnsureAudioParityCommands(); return shiftRightPlusOne!; } }
    public AsyncCommand AudioShiftRightMinusTenCommand { get { EnsureAudioParityCommands(); return shiftRightMinusTen!; } }
    public AsyncCommand AudioShiftRightPlusTenCommand { get { EnsureAudioParityCommands(); return shiftRightPlusTen!; } }
}
