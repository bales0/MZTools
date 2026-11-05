namespace MZTools.Desktop.Documents;

public sealed partial class AudioSession
{
    private IReadOnlyList<AudioAnalysisIssue> rawPreviewIssues=[];
    private IReadOnlyList<AudioSignalRegion>? rawPreviewRegions;
    private AudioRecoveryMeasurement? selectedRecoveryMeasurement;
    private AudioPcmTransform? selectedRecoveryTransform;
    internal bool SelectedRecoveryMatchesPreview=>Recovery is{} r&&selectedRecoveryTransform==DisplayedTransform&&r.StartSample==PreviewStart&&r.EndSample==PreviewEnd;
    private IReadOnlyList<AudioIssueRow>? selectedPreviewIssues;
    private IReadOnlyList<AudioSignalRegion>? selectedPreviewRegions;
    public IReadOnlyList<AudioIssueRow> PreviewIssues=>selectedPreviewIssues??issues;
    internal IReadOnlyList<AudioSignalRegion> DisplayRegions=>selectedPreviewRegions??Model.Regions;
    private void SetRawSelectedPreview(AudioRangePreview preview)
    {
        rawPreviewIssues=preview.Analysis.Issues;rawPreviewRegions=preview.Analysis.Regions;
        RefreshSelectedOverlays();
    }
    internal void UseSelectedRecoveryMeasurement(AudioRecoveryMeasurement? measurement)
    {
        selectedRecoveryMeasurement=measurement;MeasuredBlock=measurement?.Block;NotifyMeasurement();
        RefreshSelectedOverlays();
    }
    private void RefreshSelectedOverlays()
    {
        selectedPreviewIssues=rawPreviewIssues.Select(i=>new AudioIssueRow(i)).ToArray();
        selectedPreviewRegions=rawPreviewRegions??Model.Regions;
        Changed(nameof(PreviewIssues));Changed(nameof(DisplayRegions));
        if(!SelectedRecoveryMatchesPreview||Recovery is not{} result)return;
        var measurement=selectedRecoveryMeasurement;
        var findings=rawPreviewIssues.Concat(measurement?.Issues??[])
            .Concat(result.Candidates.Where(c=>!c.ChecksumValid).Select(c=>new AudioAnalysisIssue("AUDIO_CHECKSUM_FAILURE",DskIssueSeverity.Warning,$"{c.Kind} checksum failed. Exact damaged byte unknown.",c.SignalTrace?.DataStart??c.StartSample,c.EndSample,Channel:c.Channel+1,Block:c.Kind.ToString())))
            .Concat(result.Diagnostics.Where(e=>e.Code is "PULSE_CLASSIFICATION_RESET" or "BAD_BYTE_STOP" or "BLOCK_TRUNCATED").Select(e=>new AudioAnalysisIssue("AUDIO_DECODER_RESET",DskIssueSeverity.Warning,$"{e.Code}; {e.Detector}; byte {e.ByteIndex}; {e.Context}",Math.Max(result.StartSample,e.Sample-Math.Max(1,(long)Math.Ceiling(e.PulseSamples))),Math.Min(result.EndSample,e.Sample+1),Channel:e.Channel)))
            .Concat(result.Failures.Select(f=>new AudioAnalysisIssue("AUDIO_UNRESOLVED_REGION",DskIssueSeverity.Error,f.Reason+" Exact damaged byte unknown.",f.StartSample,f.EndSample)));
        selectedPreviewIssues=findings.Select(i=>new AudioIssueRow(i)).ToArray();
        var regions=result.Candidates.Select(c=>new AudioSignalRegion(c.Kind.ToString(),c.SignalTrace?.LeaderStart??c.StartSample,c.EndSample,null,c.Channel+1,c.ChecksumValid?"Checksum-valid block candidate.":"Checksum-failed block candidate."){SampleRate=Model.SampleRate}).ToArray();
        selectedPreviewRegions=regions.Length>0?regions:Model.Regions;Changed(nameof(PreviewIssues));Changed(nameof(DisplayRegions));
    }
    private (long Start,long End)? previewBounds;
    internal long PreviewStart=>previewBounds?.Start??0;
    internal long PreviewEnd=>previewBounds?.End??Model.FrameCount;
    internal void SetPreviewBounds(long first,long last)
    {
        if(first<0||last>Model.FrameCount||first>=last)throw new ArgumentOutOfRangeException(nameof(first),"Choose a nonempty interval within the source recording.");
        previewBounds=(first,last);SetSelection([(first,last)]);ShowRange(first,last);RefreshSelectedOverlays();
    }
    internal AudioSession CreateSelectedPreview(long first,long last)
    {
        if(first<0||last>Model.FrameCount||first>=last)throw new ArgumentOutOfRangeException(nameof(first));
        // Source coordinates and immutable PCM stay shared. Results, selection,
        // transform settings and operation lifetime belong to the new window.
        var model=new AudioSignalAnalysis
        {
            Source=Model.Source,SourceLength=Model.SourceLength,SourceModifiedUtc=Model.SourceModifiedUtc,SourceStamps=Model.SourceStamps,
            Format=Model.Format,SampleRate=Model.SampleRate,BitsPerSample=Model.BitsPerSample,
            ChannelCount=Model.ChannelCount,FrameCount=Model.FrameCount,Waveform=Model.Waveform,
            Regions=[new AudioSignalRegion("Unassigned audio",first,last,null,null,"Selected PCM interval; no program association inferred."){SampleRate=Model.SampleRate}]

        };
        var preview=new AudioSession(model,new(sources,AtomicFileTransaction.PathComparer));
        preview.references.Clear();preview.references.AddRange(references);preview.referenceIndex=referenceIndex;
        preview.shift=RightShift;preview.invertRight=InvertRight;preview.mix=Mix;preview.channelMode=Model.ChannelCount>1?2:0;
        preview.AppliedTransform=AppliedTransform;preview.DisplayedTransform=DisplayedTransform;
        preview.DisplayWaveform=DisplayWaveform;preview.autoScale=AutoScale;preview.gain=Gain;
        preview.Loaded(Model.Source,SavedFingerprint??sources[Model.Source]!,true);
        preview.SetPreviewBounds(first,last);return preview;
    }
}
