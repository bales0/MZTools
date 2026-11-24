using System;

namespace MZTools;

/// <summary>Additional LOW pauses around whole programs. Existing API calls default to Legacy.</summary>
public sealed record TapeExportGapOptions(int StartMs,int BetweenMs,int EndMs)
{
    public const int MaximumMilliseconds=60000;
    public static TapeExportGapOptions Legacy {get;}=new(0,0,0);
    public static TapeExportGapOptions Standard {get;}=new(1000,2000,1000);
    public void Validate()
    {
        if(StartMs<0||StartMs>MaximumMilliseconds||BetweenMs<0||BetweenMs>MaximumMilliseconds||EndMs<0||EndMs>MaximumMilliseconds)
            throw new ArgumentOutOfRangeException(nameof(TapeExportGapOptions),$"Each gap must be between 0 and {MaximumMilliseconds} ms.");
    }
    public long TotalMilliseconds(int programs,bool separate=false)=>programs<=0?0:separate?(long)programs*(StartMs+EndMs):(long)StartMs+EndMs+(long)(programs-1)*BetweenMs;
    public string Description=>$"Gaps (approximate LOW pauses): start {StartMs:N0} ms / between programs {BetweenMs:N0} ms / end {EndMs:N0} ms.";
}
