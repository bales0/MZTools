namespace MZTools.Desktop.Documents;

public sealed partial class TapeSession
{
    private const string MapUnits = "* Length is in the units shown above. Gaps are not necessarily free space.";
    public sealed record MapMessage(string Category, string Details);
    public string WpfMapReport => Model.IsQdImage ? QuickDiskAnalysisReport.Build(QdImageReaderWriter.Read(Model.IsModified || Model.QuickDiskSourceImage is null ? QuickDiskLayoutBuilder.BuildPreviewImage(Model) : Model.QuickDiskSourceImage)) : Snapshot?.Layout.Summary ?? "No QuickDisk image.";
    public string WpfMapSummary => Snapshot?.Layout.Summary ?? MapSummary;
    public string WpfMapDetail => SelectedRegion is null ? MapUnits : MapDetail;
    public IReadOnlyList<MapMessage> MapMessages => [new("Information", WpfMapDetail)];
    public System.Collections.IEnumerable MapRegions => Snapshot?.Layout.Regions ?? [];
}
