namespace MZTools.Desktop.Documents;

public sealed partial class TapeSession
{
    private string description="",originalDescription="";
    public bool CanEditDescription=>CanEdit&&Model.Format==TapeDocumentFormat.Mzf&&Model.Records.Count==1&&SelectedRow is not null;
    public bool CanEditMzfBytes=>CanEditDescription&&!HasDraft;
    public string Description
    {
        get=>description;
        set{if(Set(ref description,value)){DraftChanged();Changed(nameof(DescriptionByteCount));Changed(nameof(CanEditMzfBytes));}}
    }
    private bool HasDescriptionDraft=>description!=originalDescription;
    public string DescriptionByteCount
    {
        get{try{_=MzfDescriptionEditing.EncodeText(description);return $"{description.Length}/104 encoded B · header 0x18–0x7F";}catch(InvalidDataException e){return e.Message;}}
    }
    public string DescriptionWarning=>SelectedRow is {} row?MzfDescriptionEditing.Inspect(row.Record).Warning:"";
    public bool HasDescriptionWarning=>DescriptionWarning.Length>0;
    public string DescriptionPolicy=>CanEditDescription
        ?"Changing this text replaces the 104-byte field; unused bytes become 00. Unchanged text preserves all original bytes. Use HEX to change individual bytes or code."
        :"Description text/HEX editing is available for a standalone editable MZF. Other host formats retain their existing restrictions.";
    private void LoadDescription()
    {
        description=originalDescription=SelectedRow is {} row?MzfDescriptionEditing.DecodeText(row.Record.DescriptionRaw):"";
        foreach(string p in new[]{nameof(Description),nameof(DescriptionByteCount),nameof(DescriptionWarning),nameof(HasDescriptionWarning),nameof(DescriptionPolicy),nameof(CanEditDescription),nameof(CanEditMzfBytes)})Changed(p);
    }
    internal void ApplyMzfBytes(TapeRecord original,byte[] bytes)
    {
        EnsureEditable();
        if(Model.Format!=TapeDocumentFormat.Mzf||Model.Records.Count!=1||HasDraft)throw new InvalidOperationException("Apply or cancel Properties edits first; HEX writes require an editable standalone MZF.");
        int index=Model.Records.IndexOf(original);
        if(index<0)throw new InvalidOperationException("The record changed while the HEX editor was open. Reopen the editor.");
        if(bytes.SequenceEqual(TapeDocumentWriter.SerializeMzf(original,true)))return;
        var replacement=MzfRecordEditing.ParseEdited(original,bytes);
        ApplyRecords([replacement]);
    }
}
