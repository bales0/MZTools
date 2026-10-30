using System.Security.Cryptography;

namespace MZTools.Desktop.Documents;

public static class PathIdentity
{
    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    public static string Normalize(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        return OperatingSystem.IsMacOS() ? ExistingMacCase(full) : full;
    }
    // Resolve directory-entry spelling on existing macOS paths without assuming every APFS volume is case-insensitive.
    // An absent, case-distinct entry on a case-sensitive volume stays distinct. No probe files are created.
    private static string ExistingMacCase(string path)
    {
        var parent = System.IO.Path.GetDirectoryName(path);
        if (parent is null) return path;
        parent = ExistingMacCase(parent);
        var candidate = System.IO.Path.Combine(parent, System.IO.Path.GetFileName(path));
        if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        var entries = Directory.EnumerateFileSystemEntries(parent).ToArray();
        return entries.FirstOrDefault(p => StringComparer.Ordinal.Equals(System.IO.Path.GetFileName(p), System.IO.Path.GetFileName(candidate)))
            ?? entries.FirstOrDefault(p => StringComparer.OrdinalIgnoreCase.Equals(
                System.IO.Path.GetFileName(p).Normalize(System.Text.NormalizationForm.FormD),
                System.IO.Path.GetFileName(candidate).Normalize(System.Text.NormalizationForm.FormD))) ?? candidate;
    }
    public static bool Same(string first, string second) => Comparer.Equals(Normalize(first), Normalize(second));
}

/// <summary>Write and validate beside the target before replacement. Never writes into the source inode.</summary>
public sealed class AtomicDocumentStore
{
    public static async Task<string?> FingerprintAsync(string path, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path)) return null;
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
        return Convert.ToHexString(await SHA256.HashDataAsync(source, cancellationToken));
    }

    public async Task<string> SaveAsync(DocumentSession document, string destination, string? expectedFingerprint,
        CancellationToken cancellationToken)
    {
        destination = PathIdentity.Normalize(destination);
        RejectLinks(destination);
        if (await FingerprintAsync(destination, cancellationToken) != expectedFingerprint)
            throw new IOException("The destination changed. Reopen it or choose a different Save As destination.");
        var temporary = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(destination)!,
            ".mztools-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await document.WriteAsync(stream, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            await document.ValidateSavedAsync(temporary, cancellationToken);
            var outputHash = (await FingerprintAsync(temporary, cancellationToken))!;
            cancellationToken.ThrowIfCancellationRequested();
            RejectLinks(destination);
            if (await FingerprintAsync(destination, cancellationToken) != expectedFingerprint)
                throw new IOException("The destination changed while saving. No replacement was made.");
            if (File.Exists(destination))
            {
                if ((File.GetAttributes(destination) & FileAttributes.ReadOnly) != 0)
                    throw new UnauthorizedAccessException("The destination is read-only.");
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(destination));
            }
            // Same-directory rename: failure leaves the original intact. Create-only if target was absent.
            File.Move(temporary, destination, overwrite: expectedFingerprint is not null);
            return outputHash;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RejectLinks(string path)
    {
        for (string? current = path; current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Saving through symbolic links is unavailable. Choose a direct destination path.");
        }
    }
}
