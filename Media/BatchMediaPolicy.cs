using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal static class BatchMediaPolicy
{
    internal const string TapeExtensions = ".mzf,.m12,.mz0,.mz7,.mzt,.wav,.flac,.lep,.l16,.qd,.qdf,.mzq";
    internal const string AllExtensions = ".dsk,.hfe," + TapeExtensions;
    internal static bool ReadOnly(BatchOperation operation) => operation is BatchOperation.Analyze or BatchOperation.TestIntegrity or BatchOperation.AnalyzeMzfCompatibility;
    internal static string Extensions(BatchOperation operation, string? target = null) => operation switch
    {
        BatchOperation.ConvertFormat when target != null => BatchTapeService.Targets.Contains(target) ? TapeExtensions : ".dsk,.hfe",
        BatchOperation.Analyze or BatchOperation.TestIntegrity or BatchOperation.ConvertFormat => AllExtensions,
        BatchOperation.CompressPrograms or BatchOperation.DecompressPrograms => TapeExtensions,
        BatchOperation.AnalyzeMzfCompatibility => ".mzf,.m12,.mz0,.mz7",
        BatchOperation.ExtractAll => ".dsk," + TapeExtensions,
        BatchOperation.InstallBootSystem or BatchOperation.ApplyPatch or BatchOperation.Defragment or BatchOperation.SafeRepair => ".dsk",
        _ => ""
    };
    internal static bool Supports(BatchOperation operation, string path, string? target = null) => Extensions(operation, target).Split(',').Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    internal static string PickerFilter(BatchOperation operation, string? target = null) => "Supported inputs|" + string.Join(';', Extensions(operation, target).Split(',').Select(e => "*" + e));
    internal static IReadOnlyList<string> Targets => new[] { "HFEv3", "HFE", "DSK" }.Concat(Enum.GetNames<DskConversionTarget>()).Concat(BatchTapeService.Targets).ToArray();
    internal static string Label(BatchOperation operation) => operation switch
    {
        BatchOperation.Analyze => "Info / catalog",
        BatchOperation.TestIntegrity => "Info / test integrity",
        BatchOperation.ConvertFormat => "Convert format",
        BatchOperation.CompressPrograms => "Compress programs (ZX0 / ZX7)",
        BatchOperation.DecompressPrograms => "Decompress programs (recognized ZX0 / ZX7)",
        BatchOperation.AnalyzeMzfCompatibility => "Analyze MZF/M12 → COM compatibility",
        BatchOperation.InstallBootSystem => "Install boot system (DSK)",
        BatchOperation.ApplyPatch => "Apply patch (DSK)",
        BatchOperation.ExtractAll => "Extract all files / programs",
        BatchOperation.Defragment => "Defragment (DSK)",
        BatchOperation.SafeRepair => "Safe repair (DSK)",
        _ => operation.ToString()
    };
}
