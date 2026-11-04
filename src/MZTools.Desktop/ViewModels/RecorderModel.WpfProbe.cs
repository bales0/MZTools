using MZTools.Capture;

namespace MZTools.Desktop.ViewModels;

public sealed partial class RecorderModel
{
    private readonly List<AudioCaptureFormat> probedFormats=[];
    public async Task ProbeFormatAsync(AudioCaptureFormat requested)
    {
        bool accepted=false;var selected=Device;
        await RunAsync(async ct=>
        {
            if(IsActive||selected is null)throw new InvalidOperationException("Stop input and select a device before probing a format.");
            // Open validates the driver format, but never calls StartAsync or creates an archive.
            var candidate=factory.Create(BackendIndex,Raw,EngineConversion);
            await using(var probe=await candidate.OpenAsync(new(selected.Id,requested,false),ct))
            {
                ct.ThrowIfCancellationRequested();
                if(probe.Info.ActualFormat!=requested)throw new InvalidOperationException("Exact format was not accepted. Select Exclusive mode for a native Windows format.");
            }
            ct.ThrowIfCancellationRequested();
            if(!probedFormats.Contains(requested))probedFormats.Add(requested);
            Changed(nameof(Formats));accepted=true;
            Status="Exact format accepted. Input has not been started; availability is checked again at Start.";
        });
        if(accepted&&!disposed&&Device==selected)Format=requested;
    }
}
