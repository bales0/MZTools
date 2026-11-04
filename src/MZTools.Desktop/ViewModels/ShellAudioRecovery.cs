using System.Globalization;
using System.Text.Json;
using MZTools.Desktop.Documents;

namespace MZTools.Desktop.ViewModels;

public sealed partial class ShellViewModel
{
    private async Task<bool> RunRecoveryAsync(AudioSession session,CancellationToken ct)
    {
        session.RequireSingleInterval();long first=session.SelectionStart,last=session.SelectionEnd;
        var fields=RecoveryFields(session);
        var answer=await AnalysisDialogs!.DiskFormAsync("Deeper audio analysis",fields,session.SelectionSummary+"\n"+session.RequestedTransform().Description+"\nChecksums remain mandatory for valid programs. Include leader/header/payload for a complete program. Threshold/reference tests use existing Core; no PCM or source timestamp is rewritten.");
        if(answer is null)return false;
        bool result=await AnalyzeRecoveryAsync(session,answer,first,last,ct);if(result)session.ViewIndex=8;return result;
    }
    internal IReadOnlyList<DiskField> RecoveryFields(AudioSession session)
    {
        string[] yesNo=["Yes","No"];var labels=session.ReferenceNames.Select((name,i)=>$"{i+1}: {name}").ToArray();
        var fields=new List<DiskField>
        {
            new("scope","Decode scope","Complete MZF program",["Complete MZF program","Payload only"]),
            new("length","Expected payload length (decimal; blank = unknown)",""),
            new("unknown","Unknown payload length / framing boundaries","No",yesNo),
            new("zero","Zero crossing detector","Yes",yesNo),new("schmitt","Hysteresis (Schmitt) detector","No",yesNo),new("peak","Adaptive zero crossing / peak midpoint detector","No",yesNo),
            new("normal","Normal polarity","Yes",yesNo),new("inverted","Inverted polarity","Yes",yesNo),
            new("channel","Channel","All",session.Model.ChannelCount==1?["All","Mono"]:["All","Left","Right","Mix"]),
            new("rightShift","Right channel offset relative to left (source frames; + delays R)",session.RightShift),
            new("invertRight","Invert right channel polarity",session.InvertRight?"Yes":"No",yesNo),
            new("mixChannels","Mix aligned channels (L + R) / 2",session.Mix?"Yes":"No",yesNo),
            new("adaptive","Adaptive classification","Yes",yesNo),new("fixed","Fixed time scale classification","No",yesNo),new("fuzzy","Fuzzy classification","No",yesNo),
            new("thresholds","Gating scales (semicolon-separated)","0.4;0.65;1;1.4"),new("times","Fixed time scales (semicolon-separated)","0.9;1;1.1"),
            new("short","Fixed SHORT pulse (µs)",Math.Round(PulseReferenceCatalog.GetWriterTiming("rom800").ShortHighMicroseconds).ToString(CultureInfo.InvariantCulture)),
            new("fuzzyTolerance","Fuzzy tolerance (%)","35"),new("deadband","Ignore near zero (±% FS)","0"),
            new("referenceTests","Run explicit reference tests","No",yesNo),new("refs","Reference profiles",JsonSerializer.Serialize(new[]{labels[Math.Clamp(session.ReferenceIndex,0,labels.Length-1)]}),labels,Multiple:true),
            new("referenceTolerance","Reference tolerance (%)","20"),new("half","Test pulse duration at ±0.5 sample (PCM stays fixed)","No",yesNo),new("diagnostics","Retain bounded recovery diagnostics","Yes",yesNo)
        };
        fields=fields.Select(f=>f.Key is "rightShift" or "invertRight" or "mixChannels"?f:session.RecoveryFormValues.TryGetValue(f.Key,out var value)?f with{Value=value}:f).ToList();
        return fields;
    }
    internal async Task<bool> AnalyzeRecoveryAsync(AudioSession session,DiskAnswer answer,long first,long last,CancellationToken ct)
    {
        session.SetSelection([(first,last)]);
        var labels=session.ReferenceNames.Select((name,i)=>$"{i+1}: {name}").ToArray();
        session.RecoveryFormValues=new(answer.Values);
        bool Yes(string key)=>answer[key]=="Yes";
        double Parse(string text)=>double.TryParse(text,NumberStyles.Float,CultureInfo.CurrentCulture,out double value)||double.TryParse(text,NumberStyles.Float,CultureInfo.InvariantCulture,out value)?value:throw new InvalidDataException("Enter a valid number: "+text);
        double Number(string key)=>Parse(answer[key]);
        double[] List(string key)=>answer[key].Split(new[]{';',' ','\t'},StringSplitOptions.RemoveEmptyEntries|StringSplitOptions.TrimEntries).Select(Parse).ToArray();
        var selected=JsonSerializer.Deserialize<string[]>(answer["refs"])??[];
        var references=selected.Select(label=>{int index=Array.IndexOf(labels,label);return index>=0?session.ReferenceAt(index):throw new InvalidDataException("Unknown reference selection.");}).ToArray();
        int rightShift=answer.Number("rightShift");
        if(!session.CanAlign&&(rightShift!=0||Yes("invertRight")))throw new InvalidDataException("Right-channel alignment requires stereo audio.");
        session.RightShift=rightShift.ToString(CultureInfo.InvariantCulture);session.InvertRight=Yes("invertRight");session.Mix=Yes("mixChannels");
        var transform=session.RequestedTransform() with{Mix=session.RequestedTransform().Mix||answer["channel"]=="Mix"};
        bool payloadOnly=answer["scope"]=="Payload only";
        bool unknownLength=payloadOnly&&(Yes("unknown")||string.IsNullOrWhiteSpace(answer["length"]));
        int? length=unknownLength||string.IsNullOrWhiteSpace(answer["length"])?null:answer.Number("length");
        var options=new AudioRecoveryOptions
        {
            PayloadOnly=payloadOnly,UnknownLength=unknownLength,Diagnostics=Yes("diagnostics"),ZeroCrossing=Yes("zero"),Schmitt=Yes("schmitt"),AdaptiveZeroCrossing=Yes("peak"),
            Normal=Yes("normal"),Inverted=Yes("inverted"),Channel=answer["channel"]=="All"?null:answer["channel"]=="Right"?1:0,
            Adaptive=Yes("adaptive"),FixedTiming=Yes("fixed"),Fuzzy=Yes("fuzzy"),ThresholdScales=List("thresholds"),TimeScales=List("times"),ShortMicroseconds=Number("short"),
            FuzzyTolerance=Number("fuzzyTolerance")/100,ZeroDeadband=Number("deadband")/100,ReferenceTests=Yes("referenceTests"),References=references,ReferenceTolerance=Number("referenceTolerance")/100,HalfSample=Yes("half"),Transform=transform
        };
        return await session.RecoverAsync(options,length,Documents.AudioProgress(),ct);
    }
}
