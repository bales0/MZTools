using System;
using System.IO;
using System.Linq;

namespace MZTools;

internal static class DskImportPolicy
{
        internal static string GetImportName(DskFileSystemType fileSystemType, string path, string embeddedMzfName)
        {
            if (fileSystemType is not (DskFileSystemType.Cpm or DskFileSystemType.Mrs))
            {
                return embeddedMzfName.Trim();
            }

            string sourceName = Path.GetFileNameWithoutExtension(path);
            string sourceExtension = Path.GetExtension(path).TrimStart('.');
            string baseName = SanitizeCpmNamePart(sourceName, 8);
            string extension = SanitizeCpmNamePart(sourceExtension, 3);
            if (baseName.Length == 0) baseName = "FILE";
            return extension.Length == 0 ? baseName : $"{baseName}.{extension}";
        }

        private static string SanitizeCpmNamePart(string value, int maximumLength)
        {
            const string punctuation = "!#$%&'()-@^_`{}~";
            return new string(value.ToUpperInvariant()
                .Select(character => char.IsAsciiLetterOrDigit(character) || punctuation.Contains(character) ? character : '_')
                .Take(maximumLength)
                .ToArray())
                .Trim('_');
        }

}
