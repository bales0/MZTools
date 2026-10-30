namespace MZTools.Desktop.Documents;

public sealed class AudioBlockRow
{
    internal AudioBlockAnalysis Value {get;}
    internal AudioBlockRow(AudioBlockAnalysis value)=>Value=value;
    public int Record=>Value.Record;public string Name=>Value.Name;public string Block=>Value.Block;public string Profile=>Value.Profile;
    public int Channel=>Value.Channel;public long Start=>Value.StartSample;public long End=>Value.EndSample;
    public bool Checksum=>Value.ChecksumValid;public bool Recovered=>Value.Recovered;public bool Inverted=>Value.Inverted;public string Detector=>Value.DecodeDetector;
    public string Detail=>$"Record {Record} · {Name} / {Block} · {Profile}\nFrames [{Start:N0}, {End:N0}) · channel {Channel} · {Detector} · inverted {Inverted}\nChecksum {Checksum} · recovered {Recovered}\n{Value.PulseMeasurement}\n{Value.MeasurementProvenance}\nGlobal scale {Value.GlobalTimingScale:0.#####} · speed offset {Value.SpeedOffsetPercent:0.###}%\n{Value.PulseSource?.Meaning}";
}
public sealed class AudioRegionRow
{
    internal AudioSignalRegion Value {get;}internal AudioRegionRow(AudioSignalRegion value)=>Value=value;
    public string Kind=>Value.Kind;public long Start=>Value.StartSample;public long End=>Value.EndSample;public int? Record=>Value.Record;public int? Channel=>Value.Channel;public string Evidence=>Value.Evidence;
}
public sealed class AudioIssueRow
{
    internal AudioAnalysisIssue Value {get;}internal AudioIssueRow(AudioAnalysisIssue value)=>Value=value;
    public string Code=>Value.Code;public string Severity=>Value.Severity.ToString();public string Description=>Value.Description;public long Start=>Value.StartSample;public long End=>Value.EndSample;public int? Channel=>Value.Channel;
}
public sealed class AudioLevelRow
{
    internal AudioChannelLevel Value {get;}internal AudioLevelRow(AudioChannelLevel value)=>Value=value;
    public string Channel=>Value.Name;public double Peak=>Value.Peak;public double Rms=>Value.Rms;public double Dc=>Value.DcOffset;
    public double? PeakDbfs=>Peak>0?20*Math.Log10(Peak):null;public double? RmsDbfs=>Rms>0?20*Math.Log10(Rms):null;
    public double Clipping=>Value.ClippingPercent;public int Records=>Value.ValidRecords;public double? EstimatedNoise=>Value.EstimatedNoiseRms;
    public string Detail=>$"{Channel} · {Value.Samples:N0} frames\nPeak {Peak:0.######} FS · RMS {Rms:0.######} FS · DC {Dc:0.######} FS\nPeak/RMS {PeakDbfs:0.###}/{RmsDbfs:0.###} dBFS (zero is unavailable)\nClipping {Value.ClippingCount:N0} ({Clipping:0.###}%) · asymmetry {Value.Asymmetry:0.######} FS\nEstimated noise {EstimatedNoise:0.######} FS · estimated separation {Value.EstimatedSeparationDb:0.###} dB, not laboratory SNR";
}
public sealed class AudioDistributionRow
{
    internal AudioPulseDistribution Value {get;}internal AudioDistributionRow(AudioPulseDistribution value)=>Value=value;
    public string Group=>Value.Group;public long Count=>Value.Count;public double? Mean=>Value.Mean;public double? Median=>Value.Median;public double? StdDev=>Value.StdDev;public double? Reference=>Value.Reference;public double? Deviation=>Value.DeviationPercent;
}
public sealed class AudioAgreementRow
{
    internal AudioDecodeAgreement Value {get;}internal AudioAgreementRow(AudioDecodeAgreement value)=>Value=value;
    public int Channel=>Value.Channel;public int ZeroBlocks=>Value.ZeroCrossValidBlocks;public int SchmittBlocks=>Value.SchmittValidBlocks;public int Common=>Value.CommonValidBlocks;public double? Agreement=>Value.AgreementPercent;public string Definition=>Value.Definition;
}
public sealed class AudioCandidateRow
{
    internal SharpBlockCandidate Value {get;}internal AudioCandidateRow(SharpBlockCandidate value,int index){Value=value;Index=index;}
    public int Index {get;}public string Kind=>Value.Kind.ToString();public int Channel=>Value.Channel+1;public string Detector=>Value.PulseMode.ToString();public bool Inverted=>Value.Inverted;
    public long Start=>Value.StartSample;public long End=>Value.EndSample;public int Bytes=>Value.Data.Length;
    public string Status=>!Value.ChecksumAvailable?"Partial / checksum unknown":!Value.ChecksumValid?"Checksum mismatch":!Value.LengthVerified?"Checksum match / unverified length":"Verified checksum";
    public bool CanSynthesize=>Value.Kind==SharpBlockKind.Payload&&Value.ChecksumAvailable&&Value.ChecksumValid;
    public string Hex=>Status+"\n"+Value.BoundaryEvidence+"\nFirst 4096 payload bytes (export uses all bytes):\n"+string.Join("\n",Convert.ToHexString(Value.Data.AsSpan(0,Math.Min(4096,Bytes))).Chunk(32).Select(chars=>string.Join(' ',new string(chars).Chunk(2).Select(x=>new string(x)))));
}
public sealed class AudioPassRow
{
    internal AudioRecoveryPass Value {get;}internal AudioPassRow(AudioRecoveryPass value,int index){Value=value;Index=index;}
    public int Index {get;}public string Test=>Value.Test;public string Detectors=>Value.Detectors;public string Channels=>Value.Channels;public string Polarity=>Value.Polarity;
    public double Threshold=>Value.ThresholdScale;public int Headers=>Value.ValidHeaders;public int Payloads=>Value.ValidPayloads;public int Candidates=>Value.Candidates;public string? Reference=>Value.Reference;
    public string Detail=>$"{Test} · {Detectors} · {Channels} · {Polarity}\n{Value.AudioTransform}\nThreshold {Threshold} · time scale {Value.TimeScale} · fuzzy {Value.FuzzyTolerance}\nReference {Reference} · {Value.ReferenceEvidence}\nHalf-sample {Value.HalfSample} · deadband {Value.ZeroDeadbandPercent}% FS";
}
public sealed class AudioDifferenceRow
{
    internal AudioPassDifference Value {get;}internal AudioDifferenceRow(AudioPassDifference value)=>Value=value;
    public string Stage=>Value.Stage;public string Status=>Value.Status;public long? Frame=>Value.Sample;public string Left=>Value.Left;public string Right=>Value.Right;public string Evidence=>Value.Evidence;
}
public sealed class AudioDiagnosticRow
{
    internal AudioRecoveryEvent Value {get;}internal AudioDiagnosticRow(AudioRecoveryEvent value)=>Value=value;
    public string Stage=>Value.Stage;public string Code=>Value.Code;public string Reason=>Value.Reason;public long Frame=>Value.Sample;
}
