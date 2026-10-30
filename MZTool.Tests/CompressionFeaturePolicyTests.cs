namespace MZTools.Tests;

public class CompressionFeaturePolicyTests
{
    [Fact]
    public void HeuristicFailures_UseStructuredStandardDecoderFallbackPolicy()
    {
        Assert.True(AudioImportPolicy.ShouldTryStandardDecoder(failureCount: 1));
        Assert.True(AudioImportPolicy.ShouldTryStandardDecoder(failureCount: 3));
        Assert.False(AudioImportPolicy.ShouldTryStandardDecoder(failureCount: 0));
    }

    [Fact]
    public void FiltersExposePackedAndIplFormatsContextually()
    {
        string open = FeatureModePolicy.GetOpenFilter().ToLowerInvariant();

        Assert.Contains(".mz0", open);
        Assert.Contains(".mz7", open);
        Assert.Contains("multi-game", FeatureModePolicy.GetExportFilter(singleRecord: false).ToLowerInvariant());
        Assert.Contains(".dsk", FeatureModePolicy.GetExportFilter(singleRecord: false).ToLowerInvariant());
        Assert.Contains("bootable", FeatureModePolicy.GetExportFilter(singleRecord: true).ToLowerInvariant());
        Assert.Contains(".dsk", FeatureModePolicy.GetExportFilter(singleRecord: true).ToLowerInvariant());
    }

    [Fact]
    public void CompressionOptionsControl_ConstructsOnStaThread()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                _ = new CompressionOptionsControl();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        Assert.Null(failure);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(-1, 0)]
    public void AudioImportOptions_MapsReportSelection(int selectedIndex, int expected)
    {
        Assert.Equal((AudioReportMode)expected, WavImportOptionsWindow.GetReportMode(selectedIndex));
    }
}
