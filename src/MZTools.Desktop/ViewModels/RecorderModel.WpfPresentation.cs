namespace MZTools.Desktop.ViewModels;

public sealed partial class RecorderModel
{
    public IReadOnlyList<double> GainChoices {get;}=[.5,1,2,4,8];
    public IReadOnlyList<string> TriggerSlopes {get;}=["Rising","Falling"];
    public int TriggerSlopeIndex {get=>RisingTrigger?0:1;set{RisingTrigger=value==0;Changed(nameof(TriggerSlopeIndex));}}
}
