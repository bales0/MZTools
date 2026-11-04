namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public double WpfAudioProgress=>Documents.IsBusy?Documents.ProgressFraction:ActiveAudio is null?0:1;
    public string WpfAudioStatus=>Documents.IsBusy?Documents.Status:ActiveAudio is null?"": "Analysis complete. Select an issue or double-click a region to locate it. Pulse durations and references are in µs.";
    public string WpfStatus=>Documents.IsBusy?Documents.Status:ActiveTape?.WpfStatus??(ActiveDisk is{} disk?disk.WpfInformation:Documents.Status);
}
