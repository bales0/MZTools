using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading;

namespace MZTools;

internal sealed record DskVisualSector(DskSectorAddress Address, byte C, byte H, byte R, byte N,
    byte St1, byte St2, int StoredBytes, int DeclaredBytes, long DescriptorOffset, long DataOffset,
    DskSectorRole Role, IReadOnlyList<DskAllocationOwner> Owners, IReadOnlyList<int> LogicalBlocks,
    IReadOnlyList<int> AllocationBlocks, IReadOnlyList<DskAnalysisIssue> Issues, [property: JsonIgnore] byte[] Data, bool PhysicalCells=false)
{
    public int Track => Address.Track;
    public int Descriptor => Address.Sector;
    public long EndOffset => DataOffset + StoredBytes*(PhysicalCells?16L:1L);
    public long? ParsedNBytes => N <= 7 ? 128L << N : null;
    public string Ownership => Owners.Count == 0 ? "Unassigned" : string.Join(", ", Owners.Select(OwnerLabel).Distinct());
    public string Users => string.Join(", ",Owners.Where(o=>o.User!=null).Select(o=>o.User!.Value).Distinct().Order());
    internal static string OwnerLabel(DskAllocationOwner owner)=>owner.User is int u?$"{owner.FileName} (user {u})":owner.FileName;
    public string Status => DskVisualSnapshot.StatusFlags(St1, St2);
    public bool? HeaderCrcValid=>PhysicalCells?(St1&0x20)==0:null;
    public bool? DataCrcValid=>PhysicalCells?(St2&0x20)==0:null;
    public int IssueCount => Issues.Count;
    public string DataSha256 => Convert.ToHexString(SHA256.HashData(Data));
    public string Label => $"#{Descriptor} R{R} ({C},{H},{R},{N})";
}

internal sealed record DskVisualTrack(int PhysicalIndex, int SlotCylinder, int SlotSide,
    int DeclaredCylinder, int DeclaredSide, long FileOffset, int BlockSize, int PaddingBytes,
    bool Missing, byte? Gap3, byte? Filler, byte? DataRateCode, byte? RecordingCode,
    IReadOnlyList<DskVisualSector> Sectors, IReadOnlyList<DskAnalysisIssue> Issues)
{
    public string Label => $"Track {PhysicalIndex} / C:{DeclaredCylinder} / H:{DeclaredSide}" + (Missing ? " — missing" : "");
    public int SectorCount => Sectors.Count;
    public int StoredBytes => Sectors.Sum(s=>s.StoredBytes);
    public int DeclaredBytes => Sectors.Sum(s=>s.DeclaredBytes);
    public int IssueCount => Issues.Count;
    public string DescriptorSequence => string.Join(", ", Sectors.Select(s=>s.R));
}

// Projection of the editor's existing analysis. No second filesystem reader or issue analyzer.
internal sealed class DskVisualSnapshot
{
    internal const string Origin = "CONTAINER / DESCRIPTOR ORDER; LOGICAL / FILESYSTEM mapping from DskAnalyzer";
    internal const string SurfaceOrigin = "Schematic / not measured angular positions; C0 outermost; clockwise descriptor order";
    internal const string Unavailable = "Not available in DSK";
    private readonly IReadOnlyList<DskImage.DskTrack?> generatedSource=[];
    internal HfeImage? PhysicalImage {get;}
    internal DskDocument? FileProjection {get;}
    internal bool IsPhysical=>PhysicalImage!=null;
    internal string SourceFormat=>PhysicalImage?.Variant??"DSK";
    internal string OriginText=>IsPhysical?"Stored HFE bit cells; decoded MFM/FM sectors; validated filesystem projection only":Origin;
    internal string SurfaceText=>IsPhysical?"Schematic surface; C0 outermost; clockwise decoded sector order (not measured flux)":SurfaceOrigin;
    internal string TimingText=>IsPhysical?"Stored cells, index, weak masks and declared timing; no measured flux":Unavailable;
    public int Cylinders { get; }
    public int Sides { get; }
    public string FileSystem { get; }
    public IReadOnlyList<DskVisualTrack> Tracks { get; }
    public IReadOnlyList<DskAnalysisIssue> Issues { get; }
    public int TrailingBytes { get; }
    public IEnumerable<DskVisualSector> Sectors => Tracks.SelectMany(t=>t.Sectors);
    internal DskVisualSnapshot(HfeImage image)
    {
        PhysicalImage=image; Cylinders=image.Cylinders; Sides=image.Sides;
        var semantic=HfeSemanticInspectionService.Inspect(image);
        DskLayoutModel? layout=semantic.ValidatedLayout;FileProjection=semantic.Projection;
        FileSystem=semantic.Available?semantic.Layout:"Raw / unknown";
        var findings=new List<DskAnalysisIssue>(layout?.Issues??new());
        if(!semantic.Available) findings.Add(new("HFE_FILESYSTEM",DskIssueSeverity.Info,"Filesystem interpretation unavailable",semantic.Report));
        Tracks=image.Tracks.Select((t,i)=> {
            if(t.Encoding==PhysicalEncoding.Unknown) findings.Add(new("HFE_ENCODING",DskIssueSeverity.Info,"Unknown encoding; raw cells remain inspectable",$"Container encoding {t.ContainerEncoding}; no decoded bytes invented",i));
            if(t.V2Controls.Any(c=>c.Opcode==1)) findings.Add(new("HFE_V2_BAUD",DskIssueSeverity.Info,"HFEv2 baud registers retained", "SPBRG registers are retained verbatim; oscillator is not specified, so local Hz is not inferred.",i));
            var sectors=t.Sectors.Select((s,j)=> {
                if(!s.HeaderCrcValid || !s.DataCrcValid) findings.Add(new("HFE_CRC",DskIssueSeverity.Warning,"Decoded sector CRC mismatch",$"Header CRC: {s.HeaderCrcValid}; data CRC: {s.DataCrcValid}; cells {s.HeaderCell}/{s.DataCell}",i,j));
                if(s.WeakBitMask.Any(b=>b!=0)) findings.Add(new("HFE_WEAK",DskIssueSeverity.Warning,"Weak cells in sector data","Decoded weak values are a deterministic inspection sample, not recovered bytes.",i,j));
                var mapped=layout?.Tracks.ElementAtOrDefault(i)?.Sectors.ElementAtOrDefault(j);
                return new DskVisualSector(new(i,j),s.C,s.H,s.R,s.N,(byte)(s.HeaderCrcValid?0:0x20),(byte)((s.DataCrcValid?0:0x20)|(s.DataMark==0xF8?0x40:0)),s.Data.Length,s.Data.Length,s.HeaderCell,s.DataCell,mapped?.Role??DskSectorRole.Unknown,mapped?.Owners.ToArray()??[],mapped?.LogicalBlocks.ToArray()??[],mapped?.AllocationBlocks.ToArray()??[],findings.Where(f=>f.Track==i && f.Sector==j).ToArray(),(byte[])s.Data.Clone(),true);
            }).ToArray();
            return new DskVisualTrack(i,t.Cylinder,t.Side,t.Cylinder,t.Side,0,t.PackedBitCells.Length,0,false,null,null,null,null,sectors,findings.Where(f=>f.Track==i).ToArray());
        }).ToArray();
        Issues=findings.ToArray();
    }
    internal DskVisualSnapshot(DskImage image, DskLayoutModel layout)
    {
        // Clone raw objects instead of round-tripping Serialize: malformed N/length values
        // must remain inspectable even when the normal writer correctly refuses them.
        generatedSource = image.Tracks.Select(t=>t?.CloneForInspection()).ToArray();
        Cylinders = layout.Cylinders; Sides = layout.Sides; FileSystem = layout.FileSystem;
        Issues = layout.Issues.ToArray(); TrailingBytes = image.ContainerTrailingData.Length;
        var issuesByTrack=Issues.Where(i=>i.Track!=null).GroupBy(i=>i.Track!.Value).ToDictionary(g=>g.Key,g=>g.ToArray());
        var issuesByFile=Issues.Where(i=>i.FileKey!=null).GroupBy(i=>i.FileKey!).ToDictionary(g=>g.Key,g=>g.ToArray());
        Tracks = layout.Tracks.Select(t=> {
            var raw = image.Tracks[t.PhysicalIndex];
            var trackIssues=issuesByTrack.GetValueOrDefault(t.PhysicalIndex)??Array.Empty<DskAnalysisIssue>();
            var sectors = t.Sectors.Select(s=>new DskVisualSector(s.Address,s.C,s.H,s.R,s.N,s.St1,s.St2,
                s.DataLength,s.DeclaredDataLength,s.DescriptorFileOffset,s.FileOffset,s.Role,s.Owners.ToArray(),
                s.LogicalBlocks.ToArray(),s.AllocationBlocks.ToArray(),
                trackIssues.Where(i=>i.Sector==null || i.Sector==s.PhysicalIndex).Concat(s.Owners.SelectMany(o=>issuesByFile.GetValueOrDefault(o.FileKey)??Array.Empty<DskAnalysisIssue>())).Distinct().ToArray(),(byte[])s.Data.Clone())).ToArray();
            return new DskVisualTrack(t.PhysicalIndex,t.PhysicalIndex / Math.Max(1,Sides),t.PhysicalIndex % Math.Max(1,Sides),
                t.Cylinder,t.Side,t.FileOffset,t.BlockSize,t.PaddingLength,t.IsMissing,
                raw?.Gap,raw?.Filler,raw?.StoredDataRate,raw?.StoredRecordingMode,sectors,trackIssues);
        }).ToArray();
    }
    internal PhysicalTrack Generate(int track, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(PhysicalImage!=null) return PhysicalImage.Tracks[track];
        var source = generatedSource[track] ?? throw new InvalidOperationException("Missing/unformatted track.");
        if (source.Sectors.Count == 0 || source.Sectors.Any(s=>s.SizeCode > 3 || s.Data.Length != (128 << s.SizeCode)))
            throw new InvalidOperationException("Unsupported N or stored data length; sector view remains available.");
        var result = FloppyTrackCodec.Encode(source,track / Math.Max(1,Sides),track % Math.Max(1,Sides),token);
        token.ThrowIfCancellationRequested(); return result;
    }
    internal static string StatusFlags(byte st1, byte st2)
    {
        var parts = new List<string>();
        void Flags(byte value, string name, (byte Mask,string Text)[] flags) {
            foreach(var f in flags) if((value & f.Mask) != 0) parts.Add(name+": "+f.Text);
            int known = flags.Aggregate(0,(v,f)=>v|f.Mask);
            if((value & ~known)!=0) parts.Add($"{name}: reserved bits 0x{value & ~known:X2}");
        }
        Flags(st1,"ST1",[(0x80,"end of cylinder"),(0x20,"data error"),(0x10,"overrun"),(4,"no data"),(2,"not writable"),(1,"missing address mark")]);
        Flags(st2,"ST2",[(0x40,"control/deleted mark"),(0x20,"data field error"),(0x10,"wrong cylinder"),(8,"scan equal"),(4,"scan not satisfied"),(2,"bad cylinder"),(1,"missing data address mark")]);
        return parts.Count == 0 ? "No stored ST1/ST2 flags; physical integrity not evaluated" : string.Join("; ",parts);
    }
}

internal readonly record struct DskVisualSpan(DskSectorAddress Address, double Start, double End);
internal readonly record struct DskSurfaceBand(int Track, double Inner, double Outer, IReadOnlyList<DskVisualSpan> Sectors);
internal sealed class DskSurfaceRenderModel
{
    public IReadOnlyList<DskSurfaceBand> Bands { get; }
    internal DskSurfaceRenderModel(DskVisualSnapshot snapshot, int side, bool proportional)
    {
        var tracks = snapshot.Tracks.Where(t=>t.SlotSide == side).OrderBy(t=>t.SlotCylinder).ToArray();
        double thickness = .86 / Math.Max(1,tracks.Length);
        Bands = tracks.Select((t,i)=>new DskSurfaceBand(t.PhysicalIndex,.98-(i+1)*thickness,.98-i*thickness,
            Spans(t.Sectors,proportional))).ToArray();
    }
    internal static IReadOnlyList<DskVisualSpan> Spans(IReadOnlyList<DskVisualSector> sectors, bool proportional)
    {
        double total = sectors.Sum(s=>proportional ? Math.Max(1,s.StoredBytes) : 1);
        double start = 0; var result = new List<DskVisualSpan>();
        foreach(var s in sectors) { double end = start + (proportional ? Math.Max(1,s.StoredBytes) : 1) / total;
            result.Add(new(s.Address,start,end)); start = end; }
        return result;
    }
    internal (int Track,DskSectorAddress? Sector)? Hit(double x, double y)
    {
        if(!double.IsFinite(x) || !double.IsFinite(y)) return null;
        double radius = Math.Sqrt(x*x+y*y);
        var band = Bands.FirstOrDefault(b=>radius>=b.Inner && radius<b.Outer);
        if(band == default || radius-band.Inner < .0004 || band.Outer-radius < .0004) return null;
        double angle = (Math.Atan2(x,-y) + Math.PI*2) % (Math.PI*2) / (Math.PI*2);
        foreach(var span in band.Sectors) if(angle>=span.Start && angle<span.End) {
            if(angle-span.Start < .0005 || span.End-angle < .0005) return null;
            return (band.Track,span.Address);
        }
        return (band.Track,null);
    }
}
