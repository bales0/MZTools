using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using static QDTool.MzfFormatSupport;
using static QDTool.SharpBinary;

namespace QDTool
{
    internal static class MzfFormatSupport
    {
        public static readonly byte[] ExpectedStartSign = [0x00, 0x16, 0x16, 0xA5];
        public static readonly byte[] ExpectedCrc = [(byte)'C', (byte)'R', (byte)'C'];
        public static readonly byte[] ExpectedUnused = [0x00, 0x00];
        public static readonly byte[] ZeroStartSign = [0x00, 0x00, 0x00, 0x00];
        public static readonly byte[] ZeroCrc = [0x00, 0x00, 0x00];
    }

    internal static class SharpMzEncoding
    {
        private static readonly byte[] SharpAscii =
        [
            (byte)'_', (byte)' ', (byte)'e', (byte)' ', (byte)'~', (byte)' ', (byte)'t', (byte)'g',
            (byte)'h', (byte)' ', (byte)'b', (byte)'x', (byte)'d', (byte)'r', (byte)'p', (byte)'c',
            (byte)'q', (byte)'a', (byte)'z', (byte)'w', (byte)'s', (byte)'u', (byte)'i', (byte)' ',
            (byte)' ', (byte)'k', (byte)'f', (byte)'v', (byte)' ', (byte)' ', (byte)' ', (byte)'j',
            (byte)'n', (byte)' ', (byte)' ', (byte)'m', (byte)' ', (byte)' ', (byte)' ', (byte)'o',
            (byte)'l', (byte)' ', (byte)' ', (byte)' ', (byte)' ', (byte)'y', (byte)'{', (byte)' ',
            (byte)'|'
        ];

        public static byte FromSHASCII(byte value)
        {
            if (value <= 0x5D) return value;
            if (value == 0x80) return (byte)'}';
            if (value < 0x90 || value > 0xC0) return (byte)' ';
            return SharpAscii[value - 0x90];
        }

        public static string ConvertMzfNameToASCIIString(byte[] bytes)
        {
            byte[] convertedBytes = bytes.Select(FromSHASCII).ToArray();
            int endIndex = Array.FindIndex(convertedBytes, value => value is 0x00 or 0x0D);
            if (endIndex == -1)
            {
                endIndex = convertedBytes.Length;
            }
            return Encoding.ASCII.GetString(convertedBytes, 0, endIndex);
        }

        public static byte[] ConvertASCIIStringToSHASCIIBytes(string value)
        {
            ArgumentNullException.ThrowIfNull(value);
            var result = new byte[value.Length];
            for (int index = 0; index < value.Length; index++)
            {
                char character = value[index];
                if (character <= 0x5D)
                {
                    result[index] = character >= 0x20 ? (byte)character : (byte)' ';
                    continue;
                }

                if (character == '}')
                {
                    result[index] = 0x80;
                    continue;
                }

                int sharpIndex = character <= 0x7F
                    ? Array.IndexOf(SharpAscii, (byte)character)
                    : -1;
                result[index] = sharpIndex >= 0
                    ? (byte)(0x90 + sharpIndex)
                    : (byte)' ';
            }
            return result;
        }

        public static string ConvertMzfDescriptionToASCIIString(byte[] bytes)
        {
            char[] convertedCharacters = bytes
                .Select(FromSHASCII)
                .Select(value => char.IsControl((char)value) ? ' ' : (char)value)
                .ToArray();

            return new string(convertedCharacters)
                .Trim();
        }

        public static string ConvertFtypeToDescription(byte fileType) => fileType switch
        {
            0x01 => "OBJ",
            0x02 => "BTX",
            0x03 => "BSD",
            0x04 => "BRD",
            0x05 => "RB ",
            0x06 => "ASC",
            0x07 => "LIB",
            0x08 => "PTX",
            0x09 => "PSD",
            0x0A => "SYS",
            0x0B => "GR ",
            0x0C => "LOG",
            0x0D => "PIC",
            0x41 => "AS1",
            0x42 => "AS2",
            0x44 => "DZ2",
            0x58 => "XB1",
            0x94 => "TXT",
            0x95 => "LSP",
            0xA0 => "PTX",
            0xA1 => "PSD",
            0xFE => "FET",
            _ => "???"
        };
    }

    internal static class SharpBinary
    {
        public static byte[] ReadBytesExact(BinaryReader reader, int count, string fieldName)
        {
            byte[] bytes = reader.ReadBytes(count);
            if (bytes.Length != count)
            {
                throw new EndOfStreamException(
                    $"Unexpected end of file while reading {fieldName}: expected {count} bytes, got {bytes.Length}.");
            }
            return bytes;
        }
    }

    internal enum TapeProfile
    {
        Normal1_1,
        Normal1_2,
        Normal1_3,
        Normal1_4,
        Mz700_1_1,
        Mz700_1_3,
        Ic1_2,
        Ic1_3,
        Ic1_4,
        Tc1_2,
        Tc1_3,
        Ultra,
        UltraMz800,
        UltraMz700,

        // Added at the end intentionally, so the numeric values of all
        // pre-existing profiles remain stable for existing code/tests.
        Ic1_1,
        Tc1_1
    }

    internal enum MetadataOrigin
    {
        Implicit,
        LoadedFromMfi,
        LoadedFromMti,
        CreatedOrModifiedInAdvanced
    }

    internal enum TapeDocumentFormat
    {
        None,
        Mzf,
        Mzt,
        Mzq,
        Qdf,
        QdSharpLegacy,
        QdHxc,
        QdFlashFloppy
    }

    internal sealed class TapeRecord
    {
        public const int HeaderLength = 128;

        public TapeRecord(
            byte[] rawHeader,
            MZQFileHeader header,
            MZQFileBody body,
            TapeProfile profile = TapeProfile.Normal1_1,
            MetadataOrigin metadataOrigin = MetadataOrigin.Implicit)
        {
            if (rawHeader.Length != HeaderLength)
            {
                throw new ArgumentException("An MZF raw header must contain exactly 128 bytes.", nameof(rawHeader));
            }

            RawHeader = (byte[])rawHeader.Clone();
            Header = header;
            Body = body;
            Profile = profile;
            MetadataOrigin = metadataOrigin;
        }

        public byte[] RawHeader { get; }

        public MZQFileHeader Header { get; set; }

        public MZQFileBody Body { get; set; }

        public TapeProfile Profile { get; set; }

        public MetadataOrigin MetadataOrigin { get; set; }

        public byte[] GetSerializedHeader()
        {
            byte[] result = (byte[])RawHeader.Clone();
            MZQFileHeader header = Header;
            result[0] = header.MzfFtype;
            CopyFixed(header.MzfFname, result.AsSpan(1, 16));
            result[17] = header.MzfFnameEnd;
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), header.MzfExec);
            return result;
        }

        public byte[] DescriptionRaw => GetSerializedHeader()[24..128];

        public void RemoveTrailingData()
        {
            MZQFileBody body = Body;
            body.TrailingData = Array.Empty<byte>();
            Body = body;
        }

        public void ResetMetadataToImplicit()
        {
            Profile = TapeProfile.Normal1_1;
            MetadataOrigin = MetadataOrigin.Implicit;
        }

        public TapeRecord DeepClone()
        {
            MZQFileHeader header = Header;
            header.StartSign = CloneArray(header.StartSign);
            header.MzfFname = CloneArray(header.MzfFname);
            header.Unused1 = CloneArray(header.Unused1);
            header.MzfHeaderDescription = CloneArray(header.MzfHeaderDescription);
            header.Crc = CloneArray(header.Crc);
            MZQFileBody body = Body;
            body.StartSign = CloneArray(body.StartSign);
            body.MzfBody = CloneArray(body.MzfBody);
            body.Crc = CloneArray(body.Crc);
            body.TrailingData = CloneArray(body.TrailingData);
            return new TapeRecord(RawHeader, header, body, Profile, MetadataOrigin);
        }

        public static TapeRecord FromLegacy(MZQFileHeader header, MZQFileBody body)
        {
            byte[] rawHeader = new byte[HeaderLength];
            rawHeader[0] = header.MzfFtype;
            CopyFixed(header.MzfFname, rawHeader.AsSpan(1, 16));
            rawHeader[17] = header.MzfFnameEnd;
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(18, 2), header.MzfSize);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(20, 2), header.MzfStart);
            BinaryPrimitives.WriteUInt16LittleEndian(rawHeader.AsSpan(22, 2), header.MzfExec);
            CopyFixed(header.MzfHeaderDescription, rawHeader.AsSpan(24, 104));
            return new TapeRecord(rawHeader, header, body);
        }

        private static void CopyFixed(byte[]? source, Span<byte> destination)
        {
            destination.Clear();
            if (source != null)
            {
                source.AsSpan(0, Math.Min(source.Length, destination.Length)).CopyTo(destination);
            }
        }

        private static byte[] CloneArray(byte[]? source) =>
            source == null ? Array.Empty<byte>() : (byte[])source.Clone();
    }

    internal sealed class TapeDocument
    {
        public List<TapeRecord> Records { get; } = new();

        public string? FilePath { get; set; }

        public TapeDocumentFormat Format { get; set; }

        public bool IsModified { get; set; }

        public QuickDiskPhysicalProfile? QuickDiskProfile { get; set; }

        public byte[] ContainerTrailingData { get; set; } = Array.Empty<byte>();

        public string? SidecarPath { get; set; }

        public Mz800IplDskInfo? IplDskInfo { get; set; }

        public bool HasSidecarBinding => SidecarPath != null;

        public bool IsQuickDisk => Format is
            TapeDocumentFormat.Mzq or TapeDocumentFormat.Qdf or
            TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;

        public bool IsQdImage => Format is
            TapeDocumentFormat.QdSharpLegacy or TapeDocumentFormat.QdHxc or TapeDocumentFormat.QdFlashFloppy;

        public void Clear()
        {
            Records.Clear();
            FilePath = null;
            Format = TapeDocumentFormat.None;
            IsModified = false;
            QuickDiskProfile = null;
            ContainerTrailingData = Array.Empty<byte>();
            SidecarPath = null;
            IplDskInfo = null;
        }
    }

    internal static class TapeProfileNames
    {
        public static string ToDisplayName(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => "NORMAL 1:1",
            TapeProfile.Normal1_2 => "NORMAL 1:2",
            TapeProfile.Normal1_3 => "NORMAL 1:3",
            TapeProfile.Normal1_4 => "NORMAL 1:4",
            TapeProfile.Mz700_1_1 => "MZ700 1:1",
            TapeProfile.Mz700_1_3 => "MZ700 1:3",
            TapeProfile.Ic1_1 => "IC 1:1",
            TapeProfile.Ic1_2 => "IC 1:2",
            TapeProfile.Ic1_3 => "IC 1:3",
            TapeProfile.Ic1_4 => "IC 1:4",
            TapeProfile.Tc1_1 => "TC 1:1",
            TapeProfile.Tc1_2 => "TC 1:2",
            TapeProfile.Tc1_3 => "TC 1:3",
            TapeProfile.Ultra => "UL",
            TapeProfile.UltraMz800 => "UL_MZ800",
            TapeProfile.UltraMz700 => "UL_MZ700",
            _ => profile.ToString()
        };
    }

    internal static class TapeProfileComponents
    {
        private static readonly string[] AllLoaders =
        [
            "NORMAL", "MZ700", "IC", "TC", "UL", "UL_MZ800", "UL_MZ700"
        ];

        private static readonly string[] NormalSpeeds = ["1:1", "1:2", "1:3", "1:4"];
        private static readonly string[] Mz700Speeds = ["1:1", "1:3"];
        private static readonly string[] IcSpeeds = ["1:1", "1:2", "1:3", "1:4"];
        private static readonly string[] TcSpeeds = ["1:1", "1:2", "1:3"];
        private static readonly string[] NoSpeed = [""];

        public static IReadOnlyList<string> LoaderTypes => AllLoaders;

        public static IReadOnlyList<string> GetAvailableSpeeds(string loaderType) => loaderType switch
        {
            "NORMAL" => NormalSpeeds,
            "MZ700" => Mz700Speeds,
            "IC" => IcSpeeds,
            "TC" => TcSpeeds,
            "UL" or "UL_MZ800" or "UL_MZ700" => NoSpeed,
            _ => NormalSpeeds
        };

        public static string NormalizeSpeed(string loaderType, string? speed)
        {
            IReadOnlyList<string> available = GetAvailableSpeeds(loaderType);
            return speed != null && available.Contains(speed, StringComparer.Ordinal)
                ? speed
                : available[0];
        }

        public static (string LoaderType, string Speed) Split(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => ("NORMAL", "1:1"),
            TapeProfile.Normal1_2 => ("NORMAL", "1:2"),
            TapeProfile.Normal1_3 => ("NORMAL", "1:3"),
            TapeProfile.Normal1_4 => ("NORMAL", "1:4"),
            TapeProfile.Mz700_1_1 => ("MZ700", "1:1"),
            TapeProfile.Mz700_1_3 => ("MZ700", "1:3"),
            TapeProfile.Ic1_1 => ("IC", "1:1"),
            TapeProfile.Ic1_2 => ("IC", "1:2"),
            TapeProfile.Ic1_3 => ("IC", "1:3"),
            TapeProfile.Ic1_4 => ("IC", "1:4"),
            TapeProfile.Tc1_1 => ("TC", "1:1"),
            TapeProfile.Tc1_2 => ("TC", "1:2"),
            TapeProfile.Tc1_3 => ("TC", "1:3"),
            TapeProfile.Ultra => ("UL", ""),
            TapeProfile.UltraMz800 => ("UL_MZ800", ""),
            TapeProfile.UltraMz700 => ("UL_MZ700", ""),
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };

        public static TapeProfile Combine(string loaderType, string? speed)
        {
            string normalizedSpeed = NormalizeSpeed(loaderType, speed);
            return (loaderType, normalizedSpeed) switch
            {
                ("NORMAL", "1:1") => TapeProfile.Normal1_1,
                ("NORMAL", "1:2") => TapeProfile.Normal1_2,
                ("NORMAL", "1:3") => TapeProfile.Normal1_3,
                ("NORMAL", "1:4") => TapeProfile.Normal1_4,
                ("MZ700", "1:1") => TapeProfile.Mz700_1_1,
                ("MZ700", "1:3") => TapeProfile.Mz700_1_3,
                ("IC", "1:1") => TapeProfile.Ic1_1,
                ("IC", "1:2") => TapeProfile.Ic1_2,
                ("IC", "1:3") => TapeProfile.Ic1_3,
                ("IC", "1:4") => TapeProfile.Ic1_4,
                ("TC", "1:1") => TapeProfile.Tc1_1,
                ("TC", "1:2") => TapeProfile.Tc1_2,
                ("TC", "1:3") => TapeProfile.Tc1_3,
                ("UL", "") => TapeProfile.Ultra,
                ("UL_MZ800", "") => TapeProfile.UltraMz800,
                ("UL_MZ700", "") => TapeProfile.UltraMz700,
                _ => throw new ArgumentException($"Unsupported tape profile: {loaderType} {speed}".Trim())
            };
        }
    }
}

namespace QDTool
{
    internal sealed class MztReadResult
    {
        public List<TapeRecord> Records { get; } = new();
        public byte[] ContainerTrailingData { get; set; } = Array.Empty<byte>();
    }

    internal class MZTFileReader
    {
        public long currentPosition;
        public long totalLength;
        public long bytesRemaining;

        public TapeRecord ReadStandaloneMzf(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            TapeRecord record = ReadMzfRecord(reader);
            int trailingLength = checked((int)(stream.Length - stream.Position));
            if (trailingLength > 0)
            {
                MZQFileBody body = record.Body;
                body.TrailingData = ReadBytesExact(reader, trailingLength, "MZF trailing data");
                record.Body = body;
            }
            SetPosition(stream);
            return record;
        }

        public MztReadResult ReadMzt(string filePath)
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            var result = new MztReadResult();

            while (stream.Position < stream.Length)
            {
                long recordStart = stream.Position;
                if (!TryReadMzfRecord(reader, out TapeRecord? record))
                {
                    stream.Position = recordStart;
                    int trailingLength = checked((int)(stream.Length - stream.Position));
                    result.ContainerTrailingData = ReadBytesExact(reader, trailingLength, "MZT container trailing data");
                    break;
                }
                result.Records.Add(record!);
            }

            if (result.Records.Count == 0)
            {
                throw new InvalidDataException("The MZT does not contain a complete, structurally valid MZF record.");
            }
            SetPosition(stream);
            return result;
        }

        public bool TryReadMzfRecord(BinaryReader reader, out TapeRecord? record)
        {
            record = null;
            Stream stream = reader.BaseStream;
            long start = stream.Position;
            if (stream.Length - start < TapeRecord.HeaderLength)
            {
                return false;
            }

            byte[] rawHeader = reader.ReadBytes(TapeRecord.HeaderLength);
            ushort bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2));
            bool acceptableHeader = IsStructurallyAcceptableHeader(rawHeader);
            if (!acceptableHeader || stream.Length - stream.Position < bodyLength)
            {
                stream.Position = start;
                return false;
            }

            byte[] bodyBytes = reader.ReadBytes(bodyLength);
            record = CreateRecord(rawHeader, bodyBytes);
            return true;
        }

        private static bool IsStructurallyAcceptableHeader(ReadOnlySpan<byte> rawHeader)
        {
            // MZF has no magic signature. A short name normally ends with CR in
            // byte 17, while several real tools write NUL when all 16 filename
            // bytes are occupied. Requiring CR rejected otherwise valid records.
            // File type zero is not a defined MZF type and keeps zero-filled or
            // alignment tails from being mistaken for another record.
            return rawHeader[0] != 0 && (rawHeader[17] == 0x0D || rawHeader[17] == 0x00);
        }

        public TapeRecord ReadMzfRecord(BinaryReader reader)
        {
            long available = reader.BaseStream.Length - reader.BaseStream.Position;
            if (available < TapeRecord.HeaderLength)
            {
                throw new InvalidDataException(
                    $"Incomplete MZF header: expected {TapeRecord.HeaderLength} bytes, only {available} remain.");
            }

            byte[] rawHeader = ReadBytesExact(reader, TapeRecord.HeaderLength, "MZF header");
            ushort bodyLength = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2));
            byte[] body = ReadBytesExact(reader, bodyLength, "MZF file body");
            return CreateRecord(rawHeader, body);
        }

        // Compatibility entry points retained for QD/MZQ code and older tests.
        public (MZQFileHeader, MZQFileBody) ReadMzfFile(BinaryReader reader)
        {
            TapeRecord record = ReadMzfRecord(reader);
            return (record.Header, record.Body);
        }

        public List<(MZQFileHeader, MZQFileBody)> ReadMztFile(string filePath) =>
            ReadMzt(filePath).Records.Select(record => (record.Header, record.Body)).ToList();

        public void WriteMZFFileHeaderToFile(FileStream fileStream, MZQFileHeader mzfHeader) =>
            fileStream.Write(TapeRecord.FromLegacy(mzfHeader, default).GetSerializedHeader());

        public void WriteMZFFileBodyToFile(FileStream fileStream, MZQFileBody mzfBody) =>
            fileStream.Write(mzfBody.MzfBody, 0, mzfBody.DataSize);

        public void WriteMZFTrailingDataToFile(FileStream fileStream, MZQFileBody mzfBody)
        {
            if (mzfBody.TrailingData is { Length: > 0 })
            {
                fileStream.Write(mzfBody.TrailingData);
            }
        }

        private static TapeRecord CreateRecord(byte[] rawHeader, byte[] bodyBytes)
        {
            var header = new MZQFileHeader
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfHeaderSign = 0,
                DataSize = 0x0040,
                MzfFtype = rawHeader[0],
                MzfFname = rawHeader[1..17],
                MzfFnameEnd = rawHeader[17],
                Unused1 = ExpectedUnused.ToArray(),
                MzfSize = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(18, 2)),
                MzfStart = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(20, 2)),
                MzfExec = BinaryPrimitives.ReadUInt16LittleEndian(rawHeader.AsSpan(22, 2)),
                MzfHeaderDescription = rawHeader[24..128],
                Crc = ExpectedCrc.ToArray()
            };
            var body = new MZQFileBody
            {
                StartSign = ExpectedStartSign.ToArray(),
                MzfBodySign = 0x05,
                DataSize = header.MzfSize,
                MzfBody = bodyBytes,
                Crc = ExpectedCrc.ToArray(),
                TrailingData = Array.Empty<byte>()
            };
            return new TapeRecord(rawHeader, header, body);
        }

        private void SetPosition(Stream stream)
        {
            currentPosition = stream.Position;
            totalLength = stream.Length;
            bytesRemaining = totalLength - currentPosition;
        }
    }
}

namespace QDTool
{
    internal static class SidecarService
    {
        public static string GetSidecarPath(string mainPath)
        {
            string extension = Path.GetExtension(mainPath).Equals(".mzt", StringComparison.OrdinalIgnoreCase)
                ? ".mti"
                : ".mfi";
            return Path.ChangeExtension(mainPath, extension);
        }

        public static string? LoadForMzf(string mzfPath, TapeRecord record)
        {
            string path = GetSidecarPath(mzfPath);
            if (!File.Exists(path))
            {
                return null;
            }

            if (TryParseProfile(File.ReadAllLines(path), out TapeProfile profile))
            {
                record.Profile = profile;
                record.MetadataOrigin = MetadataOrigin.LoadedFromMfi;
            }

            return path;
        }

        public static string? LoadForMzt(string mztPath, IReadOnlyList<TapeRecord> records)
        {
            string path = GetSidecarPath(mztPath);
            if (!File.Exists(path))
            {
                return null;
            }

            Dictionary<int, List<string>> sections = ParseMtiSections(File.ReadAllLines(path));
            for (int index = 0; index < records.Count; index++)
            {
                if (sections.TryGetValue(index + 1, out List<string>? lines) &&
                    TryParseProfile(lines, out TapeProfile profile))
                {
                    records[index].Profile = profile;
                    records[index].MetadataOrigin = MetadataOrigin.LoadedFromMti;
                }
            }

            return path;
        }

        public static byte[] SerializeMfi(TapeRecord record) =>
            Encoding.ASCII.GetBytes(SerializeProfile(record.Profile));

        public static byte[] SerializeMti(IReadOnlyList<TapeRecord> records)
        {
            var text = new StringBuilder();
            for (int index = 0; index < records.Count; index++)
            {
                text.Append("RECORD=").Append(index + 1).Append('\n');
                text.Append(SerializeProfile(records[index].Profile));
                if (index + 1 < records.Count)
                {
                    text.Append('\n');
                }
            }
            return Encoding.ASCII.GetBytes(text.ToString());
        }

        internal static bool TryParseProfile(IEnumerable<string> sourceLines, out TapeProfile profile)
        {
            Dictionary<string, string> values = sourceLines
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#') && line.Contains('='))
                .Select(line => line.Split('=', 2))
                .GroupBy(parts => parts[0].Trim(), StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Last()[1].Trim(), StringComparer.OrdinalIgnoreCase);

            values.TryGetValue("TYPE", out string? type);
            values.TryGetValue("SPEED", out string? speed);
            profile = (type?.ToUpperInvariant(), speed?.ToUpperInvariant()) switch
            {
                ("NORMAL", "1:1") => TapeProfile.Normal1_1,
                ("NORMAL", "1:2") => TapeProfile.Normal1_2,
                ("NORMAL", "1:3") => TapeProfile.Normal1_3,
                ("NORMAL", "1:4") => TapeProfile.Normal1_4,
                ("MZ700", "1:1") => TapeProfile.Mz700_1_1,
                ("MZ700", "1:3") => TapeProfile.Mz700_1_3,
                ("IC", "1:1") => TapeProfile.Ic1_1,
                ("IC", "1:2") => TapeProfile.Ic1_2,
                ("IC", "1:3") => TapeProfile.Ic1_3,
                ("IC", "1:4") => TapeProfile.Ic1_4,
                ("TC", "1:1") => TapeProfile.Tc1_1,
                ("TC", "1:2") => TapeProfile.Tc1_2,
                ("TC", "1:3") => TapeProfile.Tc1_3,
                ("UL", null or "") => TapeProfile.Ultra,
                ("UL_MZ800", null or "") => TapeProfile.UltraMz800,
                ("UL_MZ700", null or "") => TapeProfile.UltraMz700,
                _ => (TapeProfile)(-1)
            };
            return Enum.IsDefined(profile);
        }

        private static Dictionary<int, List<string>> ParseMtiSections(IEnumerable<string> lines)
        {
            var result = new Dictionary<int, List<string>>();
            List<string>? current = null;
            foreach (string sourceLine in lines)
            {
                string line = sourceLine.Trim();
                if (line.StartsWith("RECORD=", StringComparison.OrdinalIgnoreCase))
                {
                    current = int.TryParse(line[7..].Trim(), out int number) && number > 0
                        ? result[number] = new List<string>()
                        : null;
                }
                else
                {
                    current?.Add(line);
                }
            }
            return result;
        }

        private static string SerializeProfile(TapeProfile profile) => profile switch
        {
            TapeProfile.Normal1_1 => "TYPE=NORMAL\nSPEED=1:1\n",
            TapeProfile.Normal1_2 => "TYPE=NORMAL\nSPEED=1:2\n",
            TapeProfile.Normal1_3 => "TYPE=NORMAL\nSPEED=1:3\n",
            TapeProfile.Normal1_4 => "TYPE=NORMAL\nSPEED=1:4\n",
            TapeProfile.Mz700_1_1 => "TYPE=MZ700\nSPEED=1:1\n",
            TapeProfile.Mz700_1_3 => "TYPE=MZ700\nSPEED=1:3\n",
            TapeProfile.Ic1_1 => "TYPE=IC\nSPEED=1:1\n",
            TapeProfile.Ic1_2 => "TYPE=IC\nSPEED=1:2\n",
            TapeProfile.Ic1_3 => "TYPE=IC\nSPEED=1:3\n",
            TapeProfile.Ic1_4 => "TYPE=IC\nSPEED=1:4\n",
            TapeProfile.Tc1_1 => "TYPE=TC\nSPEED=1:1\n",
            TapeProfile.Tc1_2 => "TYPE=TC\nSPEED=1:2\n",
            TapeProfile.Tc1_3 => "TYPE=TC\nSPEED=1:3\n",
            TapeProfile.Ultra => "TYPE=UL\n",
            TapeProfile.UltraMz800 => "TYPE=UL_MZ800\n",
            TapeProfile.UltraMz700 => "TYPE=UL_MZ700\n",
            _ => throw new ArgumentOutOfRangeException(nameof(profile))
        };
    }
}

namespace QDTool
{
    internal static class TapeDocumentWriter
    {
        public static void SaveMzf(
            string path,
            TapeRecord record,
            bool preserveTrailing,
            bool createSidecar = false)
        {
            string sidecarPath = SidecarService.GetSidecarPath(path);
            bool writeSidecar = createSidecar || File.Exists(sidecarPath);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = SerializeMzf(record, preserveTrailing)
            };
            if (writeSidecar)
            {
                files[sidecarPath] = SidecarService.SerializeMfi(record);
            }

            WriteAtomically(files);
            if (!preserveTrailing)
            {
                record.RemoveTrailingData();
            }
            ApplySavedMetadataState(new[] { record }, writeSidecar, MetadataOrigin.LoadedFromMfi);
        }

        public static void SaveMzt(
            string path,
            IReadOnlyList<TapeRecord> records,
            bool createSidecar = false)
        {
            string sidecarPath = SidecarService.GetSidecarPath(path);
            bool writeSidecar = createSidecar || File.Exists(sidecarPath);
            var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [path] = SerializeMzt(records)
            };
            if (writeSidecar)
            {
                files[sidecarPath] = SidecarService.SerializeMti(records);
            }

            WriteAtomically(files);
            foreach (TapeRecord record in records)
            {
                record.RemoveTrailingData();
            }
            ApplySavedMetadataState(records, writeSidecar, MetadataOrigin.LoadedFromMti);
        }

        public static string GenerateSidecar(
            string mainPath,
            TapeDocumentFormat format,
            IReadOnlyList<TapeRecord> records)
        {
            if (string.IsNullOrWhiteSpace(mainPath))
            {
                throw new ArgumentException("The current document has no file path.", nameof(mainPath));
            }

            byte[] content;
            MetadataOrigin persistedOrigin;
            if (format == TapeDocumentFormat.Mzf)
            {
                if (records.Count != 1)
                {
                    throw new InvalidOperationException("An MFI sidecar requires exactly one MZF record.");
                }
                content = SidecarService.SerializeMfi(records[0]);
                persistedOrigin = MetadataOrigin.LoadedFromMfi;
            }
            else if (format == TapeDocumentFormat.Mzt)
            {
                if (records.Count == 0)
                {
                    throw new InvalidOperationException("An MTI sidecar requires at least one MZT record.");
                }
                content = SidecarService.SerializeMti(records);
                persistedOrigin = MetadataOrigin.LoadedFromMti;
            }
            else
            {
                throw new InvalidOperationException("MFI/MTI can be generated only for an MZF or MZT document.");
            }

            string sidecarPath = SidecarService.GetSidecarPath(mainPath);
            WriteAtomically(new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
            {
                [sidecarPath] = content
            });
            ApplySavedMetadataState(records, sidecarWritten: true, persistedOrigin);
            return sidecarPath;
        }

        public static byte[] SerializeMzf(TapeRecord record, bool preserveTrailing)
        {
            using var stream = new MemoryStream();
            WriteRecord(stream, record);
            if (preserveTrailing && record.Body.TrailingData is { Length: > 0 } trailing)
            {
                stream.Write(trailing);
            }
            return stream.ToArray();
        }

        public static byte[] SerializeMzt(IReadOnlyList<TapeRecord> records)
        {
            using var stream = new MemoryStream();
            foreach (TapeRecord record in records)
            {
                WriteRecord(stream, record);
            }
            return stream.ToArray();
        }

        public static void WriteRecord(Stream stream, TapeRecord record)
        {
            byte[] header = record.GetSerializedHeader();
            stream.Write(header);
            MZQFileBody body = record.Body;
            if (body.MzfBody == null || body.MzfBody.Length != body.DataSize || body.DataSize != record.Header.MzfSize)
            {
                throw new InvalidDataException("The MZF header and body sizes are inconsistent.");
            }
            stream.Write(body.MzfBody, 0, body.DataSize);
        }

        private static void ApplySavedMetadataState(
            IEnumerable<TapeRecord> records,
            bool sidecarWritten,
            MetadataOrigin persistedOrigin)
        {
            foreach (TapeRecord record in records)
            {
                if (sidecarWritten)
                {
                    record.MetadataOrigin = persistedOrigin;
                }
                else
                {
                    record.ResetMetadataToImplicit();
                }
            }
        }

        private static void WriteAtomically(IReadOnlyDictionary<string, byte[]> files)
        {
            string transactionId = Guid.NewGuid().ToString("N");
            var temporary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var backups = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            var installed = new List<string>();

            try
            {
                foreach ((string target, byte[] content) in files)
                {
                    string fullTarget = Path.GetFullPath(target);
                    string? directory = Path.GetDirectoryName(fullTarget);
                    if (string.IsNullOrEmpty(directory))
                    {
                        throw new InvalidOperationException("The output path has no directory.");
                    }
                    Directory.CreateDirectory(directory);
                    string temp = Path.Combine(directory, $".{Path.GetFileName(fullTarget)}.{transactionId}.tmp");
                    File.WriteAllBytes(temp, content);
                    temporary[fullTarget] = temp;
                    backups[fullTarget] = File.Exists(fullTarget)
                        ? Path.Combine(directory, $".{Path.GetFileName(fullTarget)}.{transactionId}.bak")
                        : null;
                }

                foreach ((string target, string temp) in temporary)
                {
                    string? backup = backups[target];
                    if (backup != null)
                    {
                        File.Copy(target, backup, overwrite: true);
                        File.Move(temp, target, overwrite: true);
                    }
                    else
                    {
                        File.Move(temp, target);
                    }
                    installed.Add(target);
                }
            }
            catch
            {
                for (int index = installed.Count - 1; index >= 0; index--)
                {
                    string target = installed[index];
                    string? backup = backups[target];
                    if (backup != null && File.Exists(backup))
                    {
                        File.Move(backup, target, overwrite: true);
                    }
                    else if (backup == null && File.Exists(target))
                    {
                        File.Delete(target);
                    }
                }
                throw;
            }
            finally
            {
                foreach (string temp in temporary.Values)
                {
                    if (File.Exists(temp)) File.Delete(temp);
                }
                foreach (string? backup in backups.Values)
                {
                    if (backup != null && File.Exists(backup)) File.Delete(backup);
                }
            }
        }
    }
}
