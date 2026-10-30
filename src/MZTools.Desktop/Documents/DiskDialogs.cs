namespace MZTools.Desktop.Documents;

public sealed record DiskField(string Key,string Label,string Value="",IReadOnlyList<string>? Choices=null,bool Multiline=false,bool Multiple=false);
public sealed record DiskAnswer(IReadOnlyDictionary<string,string> Values)
{
    public string this[string key] => Values[key];
    public int Number(string key) => int.Parse(this[key],System.Globalization.CultureInfo.InvariantCulture);
}
public interface IDiskDialogs
{
    Task<DiskAnswer?> DiskFormAsync(string title,IReadOnlyList<DiskField> fields,string explanation="");
    Task<IReadOnlyList<string>> PickDiskFilesAsync(string title,bool multiple=false);
    Task<string?> PickDiskOutputAsync(string title,string suggestedName);
    Task<bool> ConfirmDiskAsync(string title,string report);
}
