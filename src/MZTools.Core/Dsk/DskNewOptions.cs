using System.Collections.Generic;
namespace MZTools {
    internal enum DskNewFormat
    {
        IplSingle,
        IplMulti,
        MzBasic,
        IplDisk,
        PersonalCpm,
        Sds400,
        LecCpmDd,
        LecCpmHd,
        Mrs,
        Lemmings,
        CustomRaw
    }

    internal sealed record DskNewOptions(
        DskNewFormat Format,
        int Tracks,
        int Sides,
        int Sectors = 16,
        int SectorSize = 256,
        byte Filler = 0xFF,
        DskDocumentFactory.RawSectorOrder SectorOrder = DskDocumentFactory.RawSectorOrder.Normal,
        IReadOnlyList<int>? SectorIds = null,
        HfeVersion? HfeVersion = null);

}

