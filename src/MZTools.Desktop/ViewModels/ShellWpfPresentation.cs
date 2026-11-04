namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    public string WpfStatus=>Documents.IsBusy?Documents.Status:ActiveTape?.WpfStatus??(ActiveDisk is{} disk?disk.Summary+" · "+disk.Capacity:Documents.Status);
}
