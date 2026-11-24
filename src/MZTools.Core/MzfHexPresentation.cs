using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;

namespace MZTools;

internal enum MzfHexField { Type, Name, NameEnd, Length, Load, Execute, Description, Data, Trailing }
internal static class MzfHexPresentation
{
    internal static MzfHexField Field(int offset,int payloadLength)=>offset switch
    {0=>MzfHexField.Type,<=16=>MzfHexField.Name,17=>MzfHexField.NameEnd,<=19=>MzfHexField.Length,<=21=>MzfHexField.Load,<=23=>MzfHexField.Execute,<=127=>MzfHexField.Description,_=>offset<128+payloadLength?MzfHexField.Data:MzfHexField.Trailing};
    internal static string Label(MzfHexField field)=>field switch
    {MzfHexField.Type=>"Type",MzfHexField.Name=>"Name",MzfHexField.NameEnd=>"Name terminator",MzfHexField.Length=>"Payload length",MzfHexField.Load=>"LOAD",MzfHexField.Execute=>"EXEC",MzfHexField.Description=>"Description",MzfHexField.Data=>"Data",_=>"Trailing"};
    internal static uint Color(MzfHexField field)=>field switch
    {MzfHexField.Type=>0xfff6c7c7,MzfHexField.Name=>0xffd2edf4,MzfHexField.NameEnd=>0xffeeeeee,MzfHexField.Length=>0xffffebaa,MzfHexField.Load=>0xffccebc8,MzfHexField.Execute=>0xffe2d2ef,MzfHexField.Description=>0xfff5dfc5,MzfHexField.Data=>0xffffffff,_=>0xffe5e5e5};
    internal static int Payload(byte[] bytes)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(18,2));
    internal static ushort Load(byte[] bytes)=>BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(20,2));
    internal static string Location(byte[] bytes,int offset)
    {
        var field=Field(offset,Payload(bytes));
        string ram=field==MzfHexField.Data?$"0x{(Load(bytes)+offset-128)&0xffff:X4}":"—";
        return $"Offset 0x{offset:X8} · RAM {ram} · {Label(field)} · byte 0x{bytes[offset]:X2} · HEX / ASCII / MZASCII";
    }
}

/// <summary>One fixed-size draft used by both viewers. Bytes retains its identity so realized rows can repaint in place.</summary>
internal sealed class MzfHexBuffer
{
    private byte[] source;
    private readonly List<byte[]> undo=[];
    internal byte[] Bytes {get;}
    internal MzfHexBuffer(TapeRecord record){source=TapeDocumentWriter.SerializeMzf(record,true);Bytes=(byte[])source.Clone();}
    internal int Changes=>Bytes.Where((b,i)=>b!=source[i]).Count();
    internal void Set(byte[] next)
    {
        if(next.Length!=Bytes.Length)throw new InvalidOperationException("HEX editing cannot insert or delete bytes.");
        if(next.AsSpan().SequenceEqual(Bytes))return;
        undo.Add((byte[])Bytes.Clone());if(undo.Count>32)undo.RemoveAt(0);next.CopyTo(Bytes,0);
    }
    internal bool Type(int offset,bool lowNibble,string digits,out int next,out bool low)
    {
        next=offset;low=lowNibble;
        if(digits.Length==0||digits.Any(c=>!FixedHexInput.IsDigit(c))||offset<0)return false;
        int remaining=(Bytes.Length-offset)*2-(lowNibble?1:0);if(digits.Length>remaining)return false;
        var copy=(byte[])Bytes.Clone();
        foreach(char c in digits)
        {
            int value=c<='9'?c-'0':char.ToUpperInvariant(c)-'A'+10;
            copy[next]=(byte)(low?(copy[next]&0xf0)|value:(copy[next]&0x0f)|(value<<4));
            if(low)next++;low=!low;
        }
        Set(copy);return true;
    }
    internal bool Paste(int offset,string text,out int next)
    {
        next=offset;var tokens=text.Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        if(offset<0||tokens.Length==0||tokens.Any(t=>t.Length%2!=0||t.Any(c=>!FixedHexInput.IsDigit(c))))return false;
        return Type(offset,false,string.Concat(tokens),out next,out _);
    }
    internal void Undo(){if(undo.Count==0)return;undo[^1].CopyTo(Bytes,0);undo.RemoveAt(undo.Count-1);}
    internal void Revert()=>Set(source);
    internal void Accept(){source=(byte[])Bytes.Clone();undo.Clear();}
}
