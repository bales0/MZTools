using System.Globalization;
using Avalonia.Controls;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.Platform;

// Presentation rules copied from DskNewDialog; creation and validation stay in Core.
internal static class DiskFormatForm
{
    internal static int DefaultTracks(DskNewFormat format)=>format is DskNewFormat.IplSingle or DskNewFormat.IplMulti or DskNewFormat.MzBasic or DskNewFormat.IplDisk or DskNewFormat.PersonalCpm or DskNewFormat.Sds400?40:80;
    internal static bool FixedGeometry(DskNewFormat format)=>format is DskNewFormat.IplSingle or DskNewFormat.IplMulti or DskNewFormat.PersonalCpm or DskNewFormat.Sds400 or DskNewFormat.Lemmings;
    internal static long Capacity(DskNewFormat format,int tracks,int sides,int sectors=16,int size=256)
    {
        if(tracks<1||sides is <1 or >2||checked(tracks*sides)>DskImage.MaximumAbsoluteTracks)throw new FormatException("Enter a supported track/side count.");
        return format switch{
            DskNewFormat.PersonalCpm=>320L*1024,DskNewFormat.Sds400=>400L*1024,
            DskNewFormat.LecCpmDd or DskNewFormat.Mrs or DskNewFormat.Lemmings=>checked((long)tracks*sides*9*512),
            DskNewFormat.LecCpmHd=>checked((long)tracks*sides*18*512),
            DskNewFormat.CustomRaw when sectors>0&&size>0=>checked((long)tracks*sides*sectors*size),
            DskNewFormat.CustomRaw=>throw new FormatException("Enter positive raw sector count and size."),
            _=>checked((long)tracks*sides*16*256)};
    }
    internal static void Attach(IReadOnlyDictionary<string,Control> values,IReadOnlyDictionary<string,Control> rows,IList<Control> controls)
    {
        var format=(ComboBox)values["format"];var tracks=(TextBox)values["tracks"];var sides=(TextBox)values["sides"];
        var capacity=new TextBlock{TextWrapping=Avalonia.Media.TextWrapping.Wrap};controls.Insert(0,capacity);
        string[] raw=["sectors","size","filler","order","ids"];
        void UpdateCapacity()
        {
            try{
                var profile=Enum.Parse<DskNewFormat>((string)format.SelectedItem!);
                int Number(string key)=>int.Parse(((TextBox)values[key]).Text??"",CultureInfo.InvariantCulture);
                long bytes=Capacity(profile,Number("tracks"),Number("sides"),profile==DskNewFormat.CustomRaw?Number("sectors"):16,profile==DskNewFormat.CustomRaw?Number("size"):256);
                bool standard=Number("tracks")==DefaultTracks(profile)&&Number("sides")==2;
                capacity.Text=$"Capacity: {bytes/1024d:0.##} KiB / {bytes:N0} B"+(profile==DskNewFormat.CustomRaw?" · raw sector data":standard?" · standard geometry":" · NONSTANDARD geometry; Core validation applies");
            }catch(Exception e)when(e is FormatException or OverflowException){capacity.Text=e.Message;}
        }
        void UpdateSectorIds()
        {
            var ids=(TextBox)values["ids"];var order=(ComboBox)values["order"];bool custom=(string?)order.SelectedItem=="Custom";ids.IsReadOnly=!custom;
            if(custom)return;
            try{ids.Text=string.Join(",",DskDocumentFactory.GetRawSectorIds(int.Parse(((TextBox)values["sectors"]).Text??"",CultureInfo.InvariantCulture),1,Enum.Parse<DskDocumentFactory.RawSectorOrder>((string)order.SelectedItem!)));}
            catch(Exception e)when(e is ArgumentException or FormatException or OverflowException){ids.Text=e.Message;}
        }
        void UpdateProfile()
        {
            var profile=Enum.Parse<DskNewFormat>((string)format.SelectedItem!);
            tracks.Text=DefaultTracks(profile).ToString(CultureInfo.InvariantCulture);sides.Text="2";
            tracks.IsEnabled=sides.IsEnabled=!FixedGeometry(profile);
            foreach(string key in raw)rows[key].IsVisible=profile==DskNewFormat.CustomRaw;
            UpdateCapacity();
        }
        format.SelectionChanged+=(_,_)=>UpdateProfile();
        foreach(string key in new[]{"tracks","sides","sectors","size"})((TextBox)values[key]).TextChanged+=(_,_)=>UpdateCapacity();
        ((ComboBox)values["order"]).SelectionChanged+=(_,_)=>UpdateSectorIds();
        ((TextBox)values["sectors"]).TextChanged+=(_,_)=>UpdateSectorIds();
        UpdateProfile();UpdateSectorIds();
    }
}
