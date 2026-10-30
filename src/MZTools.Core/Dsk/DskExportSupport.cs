using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
namespace MZTools {
    internal static class DskMzfConverter
    {
        internal static byte[] Serialize(DskFileEntry entry, byte[] data)
        {
            ArgumentNullException.ThrowIfNull(entry);
            ArgumentNullException.ThrowIfNull(data);
            if (data.Length > ushort.MaxValue)
                throw new InvalidDataException("MZF payloads cannot exceed 65535 bytes; no output file was created.");
            var header = new byte[128]; header[0] = entry.FileType;
            byte[] name = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.Name);
            name.AsSpan(0, Math.Min(16, name.Length)).CopyTo(header.AsSpan(1, 16)); header[17] = 0x0D;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(18, 2), checked((ushort)data.Length));
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20, 2), entry.LoadAddress);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22, 2), entry.ExecuteAddress);
            return header.Concat(data).ToArray();
        }
    }

    internal static class DskExportSupport
    {
        internal static bool CanExportMzf(DskFileSystemType type, DskFileEntry entry) =>
            type is DskFileSystemType.Fsmz or DskFileSystemType.SingleIpl ||
            type == DskFileSystemType.BootOnly && entry.Key == "iplpro";

        internal static string GetFilter(DskFileSystemType type, bool canExportMzf)
        {
            if (type is DskFileSystemType.Cpm or DskFileSystemType.Mrs)
                return "Original binary file|*.*";
            if (!canExportMzf) return "Raw payload|*.bin";
            string mzfLabel = type == DskFileSystemType.Fsmz
                ? "Native MZF file"
                : type == DskFileSystemType.BootOnly
                    ? "Reconstructed IPL bootstrap MZF"
                    : "Reconstructed MZF from IPL metadata";
            return $"Raw payload|*.bin|{mzfLabel}|*.mzf";
        }

        internal static IReadOnlyList<string> BuildBatchFileNames(
            IReadOnlyList<DskFileEntry> entries,
            DskFileSystemType type,
            bool exportMzf)
        {
            string[] naturalNames = entries
                .Select(entry => SanitizeFileName(exportMzf
                    ? Path.ChangeExtension(SuggestedName(entry), ".mzf")
                    : SuggestedName(entry)))
                .ToArray();

            if (type == DskFileSystemType.Cpm)
            {
                HashSet<string> duplicates = naturalNames
                    .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (int index = 0; index < naturalNames.Length; index++)
                {
                    if (duplicates.Contains(naturalNames[index]))
                        naturalNames[index] = $"U{entries[index].User:D2}_{naturalNames[index]}";
                }
            }

            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (int index = 0; index < naturalNames.Length; index++)
            {
                string candidate = naturalNames[index];
                string stem = Path.GetFileNameWithoutExtension(candidate);
                string extension = Path.GetExtension(candidate);
                int suffix = 2;
                while (!used.Add(candidate)) candidate = $"{stem}_{suffix++}{extension}";
                naturalNames[index] = candidate;
            }
            return naturalNames;
        }

        private static string SuggestedName(DskFileEntry entry) => string.IsNullOrEmpty(entry.Extension)
            ? entry.Name
            : $"{entry.Name}.{entry.Extension}";

        private static string SanitizeFileName(string value)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = new(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
            result = result.Trim().TrimEnd('.');
            return string.IsNullOrWhiteSpace(result) ? "PROGRAM" : result;
        }
    }

}
