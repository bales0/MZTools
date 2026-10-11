using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace MZTools;

internal static class DskVisualReport
{
    internal static string Export(DskVisualSnapshot model, string format, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if(model.PhysicalImage is {} physical) return ExportPhysical(model,physical,format,token);
        if(format == "json") return JsonSerializer.Serialize(new {
            DataOrigin = model.OriginText, Surface = model.SurfaceText,
            Units = new { Length="bytes", Offset="bytes from container start", Surface="normalized schematic" },
            MeasuredFlux=false, ActualTiming=model.TimingText, PhysicalCrc=model.TimingText,
            GeneratedMfm = new { DataOrigin="SYNTHETIC / GENERATED MFM", GeneratedFrom="DSK", MeasuredFlux=false, NominalRpm=300,
                Availability="Generated on demand with FloppyTrackCodec.Encode; not captured source data" },
            model.Cylinders, model.Sides, model.FileSystem, model.TrailingBytes, model.Tracks, model.Issues
        },new JsonSerializerOptions { WriteIndented=true, Converters = { new JsonStringEnumConverter() } });
        var text = new StringBuilder();
        string Cell(object? v) => "\""+(Convert.ToString(v,CultureInfo.InvariantCulture)??"").Replace("\"","\"\"")+"\"";
        void Row(params object?[] cells) => text.AppendLine(string.Join(",",cells.Select(Cell)));
        if(format == "csv") {
            Row("Table","Track","Slot side","Descriptor","C","H","R","N","Stored bytes","Declared bytes","Descriptor offset","Data offset","End offset","ST1","ST2","Role","Owner","Issue count","DataOrigin","Detail");
            Row("Provenance",null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,null,model.OriginText,model.SurfaceText+"; physical CRC / flux: "+model.TimingText+"; GeneratedFrom=DSK; measuredFlux=false; MFM synthetic, generated on demand");
            foreach(var t in model.Tracks) { token.ThrowIfCancellationRequested();
                Row("Track",t.PhysicalIndex,t.SlotSide,null,t.DeclaredCylinder,t.DeclaredSide,null,null,t.StoredBytes,t.DeclaredBytes,null,t.FileOffset,t.FileOffset+t.BlockSize,null,null,null,null,t.IssueCount,model.OriginText,$"Missing={t.Missing}; padding={t.PaddingBytes}; descriptor R order={t.DescriptorSequence}");
                foreach(var s in t.Sectors) Row("Sector",s.Track,t.SlotSide,s.Descriptor,s.C,s.H,s.R,s.N,s.StoredBytes,s.DeclaredBytes,s.DescriptorOffset,s.DataOffset,s.EndOffset,$"0x{s.St1:X2}",$"0x{s.St2:X2}",s.Role,s.Ownership,s.IssueCount,model.OriginText,s.Status);
            }
            foreach(var issue in model.Issues) Row("Issue",issue.Track,null,issue.Sector,null,null,null,null,null,null,null,null,null,null,null,null,issue.FileKey,null,"Stored container / existing analyzer",$"{issue.Severity} {issue.Code}: {issue.Explanation}");
            return text.ToString();
        }
        text.AppendLine("MZTools DSK visual analysis — read-only").AppendLine(model.OriginText).AppendLine(model.SurfaceText)
            .AppendLine($"Filesystem: {model.FileSystem}; {model.Cylinders} cylinders × {model.Sides} sides; trailing {model.TrailingBytes} B")
            .AppendLine("Actual rotation / flux / physical CRC / weak bits: "+model.TimingText)
            .AppendLine("Generated MFM: synthetic, GeneratedFrom=DSK; measuredFlux=false; nominal 300 RPM; generated on demand.");
        foreach(var group in model.Issues.GroupBy(i=>i.Severity)) text.AppendLine($"{group.Key}: {group.Count()}");
        foreach(var t in model.Tracks) { token.ThrowIfCancellationRequested();
            text.AppendLine().AppendLine($"{t.Label}; slot C{t.SlotCylinder}/H{t.SlotSide}; block {t.BlockSize} B; padding {t.PaddingBytes} B; GAP3={t.Gap3}; filler={t.Filler}; stored rate/mode codes={t.DataRateCode}/{t.RecordingCode}")
                .AppendLine("Interleave pattern inferred from descriptor order: "+t.DescriptorSequence);
            foreach(var s in t.Sectors) text.AppendLine($"  {s.Label}: stored={s.StoredBytes} B declared={s.DeclaredBytes} B parsed N={s.ParsedNBytes}; descriptor=0x{s.DescriptorOffset:X}; data=[0x{s.DataOffset:X},0x{s.EndOffset:X}); ST1={s.St1:X2} ST2={s.St2:X2}; {s.Status}; role={s.Role}; owner={s.Ownership}; issues={s.IssueCount}; SHA256={s.DataSha256}");
        }
        foreach(var issue in model.Issues) text.AppendLine().AppendLine(issue.Explanation);
        return text.ToString();
    }
    private static string ExportPhysical(DskVisualSnapshot model,HfeImage physical,string format,CancellationToken token)
    {
        var rows=physical.Tracks.Select((t,i)=>new {
            Track=i,t.Cylinder,t.Side,t.Encoding,t.ContainerEncoding,t.BitCellCount,t.BitRate,t.Rpm,
            WeakCells=t.WeakBitMask.Sum(b=>System.Numerics.BitOperations.PopCount((uint)b)),
            IndexCells=t.IndexCells.ToArray(),Timing=t.Timing.Select(p=>new {p.CellOffset,p.Divisor,p.BitRate}).ToArray(),
            Controls=t.V2Controls.Select(c=>new {c.CellOffset,c.Opcode,c.Value,c.Reserved}).ToArray(),
            Sectors=t.Sectors.Select(s=>new {s.C,s.H,s.R,s.N,s.HeaderCell,s.DataCell,s.HeaderCrcValid,s.DataCrcValid,s.DataMark,Bytes=s.Data.Length}).ToArray()
        }).ToArray();
        token.ThrowIfCancellationRequested();
        if(format=="json") return JsonSerializer.Serialize(new {DataOrigin=model.OriginText,Surface=model.SurfaceText,MeasuredFlux=false,Format=physical.Variant,OffsetUnit="bit cells from track start",model.FileSystem,Tracks=rows,model.Issues},new JsonSerializerOptions {WriteIndented=true,Converters={new JsonStringEnumConverter()}});
        var text=new StringBuilder();
        string Q(object? value)=>"\""+(Convert.ToString(value,CultureInfo.InvariantCulture)??"").Replace("\"","\"\"")+"\"";
        void Row(params object?[] values)=>text.AppendLine(string.Join(",",values.Select(Q)));
        if(format=="csv") {Row("Table","Track","Side","R","Bytes","Header cell","Data cell","Header CRC","Data CRC","Details");Row("Provenance",null,null,null,null,null,null,null,null,physical.Variant+"; "+model.OriginText+"; offsets=track bit cells; measuredFlux=false");}
        else text.AppendLine("MZTools "+physical.Variant+" visual analysis — read-only").AppendLine(model.OriginText).AppendLine(model.SurfaceText).AppendLine("No measured flux. Offsets are physical cells within each track.");
        foreach(var t in rows) {
            token.ThrowIfCancellationRequested();
            Row("Track",t.Track,t.Side,null,t.BitCellCount,null,null,null,null,$"{t.Encoding}; rate={t.BitRate}; RPM={t.Rpm}; weak={t.WeakCells}; index={string.Join(';',t.IndexCells)}");
            foreach(var timing in t.Timing) Row("Timing",t.Track,t.Side,null,null,timing.CellOffset,null,null,null,$"divisor={timing.Divisor}; bitrate={timing.BitRate}");
            foreach(var control in t.Controls) Row("V2 control",t.Track,t.Side,null,null,control.CellOffset,null,null,null,$"opcode={control.Opcode}; value={control.Value}; reserved={control.Reserved}");
            foreach(var s in t.Sectors) Row("Sector",t.Track,t.Side,s.R,s.Bytes,s.HeaderCell,s.DataCell,s.HeaderCrcValid,s.DataCrcValid,model.Sectors.First(v=>v.Track==t.Track && v.DataOffset==s.DataCell).Ownership);
        }
        foreach(var issue in model.Issues) Row("Issue",issue.Track,null,issue.Sector,null,null,null,null,null,issue.Explanation);
        return text.ToString();
    }

}
