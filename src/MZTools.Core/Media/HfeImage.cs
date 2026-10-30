using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum HfeVersion { V1=1,V2=2,V3=3 }

// HFE container layout/opcode semantics adapted from bales0/HxCFloppyEmulator.
// Copyright (C) 2006-2026 Jean-François DEL NERO. GPL-2.0-or-later.
// No warranty, including merchantability or fitness for a particular purpose.
// See THIRD_PARTY_NOTICES.md and https://www.gnu.org/licenses/.
internal sealed class HfeImage : PhysicalDiskImage, IMediaFormat
{
    private byte[]? original;
    private byte[]? header;
    private IReadOnlyDictionary<int,byte[]> preservedBlocks=new Dictionary<int,byte[]>();
    private IReadOnlyDictionary<int,byte[]> preservedStreams=new Dictionary<int,byte[]>();
    internal HfeVersion Version {get;private init;}=HfeVersion.V1;
    internal bool IsV3=>Version==HfeVersion.V3;
    internal string Variant=>Version==HfeVersion.V1?"HFE":Version==HfeVersion.V2?"HFEv2":"HFEv3";
    internal bool WriteAllowed=>header==null || header[20]!=0;
    public string Id => "hfe";
    public string DisplayName => "HFE / HFEv2 / HFEv3";
    public IReadOnlyList<string> Extensions => [".hfe"];
    public MediaCapabilities Capabilities => MediaCapabilities.Detect | MediaCapabilities.Read | MediaCapabilities.Write | MediaCapabilities.PhysicalTrackModel | MediaCapabilities.SectorModel | MediaCapabilities.PreserveTiming | MediaCapabilities.PreserveWeakBits;
    public MediaDetection Identify(ReadOnlySpan<byte> source) => source.Length >= 8
        ? new(source[..8].SequenceEqual("HXCPICFE"u8) || source[..8].SequenceEqual("HXCHFEV3"u8), source[..8].SequenceEqual("HXCHFEV3"u8) ? "HFEv3" : source.Length>8 && source[8]==1?"HFEv2":"HFE") : new(false, "");
    private static ushort U(ReadOnlySpan<byte> bytes, int offset) => BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(offset, 2));
    private static void U(Span<byte> bytes, int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(bytes.Slice(offset, 2), checked((ushort)value));

    internal static HfeImage Parse(byte[] bytes)
    {
        if (bytes.Length < 512 || !new HfeImage().Identify(bytes).Matches) throw new InvalidDataException("HFE signature/header is missing or truncated.");
        bool v3 = bytes.AsSpan(0, 8).SequenceEqual("HXCHFEV3"u8);
        var version=v3?HfeVersion.V3:bytes[8]==1?HfeVersion.V2:HfeVersion.V1;
        if ((v3?bytes[8]!=0:bytes[8]>1) || bytes[9] == 0 || bytes[10] is not (1 or 2)) throw new InvalidDataException("Unsupported HFE revision or geometry.");
        int cylinders = bytes[9], sides = bytes[10], table = U(bytes, 18) * 512;
        if (table < 512 || table > bytes.Length - cylinders * 4) throw new InvalidDataException("HFE track table is outside the image.");
        int bitrate = U(bytes, 12) * 1000; int rpm = U(bytes, 14);
        var tracks = new List<PhysicalTrack>(); var ranges = new List<(int Start, int End)>();
        for (int cylinder = 0; cylinder < cylinders; cylinder++)
        {
            int start = U(bytes, table + cylinder * 4) * 512, len = U(bytes, table + cylinder * 4 + 2);
            int stored = (len + 511) / 512 * 512;
            if (len == 0 || (len & 1) != 0 || start < 512 || start > bytes.Length - stored || (start < table + cylinders * 4 && start + stored > table))
                throw new InvalidDataException("HFE track length, bounds or metadata overlap is invalid.");
            if (ranges.Any(r => start < r.End && start + stored > r.Start)) throw new InvalidDataException("HFE track data overlaps another track.");
            ranges.Add((start, start + stored));
            for (int side = 0; side < sides; side++)
            {
                int encoding = bytes[11];
                if (cylinder == 0 && bytes[22 + side * 2] == 0) encoding = bytes[23 + side * 2];
                var stream = new byte[len / 2];
                for (int i = 0; i < stream.Length; i++) stream[i] = bytes[start + (i / 256) * 512 + side * 256 + i % 256];
                IReadOnlyList<HfeV2Control> controls=[];
                var decoded = version==HfeVersion.V2?DecodeV2(stream,out controls):DecodeStream(stream, v3, bitrate);
                var track = new PhysicalTrack { Cylinder = cylinder, Side = side, BitRate = bitrate == 0 ? null : bitrate, Rpm = rpm == 0 ? null : rpm,
                    Encoding = encoding switch { 0 => PhysicalEncoding.Mfm, 2 => PhysicalEncoding.Fm, _ => PhysicalEncoding.Unknown }, ContainerEncoding = (byte)encoding, PackedBitCells = decoded.Bits, BitCellCount = decoded.Count,
                    WeakBitMask = decoded.Weak, Timing = decoded.Timing, IndexCells = decoded.Index,
                    V2Controls=controls };
                track.Sectors = track.Encoding == PhysicalEncoding.Unknown ? [] : FloppyTrackCodec.Decode(track); tracks.Add(track);
            }
        }
        return new HfeImage { Version = version, Cylinders = cylinders, Sides = sides, Tracks = tracks, original = (byte[])bytes.Clone(), header = bytes[..512] };
    }

    // HxC published v2 four-byte instructions. Baud registers stay raw: the
    // specification does not define the PIC oscillator needed to turn SPBRG into Hz.
    private static (byte[] Bits,int Count,byte[] Weak,PhysicalTiming[] Timing,long[] Index) DecodeV2(byte[] stream,out IReadOnlyList<HfeV2Control> metadata)
    {
        byte[] bits=new byte[stream.Length],weak=new byte[stream.Length];int count=0;bool indexState=false;
        var controls=new List<HfeV2Control>();var indexes=new List<long>();
        void Data(byte value,bool random=false) {for(int b=7;b>=0;b--) {PackedBitCells.Set(bits,count,(value&(1<<b))!=0);PackedBitCells.Set(weak,count,random);count++;}}
        for(int i=0;i<stream.Length;i++) {
            byte value=PackedBitCells.Reverse(stream[i]);if(value!=0) {Data(value);continue;}
            if(i+3>=stream.Length) throw new InvalidDataException("Truncated HFEv2 four-byte instruction.");
            byte op=PackedBitCells.Reverse(stream[i+1]),argument=PackedBitCells.Reverse(stream[i+2]),reserved=PackedBitCells.Reverse(stream[i+3]);i+=3;
            switch(op) {
                case 1: case 7: controls.Add(new(count,op,argument,reserved));break;
                case 2: Data(0,true);break;
                case 3: Data(argument);break;
                case 4:
                    controls.Add(new(count,op,argument,reserved));bool next=(argument&1)!=0;
                    if(next && !indexState) indexes.Add(count);indexState=next;break;
                case 5: break;
                case 6: i=Math.Min(stream.Length,((i+1+255)/256)*256)-1;break;
                default: throw new InvalidDataException($"Unknown HFEv2 instruction {op:X2}.");
            }
        }
        metadata=controls.ToArray();return(bits[..((count+7)/8)],count,weak[..((count+7)/8)],[],indexes.ToArray());
    }

    private static byte[] EncodeV2(PhysicalTrack track,int rate)
    {
        if(track.BitCellCount%8!=0 || track.Timing.Count>0 || (track.BitRate??0)!=rate || track.IndexCells.Any(c=>c%8!=0) || track.V2Controls.Any(c=>c.CellOffset%8!=0))
            throw new InvalidDataException("HFEv2 cannot preserve this sub-byte length/index or HFEv3 timing divisor. Use HFEv3 or the original variant.");
        var output=new List<byte>();var ordinary=new Stack<int>();
        void Op(byte op,byte value,byte reserved=0)=>output.AddRange(new byte[] {0,PackedBitCells.Reverse(op),PackedBitCells.Reverse(value),PackedBitCells.Reverse(reserved)});
        var controls=track.V2Controls.GroupBy(c=>c.CellOffset).ToDictionary(g=>g.Key,g=>g.ToArray());
        var indexes=track.IndexCells.ToHashSet();
        for(long cell=0;cell<=track.BitCellCount;cell+=8) {
            if(controls.TryGetValue(cell,out var items)) foreach(var item in items) Op(item.Opcode,item.Value,item.Reserved);
            if(indexes.Contains(cell) && !track.V2Controls.Any(c=>c.Opcode==4)) {Op(4,1);Op(4,0);}
            if(cell==track.BitCellCount) break;
            bool random=track.WeakBitMask.Length>0 && PackedBitCells.Get(track.WeakBitMask,(int)cell);
            byte value=0;for(int bit=0;bit<8;bit++) {
                if((track.WeakBitMask.Length>0 && PackedBitCells.Get(track.WeakBitMask,(int)cell+bit))!=random) throw new InvalidDataException("HFEv2 cannot preserve a partial-byte weak region.");
                value=(byte)((value<<1)|(PackedBitCells.Get(track.PackedBitCells,(int)cell+bit)?1:0));
            }
            if(random) Op(2,0);else if(value==0) Op(3,0);else {ordinary.Push(output.Count);output.Add(PackedBitCells.Reverse(value));}
        }
        // NOPs occupy four bytes; align without adding physical cells.
        while(output.Count%4!=0) {int at=ordinary.Pop();byte value=output[at];output.RemoveAt(at);output.InsertRange(at,new byte[] {0,PackedBitCells.Reverse(3),value,0});}
        // Four-byte instructions must stay inside a 256-byte side buffer.
        // Reserve room for BREAK BUFFER before a later instruction could straddle it.
        var packed=new List<byte>();
        for(int at=0;at<output.Count;) {
            int width=output[at]==0?4:1,remaining=256-packed.Count%256;
            int left=remaining-width;
            if(width>remaining || left is >0 and <4) {
                if(remaining<4) throw new InvalidDataException("HFEv2 instruction buffer alignment failed.");
                packed.AddRange(new byte[] {0,PackedBitCells.Reverse(6),0,0});
                while(packed.Count%256!=0) packed.Add(0xAA);
            }
            packed.AddRange(output.GetRange(at,width));at+=width;
        }
        if(packed.Count%4!=0) {
            packed.AddRange(new byte[] {0,PackedBitCells.Reverse(6),0,0});
            while(packed.Count%256!=0) packed.Add(0xAA);
        }
        return packed.ToArray();
    }

    private static (byte[] Bits, int Count, byte[] Weak, PhysicalTiming[] Timing, long[] Index) DecodeStream(byte[] stream, bool v3, int rate)
    {
        if (!v3) return ((byte[])stream.Clone(), stream.Length * 8, [], [], []);
        var bits = new byte[stream.Length]; var weak = new byte[stream.Length];
        var timing = new List<PhysicalTiming>(); var indexes = new List<long>(); int count = 0, skip = 0; bool pendingSkip = false;
        for (int i = 0; i < stream.Length; i++)
        {
            byte value = PackedBitCells.Reverse(stream[i]); bool random = false;
            if (value >= 0xF0)
            {
                switch (value)
                {
                    case 0xF0: continue;
                    case 0xF1: indexes.Add(count); continue;
                    case 0xF2:
                        if (++i >= stream.Length) throw new InvalidDataException("Truncated HFEv3 bitrate opcode.");
                        int divisor = PackedBitCells.Reverse(stream[i]);
                        if (divisor == 0) throw new InvalidDataException("HFEv3 bitrate divisor is zero.");
                        timing.Add(new(count, divisor)); continue;
                    case 0xF3:
                        if (pendingSkip || ++i >= stream.Length) throw new InvalidDataException("Invalid HFEv3 skip opcode.");
                        skip = PackedBitCells.Reverse(stream[i]);
                        if (skip > 7) throw new InvalidDataException("Unsupported HFEv3 skip count.");
                        pendingSkip = true;
                        continue;
                    case 0xF4: random = true; value = 0; break;
                    default: throw new InvalidDataException($"Unsupported HFEv3 opcode {value:X2}; no silent loss is allowed.");
                }
            }
            for (int bit = skip; bit < 8; bit++)
            {
                PackedBitCells.Set(bits, count, (value & (0x80 >> bit)) != 0);
                PackedBitCells.Set(weak, count, random); count++;
            }
            skip = 0; pendingSkip = false;
        }
        if (pendingSkip || count == 0) throw new InvalidDataException("HFEv3 stream is empty or ends in a pending skip.");
        return (bits[..((count + 7) / 8)], count, weak[..((count + 7) / 8)], timing.ToArray(), indexes.ToArray());
    }

    internal static HfeImage FromDsk(DskDocument document,bool uniformTiming=false)
    {
        var image = document.Image;
        if (image.Tracks.Any(t => t == null || t.Sectors.Count == 0)) throw new InvalidDataException("DSK → HFE requires complete populated tracks; missing tracks are not fabricated.");
        var tracks=image.Tracks.Select((t,i)=>FloppyTrackCodec.Encode(t!,i/image.SideCount,i%image.SideCount)).ToArray();
        if(uniformTiming) {
            int rate=tracks.Max(t=>t.BitRate??250_000);
            for(int i=0;i<tracks.Length;i++) if(tracks[i].BitRate!=rate) tracks[i]=FloppyTrackCodec.Encode(image.Tracks[i]!,i/image.SideCount,i%image.SideCount,bitRate:rate);
        }
        return new HfeImage { Version = HfeVersion.V3, Cylinders = image.TrackCount, Sides = image.SideCount,Tracks=tracks };
    }

    internal byte[] Serialize(bool v3, bool preserveOriginal = true)
        =>Serialize(v3?HfeVersion.V3:HfeVersion.V1,preserveOriginal);
    internal byte[] Serialize(HfeVersion version,bool preserveOriginal=true)
    {
        bool v3=version==HfeVersion.V3,v2=version==HfeVersion.V2;
        if(!Enum.IsDefined(version)) throw new ArgumentOutOfRangeException(nameof(version));
        if (preserveOriginal && original != null && version == Version) return (byte[])original.Clone();
        if (Tracks.Where((t,i)=>!preservedBlocks.ContainsKey(i/Sides)).Any(t => t.Encoding == PhysicalEncoding.Unknown))
            throw new InvalidDataException("Unknown HFE encoding supports physical inspection and byte-identical Save As in its original variant only. Re-encoding/conversion is unavailable.");
        int rate = Tracks[0].BitRate ?? 0;
        if (rate % 1000 != 0) throw new InvalidDataException("HFE header bitrate must be expressible in whole kbit/s.");
        var trackStreams = new List<byte[]>();
        for (int cylinder = 0; cylinder < Cylinders; cylinder++)
        {
            if(version==Version && preservedBlocks.TryGetValue(cylinder,out var preserved)) {trackStreams.Add((byte[])preserved.Clone());continue;}
            var sides = Tracks.Where(t => t.Cylinder == cylinder).OrderBy(t => t.Side).ToArray();
            if (sides.Length != Sides) throw new InvalidDataException("Missing physical side.");
            if(version!=HfeVersion.V2 && sides.Any(t=>t.V2Controls.Any(c=>c.Opcode is 1 or 4 or 7)))
                throw new InvalidDataException("Target cannot preserve HFEv2 baud-register, index-state or write-mode controls. Save as HFEv2 to retain them.");
            if (!v3 && !v2 && sides.Any(t => t.BitCellCount % 8 != 0 || t.BitRate != Tracks[0].BitRate || t.WeakBitMask.Any(b => b != 0) || t.Timing.Count != 0 || t.IndexCells.Any(i => i != 0)))
                throw new InvalidDataException("Legacy HFE cannot preserve these timing, weak bits or index/length properties. Select HFEv3.");
            if (!v3 && !v2 && sides.Select(t => t.BitCellCount).Distinct().Count() != 1) throw new InvalidDataException("Legacy HFE cannot preserve unequal side lengths. Select HFEv3.");
            var streams = sides.Select(t => version==Version && preservedStreams.TryGetValue(cylinder*Sides+t.Side,out var stored)?stored:v3 ? EncodeStream(t, rate) : v2?EncodeV2(t,rate):t.PackedBitCells[..checked((int)(t.BitCellCount / 8))]).ToArray();
            int length = streams.Max(s => s.Length);
            if (length * 2 > ushort.MaxValue) throw new InvalidDataException("HFE opcode stream exceeds the 16-bit track length limit.");
            if(v2 && sides.Length==2 && streams[0].Length!=streams[1].Length) {
                int aligned=(length+3)/4*4;
                for(int s=0;s<streams.Length;s++) {var padded=streams[s].ToList();while(padded.Count<aligned) padded.AddRange(new byte[] {0,PackedBitCells.Reverse(5),0,0});streams[s]=padded.ToArray();}
                length=aligned;if(length*2>ushort.MaxValue) throw new InvalidDataException("HFEv2 padded track is too long.");
            }
            var block = Enumerable.Repeat(v3 ? PackedBitCells.Reverse(0xF0) : (byte)0, (length * 2 + 511) / 512 * 512).ToArray();
            for (int side = 0; side < Sides; side++) for (int i = 0; i < streams[side].Length; i++) block[(i / 256) * 512 + side * 256 + i % 256] = streams[side][i];
            // Prefix the declared length for table assembly below.
            var packed = new byte[block.Length + 2]; U(packed, 0, length * 2); block.CopyTo(packed, 2); trackStreams.Add(packed);
        }
        int tableBlocks = (Cylinders * 4 + 511) / 512;
        var output = Enumerable.Repeat((byte)0xFF, 512 + tableBlocks * 512 + trackStreams.Sum(t => t.Length - 2)).ToArray();
        if (header != null) header.CopyTo(output, 0);
        (v3 ? "HXCHFEV3"u8 : "HXCPICFE"u8).CopyTo(output);
        output[8] = v2?(byte)1:(byte)0; output[9] = checked((byte)Cylinders); output[10] = checked((byte)Sides);
        output[11] = Tracks.First(t => t.Cylinder != 0 || Cylinders == 1).Encoding == PhysicalEncoding.Mfm ? (byte)0 : (byte)2;
        if (Tracks.Any(t => t.Cylinder > 0 && (t.Encoding == PhysicalEncoding.Mfm ? 0 : 2) != output[11])) throw new InvalidDataException("Mixed track encoding outside cylinder zero is not representable by HFE.");
        U(output, 12, rate / 1000); U(output, 14, checked((int)(Tracks[0].Rpm ?? 0))); U(output, 18, 1);
        if (header == null) { output[16] = 0; output[17] = 1; output[20] = 0xFF; output[21] = 0xFF; }
        for (int side = 0; side < Sides; side++) { output[22 + side * 2] = 0; output[23 + side * 2] = Tracks.First(t => t.Cylinder == 0 && t.Side == side).Encoding == PhysicalEncoding.Mfm ? (byte)0 : (byte)2; }
        int position = 512 + tableBlocks * 512;
        for (int c = 0; c < Cylinders; c++) { U(output, 512 + c * 4, position / 512); U(output, 514 + c * 4, U(trackStreams[c], 0)); trackStreams[c].AsSpan(2).CopyTo(output.AsSpan(position)); position += trackStreams[c].Length - 2; }
        var check = Parse(output); VerifyPhysical(this, check);
        return output;
    }

    // Sector editing preserves the original physical layout. Only data fields and
    // their CRC/adjacent MFM clock cell change; untouched cylinder blocks stay exact.
    internal HfeImage UpdateSectors(DskImage baseline,DskImage updated)
    {
        if(baseline.TrackCount!=updated.TrackCount || baseline.SideCount!=updated.SideCount || updated.Tracks.Count!=Tracks.Count)
            throw new InvalidDataException("Changing HFE geometry requires explicit Format, not a sector edit.");
        var tracks=new List<PhysicalTrack>();var preserved=new Dictionary<int,byte[]>();var streams=new Dictionary<int,byte[]>();var changed=new HashSet<int>();
        for(int i=0;i<Tracks.Count;i++) {
            var before=baseline.Tracks[i]!;var after=updated.Tracks[i]!;var source=Tracks[i];
            if(before==null || after==null || before.Sectors.Count!=after.Sectors.Count || after.Sectors.Count!=source.Sectors.Count)
                throw new InvalidDataException("Changing HFE sector layout requires explicit Format.");
            byte[]? cells=null;
            for(int j=0;j<after.Sectors.Count;j++) {
                var a=before.Sectors[j];var b=after.Sectors[j];
                if(a.Cylinder!=b.Cylinder || a.Side!=b.Side || a.SectorId!=b.SectorId || a.SizeCode!=b.SizeCode || a.Data.Length!=b.Data.Length || a.FdcStatus1!=b.FdcStatus1 || a.FdcStatus2!=b.FdcStatus2)
                    throw new InvalidDataException("HFE sector identities/status cannot be changed by a filesystem edit. Use explicit Format for a new layout.");
                if(a.Data.AsSpan().SequenceEqual(b.Data)) continue;
                cells??=(byte[])source.PackedBitCells.Clone();
                FloppyTrackCodec.WriteSectorData(source,cells,source.Sectors[j],b.Data);changed.Add(i/Sides);
            }
            if(cells==null) {
                tracks.Add(source);
                if(original!=null) {
                    int table=U(original,18)*512,c=i/Sides,side=i%Sides,start=U(original,table+c*4)*512,length=U(original,table+c*4+2)/2;
                    if(Version!=HfeVersion.V2 || length%4==0) {var stream=new byte[length];for(int p=0;p<length;p++) stream[p]=original[start+p/256*512+side*256+p%256];streams[i]=stream;}
                }
                continue;
            }
            var track=new PhysicalTrack {Cylinder=source.Cylinder,Side=source.Side,Rpm=source.Rpm,BitRate=source.BitRate,Encoding=source.Encoding,ContainerEncoding=source.ContainerEncoding,
                PackedBitCells=cells,BitCellCount=source.BitCellCount,WeakBitMask=(byte[])source.WeakBitMask.Clone(),Timing=source.Timing,IndexCells=source.IndexCells,V2Controls=source.V2Controls};
            track.Sectors=FloppyTrackCodec.Decode(track);
            if(track.Sectors.Count!=after.Sectors.Count || track.Sectors.Where((s,j)=>!s.HeaderCrcValid || !s.DataCrcValid || !s.Data.AsSpan().SequenceEqual(after.Sectors[j].Data)).Any())
                throw new InvalidDataException("Edited HFE track failed sector/CRC verification.");
            tracks.Add(track);
        }
        if(changed.Count==0) return this;
        if(original!=null) {
            int table=U(original,18)*512;
            for(int c=0;c<Cylinders;c++) if(!changed.Contains(c)) {
                int start=U(original,table+c*4)*512,len=U(original,table+c*4+2),stored=(len+511)/512*512;
                var block=new byte[stored+2];U(block,0,len);original.AsSpan(start,stored).CopyTo(block.AsSpan(2));preserved[c]=block;
            }
        }
        return new HfeImage {Version=Version,Cylinders=Cylinders,Sides=Sides,Tracks=tracks,header=header,preservedBlocks=preserved,preservedStreams=streams};
    }

    private static byte[] EncodeStream(PhysicalTrack track, int globalRate)
    {
        var output = new List<byte>();
        void Emit(byte b) => output.Add(PackedBitCells.Reverse(b));
        var timing = track.Timing.GroupBy(t => t.CellOffset).ToDictionary(g => g.Key, g => g.Last().Divisor);
        if (track.BitRate is int trackRate && trackRate != globalRate && !timing.ContainsKey(0))
        {
            int divisor = 18_000_000 / trackRate;
            if (divisor is < 1 or > 255 || 18_000_000 % trackRate != 0) throw new InvalidDataException("Track bitrate cannot be represented exactly by HFEv3.");
            timing[0] = divisor;
        }
        var indexes = track.IndexCells.ToHashSet();
        long count = track.BitCellCount;
        for (int cell = 0; cell < count;)
        {
            if (indexes.Contains(cell)) Emit(0xF1);
            if (timing.TryGetValue(cell, out int divisor)) { Emit(0xF2); Emit(checked((byte)divisor)); }
            bool weak = track.WeakBitMask.Length > 0 && PackedBitCells.Get(track.WeakBitMask, cell);
            int n = 1;
            while (n < 8 && cell + n < count && !indexes.Contains(cell + n) && !timing.ContainsKey(cell + n) &&
                (track.WeakBitMask.Length > 0 && PackedBitCells.Get(track.WeakBitMask, cell + n)) == weak) n++;
            byte value = 0;
            for (int i = 0; i < n; i++) value = (byte)((value << 1) | (PackedBitCells.Get(track.PackedBitCells, cell + i) ? 1 : 0));
            if (!weak && n == 8 && value >= 0xF0) { n = 4; value >>= 4; }
            if (n < 8) { Emit(0xF3); Emit((byte)(8 - n)); }
            Emit(weak ? (byte)0xF4 : value); cell += n;
        }
        if (indexes.Contains(count)) Emit(0xF1);
        if (timing.TryGetValue(count, out int endDivisor)) { Emit(0xF2); Emit(checked((byte)endDivisor)); }
        return output.ToArray();
    }

    internal static void VerifyPhysical(HfeImage source, HfeImage target)
    {
        if (source.Tracks.Count != target.Tracks.Count) throw new InvalidDataException("HFE physical track count changed.");
        for (int i = 0; i < source.Tracks.Count; i++)
        {
            var a = source.Tracks[i]; var b = target.Tracks[i];
            if (source.Version==HfeVersion.V2 && !a.V2Controls.SequenceEqual(b.V2Controls)) throw new InvalidDataException("HFEv2 control instructions changed.");
            if (a.BitCellCount != b.BitCellCount || a.Encoding != b.Encoding || a.Rpm != b.Rpm || !a.IndexCells.SequenceEqual(b.IndexCells)) throw new InvalidDataException("HFE physical metadata changed.");
            for (int cell = 0; cell < a.BitCellCount; cell++)
            {
                bool aw = a.WeakBitMask.Length > 0 && PackedBitCells.Get(a.WeakBitMask, cell), bw = b.WeakBitMask.Length > 0 && PackedBitCells.Get(b.WeakBitMask, cell);
                if (aw != bw || (!aw && PackedBitCells.Get(a.PackedBitCells, cell) != PackedBitCells.Get(b.PackedBitCells, cell))) throw new InvalidDataException("HFE bitcell/weak mask changed.");
            }
            double Effective(PhysicalTrack t, long offset) => t.Timing.LastOrDefault(p => p.CellOffset <= offset)?.BitRate ?? t.BitRate ?? 0;
            foreach (long offset in a.Timing.Select(t => t.CellOffset).Concat(b.Timing.Select(t => t.CellOffset)).Append(0).Distinct())
                if (Math.Abs(Effective(a, offset) - Effective(b, offset)) > 0.001) throw new InvalidDataException("HFE timing changed.");
        }
    }
}
