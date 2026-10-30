using System.Text.Json;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

/// <summary>Application-owned UI preferences only. No device IDs, start flags, source PCM or user tape metadata are restored.</summary>
internal sealed class DesktopPreferences
{
    private readonly string path;
    private string? fingerprint;
    internal double Width {get;private set;}=1440;
    internal double Height {get;private set;}=960;
    internal string? LastFolder {get;private set;}
    internal string? Warning {get;private set;}
    private sealed record Data(int Schema,double Width,double Height,string? LastFolder);
    internal DesktopPreferences(string? root=null)
    {
        root??=Environment.GetEnvironmentVariable("MZTOOLS_SETTINGS_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),"MZTools");path=Path.Combine(Path.GetFullPath(root),"desktop.json");
        try
        {
            fingerprint=AtomicFileTransaction.Hash(path);if(fingerprint is null)return;var value=JsonSerializer.Deserialize<Data>(File.ReadAllBytes(path))??throw new InvalidDataException("Empty desktop settings.");
            if(value.Schema!=1||!double.IsFinite(value.Width)||!double.IsFinite(value.Height)||value.Width<640||value.Height<480||value.Width>8192||value.Height>8192)throw new InvalidDataException("Unsupported desktop settings.");
            Width=value.Width;Height=value.Height;LastFolder=value.LastFolder is { } folder&&Directory.Exists(folder)?folder:null;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException or JsonException){Warning="Desktop settings were not loaded: "+e.Message;}
    }
    internal void Save(double width,double height,string? folder=null)
    {
        if(!double.IsFinite(width)||!double.IsFinite(height))return;Width=Math.Clamp(width,640,8192);Height=Math.Clamp(height,480,8192);if(folder is not null&&Directory.Exists(folder))LastFolder=Path.GetFullPath(folder);
        try
        {
            var data=JsonSerializer.SerializeToUtf8Bytes(new Data(1,Width,Height,LastFolder),new JsonSerializerOptions{WriteIndented=true});AtomicFileTransaction.Write(new Dictionary<string,byte[]>{[path]=data},new Dictionary<string,string?>{[path]=fingerprint});fingerprint=AtomicFileTransaction.Hash(path);Warning=null;
        }
        catch(Exception e)when(e is IOException or UnauthorizedAccessException){Warning="Desktop settings were not saved: "+e.Message;}
    }
}
