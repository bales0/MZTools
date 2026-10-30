using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MZTools;

internal sealed record QuickDiskVisualFinding(string Severity,string Description,long? Start=null,long? End=null);
internal sealed class QuickDiskVisualSnapshot
{
    internal QuickDiskLayout Layout {get;}
    internal IReadOnlyList<QuickDiskVisualFinding> Findings {get;}
    internal IReadOnlyList<string[]> Statistics {get;}
    internal string Hash {get;}
    internal string Host {get;}
    internal async Task ExportRegionAsync(QuickDiskRegion region,string path,bool overwrite,CancellationToken cancellation)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            byte[] bytes=await Task.Run(()=>Layout.GetBlockBytes(region),cancellation);
            await File.WriteAllBytesAsync(temporary,bytes,cancellation);
            cancellation.ThrowIfCancellationRequested();File.Move(temporary,path,overwrite);
        } finally {if(File.Exists(temporary)) File.Delete(temporary);}
    }
    private byte[]? generatedCells;
    // Logical coordinates stay in bytes. Only the detail signal is synthesized.
    internal int SignalBit(long cell)
    {
        if(Layout.IsPhysical) return Bit(cell);
        if(cell<0 || cell>=Layout.Length*16) throw new ArgumentOutOfRangeException(nameof(cell));
        generatedCells??=QuickDiskMfmCodec.Encode(Layout.Image);
        return (generatedCells[checked((int)(cell/8))]>>(int)(cell%8))&1;
    }
    internal static QuickDiskVisualSnapshot Capture(TapeDocument document)
    {
        bool preview=document.IsModified || document.QuickDiskSourceImage==null;
        return new(preview?QuickDiskLayoutBuilder.BuildPreviewImage(document):document.QuickDiskSourceImage!,document.Format,preview);
    }
    internal QuickDiskVisualSnapshot(byte[] bytes,TapeDocumentFormat format,bool preview=false)
    {
        bytes=(byte[])bytes.Clone(); Hash=ImageVerificationService.Hash(bytes);
        var findings=new List<QuickDiskVisualFinding>();
        try { Layout=QuickDiskLayoutBuilder.Read(bytes,format,preview); }
        catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException) {
            findings.Add(new("Error",ex.Message));
            // A failed decode must still allow inspection of the original container.
            Layout=new(format,preview,false,bytes.Length,0,null,null,
                bytes.Length==0?Array.Empty<QuickDiskRegion>():new[] {new QuickDiskRegion(QuickDiskRegionKind.Gap,0,bytes.Length,"Undecoded container bytes")}) {Image=bytes};
        }
        Host="SHARP MZ logical blocks";
        string confidence="Validated logical blocks",integrity=format==TapeDocumentFormat.Qdf?"SHARP frame CRCs validated by existing reader":"Validated by existing reader; compact CRC markers are not measured CRCs";
        if(format is TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy) {
            Host="Unknown"; confidence="Unknown"; integrity="No complete native frame sequence certified";
            try {
                var analysis=Layout.HostAnalysis??QdImageReaderWriter.Read(bytes).Analysis; var identification=analysis.Identification;
                Host=identification.DisplayName; confidence=identification.Confidence.ToString();
                foreach(string warning in identification.Warnings) findings.Add(new("Warning",warning));
                if(identification.IsBlank) findings.Add(new("Info","Blank / unformatted physical track; no files inferred."));
                else if(identification.HostFormat==QuickDiskHostFormat.Unknown) findings.Add(new("Warning","Host format is unknown; unassigned regions are not necessarily free."));
                switch(analysis.Content) {
                    case SharpMzQuickDiskContent: integrity="Complete SHARP frame sequence and CRCs validated by existing reader"; break;
                    case RolandQuickDiskContent roland:
                        foreach(var block in roland.Blocks.Where(b=>!b.IntegrityValid || !b.IsComplete)) findings.Add(new("Warning",$"Roland block {block.Index}: invalid CRC or truncated block",block.CellOffset,block.CellOffset+block.CellLength));
                        integrity="Roland block CRC/completeness from existing host decoder"; break;
                    case AkaiQuickDiskContent akai:
                        if(!akai.Block.IntegrityValid || !akai.Block.IsComplete) findings.Add(new("Warning","Akai block: invalid CRC or truncated block",akai.Block.CellOffset,akai.Block.CellOffset+akai.Block.CellLength));
                        integrity="Akai block CRC/completeness from existing host decoder"; break;
                    case Mo5QuickDiskContent mo5:
                        foreach(var sector in mo5.Sectors.Where(s=>!s.HeaderValid || !s.DataValid || s.Duplicate)) findings.Add(new("Warning",$"MO5 ID {sector.SectorId}: header {sector.HeaderValid}, data {sector.DataValid}, duplicate {sector.Duplicate}",sector.CellOffset,sector.CellOffset+sector.CellLength));
                        integrity="MO5 header/data checksums and duplicates from existing decoder"; break;
                }
            } catch(Exception ex) when(ex is InvalidDataException or ArgumentException or OverflowException) { findings.Add(new("Error",ex.Message)); }
        }
        if(findings.Any(f=>f.Severity=="Error")) { if(!Layout.IsPhysical) Host="Unvalidated container"; confidence="Failed / unavailable";integrity="Decode failed; see findings"; }
        Findings=findings.Distinct().ToArray();
        Statistics=new List<string[]> {
            new[] {"Container",QuickDiskFormatLabels.Display(format)}, new[] {"Image",preview?"Rebuilt preview; not original positions":"Original stored image"},
            new[] {"Coordinates",Layout.IsPhysical?"Stored track bitcells, LSB-first; not flux timing":"Logical container bytes; no physical track positions"},
            new[] {"SHA-256",Hash},new[] {"Host",Host},new[] {"Confidence",confidence},new[] {"Integrity",integrity},
            new[] {"Length",$"{Layout.Length:N0} {Layout.Unit}"},new[] {"Files",Layout.Regions.Where(r=>r.FileIndex>=0).Select(r=>r.FileIndex).Distinct().Count().ToString()},
            new[] {"Regions",Layout.Regions.Count.ToString()},new[] {"Findings",Findings.Count.ToString()}
        };
        if(Layout.IsPhysical) {
            var rows=(List<string[]>)Statistics;
            rows.Add(new[] {"Track file offset",$"0x{Layout.TrackFileOffset:X}"});
            rows.Add(new[] {"Data window",$"[{Layout.WindowStart}, {Layout.WindowEnd}) cells"});
            var container=HxcFlashFloppyQdContainer.Parse(bytes,format==TapeDocumentFormat.QdHxc?QdImageFormat.HxcPhysical:QdImageFormat.FlashFloppyPhysical);
            rows.Add(new[] {"Container bit rate",$"{container.BitRate} bit/s; nominal metadata, not measured timing"});
        }
    }
    internal int Bit(long cell)
    {
        if(!Layout.IsPhysical || cell<0 || cell>=Layout.Length) throw new ArgumentOutOfRangeException(nameof(cell));
        return (Layout.Image[checked((int)(Layout.TrackFileOffset+cell/8))]>>(int)(cell%8))&1;
    }
    internal byte Byte(QuickDiskRegion region,int index)
    {
        if(!Layout.Regions.Contains(region) || index<0) throw new ArgumentOutOfRangeException(nameof(index));
        if(Layout.HostBytes.TryGetValue(region,out var host)) return host[index];
        if(!Layout.IsPhysical) {if(index>=region.Length) throw new ArgumentOutOfRangeException(nameof(index));return Layout.Image[checked((int)region.Start+index)];}
        long first=region.Start+index*16L; if(first+14>=region.End) throw new ArgumentOutOfRangeException(nameof(index));
        byte value=0; for(int bit=0;bit<8;bit++) value|=(byte)(Bit(first+bit*2)<<bit); return value;
    }
    internal long ByteStart(QuickDiskRegion region)
    {
        // MO5 HostBytes contains payload only, while the mapped region includes
        // header, sync, data marker and checksum. The decoder ends it after checksum.
        return region.Kind==QuickDiskRegionKind.HostSector && Layout.HostBytes.TryGetValue(region,out var payload) && payload.Length>0
            ? region.End-(payload.Length+1L)*16 :region.Start;
    }
    internal ReportPresentation Report()
    {
        var tables=new[] {
            new ReportTable("Statistics",new[] {"Property","Value"},Statistics),
            new ReportTable("Regions",new[] {"Role","Name","File","Start","End exclusive","Length","Units"},Layout.Regions.Select(r=>new[] {r.Kind.ToString(),r.Name,r.FileNumber,r.Start.ToString(CultureInfo.InvariantCulture),r.End.ToString(CultureInfo.InvariantCulture),r.Length.ToString(CultureInfo.InvariantCulture),Layout.Unit}).ToArray()),
            new ReportTable("Findings",new[] {"Severity","Description","Start","End exclusive"},Findings.Select(f=>new[] {f.Severity,f.Description,f.Start?.ToString(CultureInfo.InvariantCulture)??"",f.End?.ToString(CultureInfo.InvariantCulture)??""}).ToArray())
        };
        return new("QuickDisk visual analysis",Layout.Summary,string.Join("\n",tables.Select(t=>t.Title+"\n"+string.Join("\n",t.Rows.Select(row=>string.Join(" | ",row))))),tables);
    }
    internal string Export(string format)
    {
        var report=Report();
        if(format=="json") return JsonSerializer.Serialize(new {Summary=Layout.Summary,Hash,Host,Statistics,Regions=Layout.Regions,Findings},new JsonSerializerOptions {WriteIndented=true});
        if(format=="txt") return report.ExportText;
        if(format!="csv") throw new ArgumentException("Unsupported report format.");
        string Q(string s)=>"\""+s.Replace("\"","\"\"")+"\"";
        var text=new StringBuilder(); foreach(var table in report.Tables) {
            text.AppendLine(Q(table.Title)); text.AppendLine(string.Join(",",table.Columns.Select(Q)));
            foreach(var row in table.Rows) text.AppendLine(string.Join(",",row.Select(Q)));
        } return text.ToString();
    }
}
