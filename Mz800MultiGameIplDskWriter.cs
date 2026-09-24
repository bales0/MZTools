using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace QDTool
{
    internal sealed record MultiGameIplInput(
        TapeRecord Record,
        string DisplayName,
        MzfCompressionOptions AppliedCompression,
        int OriginalSize);

    internal sealed record PreparedMultiGameIplEntry(
        string DisplayName,
        ushort StartBlock,
        ushort Size,
        ushort Load,
        ushort Exec,
        int SectorCount,
        byte Flags,
        byte CompressionCode,
        string Compression,
        int OriginalSize,
        byte[] Payload);

    internal sealed record MultiGameIplBuildResult(
        byte[] Image,
        IReadOnlyList<PreparedMultiGameIplEntry> Entries,
        int MenuByteSize,
        int MenuSectorCount,
        int UsedSectorCount)
    {
        public int FreeSectorCount => Mz800DskImage.LogicalSectorCount - UsedSectorCount;
        public int UsedBytes => UsedSectorCount * Mz800DskImage.SectorSize;
        public int FreeBytes => FreeSectorCount * Mz800DskImage.SectorSize;
    }

    internal static class Mz800MultiGameIplDskWriter
    {
        public const int FormatVersion = 1;
        public const int EntrySize = 26;
        public const int MaxEntries = byte.MaxValue;
        public const int ItemsPerPage = 9;
        public const int MenuLoadAddress = 0x1200;
        public const int FinalStubAddress = 0x1000;

        private const int MultiGameMetadataOffset = 0x20;

        public static MultiGameIplBuildResult Build(IReadOnlyList<MultiGameIplInput> inputs)
        {
            ArgumentNullException.ThrowIfNull(inputs);
            if (inputs.Count == 0)
            {
                throw new InvalidOperationException("Cannot create multi-game IPL DSK: select at least one program.");
            }
            if (inputs.Count > MaxEntries)
            {
                throw new InvalidOperationException(
                    $"Cannot create multi-game IPL DSK: at most {MaxEntries} menu entries are supported.");
            }

            List<PreparedMultiGameIplEntry> entries = inputs
                .Select((input, index) => ValidateAndCreateEntry(input, index))
                .ToList();

            MenuProgram draftMenu = BuildMenuProgram(entries);
            if (draftMenu.Bytes.Length > ushort.MaxValue ||
                MenuLoadAddress + draftMenu.Bytes.Length > 0x10000)
            {
                throw new InvalidOperationException(
                    "Cannot create multi-game IPL DSK: the generated boot menu exceeds the 16-bit load area.");
            }

            int menuSectors = GetSectorCount(draftMenu.Bytes.Length);
            int nextBlock = checked(1 + menuSectors);
            for (int index = 0; index < entries.Count; index++)
            {
                PreparedMultiGameIplEntry entry = entries[index];
                if (nextBlock > ushort.MaxValue)
                {
                    throw new InvalidOperationException("Cannot create multi-game IPL DSK: block allocation overflowed.");
                }
                entries[index] = entry with { StartBlock = (ushort)nextBlock };
                nextBlock = checked(nextBlock + entry.SectorCount);
            }

            if (nextBlock > Mz800DskImage.LogicalSectorCount)
            {
                throw new InvalidOperationException(
                    $"Cannot create multi-game IPL DSK: {nextBlock} sectors are required, but the image contains {Mz800DskImage.LogicalSectorCount} sectors.");
            }

            MenuProgram menu = BuildMenuProgram(entries);
            if (menu.Bytes.Length != draftMenu.Bytes.Length)
            {
                throw new InvalidOperationException("Internal multi-game layout error: menu size changed during block allocation.");
            }

            DskImage image = Mz800DskImage.CreateModel("MZTools Multi");
            byte[] ipl = BuildIplBlock(
                menu.Bytes.Length,
                entries.Count,
                menu.TableOffset,
                menuSectors);
            Mz800DskImage.WriteLogicalBlock(image, 0, ipl);
            WriteBytes(image, 1, menu.Bytes);
            foreach (PreparedMultiGameIplEntry entry in entries)
            {
                WriteBytes(image, entry.StartBlock, entry.Payload);
            }

            return new MultiGameIplBuildResult(
                image.Serialize(),
                entries,
                menu.Bytes.Length,
                menuSectors,
                nextBlock);
        }

        public static void WriteFile(string path, IReadOnlyList<MultiGameIplInput> inputs) =>
            File.WriteAllBytes(path, Build(inputs).Image);

        public static string NormalizeMenuName(string? value)
        {
            string source = string.IsNullOrWhiteSpace(value) ? "PROGRAM" : value.Trim();
            var builder = new StringBuilder(16);
            foreach (char character in source)
            {
                if (builder.Length == 16)
                {
                    break;
                }
                builder.Append(character is >= ' ' and <= '~' ? character : ' ');
            }
            string result = builder.ToString().Trim();
            return result.Length == 0 ? "PROGRAM" : result;
        }

        private static PreparedMultiGameIplEntry ValidateAndCreateEntry(MultiGameIplInput input, int index)
        {
            ArgumentNullException.ThrowIfNull(input);
            ArgumentNullException.ThrowIfNull(input.Record);
            byte[]? body = input.Record.Body.MzfBody;
            string name = NormalizeMenuName(input.DisplayName);
            if (body == null || body.Length == 0)
            {
                throw EntryError(index, name, "the prepared program body is empty.");
            }
            if (body.Length != input.Record.Body.DataSize || body.Length != input.Record.Header.MzfSize)
            {
                throw EntryError(index, name, "the MZF header and body sizes are inconsistent.");
            }
            if (body.Length > Mz800IplDskWriter.MaxIplStagedSize)
            {
                throw EntryError(
                    index,
                    name,
                    $"the prepared payload is {body.Length} bytes, above the safe IPL staging limit {Mz800IplDskWriter.MaxIplStagedSize} bytes ($BCE9).");
            }
            if ((long)input.Record.Header.MzfStart + body.Length > 0x10000L)
            {
                throw EntryError(index, name, "LOAD and SIZE exceed the 16-bit MZ address space.");
            }

            byte compressionCode = input.AppliedCompression.Algorithm switch
            {
                MzfCompressionAlgorithm.None => 0,
                MzfCompressionAlgorithm.Zx0 => 1,
                MzfCompressionAlgorithm.Zx7 => 2,
                _ => throw EntryError(index, name, "compression must resolve to None, ZX0 or ZX7 before layout.")
            };
            byte flags = compressionCode == 0 ? (byte)0 : (byte)1;
            return new PreparedMultiGameIplEntry(
                name,
                StartBlock: 0,
                input.Record.Header.MzfSize,
                input.Record.Header.MzfStart,
                input.Record.Header.MzfExec,
                GetSectorCount(body.Length),
                flags,
                compressionCode,
                FormatCompression(input.AppliedCompression),
                input.OriginalSize,
                (byte[])body.Clone());
        }

        private static InvalidOperationException EntryError(int index, string name, string detail) =>
            new($"Entry {index + 1} ({name}): {detail}");

        private static int GetSectorCount(int byteCount) =>
            checked((byteCount + Mz800DskImage.SectorSize - 1) / Mz800DskImage.SectorSize);

        private static string FormatCompression(MzfCompressionOptions options)
        {
            if (options.Algorithm == MzfCompressionAlgorithm.None)
            {
                return "None";
            }
            string result = options.Algorithm.ToString().ToUpperInvariant();
            if (options.Zx0Quick)
            {
                result += " quick";
            }
            return result + (options.Direction == CompressionDirection.Backward ? " backward" : " forward");
        }

        private static byte[] BuildIplBlock(int menuSize, int entryCount, int tableOffset, int menuSectors)
        {
            var result = new byte[Mz800DskImage.SectorSize];
            result[0] = 0x03;
            "IPLPRO"u8.CopyTo(result.AsSpan(1, 6));
            result.AsSpan(0x07, 13).Fill(0x0D);
            SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("MZTOOLS MULTI").CopyTo(result, 0x07);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x14, 2), checked((ushort)menuSize));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x16, 2), MenuLoadAddress);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x18, 2), MenuLoadAddress);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x1E, 2), 1);

            "QDMG"u8.CopyTo(result.AsSpan(MultiGameMetadataOffset, 4));
            result[0x24] = FormatVersion;
            result[0x25] = EntrySize;
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x26, 2), checked((ushort)entryCount));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x28, 2), checked((ushort)tableOffset));
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(0x2A, 2), checked((ushort)menuSectors));
            return result;
        }

        private static void WriteBytes(DskImage image, int startBlock, ReadOnlySpan<byte> source)
        {
            int sectorCount = GetSectorCount(source.Length);
            var sector = new byte[Mz800DskImage.SectorSize];
            for (int index = 0; index < sectorCount; index++)
            {
                Array.Clear(sector);
                int sourceOffset = index * sector.Length;
                int length = Math.Min(sector.Length, source.Length - sourceOffset);
                source.Slice(sourceOffset, length).CopyTo(sector);
                Mz800DskImage.WriteLogicalBlock(image, startBlock + index, sector);
            }
        }

        private static MenuProgram BuildMenuProgram(IReadOnlyList<PreparedMultiGameIplEntry> entries)
        {
            StubProgram stub = BuildFinalStub();
            int pageCount = (entries.Count + ItemsPerPage - 1) / ItemsPerPage;
            var code = new Z80CodeBuilder(MenuLoadAddress);

            code.Label("start");
            code.Emit(0xAF);                                      // XOR A
            code.Emit(0x32); code.WordLabel("page");             // LD (page),A
            code.Label("render");
            code.Emit(0xCD); code.Word(0xEA59);                   // CALL ROM CLRS
            code.Emit(0x3A); code.WordLabel("page");             // LD A,(page)
            code.Emit(0x87, 0x5F, 0x16, 0x00);                   // *2, E=A, D=0
            code.Emit(0x21); code.WordLabel("pagePointers");
            code.Emit(0x19, 0x5E, 0x23, 0x56, 0xEB);             // HL=&ptr, DE=*ptr, EX DE,HL
            code.Label("printLoop");
            code.Emit(0x7E, 0x23, 0xB7);                         // LD A,(HL); INC HL; OR A
            code.Jr(0x28, "input");                              // JR Z,input
            code.Emit(0xFE, 0x0D);                               // CP CR
            code.Jr(0x28, "printNewline");
            code.Emit(0xCD); code.Word(0x0012);                   // CALL PRNTC
            code.Jr(0x18, "printLoop");
            code.Label("printNewline");
            code.Emit(0xCD); code.Word(0x0006);                   // CALL LETNL
            code.Jr(0x18, "printLoop");

            code.Label("input");
            code.Emit(0xCD); code.Word(0x001B);                   // CALL GETKY
            code.Emit(0xB7);                                     // OR A
            code.Jr(0x28, "input");
            code.Emit(0xFE, (byte)'1');
            code.Jr(0x38, "navigation");                         // JR C,navigation
            code.Emit(0xFE, (byte)('9' + 1));
            code.Jr(0x30, "navigation");                         // JR NC,navigation
            code.Emit(0xD6, (byte)'1', 0x4F);                    // SUB '1'; LD C,A
            code.Emit(0x3A); code.WordLabel("page");
            code.Emit(0x47, 0x87, 0x87, 0x87, 0x80, 0x81);       // A=page*9+C
            code.Jp(0xDA, "input");                              // JP C,input (index overflow)
            code.Emit(0xFE, checked((byte)entries.Count));
            code.Jp(0xD2, "input");                              // JP NC,input

            code.Emit(0x6F, 0x26, 0x00, 0x29, 0x54, 0x5D);       // HL=index*2; DE=index*2
            code.Emit(0x29, 0x29, 0xE5, 0x29, 0x19, 0xD1, 0x19); // HL=index*26
            code.Emit(0x11); code.WordLabel("entryTable");
            code.Emit(0x19);                                     // ADD HL,DE
            code.Emit(0x11); code.Word(16);
            code.Emit(0x19);                                     // skip name
            code.Emit(0x11); code.WordLabel("stubSelectedSource");
            code.Emit(0x01); code.Word(8);
            code.Emit(0xED, 0xB0);                               // patch selected metadata
            code.Emit(0x21); code.WordLabel("stubSource");
            code.Emit(0x11); code.Word(FinalStubAddress);
            code.Emit(0x01); code.Word(stub.Bytes.Length);
            code.Emit(0xED, 0xB0);
            code.Emit(0xC3); code.Word(FinalStubAddress);         // JP final stub

            code.Label("navigation");
            code.Emit(0xE6, 0xDF);                               // ASCII uppercase
            code.Emit(0xFE, (byte)'N');
            code.Jr(0x28, "nextPage");
            code.Emit(0xFE, (byte)'P');
            code.Jr(0x28, "previousPage");
            code.Jp(0xC3, "input");
            code.Label("nextPage");
            code.Emit(0x3A); code.WordLabel("page");
            code.Emit(0x3C, 0xFE, checked((byte)pageCount));
            code.Jp(0xD2, "input");
            code.Emit(0x32); code.WordLabel("page");
            code.Jp(0xC3, "render");
            code.Label("previousPage");
            code.Emit(0x3A); code.WordLabel("page");
            code.Emit(0xB7);
            code.Jp(0xCA, "input");
            code.Emit(0x3D, 0x32); code.WordLabel("page");
            code.Jp(0xC3, "render");

            code.Label("page");
            code.Emit(0x00);
            code.Label("pagePointers");
            for (int page = 0; page < pageCount; page++)
            {
                code.WordLabel($"page{page}");
            }
            for (int page = 0; page < pageCount; page++)
            {
                code.Label($"page{page}");
                code.Emit(BuildPageText(entries, page, pageCount));
            }

            code.Label("entryTable");
            foreach (PreparedMultiGameIplEntry entry in entries)
            {
                code.Emit(SerializeEntry(entry));
            }
            code.Label("stubSource");
            code.Emit(stub.Bytes.AsSpan(0, stub.SelectedDataOffset));
            code.Label("stubSelectedSource");
            code.Emit(stub.Bytes.AsSpan(stub.SelectedDataOffset));

            byte[] bytes = code.Build();
            return new MenuProgram(bytes, code.GetAddress("entryTable") - MenuLoadAddress);
        }

        private static byte[] BuildPageText(
            IReadOnlyList<PreparedMultiGameIplEntry> entries,
            int page,
            int pageCount)
        {
            var result = new List<byte>();
            AppendTextLine(result, "MZTools Multi Game");
            AppendTextLine(result, $"Page {page + 1}/{pageCount}");
            result.Add(0x0D);
            int first = page * ItemsPerPage;
            int count = Math.Min(ItemsPerPage, entries.Count - first);
            for (int index = 0; index < count; index++)
            {
                AppendTextLine(result, $"{index + 1}: {entries[first + index].DisplayName}");
            }
            result.Add(0x0D);
            if (pageCount > 1)
            {
                AppendTextLine(result, "N: next   P: previous");
            }
            AppendTextLine(result, $"Select 1-{count}");
            result.Add(0x00);
            return result.ToArray();
        }

        private static void AppendTextLine(List<byte> target, string text)
        {
            target.AddRange(SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(text));
            target.Add(0x0D);
        }

        private static byte[] SerializeEntry(PreparedMultiGameIplEntry entry)
        {
            var result = new byte[EntrySize];
            result.AsSpan(0, 16).Fill((byte)' ');
            byte[] name = SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes(entry.DisplayName);
            name.AsSpan(0, Math.Min(name.Length, 16)).CopyTo(result);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(16, 2), entry.StartBlock);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(18, 2), entry.Size);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(20, 2), entry.Load);
            BinaryPrimitives.WriteUInt16LittleEndian(result.AsSpan(22, 2), entry.Exec);
            result[24] = entry.Flags;
            result[25] = entry.CompressionCode;
            return result;
        }

        private static StubProgram BuildFinalStub()
        {
            // Verified against Sharp 9Z-504M ROM (SHA-256
            // 68A91B82517D5642E250CDDDB519DE780FB20BCDE39B2DE8BAEE387CA6BF446A):
            // BOOT=$E4D1, FDCC&=$E8D5, FDCB=$CEE9, FDARET=$CEFE,
            // FDREAD=$E5A7, FDDESL=$E530 and JGOPGM=$ECFC. JGOPGM relocates the staged
            // payload from $1200 with LDIR/LDDR and therefore supports LOAD
            // addresses on either side of $1200 without keeping this stub alive.
            var code = new Z80CodeBuilder(FinalStubAddress);
            code.Label("start");
            code.Emit(0xF3);                                     // DI
            code.Emit(0xCD); code.Word(0xE8D5);                  // CALL ROM FDCC&
            code.Jp(0xC2, "error");                             // JP NZ,error
            code.Emit(0x21); code.Word(0xE4D1);                  // LD HL,BOOT
            code.Emit(0x11); code.Word(0xCEE9);                  // LD DE,FDCB
            code.Emit(0x01); code.Word(11);
            code.Emit(0xED, 0xB0);                               // LDIR
            code.Emit(0x2A); code.WordLabel("selected");
            code.Emit(0x22); code.Word(0xCEEA);                  // FDCB start block
            code.Emit(0x2A); code.WordLabel("selectedSize");
            code.Emit(0x22); code.Word(0xCEEC);                  // FDCB byte size
            code.Emit(0x21); code.Word(MenuLoadAddress);
            code.Emit(0x22); code.Word(0xCEEE);                  // staging destination
            code.Emit(0x21); code.WordLabel("error");
            code.Emit(0x22); code.Word(0xCEFE);                  // ROM error return
            code.Emit(0xCD); code.Word(0xE530);                  // CALL FDDESL, as FDBOOT
            code.Emit(0xDD, 0x21); code.Word(0xCEE9);            // LD IX,FDCB
            code.Emit(0xCD); code.Word(0xE5A7);                  // CALL FDREAD
            code.Emit(0xCD); code.Word(0xE530);                  // CALL FDDESL
            code.Emit(0x01); code.Word(0x0200);                  // floppy launch marker
            code.Emit(0xD9);                                     // EXX -> BC'
            code.Emit(0x21); code.WordLabel("selectedSize");     // SIZE/LOAD/EXEC tuple
            code.Emit(0xC3); code.Word(0xECFC);                  // JP JGOPGM
            code.Label("error");
            code.Emit(0xCD); code.Word(0xEA59);                  // clear screen
            code.Emit(0x11); code.WordLabel("errorMessage");
            code.Emit(0xDF);                                     // RST 18H
            code.Label("errorWait");
            code.Jr(0x18, "errorWait");
            code.Label("selected");
            int selectedOffset = code.Position;
            code.Emit(0x00, 0x00);                               // start block
            code.Label("selectedSize");
            code.Emit(new byte[6]);                              // size,load,exec
            code.Label("errorMessage");
            code.Emit(SharpMzEncoding.ConvertASCIIStringToSHASCIIBytes("DISK READ ERROR"));
            code.Emit(0x0D);
            byte[] bytes = code.Build();
            if (bytes.Length >= 0xE0)
            {
                throw new InvalidOperationException("Internal multi-game loader error: final stub overlaps the ROM error stack.");
            }
            return new StubProgram(bytes, selectedOffset);
        }

        private sealed record MenuProgram(byte[] Bytes, int TableOffset);

        private sealed record StubProgram(byte[] Bytes, int SelectedDataOffset);

        private sealed class Z80CodeBuilder
        {
            private readonly int origin;
            private readonly List<byte> bytes = new();
            private readonly Dictionary<string, int> labels = new(StringComparer.Ordinal);
            private readonly List<(int Offset, string Label, bool Relative)> fixups = new();

            public Z80CodeBuilder(int origin) => this.origin = origin;

            public int Position => bytes.Count;

            public void Emit(params byte[] values) => bytes.AddRange(values);

            public void Emit(ReadOnlySpan<byte> values)
            {
                foreach (byte value in values)
                {
                    bytes.Add(value);
                }
            }

            public void Label(string name)
            {
                if (!labels.TryAdd(name, origin + bytes.Count))
                {
                    throw new InvalidOperationException($"Duplicate Z80 label: {name}.");
                }
            }

            public int GetAddress(string name) => labels.TryGetValue(name, out int address)
                ? address
                : throw new InvalidOperationException($"Unknown Z80 label: {name}.");

            public void Word(int value)
            {
                if ((uint)value > ushort.MaxValue)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }
                bytes.Add((byte)value);
                bytes.Add((byte)(value >> 8));
            }

            public void WordLabel(string label)
            {
                fixups.Add((bytes.Count, label, Relative: false));
                bytes.Add(0);
                bytes.Add(0);
            }

            public void Jr(byte opcode, string label)
            {
                bytes.Add(opcode);
                fixups.Add((bytes.Count, label, Relative: true));
                bytes.Add(0);
            }

            public void Jp(byte opcode, string label)
            {
                bytes.Add(opcode);
                WordLabel(label);
            }

            public byte[] Build()
            {
                byte[] result = bytes.ToArray();
                foreach ((int offset, string label, bool relative) in fixups)
                {
                    int address = GetAddress(label);
                    if (relative)
                    {
                        int displacement = address - (origin + offset + 1);
                        if (displacement < sbyte.MinValue || displacement > sbyte.MaxValue)
                        {
                            throw new InvalidOperationException($"Z80 relative branch to {label} is out of range.");
                        }
                        result[offset] = unchecked((byte)(sbyte)displacement);
                    }
                    else
                    {
                        BinaryPrimitives.WriteUInt16LittleEndian(
                            result.AsSpan(offset, 2),
                            checked((ushort)address));
                    }
                }
                return result;
            }
        }
    }
}
