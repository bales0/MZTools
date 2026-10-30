namespace MZTools.Capture.Tests;

public sealed class SpectralFrequencyTests
{
    [Theory][InlineData(192000u)][InlineData(384000u)]
    public void LargeFftFrequencyAxisDoesNotOverflowAndPeakRetainsPhysicalFrequency(uint rate)
    {
        const int n=32768,bin=14000;var samples=Enumerable.Range(0,n).Select(i=>.5*Math.Sin(2*Math.PI*bin*i/n)).ToArray();
        var values=new SpectralAnalysis().Compute([samples],n,n,rate,1,out var peak,out _);
        Assert.Equal(0,values[0].FrequencyHz);Assert.Equal(rate/2.0,values[^1].FrequencyHz);Assert.Equal(bin*(double)rate/n,peak);
        for(int i=1;i<values.Length;i++)Assert.True(values[i].FrequencyHz>values[i-1].FrequencyHz);
        Assert.InRange(values[bin].LeftDbfs,-6.021,-6.020);
    }
}
