using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace MZTools;

internal sealed record DskHexByteChange(int Offset, byte Before, byte After);
internal sealed record DskHexSegment(int FileOffset, int BufferOffset, int Length, bool Inverted);

// Captures immutable document bytes; callers cannot mutate a pending transaction.
internal sealed class DskHexEditSession
{
    private readonly byte[] image;
    private readonly byte[] buffer;
    internal DskHexEditSession(byte[] image, byte[] buffer, DskHexSegment[] segments, string title, DskSectorRole role)
    {
        this.image = (byte[])image.Clone(); this.buffer = (byte[])buffer.Clone();
        Segments = Array.AsReadOnly((DskHexSegment[])segments.Clone()); Title = title; Role = role;
        SourceSha256 = DskHexEditService.Hash(image);
    }
    internal string Title { get; }
    internal DskSectorRole Role { get; }
    internal string SourceSha256 { get; }
    internal IReadOnlyList<DskHexSegment> Segments { get; }
    internal byte[] OriginalBuffer => (byte[])buffer.Clone();
    internal byte[] OriginalImage => (byte[])image.Clone();
    internal bool IsSensitive => (Role & (DskSectorRole.Boot | DskSectorRole.System | DskSectorRole.SystemFile | DskSectorRole.Directory |
        DskSectorRole.Fat | DskSectorRole.AllocationMap)) != 0;
}

internal sealed class DskHexEditPreview
{
    private readonly byte[] image;
    internal DskHexEditPreview(DskHexEditSession session, byte[] image, DskHexByteChange[] changes,
        bool requiresConfirmation, string report)
    {
        Session = session; this.image = (byte[])image.Clone();
        Changes = Array.AsReadOnly(changes); RequiresFilesystemConfirmation = requiresConfirmation; Report = report;
    }
    internal DskHexEditSession Session { get; }
    internal IReadOnlyList<DskHexByteChange> Changes { get; }
    internal bool RequiresFilesystemConfirmation { get; }
    internal string Report { get; }
    internal byte[] ResultBytes => (byte[])image.Clone();
}

internal static class DskHexEditService
{
    internal static DskHexEditSession OpenSector(DskDocument document, DskSectorAddress address)
    {
        byte[] bytes = document.Serialize();
        DskDocument snapshot = DskDocument.Open(bytes);
        DskSectorLayout sector = DskAnalyzer.Analyze(snapshot).Sectors.SingleOrDefault(s => s.Address == address)
            ?? throw new InvalidDataException("The selected physical sector does not exist.");
        return new(bytes, sector.Data, [new(checked((int)sector.FileOffset), 0, sector.DataLength, false)],
            $"Physical track {sector.Track}, descriptor {sector.PhysicalIndex}, C/H/R/N={sector.C}/{sector.H}/{sector.R}/{sector.N} (stored bytes)", sector.Role);
    }

    internal static DskHexEditSession OpenCpmBlock(DskDocument document, int block)
    {
        byte[] bytes = document.Serialize();
        DskDocument snapshot = DskDocument.Open(bytes);
        if (snapshot.FileSystem is not CpmFileSystem cpm)
            throw new InvalidDataException("Filesystem block editing requires a recognized CP/M layout.");
        if ((uint)block > cpm.Dpb.Dsm) throw new ArgumentOutOfRangeException(nameof(block));
        DskLayoutModel layout = DskAnalyzer.Analyze(snapshot);
        if (layout.Errors != 0) throw new InvalidDataException("CP/M block mapping requires a consistent filesystem and geometry.");
        var buffer = new byte[cpm.Dpb.BlockSize];
        var segments = new List<DskHexSegment>();
        DskSectorRole role = DskSectorRole.Unknown;
        for (int offset = 0; offset < buffer.Length; offset += 128)
        {
            var (track, id, sectorOffset) = CpmFileSystem.MapByteOffset(cpm.Dpb, block, offset);
            var matches = layout.Sectors.Where(s => s.Track == track && s.R == id).ToArray();
            if (matches.Length != 1 || matches[0].DataLength != 512)
                throw new InvalidDataException("The CP/M block has an ambiguous physical sector map.");
            DskSectorLayout sector = matches[0];
            for (int index = 0; index < 128; index++)
                buffer[offset + index] = (byte)(sector.Data[sectorOffset + index] ^ (cpm.Dpb.Inverted ? 0xFF : 0));
            segments.Add(new(checked((int)sector.FileOffset + sectorOffset), offset, 128, cpm.Dpb.Inverted));
            role |= sector.Role;
        }
        return new(bytes, buffer, segments.ToArray(), $"CP/M allocation block {block} (filesystem-decoded bytes)", role);
    }

    internal static DskHexEditPreview Preview(DskHexEditSession session, byte[] replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        byte[] originalBuffer = session.OriginalBuffer;
        if (replacement.Length != originalBuffer.Length)
            throw new InvalidDataException($"Buffer size must remain exactly {originalBuffer.Length} bytes; received {replacement.Length}.");
        byte[] bytes = session.OriginalImage;
        foreach (DskHexSegment segment in session.Segments)
            for (int index = 0; index < segment.Length; index++)
                bytes[segment.FileOffset + index] = (byte)(replacement[segment.BufferOffset + index] ^ (segment.Inverted ? 0xFF : 0));
        DskDocument before = DskDocument.Open(session.OriginalImage);
        DskDocument after = DskDocument.Open(bytes);
        byte[] serialized = after.Image.Serialize();
        if (!serialized.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Container serialization would change bytes outside the selected buffer.");
        // Reopen the serialized candidate, then validate both container and detected filesystem.
        after = DskDocument.Open(serialized);
        DskLayoutModel analysis = DskAnalyzer.Analyze(after);
        if (analysis.Issues.Any(i => i.Code.StartsWith("DSK_", StringComparison.Ordinal) &&
            i.Severity is DskIssueSeverity.Error or DskIssueSeverity.Unsafe))
            throw new InvalidDataException("The container has errors; hex changes cannot be applied.");
        DskLayoutModel previousAnalysis = DskAnalyzer.Analyze(before);
        bool changedDetection = before.FileSystem.Type != after.FileSystem.Type ||
            DskCapabilityService.CompatibilityKey(before) != DskCapabilityService.CompatibilityKey(after);
        bool hasFilesystemErrors = analysis.Errors != 0;
        bool requiresConfirmation = changedDetection || hasFilesystemErrors ||
            (after.IsReadOnly && after.FileSystem.Type is not (DskFileSystemType.Raw or DskFileSystemType.BootOnly));
        var changes = Enumerable.Range(0, replacement.Length).Where(i => originalBuffer[i] != replacement[i])
            .Select(i => new DskHexByteChange(i, originalBuffer[i], replacement[i])).ToArray();
        var report = new StringBuilder().AppendLine(session.Title).AppendLine($"Role: {session.Role}")
            .AppendLine($"Changed bytes: {changes.Length}")
            .AppendLine($"Filesystem: {before.FileSystem.DisplayName} → {after.FileSystem.DisplayName}")
            .AppendLine($"Analyzer errors/unsafe: {previousAnalysis.Errors} → {analysis.Errors}; warnings: {previousAnalysis.Warnings} → {analysis.Warnings}")
            .AppendLine($"Result SHA-256: {Hash(bytes)}")
            .AppendLine(session.IsSensitive ? "WARNING: edits to boot/system or filesystem metadata may prevent booting or damage files." : "The selected payload bytes will change; file contents may be affected.")
            .AppendLine(requiresConfirmation ? "WARNING: filesystem detection or validation changed. Explicit confirmation is required. Bytes remain available for Save As and raw inspection." : "Container and detected filesystem passed validation. Runtime boot behavior is not checked.");
        foreach (var issue in analysis.Issues.Where(i => i.Severity != DskIssueSeverity.Info))
            report.AppendLine($"{issue.Code}: {issue.Description}");
        foreach (var change in changes) report.AppendLine($"0x{change.Offset:X4}: {change.Before:X2} → {change.After:X2}");
        return new(session, bytes, changes, requiresConfirmation, report.ToString());
    }

    internal static void Apply(DskDocument document, DskHexEditPreview preview, bool acceptFilesystemImpact = false)
    {
        if (Hash(document.Serialize()) != preview.Session.SourceSha256)
            throw new InvalidDataException("The document changed since this hex editor was opened. Reopen the editor and preview again.");
        if (preview.RequiresFilesystemConfirmation && !acceptFilesystemImpact)
            throw new InvalidDataException("The preview requires explicit acceptance of the filesystem impact.");
        if (preview.Changes.Count != 0) document.ReplaceContents(preview.ResultBytes);
    }

    internal static byte[] ParseHex(string text, int expectedLength)
    {
        string[] tokens = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != expectedLength) throw new FormatException($"Enter exactly {expectedLength} two-digit hexadecimal bytes; found {tokens.Length}.");
        return tokens.Select(token => token.Length == 2 && byte.TryParse(token, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte value)
            ? value : throw new FormatException($"Invalid hexadecimal byte '{token}'. Use two digits per byte, separated by whitespace.")).ToArray();
    }

    internal static string FormatHex(byte[] bytes) => string.Join(Environment.NewLine,
        bytes.Chunk(16).Select(row => string.Join(" ", row.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)))));
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
}
