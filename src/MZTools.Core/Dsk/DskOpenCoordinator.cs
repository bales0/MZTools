using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal enum DskOpenStatus { Healthy, RepairAvailable, CannotSafelyRepair, UnsupportedOrUnparseable }

// A read-only snapshot. Deciding whether to show a prompt has no WPF dependency.
internal sealed class DskOpenInspection
{
    private readonly byte[] source;
    private readonly string? path;
    internal DskOpenInspection(byte[] source, string? path, DskDocument? original,
        IReadOnlyList<DskRepairPlan> plans, DskOpenStatus status, string diagnostic,
        IReadOnlyList<DskRepairPlan>? manualPlans = null)
    {
        this.source = (byte[])source.Clone(); this.path = path;
        Original = original; Plans = plans; Status = status; Diagnostic = diagnostic;
        ManualPlans = manualPlans ?? plans;
    }
    internal byte[] SourceBytes => (byte[])source.Clone();
    internal DskDocument? Original { get; }
    internal IReadOnlyList<DskRepairPlan> Plans { get; }
    internal IReadOnlyList<DskRepairPlan> ManualPlans { get; }
    internal DskOpenStatus Status { get; }
    internal string Diagnostic { get; }
    internal bool NeedsRepairPrompt => Plans.Count > 0;

    internal DskDocument Apply(DskRepairPlan plan, byte[] currentSource)
    {
        if (!Plans.Contains(plan) || plan.Preview == null)
            throw new InvalidOperationException("Select an available deterministic container repair.");
        if (!source.AsSpan().SequenceEqual(currentSource))
            throw new InvalidOperationException("The source changed after preview. Open it again; no repair was applied.");
        return DskOpenCoordinator.ApplyCopy(source, path, plan, currentSource);
    }
}

internal static class DskOpenCoordinator
{
    private static bool HasAmbiguousPhysicalMetadata(DskLayoutModel analysis) => analysis.Issues.Any(i => i.Code is
        "DSK_TRACK_CH" or "DSK_SECTOR_CH" or "DSK_DUPLICATE_SECTOR_ID" or "DSK_FDC_STATUS" or
        "DSK_TRACK_SIZE" or "DSK_SECTOR_SIZE" or "DSK_SECTOR_OUTSIDE_TRACK");

    internal static DskDocument ApplyCopy(byte[] source, string? path, DskRepairPlan plan, byte[] currentSource, bool truncateConfirmed = false)
    {
        if (plan.RequiresTruncateConfirmation && !truncateConfirmed)
            throw new InvalidOperationException("Trailing data removal requires separate destructive truncate confirmation.");
        if (!source.AsSpan().SequenceEqual(currentSource))
            throw new InvalidOperationException("The source changed after preview. Open it again; no repair was applied.");
        if (plan.Preview == null) throw new InvalidOperationException("This plan has no applicable repair.");
        plan.Preview.VerifySource(source);
        var protection = CorePlatformServices.CaptureRepairSource(path, source);
        var document = DskDocument.Open(plan.Preview.Result, path);
        _ = DskAnalyzer.Analyze(document);
        document.RequireRepairSaveAs(protection);
        return document;
    }

    internal static DskOpenInspection Inspect(byte[] bytes, string? path = null)
    {
        byte[] source = (byte[])bytes.Clone();
        if (new HfeImage().Identify(source).Matches)
            return new(source, path, DskDocument.Open(source, path), [], DskOpenStatus.Healthy, "HFE container; DSK repair is not applicable.");
        string? parseError = null;
        try { _ = DskImage.Parse(source); }
        catch (Exception e) when (e is InvalidDataException or ArgumentException or OverflowException) { parseError = e.Message; }
        // Filesystem and I/O errors must not be presented as repairable container corruption.
        var original = parseError == null ? DskDocument.Open(source, path) : null;
        var inspected = DskContainerRepairService.Inspect(source);
        var plans = inspected.Where(p => p.Risk == DskRepairRisk.SafeDeterministic &&
            p.Preview != null && !p.RequiresTruncateConfirmation &&
            p.Preview.Result[0x30] >= source[0x30] &&
            !HasAmbiguousPhysicalMetadata(DskAnalyzer.Analyze(p.Preview.Result))).ToArray();
        // Reducing a declared track count can conceal genuinely missing tracks.
        // Keep that ambiguity out of the ordinary Open repair offer.
        var manualPlans = plans.Concat(inspected.Where(p => p.IssueCode == "DSK_TRAILING_DATA" &&
            p.RequiresTruncateConfirmation && p.Preview != null)).ToArray();
        if (plans.Length > 0)
            return new(source, path, original, plans, DskOpenStatus.RepairAvailable,
                (parseError == null ? "Container header repair is available." : parseError) + "\n" + string.Join("\n", plans.Select(p => p.Description)), manualPlans);
        if (original != null)
        {
            if (HasAmbiguousPhysicalMetadata(DskAnalyzer.Analyze(original)))
                return new(source, path, original, [], DskOpenStatus.CannotSafelyRepair,
                    "Physical metadata contains inconsistencies or controller status flags. The original image can be opened unchanged; no safe header repair is offered.", manualPlans);
            return new(source, path, original, [], DskOpenStatus.Healthy,
                original.Image.ContainerTrailingData.Length > 0 ? "Trailing container bytes are preserved. No repair is required to open this image." : "No container repair is needed.", manualPlans);
        }
        bool unsupported = source.Length < 256 || !source.AsSpan(0, 34).SequenceEqual("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8);
        return new(source, path, null, [], unsupported ? DskOpenStatus.UnsupportedOrUnparseable : DskOpenStatus.CannotSafelyRepair,
            parseError + "\n" + (inspected.Any(p => p.Preview != null)
                ? "Missing tracks or ambiguous physical metadata prevent safe container repair. No content will be fabricated."
                : string.Join("\n", inspected.Select(p => p.Description))));
    }
}
