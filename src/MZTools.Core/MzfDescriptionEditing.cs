using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum MzfDescriptionKind { Text, ContainsRecognizedLoaderOrCode, SuspectedBinaryOrCode, Unknown }
internal sealed record MzfDescriptionInspection(MzfDescriptionKind Kind,string Evidence,int? Offset=null,int? Length=null)
{
    internal bool RetainedFromOriginal {get;init;}
    internal bool Warn=>Kind!=MzfDescriptionKind.Text;
    internal string Warning=>!Warn?"":$"⚠ {(RetainedFromOriginal?"Original Description":"Description")} {Evidence}. Editing these bytes may prevent the program from starting. Editing is allowed.";
}
internal static class MzfDescriptionEditing
{
    internal const int Offset=24,Length=104;
    internal static byte[] EncodeText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if(text.Length>Length)throw new InvalidDataException("Description exceeds 104 encoded bytes. No text was truncated.");
        byte[] encoded=SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(text);
        for(int i=0;i<text.Length;i++)
            if(text[i]<0x20||text[i]>0x7f||SharpMzEncoding.FromSHASCII(encoded[i])!=text[i])
                throw new InvalidDataException($"Character at position {i+1} cannot be represented in SHARP Description. Use HEX for arbitrary bytes.");
        var result=new byte[Length];encoded.CopyTo(result,0);return result;
    }
    internal static string DecodeText(ReadOnlySpan<byte> bytes)
    {
        if(bytes.Length!=Length)throw new ArgumentException("Description must contain exactly 104 bytes.");
        int count=bytes.Length;while(count>0&&bytes[count-1]==0)count--;
        var text=new char[count];
        for(int i=0;i<count;i++){byte c=SharpMzEncoding.FromSHASCII(bytes[i]);text[i]=char.IsControl((char)c)?' ':(char)c;}
        return new string(text); // Preserve spaces; this projection is NEVER serialized unless the text actually changes.
    }
    internal static MzfDescriptionInspection Inspect(TapeRecord record)
    {
        if(MzfLoaderBuilder.TryGetCompressionInfo(record,out var info)&&info is {EmbeddedLoader:true})
            return new(MzfDescriptionKind.ContainsRecognizedLoaderOrCode,$"contains a recognized {info.Algorithm} embedded decoder at header offsets 0x18–0x{Offset+info.DecoderLength-1:X2}",Offset,info.DecoderLength);
        if(record.DescriptionEditRisk is {Warn:true} retained)return retained;
        var raw=record.DescriptionRaw;int unusual=0,first=-1,last=-1;
        for(int i=0;i<raw.Length;i++)
        {
            byte b=raw[i];bool text=b is 0 or 0x0d||b is >=0x20 and <=0x5d||b==0x80||b is >=0x90 and <=0xc0;
            if(!text){unusual++;if(first<0)first=i;last=i;}
        }
        if(unusual==0)return new(MzfDescriptionKind.Text,"is compatible with the supported SHARP text mapping; absence of a loader signature does not prove safe editing");
        return new(unusual>=4?MzfDescriptionKind.SuspectedBinaryOrCode:MzfDescriptionKind.Unknown,
            $"{(unusual>=4?"may contain code or binary data":"contains bytes outside the supported text mapping (possibly graphics or binary data)")} at header offsets 0x{Offset+first:X2}–0x{Offset+last:X2}; no specific loader was identified",Offset+first,last-first+1);
    }
    internal static TapeRecord Replace(TapeRecord original,ReadOnlySpan<byte> description)
    {
        if(description.Length!=Length)throw new ArgumentException("Description must contain exactly 104 bytes.");
        if(description.SequenceEqual(original.DescriptionRaw))return original.DeepClone();
        byte[] bytes=TapeDocumentWriter.SerializeMzf(original,true);description.CopyTo(bytes.AsSpan(Offset,Length));
        return MzfRecordEditing.ParseEdited(original,bytes);
    }
}

/// <summary>Whole-file MZF overwrite editing. One parser reconstructs RawHeader, Header, payload and trailing bytes.</summary>
internal static class MzfRecordEditing
{
    internal static TapeRecord ParseEdited(TapeRecord original,byte[] bytes)
    {
        int originalLength=checked(TapeRecord.HeaderLength+original.Body.DataSize+(original.Body.TrailingData?.Length??0));
        if(bytes.Length!=originalLength)throw new InvalidDataException("HEX editing overwrites bytes without inserting or deleting them.");
        if(bytes.Length<128||bytes[0]==0)throw new InvalidDataException("The edited MZF requires a complete 128-byte header and a nonzero file type.");
        using var stream=new MemoryStream(bytes,false);using var reader=new BinaryReader(stream);
        var record=new MZTFileReader().ReadMzfRecord(reader);
        var body=record.Body;body.TrailingData=reader.ReadBytes(checked((int)(stream.Length-stream.Position)));record.Body=body;
        record.Profile=original.Profile;record.MetadataOrigin=original.MetadataOrigin;
        record.DescriptionEditRisk=MzfDescriptionEditing.Inspect(original) with {RetainedFromOriginal=true};
        record.SourceFingerprints=original.SourceFingerprints;record.SourceProfile=original.SourceProfile;
        // Parsing must not reinterpret or normalize any byte of the resulting complete file.
        if(!TapeDocumentWriter.SerializeMzf(record,true).SequenceEqual(bytes))throw new InvalidDataException("Edited MZF did not round-trip byte-exactly.");
        return record;
    }
    internal static IReadOnlyDictionary<string,string?> CaptureSources(string path)
    {
        string main=Path.GetFullPath(path),side=SidecarService.GetSidecarPath(main);
        return new Dictionary<string,string?>(AtomicFileTransaction.PathComparer){[main]=AtomicFileTransaction.Hash(main),[side]=AtomicFileTransaction.Hash(side)};
    }
}
