namespace MZTools.Desktop.Documents;

public sealed partial class AudioSession
{
    internal void SelectDetectedAtFrame(long frame)
    {
        var block=Blocks.Where(b=>b.Start<=frame&&b.End>frame).OrderBy(b=>b.End-b.Start).FirstOrDefault();
        var region=Regions.Where(r=>r.Start<=frame&&r.End>frame).OrderBy(r=>r.End-r.Start).FirstOrDefault();
        SelectedRegion=region;
        SelectedBlock=block;
        if(block is null && region is null)SetSelection([(frame,frame+1)]);
    }
    public IReadOnlyList<double> GainChoices { get; } = [.5,1,2,4,8];
    public string WpfHeading => $"{System.IO.Path.GetFileName(Model.Source)} • {Model.Format} • {Model.SampleRate:N0} Hz / {Model.BitsPerSample} bit / {Model.ChannelCount} ch • {Model.DurationSeconds:F2} s\nCaptured / measured PCM • {Model.Quality}: {Model.QualityExplanation}\nPreferred channel: {Model.BestChannel} — {Model.BestChannelReasons}";
}
