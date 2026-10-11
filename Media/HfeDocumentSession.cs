using System;
using System.IO;

namespace MZTools;

// The active physical image and its save destination move together after Save As.
internal sealed class HfeDocumentSession(HfeImage image, string? path)
{
    internal HfeImage Image { get; private set; } = image;
    internal string? FilePath { get; private set; } = path;

    internal void SaveAs(string destination)
    {
        string fullPath = Path.GetFullPath(destination);
        if (!Path.GetExtension(fullPath).Equals(".hfe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Save this physical image as .hfe. Use Export converted image for other formats.");
        byte[] bytes = Image.Serialize(Image.Version);
        var savedImage = HfeImage.Parse(bytes);
        MediaConversionService.WriteVerified(fullPath, bytes, stored =>
        {
            if (!stored.AsSpan().SequenceEqual(bytes)) throw new IOException("Saved HFE bytes differ from the prepared image.");
            HfeImage.Parse(stored);
        });
        Image = savedImage;
        FilePath = fullPath;
    }
}
