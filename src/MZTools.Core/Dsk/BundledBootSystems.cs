using System;
using System.Collections.Generic;
using System.IO;

namespace MZTools;

internal sealed record BundledBootSystem(string FileName, string DisplayName, string Sha256)
{
    public override string ToString() => DisplayName;

    internal DskDocument Open()
    {
        using var resource = typeof(BundledBootSystems).Assembly.GetManifestResourceStream("MZTools.Boot." + FileName)
            ?? throw new InvalidDataException("The bundled boot image is missing: " + FileName);
        using var output = new MemoryStream(); resource.CopyTo(output);
        byte[] bytes = output.ToArray();
        if (!ImageVerificationService.Hash(bytes).Equals(Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The bundled boot image failed verification: " + FileName);
        return DskDocument.Open(bytes);
    }
}

internal static class BundledBootSystems
{
    // Frozen source images from /boot; installers copy only the compatible system areas
    // (plus PCPM.SYS for native P-CP/M80), never the source's ordinary files.
    internal static IReadOnlyList<BundledBootSystem> All { get; } =
    [
        new("P-CPM80.dsk", "P-CP/M80 — 320 KiB", "2710D4EDADED783F6D3B06E2779AE7FF40A102997027CFE3E0182CDD02F1E41D"),
        new("CPMv42SystemDD720kB_POLL.dsk", "CP/M 4.2 — 720 KiB DD (polling)", "EF7CB4198301B99605527311CE705312941900C4D094EE9460BF99C79449F1AF"),
        new("CPMv42SystemHD1440kB_POLL.dsk", "CP/M 4.2 — 1.44 MiB HD (polling)", "C274EF81A2AB4D75F8D146681B8BFC1817A532A604D48819BE70A1453A2565B1"),
        new("CPMv41SystemDD720kB_IRQ.dsk", "CP/M 4.1 — 720 KiB DD (IRQ)", "603C462545E764801FF51D77A94334F18383E416AC6974124674F5560ECC708A"),
        new("CPMv41SystemHD1440K_IRQ.dsk", "CP/M 4.1 — 1.44 MiB HD (IRQ)", "2718E0BB11C195A107CA02D91442975B8323650059F90D5D52A2AAC6577E5050"),
        new("CPMv23System.dsk", "CP/M 2.3 — 720 KiB DD (polling)", "76FC96CDABEB4C961A2E2484B53641BBA36B88F59BDFEA016C38D6529740ED93"),
        new("CPMv23System_320K.dsk", "CP/M 2.3 — 320 KiB (standalone IPL image)", "561C24A9AB13BAD160013E14912D4A7AF0D12DD393C3598B355319274F2E46BC")
    ];

    internal static BundledBootSystem? Recommended(DskDocument target)
    {
        foreach (var item in All)
            if (DskCapabilityService.CanInstallBootSystem(target, item.Open()).IsCompatible) return item;
        return null;
    }
}
