using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace MZTools;

// Shared by legacy tape writers and the desktop document coordinator. No format logic lives here.
internal static class AtomicFileTransaction
{
    internal static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    internal static string? Hash(string path)
    {
        if (!File.Exists(path)) return null;
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    internal static void Write(IReadOnlyDictionary<string, byte[]> files,
        IReadOnlyDictionary<string, string?>? expected = null, CancellationToken cancellation = default,
        Action<int>? afterInstall = null, IReadOnlyDictionary<string,string>? sourceFiles = null, Action? beforeCommit = null)
    {
        var outputs = files.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, PathComparer);
        var sources = sourceFiles?.ToDictionary(p => Path.GetFullPath(p.Key),p => p.Value,PathComparer);
        var hashes = expected is null ? outputs.Keys.ToDictionary(p => p, Hash, PathComparer)
            : expected.ToDictionary(p => Path.GetFullPath(p.Key), p => p.Value, PathComparer);
        if (outputs.Count == 0 || outputs.Keys.Any(p => !hashes.ContainsKey(p)))
            throw new InvalidOperationException("Every output requires a captured destination fingerprint.");
        string id = Guid.NewGuid().ToString("N");
        var stages = new Dictionary<string, string>(PathComparer);
        var backups = new Dictionary<string, string>(PathComparer);
        var installed = new List<string>();
        bool keepRecovery = false;
        void Check(string path)
        {
            for (string? p = path; p != null; p = Path.GetDirectoryName(p))
                if ((File.Exists(p) || Directory.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Saving through symbolic links is unavailable: " + p);
            if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                throw new UnauthorizedAccessException("The destination is read-only: " + path);
            if (Hash(path) != hashes[path]) throw new IOException("The destination changed: " + path);
        }
        try
        {
            foreach (var output in outputs)
            {
                cancellation.ThrowIfCancellationRequested(); Check(output.Key);
                string directory = Path.GetDirectoryName(output.Key)!;
                Directory.CreateDirectory(directory);
                string temporary = Path.Combine(directory, ".mztools-" + id + "-" + stages.Count + ".tmp");
                stages.Add(output.Key, temporary);
                string? source = sources is not null && sources.TryGetValue(output.Key,out var inputPath) ? inputPath : null;
                string outputHash = source is null ? Convert.ToHexString(SHA256.HashData(output.Value))
                    : Hash(source) ?? throw new FileNotFoundException("Staged export source is missing.",source);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    if(source is null) stream.Write(output.Value);
                    else
                    {
                        using var input=File.OpenRead(source);var buffer=new byte[65536];int read;
                        while((read=input.Read(buffer))!=0){cancellation.ThrowIfCancellationRequested();stream.Write(buffer,0,read);}
                    }
                    stream.Flush(true);
                }
                if (Hash(temporary) != outputHash)
                    throw new IOException("Output verification failed: " + output.Key);
                if (File.Exists(output.Key))
                {
                    if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(output.Key));
                    string backup = temporary + ".bak";
                    File.Copy(output.Key, backup, false); backups.Add(output.Key, backup);
                }
            }
            cancellation.ThrowIfCancellationRequested();
            beforeCommit?.Invoke();
            foreach (string target in outputs.Keys) Check(target);
            // Cancellation is deferred through commit/rollback, so it cannot abandon half a file set.
            foreach (var stage in stages)
            {
                Check(stage.Key);
                File.Move(stage.Value, stage.Key, overwrite: hashes[stage.Key] != null);
                installed.Add(stage.Key); afterInstall?.Invoke(installed.Count);
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            foreach (string path in installed.AsEnumerable().Reverse())
            {
                try
                {
                    if (backups.TryGetValue(path, out var backup)) File.Move(backup, path, true);
                    else File.Delete(path);
                }
                catch (Exception recoveryFailure) { rollbackFailures.Add(recoveryFailure); }
            }
            if (rollbackFailures.Count != 0)
            {
                keepRecovery = true;
                throw new IOException("Rollback incomplete. Recovery backups retained: " + string.Join(", ", backups.Values),
                    new AggregateException(new[] { failure }.Concat(rollbackFailures)));
            }
            throw;
        }
        finally
        {
            foreach (var temporary in stages.Values) if (File.Exists(temporary)) File.Delete(temporary);
            if (!keepRecovery) foreach (var backup in backups.Values) if (File.Exists(backup)) File.Delete(backup);
        }
    }
}
