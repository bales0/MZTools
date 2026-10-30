using System;
using System.Collections.Generic;

namespace MZTools;

internal static class MediaFormats
{
    internal static IReadOnlyList<IMediaFormat> All { get; } = [new ExtendedDskFormat(), new HfeImage()];
}
internal sealed class ExtendedDskFormat : IMediaFormat
{
    public string Id => "extended-dsk";
    public string DisplayName => "Extended CPC DSK";
    public IReadOnlyList<string> Extensions => [".dsk"];
    public MediaCapabilities Capabilities => MediaCapabilities.Detect | MediaCapabilities.Read | MediaCapabilities.Write | MediaCapabilities.SectorModel;
    public MediaDetection Identify(ReadOnlySpan<byte> source) => new(source.Length >= 34 && source[..34].SequenceEqual("EXTENDED CPC DSK File\r\nDisk-Info\r\n"u8), "Extended CPC DSK");
}
