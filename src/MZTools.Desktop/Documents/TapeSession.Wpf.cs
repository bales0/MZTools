using System.Globalization;

namespace MZTools.Desktop.Documents;

public sealed partial class RecordRow
{
    public bool Editable {get;}
    public string TypeLabel=>Utility.ConvertFtypeToDescription(Record.Header.MzfFtype);
    public string SizeDecimal=>Record.Header.MzfSize.ToString(CultureInfo.InvariantCulture);
    public string SizeHex=>$"0x{Record.Header.MzfSize:X4}";
    public string Trailing=>$"{Record.Body.TrailingData?.Length??0} B";
    public string StartLabel=>"0x"+Load;
    public string ExecLabel=>"0x"+Execute;
    public string CompressionLabel=>MzfLoaderBuilder.DetectCompression(Record);
    public string Description=>SharpMzEncoding.ConvertMzfDescriptionToASCIIString(Record.DescriptionRaw);
    public string Loader=>TapeProfileComponents.Split(Record.Profile).LoaderType;
    public string Speed=>TapeProfileComponents.Split(Record.Profile).Speed;
    public IReadOnlyList<string> Loaders=>TapeProfileComponents.LoaderTypes;
    public IReadOnlyList<string> Speeds=>TapeProfileComponents.GetAvailableSpeeds(Loader);
}

public sealed partial class TapeSession
{
    public string WpfStatus
    {
        get
        {
            long declared=Model.Records.Sum(r=>(long)r.Body.DataSize),trailing=Model.Records.Sum(r=>(long)(r.Body.TrailingData?.Length??0)),container=Model.ContainerTrailingData.Length;
            long qdf=QDFFileReader.HeaderSize+Model.Records.Sum(r=>(long)QDFFileReader.FileOverhead+r.Body.DataSize),mzq=8+Model.Records.Sum(r=>84L+r.Body.DataSize),qd=mzq+8;
            string detail=trailing>0||container>0?$" ({declared} declared + {trailing} record trailing + {container} container trailing)":"";
            return $"Total {Model.Records.Count} files occupy {declared+trailing+container} bytes{detail}, est. {(float)qdf/819.36:F0}% QDF, {(float)mzq/614.71:F0}% MZQ or {(float)qd/614.55:F0}% QD.";
        }
    }
    // Same profile composition and byte-preserving clone/apply path as the WPF row handlers.
    internal void EditInline(RecordRow row,string? newName=null,string? loader=null,string? speed=null)
    {
        EnsureEditable();
        if(HasDraft)throw new InvalidOperationException("Apply or cancel the pending properties first.");
        if(row.Index<0||row.Index>=Model.Records.Count||!ReferenceEquals(row.Record,Model.Records[row.Index]))
            throw new InvalidOperationException("The record changed. Select its current row again.");
        var indices=newName is not null?new[]{row.Index}:Selection.Contains(row.Index)?Selection.ToArray():[row.Index];
        var proposed=Model.Records.Select(r=>r.DeepClone()).ToList();bool changed=false;
        foreach(int index in indices)
        {
            var record=proposed[index];
            if(newName is not null)
            {
                byte[] encoded=TapeFileNameEncoding.Encode(newName);
                if(record.Header.MzfFname.AsSpan().SequenceEqual(encoded))continue;
                var header=record.Header;header.MzfFname=encoded;record.Header=header;
            }
            else
            {
                var original=TapeProfileComponents.Split(record.Profile);
                var profile=TapeProfileComponents.Combine(loader??original.LoaderType,speed??original.Speed);
                if(profile==record.Profile)continue;
                record.Profile=profile;
            }
            record.MetadataOrigin=MetadataOrigin.CreatedOrModifiedInAdvanced;changed=true;
        }
        if(changed)ApplyRecords(proposed);
    }
}
