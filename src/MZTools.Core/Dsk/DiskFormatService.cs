using System;
using System.Linq;


namespace MZTools;

internal static class DiskFormatService
{
    internal static DskDocument CreateNew(DskNewOptions options) => SetNewContainer(Create(options),options.HfeVersion);
    internal static DskDocument SetNewContainer(DskDocument document,HfeVersion? version)
    {
        if(version==null) return document;
        var image=HfeImage.FromDsk(document,uniformTiming:version!=HfeVersion.V3);
        return DskDocument.FromFormattedHfe(document,HfeImage.Parse(image.Serialize(version.Value)),null);
    }
    internal static DskDocument Create(DskNewOptions options)=>options.Format switch {
        DskNewFormat.MzBasic=>DskDocumentFactory.CreateFsmz(false,options.Tracks,options.Sides),
        DskNewFormat.IplDisk=>DskDocumentFactory.CreateFsmz(true,options.Tracks,options.Sides),
        DskNewFormat.PersonalCpm=>DskDocumentFactory.CreatePersonalCpm80(false),
        DskNewFormat.Sds400=>DskDocumentFactory.CreatePersonalCpm80(true),
        DskNewFormat.LecCpmDd=>DskDocumentFactory.CreateCpm(false,options.Tracks,options.Sides),
        DskNewFormat.LecCpmHd=>DskDocumentFactory.CreateCpm(true,options.Tracks,options.Sides),
        DskNewFormat.Mrs=>DskDocumentFactory.CreateMrs(options.Tracks,options.Sides),
        DskNewFormat.Lemmings=>DskDocumentFactory.CreateLemmings(options.Tracks,options.Sides),
        DskNewFormat.CustomRaw=>DskDocumentFactory.CreateRaw(options.Tracks,options.Sides,options.Sectors,options.SectorSize,1,options.SectorSize==256?(byte)0x2A:(byte)0x4E,options.Filler,"MZTools",options.SectorOrder,options.SectorIds),
        _=>throw new InvalidOperationException("Choose a filesystem or raw geometry for formatting.")
    };
}

