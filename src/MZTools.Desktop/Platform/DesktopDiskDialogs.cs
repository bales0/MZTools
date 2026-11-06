using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

public sealed partial class DesktopDialogs : IDiskDialogs
{
    public Task<bool> ConfirmDiskAsync(string title,string report)=>ConfirmPreviewAsync(title,report);
    public Task<DiskAnswer?> DiskFormAsync(string title,IReadOnlyList<DiskField> fields,string explanation="")
    {
        var values=new Dictionary<string,Control>();var rows=new Dictionary<string,Control>();var controls=new List<Control>();
        if(explanation.Length!=0)controls.Add(new TextBlock {Text=explanation,TextWrapping=Avalonia.Media.TextWrapping.Wrap});
        foreach(var field in fields)
        {
            Control input;
            if(field.Multiple&&field.Choices is { } items)
            {
                var list=new ListBox {ItemsSource=items,SelectionMode=SelectionMode.Multiple,Height=160};
                string[] selected;
                try{selected=System.Text.Json.JsonSerializer.Deserialize<string[]>(field.Value)??[];}catch(System.Text.Json.JsonException){selected=[field.Value];}
                foreach(var item in selected.Where(items.Contains))list.SelectedItems!.Add(item);input=list;
            }
            else input=field.Choices is { } choices?new ComboBox {ItemsSource=choices,SelectedItem=choices.Contains(field.Value)?field.Value:choices.FirstOrDefault()}:
                new TextBox {Text=field.Value,AcceptsReturn=field.Multiline,MinHeight=field.Multiline?120:0};
            values.Add(field.Key,input);var row=Label(field.Label,input);rows.Add(field.Key,row);controls.Add(row);
        }
        if(title=="Disk format")DiskFormatForm.Attach(values,rows,controls);
        if(title=="Batch process · preflight")BatchProcessForm.Attach(values,rows);
        DiskAnswer ReadAnswer()
        {
            var answer=new DiskAnswer(values.ToDictionary(p=>p.Key,p=>p.Value is ListBox list?System.Text.Json.JsonSerializer.Serialize(list.SelectedItems!.OfType<string>()):p.Value is ComboBox box?(string?)box.SelectedItem??"":((TextBox)p.Value).Text??""));
            if(title=="Disk format")
            {
                var profile=Enum.Parse<DskNewFormat>(answer["format"]);
                _=DiskFormatForm.Capacity(profile,answer.Number("tracks"),answer.Number("sides"),answer.Number("sectors"),answer.Number("size"));
                if(profile==DskNewFormat.CustomRaw)
                {
                    try{_=DskDocumentFactory.GetRawSectorIds(answer.Number("sectors"),1,Enum.Parse<DskDocumentFactory.RawSectorOrder>(answer["order"]),string.IsNullOrWhiteSpace(answer["ids"])?null:answer["ids"].Split(',').Select(int.Parse).ToArray());}
                    catch(ArgumentException error){throw new FormatException(error.Message,error);}
                }
            }
            return answer;
        }
        return FormAsync(title,controls,ReadAnswer);
    }
    public async Task<IReadOnlyList<string>> PickDiskFilesAsync(string title,bool multiple=false)
    {
        var files=await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title=title,AllowMultiple=multiple});
        return files.Select(f=>f.TryGetLocalPath()??throw new NotSupportedException("A local file is required.")).ToArray();
    }
    public async Task<IReadOnlyList<string>> PickBatchFilesAsync(string title,string extensions)
    {
        var patterns=extensions.Split(',',StringSplitOptions.RemoveEmptyEntries).Select(extension=>"*"+extension).ToArray();
        var files=await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions{Title=title,AllowMultiple=true,FileTypeFilter=[new("Supported batch inputs"){Patterns=patterns},FilePickerFileTypes.All]});
        return files.Select(f=>f.TryGetLocalPath()??throw new NotSupportedException("A local file is required.")).ToArray();
    }
    public async Task<string?> PickDiskOutputAsync(string title,string suggestedName)
    {
        var file=await owner.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title=title,SuggestedFileName=suggestedName,ShowOverwritePrompt=false});
        return file is null?null:file.TryGetLocalPath()??throw new NotSupportedException("A local path is required.");
    }
}
