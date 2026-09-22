using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace QDTool
{
    internal enum SharpPulseTimingSource
    {
        Fixed,
        Mz800Rom1Z013B
    }

    internal enum SharpConnectorLevel
    {
        Low,
        High
    }

    // Durations are in the logical Sharp/MZ 8255-side waveform domain.
    // The exporter applies the fixed READ connector inversion separately.
    internal readonly record struct SharpPulseProfile(
        double ShortHighMicroseconds,
        double ShortLowMicroseconds,
        double LongHighMicroseconds,
        double LongLowMicroseconds,
        SharpPulseTimingSource TimingSource = SharpPulseTimingSource.Fixed);

    internal readonly record struct SharpTapeStage(
        byte[] Data,
        SharpPulseProfile Pulses,
        int LeaderShortPulses,
        int MarkLongPulses,
        int MarkShortPulses,
        int FinalMarkLongPulses,
        int TrailingPulses,
        bool TrailingPulseIsLong,
        int DelayBeforeMilliseconds = 0,
        SharpConnectorLevel DelayPhysicalLevel = SharpConnectorLevel.Low);

    internal static class SharpTapeProfileEncoder
    {
        private const double Mz800RomClockHz = 3_546_875.0;
        private const double TurboCopyClockMhz = 1.10;

        // Native MZ-800 monitor 1Z-013B. LOW widths are selected by the
        // waveform writer from leader/mark/data context and the following bit.
        private static readonly SharpPulseProfile Mz800Normal =
            new(RomTicks(844), RomTicks(906), RomTicks(1664), RomTicks(1726), SharpPulseTimingSource.Mz800Rom1Z013B);

        // Intercopy V10.2 writer rows, derived from speed table $16F7 and
        // physical writer $1D8E. Historical labels 1:3 and 1:4 correspond to
        // the 2800 Bd (7:3) and 3200 Bd (8:3) rows.
        private static readonly SharpPulseProfile Intercopy1200 = new(234.573, 263.894, 469.145, 494.802);
        private static readonly SharpPulseProfile Intercopy2400 = new(113.621, 139.278, 234.573, 260.229);
        private static readonly SharpPulseProfile Intercopy2800 = new(87.965, 124.617, 175.930, 223.577);
        private static readonly SharpPulseProfile Intercopy3200 = new(76.969, 117.286, 157.604, 179.595);
        private static readonly SharpPulseProfile Mz700Normal = new(240, 264, 464, 494);
        private static readonly SharpPulseProfile Mz700Fast3 = new(80, 80, 160, 160);

        // TurboCopy V1.22 writer: 8253 counter 0, MODE 3, nominal CKMS 1.10 MHz.
        // Source formula: SHORT_COUNT=floor(480*numerator/denominator)+71,
        // LONG_COUNT=2*SHORT_COUNT. The values below model the raw MODE3 halves.
        // TC 1x: ratio 1/1 -> SHORT_COUNT=551 -> 276/275 ticks,
        //        LONG_COUNT=1102 -> 551/551 ticks.
        private static readonly SharpPulseProfile Tc1 =
            new(TcTicks(276), TcTicks(275), TcTicks(551), TcTicks(551));
        // TC 2x: ratio 1/2 -> SHORT_COUNT=311 -> 156/155 ticks,
        //        LONG_COUNT=622 -> 311/311 ticks.
        private static readonly SharpPulseProfile Tc2 =
            new(TcTicks(156), TcTicks(155), TcTicks(311), TcTicks(311));
        // TC 3x: ratio 1/3 -> SHORT_COUNT=231 -> 116/115 ticks,
        //        LONG_COUNT=462 -> 231/231 ticks.
        private static readonly SharpPulseProfile Tc3 =
            new(TcTicks(116), TcTicks(115), TcTicks(231), TcTicks(231));

        private static readonly byte[] IcLoader =
        [
            0x3E, 0x08, 0xD3, 0xCE, 0xCD, 0x3E, 0x07, 0x36,
            0x01, 0x97, 0x57, 0x5F, 0xCD, 0x08, 0x03, 0xCD,
            0xBE, 0x02, 0xD3, 0xE2, 0x1A, 0xD3, 0xE0, 0x12,
            0x13, 0xCB, 0x62, 0x28, 0xF5, 0x3E, 0xC3, 0x32,
            0x1F, 0x06, 0x21, 0x5C, 0x11, 0x22, 0x20, 0x06,
            0x2A, 0x08, 0x11, 0x7D, 0x32, 0x12, 0x05, 0x7C,
            0x32, 0x4B, 0x0A, 0x2A, 0x0A, 0x11, 0x22, 0x02,
            0x11, 0xCD, 0xF8, 0x04, 0x01, 0xCF, 0x06, 0xED,
            0x71, 0xD3, 0xE2, 0xDA, 0xAA, 0xE9, 0x21, 0x0A,
            0x11, 0xC3, 0x08, 0xED, 0xC5, 0x3A, 0x10, 0x11,
            0xEE, 0x0C, 0x32, 0x10, 0x11, 0x01, 0xCF, 0x06,
            0xED, 0x79, 0xC1, 0xC9, 0x31, 0x39, 0x38, 0x37
        ];

        private static readonly byte[] TcLoaderTemplate =
        [
            0x3E, 0x08, 0xD3, 0xCE, 0xE5, 0x21, 0x00, 0x00,
            0xD3, 0xE4, 0x7E, 0xD3, 0xE0, 0x77, 0x23, 0x7C,
            0xFE, 0x10, 0x20, 0xF4, 0x3A, 0x4B, 0xD4, 0x32,
            0x4B, 0x0A, 0x3A, 0x4C, 0xD4, 0x32, 0x12, 0x05,
            0x21, 0x4D, 0xD4, 0x11, 0x02, 0x11, 0x01, 0x0D,
            0x00, 0xED, 0xB0, 0xE1, 0x7C, 0xFE, 0xD4, 0x28,
            0x12, 0x2A, 0x04, 0x11, 0xD9, 0x21, 0x00, 0x12,
            0x22, 0x04, 0x11, 0xCD, 0x2A, 0x00, 0xD3, 0xE4,
            0xC3, 0x9A, 0xE9, 0xCD, 0x2A, 0x00, 0xD3, 0xE4,
            0xC3, 0x24, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00
        ];

        internal static bool IsTurboCopyLoader(ReadOnlySpan<byte> loader) =>
            loader.Length == TcLoaderTemplate.Length &&
            loader[..0x4B].SequenceEqual(TcLoaderTemplate.AsSpan(0, 0x4B));

        private static readonly byte[] TcTag = [0x5B, 0x96, 0xA5, 0x9D, 0x9A, 0xB7, 0x5D, 0x00];

        // Exact 1Z-009A QADCN table from MZ-SD2CMT2-Reborn.
        private static readonly byte[] Mz700Qadcn =
        [
            0xF0,0xF0,0xF0,0xF3,0xF0,0xF5,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,
            0xF0,0xC1,0xC2,0xC3,0xC4,0xC5,0xC6,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,0xF0,
            0x00,0x61,0x62,0x63,0x64,0x65,0x66,0x67,0x68,0x69,0x6B,0x6A,0x2F,0x2A,0x2E,0x2D,
            0x20,0x21,0x22,0x23,0x24,0x25,0x26,0x27,0x28,0x29,0x4F,0x2C,0x51,0x2B,0x57,0x49,
            0x55,0x01,0x02,0x03,0x04,0x05,0x06,0x07,0x08,0x09,0x0A,0x0B,0x0C,0x0D,0x0E,0x0F,
            0x10,0x11,0x12,0x13,0x14,0x15,0x16,0x17,0x18,0x19,0x1A,0x52,0x59,0x54,0x50,0x45,
            0xC7,0xC8,0xC9,0xCA,0xCB,0xCC,0xCD,0xCE,0xCF,0xDF,0xE7,0xE8,0xE9,0xEA,0xEC,0xED,
            0xD0,0xD1,0xD2,0xD3,0xD4,0xD5,0xD6,0xD7,0xD8,0xD9,0xDA,0xDB,0xDC,0xDD,0xDE,0xC0,
            0x80,0xBD,0x9D,0xB1,0xB5,0xB9,0xB4,0x9E,0xB2,0xB6,0xBA,0xBE,0x9F,0xB3,0xB7,0xBB,
            0xBF,0xA3,0x85,0xA4,0xA5,0xA6,0x94,0x87,0x88,0x9C,0x82,0x98,0x84,0x92,0x90,0x83,
            0x91,0x81,0x9A,0x97,0x93,0x95,0x89,0xA1,0xAF,0x8B,0x86,0x96,0xA2,0xAB,0xAA,0x8A,
            0x8E,0xB0,0xAD,0x8D,0xA7,0xA8,0xA9,0x8F,0x8C,0xAE,0xAC,0x9B,0xA0,0x99,0xBC,0xB8,
            0x40,0x3B,0x3A,0x70,0x3C,0x71,0x5A,0x3D,0x43,0x56,0x3F,0x1E,0x4A,0x1C,0x5D,0x3E,
            0x5C,0x1F,0x5F,0x5E,0x37,0x7B,0x7F,0x36,0x7A,0x7E,0x33,0x4B,0x4C,0x1D,0x6C,0x5B,
            0x78,0x41,0x35,0x34,0x74,0x30,0x38,0x75,0x39,0x4D,0x6F,0x6E,0x32,0x77,0x76,0x72,
            0x73,0x47,0x7C,0x53,0x31,0x4E,0x6D,0x48,0x46,0x7D,0x44,0x1B,0x58,0x79,0x42,0x60
        ];

        public static IReadOnlyList<SharpTapeStage> Build(TapeRecord record, SharpTapeMachine implicitMachine)
        {
            ArgumentNullException.ThrowIfNull(record);
            byte[] header = record.GetSerializedHeader();
            byte[] body = record.Body.MzfBody;
            TapeProfile profile = record.Profile;
            if (profile == TapeProfile.Normal1_1 &&
                record.MetadataOrigin == MetadataOrigin.Implicit &&
                implicitMachine == SharpTapeMachine.Mz700)
            {
                profile = TapeProfile.Mz700_1_1;
            }

            return profile switch
            {
                TapeProfile.Normal1_1 => BuildConventional(header, body, Mz800Normal),
                TapeProfile.Normal1_2 => BuildConventional(header, body, Intercopy2400),
                TapeProfile.Normal1_3 => BuildConventional(header, body, Intercopy2800),
                TapeProfile.Normal1_4 => BuildConventional(header, body, Intercopy3200),
                TapeProfile.Mz700_1_1 => BuildConventional(header, body, Mz700Normal),
                TapeProfile.Mz700_1_3 => BuildMz700Fast3(header, body),
                TapeProfile.Ic1_1 => BuildIc(header, body, Intercopy1200, 0x4D),
                TapeProfile.Ic1_2 => BuildIc(header, body, Intercopy2400, 0x20),
                TapeProfile.Ic1_3 => BuildIc(header, body, Intercopy2800, 0x16),
                TapeProfile.Ic1_4 => BuildIc(header, body, Intercopy3200, 0x11),
                TapeProfile.Tc1_1 => BuildTc(header, body, Tc1, 0x52),
                TapeProfile.Tc1_2 => BuildTc(header, body, Tc2, 0x29),
                TapeProfile.Tc1_3 => BuildTc(header, body, Tc3, 0x1B),
                TapeProfile.Ultra or TapeProfile.UltraMz800 or TapeProfile.UltraMz700 =>
                    throw new InvalidOperationException(
                        $"{TapeProfileNames.ToDisplayName(profile)} uses a live WRITE/SENSE handshake and cannot be exported as a static WAV/LEP/L16 waveform."),
                _ => throw new ArgumentOutOfRangeException(nameof(record.Profile))
            };
        }

        private static IReadOnlyList<SharpTapeStage> BuildConventional(
            byte[] header, byte[] body, SharpPulseProfile pulses) =>
        [
            HeaderStage(header, pulses),
            DataStage(body, pulses)
        ];

        private static IReadOnlyList<SharpTapeStage> BuildIc(
            byte[] originalHeader, byte[] body, SharpPulseProfile turboPulses, byte speedByte)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: true);
            if (RangesOverlap(0x1110, IcLoader.Length, ReadU16(originalHeader, 20), body.Length))
            {
                throw new InvalidOperationException("The IC loader at $1110 overlaps the payload destination range.");
            }

            byte[] header = (byte[])originalHeader.Clone();
            header[0] = 0xBB;
            WriteU16(header, 18, 0);
            WriteU16(header, 20, 0x1200);
            WriteU16(header, 22, 0x1110);
            header[24] = 0x01;
            header[25] = speedByte;
            Array.Copy(originalHeader, 18, header, 26, 6);
            Array.Copy(IcLoader, 0, header, 32, IcLoader.Length);
            return
            [
                HeaderStage(header, Mz800Normal),
                DataStage(
                    body,
                    turboPulses,
                    delayBeforeMilliseconds: 345,
                    delayPhysicalLevel: SharpConnectorLevel.Low)
            ];
        }

        private static IReadOnlyList<SharpTapeStage> BuildTc(
            byte[] originalHeader,
            byte[] body,
            SharpPulseProfile turboPulses,
            byte speedByte)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: false);
            if (RangesOverlap(0xD400, TcLoaderTemplate.Length, ReadU16(originalHeader, 20), body.Length))
            {
                throw new InvalidOperationException("The TC loader at $D400 overlaps the payload destination range.");
            }

            byte[] header = (byte[])originalHeader.Clone();
            WriteU16(header, 18, (ushort)TcLoaderTemplate.Length);
            WriteU16(header, 20, 0xD400);
            WriteU16(header, 22, 0xD400);
            Array.Copy(TcTag, 0, header, 24, TcTag.Length);

            byte[] loader = (byte[])TcLoaderTemplate.Clone();
            loader[0x4B] = speedByte;
            loader[0x4C] = originalHeader[0];
            Array.Copy(originalHeader, 18, loader, 0x4D, 13);
            return
            [
                HeaderStage(header, Mz800Normal),
                DataStage(loader, Mz800Normal, trailingPulses: 98, trailingLong: false),
                DataStage(
                    body,
                    turboPulses,
                    trailingPulses: 98,
                    trailingLong: false,
                    delayBeforeMilliseconds: 110,
                    delayPhysicalLevel: SharpConnectorLevel.Low)
            ];
        }

        private static IReadOnlyList<SharpTapeStage> BuildMz700Fast3(byte[] originalHeader, byte[] body)
        {
            ValidateLoaderInput(originalHeader, body, requireMachineCode: false);
            byte[] name = originalHeader[1..18];
            int runtimeSize = 73 + DisplayedNameLength(name);
            ushort loadAddress = ReadU16(originalHeader, 20);
            ushort runtimeAddress;
            if (!RangesOverlap(0x1108, runtimeSize, loadAddress, body.Length) && StageIsEncodable(0x1108, runtimeSize))
            {
                runtimeAddress = 0x1108;
            }
            else if (!RangesOverlap(0xC000, runtimeSize, loadAddress, body.Length) && StageIsEncodable(0xC000, runtimeSize))
            {
                runtimeAddress = 0xC000;
            }
            else
            {
                throw new InvalidOperationException("No safe RAM location is available for the MZ700 FAST3 loader.");
            }

            byte[] header = BuildMz700Fast3Header(originalHeader, runtimeAddress, runtimeSize);
            return
            [
                HeaderStage(header, Mz700Normal),
                DataStage(
                    body,
                    Mz700Fast3,
                    delayBeforeMilliseconds: 400,
                    delayPhysicalLevel: SharpConnectorLevel.Low)
            ];
        }

        private static byte[] BuildMz700Fast3Header(byte[] originalHeader, ushort runtimeAddress, int runtimeSize)
        {
            byte[] stage = BuildMz700Stage(runtimeAddress, runtimeSize);
            byte[] header = new byte[128];
            header[0] = 0x01;
            for (int index = 0; index < stage.Length; index++)
            {
                header[index + 1] = EncodeQadcn(stage[index]);
            }
            header[15] = 0x0D;
            WriteU16(header, 22, 0xD080);

            int offset = 24;
            header[offset++] = 0xAF;
            header[offset++] = 0x21; WriteU16(header, offset, 0xD000); offset += 2;
            header[offset++] = 0x77;
            header[offset++] = 0x11; WriteU16(header, offset, 0xD001); offset += 2;
            header[offset++] = 0x01; WriteU16(header, offset, 0x03E7); offset += 2;
            header[offset++] = 0xED; header[offset++] = 0xB0;

            int statusLength = DisplayedNameLength(originalHeader.AsSpan(1, 17));
            ushort statusAddress = checked((ushort)(runtimeAddress + 73));
            header[offset++] = 0x21; WriteU16(header, offset, statusAddress); offset += 2;
            header[offset++] = 0x11; WriteU16(header, offset, 0xD000); offset += 2;
            header[offset++] = 0x01; WriteU16(header, offset, (ushort)statusLength); offset += 2;
            header[offset++] = 0xED; header[offset++] = 0xB0;

            header[offset++] = 0x16; header[offset++] = 0xE4;
            header[offset++] = 0x1E; header[offset++] = 0xE0;
            header[offset++] = 0x21; WriteU16(header, offset, 0); offset += 2;
            header[offset++] = 0x4A; header[offset++] = 0xED; header[offset++] = 0x79;
            header[offset++] = 0x7E; header[offset++] = 0x4B; header[offset++] = 0xED; header[offset++] = 0x79;
            header[offset++] = 0x77; header[offset++] = 0x23; header[offset++] = 0xCB; header[offset++] = 0x64;
            header[offset++] = 0x28; header[offset++] = 0xF3;

            header[offset++] = 0x3E; header[offset++] = 0x15;
            header[offset++] = 0x32; WriteU16(header, offset, 0x0A4B); offset += 2;
            header[offset++] = 0x21; WriteU16(header, offset, ReadU16(originalHeader, 18)); offset += 2;
            header[offset++] = 0x22; WriteU16(header, offset, 0x1102); offset += 2;
            header[offset++] = 0x21; WriteU16(header, offset, ReadU16(originalHeader, 20)); offset += 2;
            header[offset++] = 0x22; WriteU16(header, offset, 0x1104); offset += 2;
            header[offset++] = 0xCD; WriteU16(header, offset, 0x002A); offset += 2;
            header[offset++] = 0xDA; WriteU16(header, offset, 0x00FE); offset += 2;
            header[offset++] = 0xAF; header[offset++] = 0xD3; header[offset++] = 0xE4;
            header[offset++] = 0xC3; WriteU16(header, offset, ReadU16(originalHeader, 22)); offset += 2;

            ReadOnlySpan<byte> name = originalHeader.AsSpan(1, 17);
            int trimmedLength = TrimmedNameLength(name);
            if (trimmedLength == 0)
            {
                header[offset++] = Mz700Qadcn[0x20];
            }
            else
            {
                for (int index = 0; index < trimmedLength; index++)
                {
                    header[offset++] = Mz700Qadcn[name[index]];
                }
            }

            if (offset - 24 != runtimeSize)
            {
                throw new InvalidOperationException("Internal MZ700 FAST3 runtime size mismatch.");
            }
            return header;
        }

        private static byte[] BuildMz700Stage(ushort runtimeAddress, int runtimeSize)
        {
            byte[] stage = new byte[14];
            if (runtimeAddress == 0x1108)
            {
                stage[0] = 0xC3;
                WriteU16(stage, 1, 0x1108);
                return stage;
            }
            stage[0] = 0x21; WriteU16(stage, 1, 0x1108);
            stage[3] = 0x11; WriteU16(stage, 4, runtimeAddress);
            stage[6] = 0x01; WriteU16(stage, 7, (ushort)runtimeSize);
            stage[9] = 0xED; stage[10] = 0xB0;
            stage[11] = 0xC3; WriteU16(stage, 12, runtimeAddress);
            return stage;
        }

        private static bool StageIsEncodable(ushort runtimeAddress, int runtimeSize)
        {
            foreach (byte value in BuildMz700Stage(runtimeAddress, runtimeSize))
            {
                try { _ = EncodeQadcn(value); }
                catch (InvalidOperationException) { return false; }
            }
            return true;
        }

        private static byte EncodeQadcn(byte displayByte)
        {
            for (int source = 0; source < Mz700Qadcn.Length; source++)
            {
                if (source != 0x0D && Mz700Qadcn[source] == displayByte)
                {
                    return (byte)source;
                }
            }
            throw new InvalidOperationException($"MZ700 FAST3 stage byte ${displayByte:X2} is not QADCN-encodable.");
        }

        internal static bool TryDecodeQadcn(byte encodedByte, out byte displayByte)
        {
            for (int source = 0; source < Mz700Qadcn.Length; source++)
            {
                if (source != 0x0D && Mz700Qadcn[source] == encodedByte)
                {
                    displayByte = (byte)source;
                    return true;
                }
            }
            displayByte = 0;
            return false;
        }

        private static int TrimmedNameLength(ReadOnlySpan<byte> name)
        {
            int length = 0;
            while (length < name.Length && name[length] is not 0x00 and not 0x0D) length++;
            while (length > 0 && name[length - 1] == 0x20) length--;
            return length;
        }

        private static int DisplayedNameLength(ReadOnlySpan<byte> name) => Math.Max(1, TrimmedNameLength(name));

        private static void ValidateLoaderInput(byte[] header, byte[] body, bool requireMachineCode)
        {
            if (header[0] is not 0x01 and not 0x76)
            {
                throw new InvalidOperationException($"Loader profile does not support MZF file type ${header[0]:X2}.");
            }
            if (requireMachineCode && header[0] != 0x01)
            {
                throw new InvalidOperationException("The IC loader can be generated only for an OBJ (machine-code) record.");
            }
            if (body.Length == 0 || ReadU16(header, 18) != body.Length)
            {
                throw new InvalidOperationException("The loader requires a non-empty body matching the MZF header size.");
            }
            if ((uint)ReadU16(header, 20) + body.Length > 0x10000)
            {
                throw new InvalidOperationException("The payload destination range exceeds the 16-bit address space.");
            }
        }

        private static bool RangesOverlap(int leftStart, int leftLength, int rightStart, int rightLength) =>
            leftStart < rightStart + rightLength && rightStart < leftStart + leftLength;

        private static SharpTapeStage HeaderStage(byte[] data, SharpPulseProfile pulses) =>
            new(data, pulses, 11000, 40, 40, 2, 2, true);

        private static SharpTapeStage DataStage(
            byte[] data,
            SharpPulseProfile pulses,
            int trailingPulses = 2,
            bool trailingLong = true,
            int delayBeforeMilliseconds = 0,
            SharpConnectorLevel delayPhysicalLevel = SharpConnectorLevel.Low) =>
            new(
                data,
                pulses,
                5500,
                20,
                20,
                2,
                trailingPulses,
                trailingLong,
                delayBeforeMilliseconds,
                delayPhysicalLevel);

        private static ushort ReadU16(byte[] data, int offset) =>
            BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));

        private static void WriteU16(byte[] data, int offset, ushort value) =>
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);

        internal static double RomTicks(int ticks) => ticks * 1_000_000.0 / Mz800RomClockHz;

        internal static double TcTicks(int ticks) => ticks / TurboCopyClockMhz;
    }
}

namespace QDTool
{
    internal enum SharpTapeOutputFormat
    {
        Lep,
        L16,
        Wav
    }

    internal enum SharpTapeMachine
    {
        Mz800,
        Mz700
    }

    internal readonly record struct SharpConnectorPulse(
        SharpConnectorLevel FirstLevel,
        double FirstDurationMicroseconds,
        SharpConnectorLevel SecondLevel,
        double SecondDurationMicroseconds);

    internal static class SharpTapeExporter
    {
        // Required output rate. Fractional edge durations are preserved over
        // time by the WavSink quantization-error accumulator.
        internal const int WavSampleRate = 44100;

        private enum PulseRegion
        {
            Leader,
            TapeMark,
            Data
        }

        public static void Export(
            string filePath,
            IReadOnlyList<(MZQFileHeader Header, MZQFileBody Body)> blocks,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(blocks);
            List<TapeRecord> records = blocks.Select(block =>
            {
                TapeRecord record = TapeRecord.FromLegacy(block.Header, block.Body);
                record.Profile = machine == SharpTapeMachine.Mz700
                    ? TapeProfile.Mz700_1_1
                    : TapeProfile.Normal1_1;
                record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                return record;
            }).ToList();
            Export(filePath, records, format, machine);
        }

        public static IReadOnlyList<string> GetSeparateOutputPaths(
            string selectedPath,
            IReadOnlyList<TapeRecord> records)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(selectedPath);
            ArgumentNullException.ThrowIfNull(records);
            if (records.Count == 0)
            {
                throw new InvalidOperationException("There are no MZF files to export.");
            }

            string fullPath = Path.GetFullPath(selectedPath);
            string directory = Path.GetDirectoryName(fullPath)
                ?? throw new InvalidOperationException("The output path has no directory.");
            string extension = Path.GetExtension(fullPath);
            string baseName = Path.GetFileNameWithoutExtension(fullPath);
            var result = new List<string>(records.Count);
            for (int index = 0; index < records.Count; index++)
            {
                string recordName = SharpMzEncoding.ConvertMzfNameToASCIIString(
                    records[index].Header.MzfFname);
                string safeRecordName = SanitizeFilePart(recordName);
                string fileName = $"{baseName}_{index + 1:D2}_{safeRecordName}{extension}";
                result.Add(Path.Combine(directory, fileName));
            }
            return result;
        }

        public static IReadOnlyList<string> ExportSeparate(
            string selectedPath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine,
            bool overwrite)
        {
            IReadOnlyList<string> paths = GetSeparateOutputPaths(selectedPath, records);
            if (!overwrite)
            {
                string? existingPath = paths.FirstOrDefault(File.Exists);
                if (existingPath != null)
                {
                    throw new IOException($"The separate output file already exists: {existingPath}");
                }
            }

            for (int index = 0; index < records.Count; index++)
            {
                if (overwrite && File.Exists(paths[index]))
                {
                    File.Delete(paths[index]);
                }
                Export(paths[index], new[] { records[index] }, format, machine);
            }
            return paths;
        }

        public static void Export(
            string filePath,
            IReadOnlyList<TapeRecord> records,
            SharpTapeOutputFormat format,
            SharpTapeMachine machine = SharpTapeMachine.Mz800)
        {
            ArgumentNullException.ThrowIfNull(records);
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

            if (records.Count == 0)
            {
                throw new InvalidOperationException("There are no MZF files to export.");
            }

            // Build every plan before creating the destination so an invalid
            // loader cannot leave a truncated output file behind.
            List<IReadOnlyList<SharpTapeStage>> plans = records
                .Select(record => SharpTapeProfileEncoder.Build(record, machine))
                .ToList();

            using FileStream fileStream = new FileStream(filePath, FileMode.Create, FileAccess.ReadWrite);
            using TapeSink sink = format switch
            {
                SharpTapeOutputFormat.Lep => new EdgeDurationSink(fileStream, 50),
                SharpTapeOutputFormat.L16 => new EdgeDurationSink(fileStream, 16),
                SharpTapeOutputFormat.Wav => new WavSink(fileStream, WavSampleRate),
                _ => throw new ArgumentOutOfRangeException(nameof(format))
            };

            foreach (IReadOnlyList<SharpTapeStage> plan in plans)
            {
                foreach (SharpTapeStage stage in plan)
                {
                    WriteStage(sink, stage);
                }
            }

            sink.Complete();
        }

        public static SharpTapeOutputFormat GetFormat(string extension)
        {
            return extension.ToLowerInvariant() switch
            {
                ".lep" => SharpTapeOutputFormat.Lep,
                ".l16" => SharpTapeOutputFormat.L16,
                ".wav" => SharpTapeOutputFormat.Wav,
                _ => throw new ArgumentException($"Unsupported tape output extension: {extension}", nameof(extension))
            };
        }

        private static string SanitizeFilePart(string value)
        {
            value = value.Trim().TrimEnd('\0', '\r', '\n', '.');
            char[] invalid = Path.GetInvalidFileNameChars();
            string result = new string(value
                .Select(character => invalid.Contains(character) ? '_' : character)
                .ToArray())
                .Trim()
                .TrimEnd('.');
            if (string.IsNullOrWhiteSpace(result))
            {
                return "unnamed";
            }
            return result.Length <= 64 ? result : result[..64];
        }

        private static void WriteStage(TapeSink sink, SharpTapeStage stage)
        {
            WriteDelay(sink, stage.DelayPhysicalLevel, stage.DelayBeforeMilliseconds);
            WriteBlock(
                sink,
                stage.Pulses,
                stage.Data,
                stage.LeaderShortPulses,
                stage.MarkLongPulses,
                stage.MarkShortPulses,
                stage.FinalMarkLongPulses,
                stage.TrailingPulses,
                stage.TrailingPulseIsLong);
        }

        private static void WriteBlock(
            TapeSink sink,
            SharpPulseProfile profile,
            byte[] data,
            int leaderPulses,
            int markLongPulses,
            int markShortPulses,
            int finalMarkLongPulses,
            int trailingPulses,
            bool trailingPulseIsLong)
        {
            WritePulses(
                sink, profile, isLong: false, leaderPulses, PulseRegion.Leader);
            WritePulses(
                sink, profile, isLong: true, markLongPulses, PulseRegion.TapeMark);
            WritePulses(
                sink, profile, isLong: false, markShortPulses, PulseRegion.TapeMark);
            WritePulses(
                sink, profile, isLong: true, finalMarkLongPulses, PulseRegion.TapeMark);
            WriteFramedData(
                sink, profile, data, ComputeChecksum(data), trailingPulses, trailingPulseIsLong);
        }

        private static void WriteFramedData(
            TapeSink sink,
            SharpPulseProfile profile,
            byte[] data,
            ushort checksum,
            int trailingPulses,
            bool trailingPulseIsLong)
        {
            int framedPulseCount = checked((data.Length + 2) * 9);
            int totalPulseCount = checked(framedPulseCount + trailingPulses);
            for (int pulseIndex = 0; pulseIndex < totalPulseCount; pulseIndex++)
            {
                bool isLong = GetFramedPulse(data, checksum, framedPulseCount, pulseIndex, trailingPulseIsLong);
                bool nextIsLong = pulseIndex + 1 < totalPulseCount &&
                    GetFramedPulse(data, checksum, framedPulseCount, pulseIndex + 1, trailingPulseIsLong);
                WritePulse(sink, profile, isLong, PulseRegion.Data, nextIsLong);
            }
        }

        private static bool GetFramedPulse(
            byte[] data,
            ushort checksum,
            int framedPulseCount,
            int pulseIndex,
            bool trailingPulseIsLong)
        {
            if (pulseIndex >= framedPulseCount)
            {
                return trailingPulseIsLong;
            }

            int byteIndex = pulseIndex / 9;
            int pulseInByte = pulseIndex % 9;
            if (pulseInByte == 8)
            {
                return true;
            }

            byte value = byteIndex < data.Length
                ? data[byteIndex]
                : byteIndex == data.Length ? (byte)(checksum >> 8) : (byte)checksum;
            return (value & (1 << (7 - pulseInByte))) != 0;
        }

        private static void WritePulses(
            TapeSink sink,
            SharpPulseProfile profile,
            bool isLong,
            int count,
            PulseRegion region)
        {
            for (int i = 0; i < count; i++)
            {
                WritePulse(sink, profile, isLong, region, nextIsLong: isLong);
            }
        }

        private static void WritePulse(
            TapeSink sink,
            SharpPulseProfile profile,
            bool isLong,
            PulseRegion region,
            bool nextIsLong)
        {
            double logicalHighDuration =
                isLong ? profile.LongHighMicroseconds : profile.ShortHighMicroseconds;
            double logicalLowDuration = GetLowDuration(profile, isLong, region, nextIsLong);
            SharpConnectorPulse connectorPulse = MapLogicalPulseToConnector(
                logicalHighDuration,
                logicalLowDuration);
            sink.WriteInterval(
                physicalHigh: connectorPulse.FirstLevel == SharpConnectorLevel.High,
                connectorPulse.FirstDurationMicroseconds);
            sink.WriteInterval(
                physicalHigh: connectorPulse.SecondLevel == SharpConnectorLevel.High,
                connectorPulse.SecondDurationMicroseconds);
        }

        internal static SharpConnectorPulse MapLogicalPulseToConnector(
            double logicalHighDurationMicroseconds,
            double logicalLowDurationMicroseconds) =>
            new(
                SharpConnectorLevel.Low,
                logicalHighDurationMicroseconds,
                SharpConnectorLevel.High,
                logicalLowDurationMicroseconds);

        private static double GetLowDuration(
            SharpPulseProfile profile,
            bool isLong,
            PulseRegion region,
            bool nextIsLong)
        {
            if (profile.TimingSource != SharpPulseTimingSource.Mz800Rom1Z013B)
            {
                return isLong ? profile.LongLowMicroseconds : profile.ShortLowMicroseconds;
            }

            return region switch
            {
                PulseRegion.Leader when !isLong => SharpTapeProfileEncoder.RomTicks(918),
                PulseRegion.TapeMark when isLong => SharpTapeProfileEncoder.RomTicks(1728),
                PulseRegion.TapeMark => SharpTapeProfileEncoder.RomTicks(908),
                PulseRegion.Data when isLong && nextIsLong => SharpTapeProfileEncoder.RomTicks(1716),
                PulseRegion.Data when isLong => SharpTapeProfileEncoder.RomTicks(1726),
                PulseRegion.Data when nextIsLong => SharpTapeProfileEncoder.RomTicks(896),
                PulseRegion.Data => SharpTapeProfileEncoder.RomTicks(906),
                _ => throw new InvalidOperationException("Invalid native MZ-800 pulse context.")
            };
        }

        private static void WriteDelay(
            TapeSink sink,
            SharpConnectorLevel physicalLevel,
            int milliseconds)
        {
            for (int index = 0; index < milliseconds; index++)
            {
                sink.WriteInterval(
                    physicalHigh: physicalLevel == SharpConnectorLevel.High,
                    durationMicroseconds: 1000);
            }
        }

        private static ushort ComputeChecksum(byte[] data)
        {
            uint checksum = 0;
            foreach (byte value in data)
            {
                byte remaining = value;
                while (remaining != 0)
                {
                    checksum += (uint)(remaining & 1);
                    remaining >>= 1;
                }
            }

            return unchecked((ushort)checksum);
        }

        private abstract class TapeSink : IDisposable
        {
            public abstract void WriteInterval(
                bool physicalHigh,
                double durationMicroseconds);

            public abstract void Complete();

            public abstract void Dispose();
        }

        private sealed class EdgeDurationSink : TapeSink
        {
            private readonly BinaryWriter writer;
            private readonly int unitMicroseconds;

            public EdgeDurationSink(Stream stream, int unitMicroseconds)
            {
                writer = new BinaryWriter(new BufferedStream(stream, 65536), System.Text.Encoding.UTF8, leaveOpen: true);
                this.unitMicroseconds = unitMicroseconds;
            }

            public override void WriteInterval(
                bool physicalHigh,
                double durationMicroseconds)
            {
                // LEP/L16 stores edge durations, not sampled waveform positions.
                // Quantize every physical run independently so its stored width
                // cannot depend on the rounding error of preceding runs.
                // WAV keeps its separate sample-phase error accumulator below.
                double exactUnits = (double)durationMicroseconds / unitMicroseconds;
                int units = Math.Max(1, (int)Math.Round(exactUnits, MidpointRounding.AwayFromZero));

                if (units > 127)
                {
                    throw new InvalidOperationException("A LEP/L16 interval exceeded the supported 127-unit duration.");
                }

                // Positive means physical connector HIGH; negative means LOW.
                sbyte signedUnits = checked((sbyte)(physicalHigh ? units : -units));
                writer.Write(unchecked((byte)signedUnits));
            }

            public override void Complete()
            {
                writer.Flush();
            }

            public override void Dispose()
            {
                writer.Dispose();
            }
        }

        private sealed class WavSink : TapeSink
        {
            private const byte HighSample = 235;
            private const byte LowSample = 20;

            private readonly FileStream stream;
            private readonly BinaryWriter writer;
            private readonly byte[] sampleBuffer = new byte[65536];
            private readonly int sampleRate;
            private int bufferedSamples;
            private long totalSamples;
            private double quantizationError;

            public WavSink(FileStream stream, int sampleRate)
            {
                this.stream = stream;
                this.sampleRate = sampleRate;
                writer = new BinaryWriter(stream, System.Text.Encoding.ASCII, leaveOpen: true);
                WriteHeader(dataSize: 0);
            }

            public override void WriteInterval(
                bool physicalHigh,
                double durationMicroseconds)
            {
                double exactSamples = ((double)durationMicroseconds * sampleRate / 1_000_000) + quantizationError;
                int samples = Math.Max(1, (int)Math.Round(exactSamples, MidpointRounding.AwayFromZero));
                quantizationError = exactSamples - samples;
                byte value = physicalHigh ? HighSample : LowSample;

                for (int i = 0; i < samples; i++)
                {
                    sampleBuffer[bufferedSamples++] = value;
                    if (bufferedSamples == sampleBuffer.Length)
                    {
                        FlushSamples();
                    }
                }

                totalSamples += samples;
            }

            public override void Complete()
            {
                FlushSamples();
                if (totalSamples > uint.MaxValue - 36)
                {
                    throw new InvalidOperationException("The generated WAV exceeds the 4 GiB RIFF size limit.");
                }

                stream.Position = 0;
                WriteHeader(checked((uint)totalSamples));
                writer.Flush();
            }

            public override void Dispose()
            {
                writer.Dispose();
            }

            private void FlushSamples()
            {
                if (bufferedSamples == 0)
                {
                    return;
                }

                writer.Write(sampleBuffer, 0, bufferedSamples);
                bufferedSamples = 0;
            }

            private void WriteHeader(uint dataSize)
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36u + dataSize);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
                writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
                writer.Write(16u);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write((uint)sampleRate);
                writer.Write((uint)sampleRate);
                writer.Write((ushort)1);
                writer.Write((ushort)8);
                writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
                writer.Write(dataSize);
            }
        }
    }

    internal static class SharpTapeImporter
    {
        private enum TapeSignalFormat { Lep, L16, Wav }
        private readonly record struct SignalRun(bool PhysicalHigh, long DurationUnits);
        private readonly record struct TapeSignalSource(
            IReadOnlyList<SignalRun> Runs,
            TapeSignalFormat Format,
            uint SampleRate = 0);
        private enum PendingStage { Body, TurboCopyLoader }
        private static ReadOnlySpan<byte> TurboCopyTag => [0x5B, 0x96, 0xA5, 0x9D, 0x9A, 0xB7, 0x5D, 0x00];

        public static IReadOnlyList<TapeRecord> ReadFile(string filePath)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
            string extension = Path.GetExtension(filePath).ToLowerInvariant();
            TapeSignalSource source = extension switch
            {
                ".lep" => ReadEdgeRuns(File.ReadAllBytes(filePath), TapeSignalFormat.Lep),
                ".l16" => ReadEdgeRuns(File.ReadAllBytes(filePath), TapeSignalFormat.L16),
                ".wav" => ReadWavRuns(File.ReadAllBytes(filePath)),
                ".flac" => ReadFlacRuns(filePath),
                _ => throw new ArgumentException($"Unsupported tape input extension: {extension}", nameof(filePath))
            };
            return DecodeRecords(source);
        }

        private static IReadOnlyList<TapeRecord> DecodeRecords(TapeSignalSource source)
        {
            var records = new List<TapeRecord>();
            var decoder = new SharpMzPulseDecoder();
            var blockData = new List<byte>();
            byte[]? header = null;
            TapeProfile profile = TapeProfile.Normal1_1;
            PendingStage pendingStage = PendingStage.Body;
            SharpMzDecoderEvent? failedDataCopy = null;
            decoder.BeginHeader();

            foreach (SignalRun run in source.Runs)
            {
                decoder.FeedInterval(run.DurationUnits, run.PhysicalHigh);
                while (decoder.TryTakeEvent(out SharpMzDecoderEvent decoderEvent))
                {
                    if (decoderEvent.Type == SharpMzDecoderEventType.HeaderValid)
                    {
                        byte[] encodedHeader = decoder.ValidatedHeader
                            ?? throw new InvalidDataException("The tape decoder reported a header without its data.");
                        if (encodedHeader[0] == 0 ||
                            (encodedHeader[17] != 0x0D && encodedHeader[17] != 0x00))
                        {
                            decoder.BeginHeader();
                            header = null;
                            pendingStage = PendingStage.Body;
                            blockData.Clear();
                            failedDataCopy = null;
                            continue;
                        }

                        header = encodedHeader;
                        profile = ProfileFromTone(
                            decoder.HeaderShortPhysicalLowX8,
                            decoder.HeaderShortPhysicalHighX8,
                            decoder.HeaderLongPhysicalLowX8,
                            decoderEvent.LeaderPulses,
                            source);
                        pendingStage = PendingStage.Body;
                        if (TryRecoverIntercopyHeader(
                            encodedHeader,
                            out byte[]? recoveredIc,
                            out TapeProfile icProfile))
                        {
                            header = recoveredIc;
                            profile = icProfile;
                        }
                        else if (IsTurboCopyHeader(encodedHeader))
                        {
                            pendingStage = PendingStage.TurboCopyLoader;
                        }
                        else if (TryRecoverMz700FastHeader(encodedHeader, out byte[]? recoveredMz700))
                        {
                            header = recoveredMz700;
                            profile = TapeProfile.Mz700_1_3;
                        }

                        blockData.Clear();
                        failedDataCopy = null;
                        int dataLength = pendingStage == PendingStage.TurboCopyLoader
                            ? 90
                            : BinaryPrimitives.ReadUInt16LittleEndian(header!.AsSpan(18, 2));
                        decoder.StartData(dataLength);
                        continue;
                    }

                    if (decoderEvent.Type == SharpMzDecoderEventType.DataByte)
                    {
                        if (decoderEvent.ByteIndex == 0 && blockData.Count != 0)
                        {
                            // A bad byte-sync resets the state machine. If a
                            // later leader yields a fresh block, discard bytes
                            // emitted before the lost alignment.
                            blockData.Clear();
                        }
                        if (decoderEvent.ByteIndex != blockData.Count)
                        {
                            throw new InvalidDataException("The tape decoder produced non-contiguous data bytes.");
                        }
                        blockData.Add(decoderEvent.Value);
                        continue;
                    }

                    if (decoderEvent.Type == SharpMzDecoderEventType.BlockInvalid)
                    {
                        if (pendingStage == PendingStage.TurboCopyLoader ||
                            profile is TapeProfile.Ic1_1 or TapeProfile.Ic1_2 or TapeProfile.Ic1_3 or TapeProfile.Ic1_4 or
                                TapeProfile.Tc1_1 or TapeProfile.Tc1_2 or TapeProfile.Tc1_3 ||
                            decoderEvent.CopyIndex != 0)
                        {
                            throw ChecksumException(decoderEvent);
                        }

                        failedDataCopy = decoderEvent;
                        blockData.Clear();
                        int dataLength = header is null
                            ? 0
                            : BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
                        decoder.StartRecoveryData(dataLength);
                        continue;
                    }

                    if (decoderEvent.Type != SharpMzDecoderEventType.BlockValid)
                    {
                        continue;
                    }

                    failedDataCopy = null;
                    if (pendingStage == PendingStage.TurboCopyLoader)
                    {
                        header = RecoverTurboCopyHeader(header!, blockData.ToArray(), out profile);
                        pendingStage = PendingStage.Body;
                        blockData.Clear();
                        decoder.StartData(BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)));
                        continue;
                    }

                    byte[] data = blockData.ToArray();
                    byte[] mzf = new byte[TapeRecord.HeaderLength + data.Length];
                    header!.CopyTo(mzf, 0);
                    data.CopyTo(mzf, TapeRecord.HeaderLength);
                    using var stream = new MemoryStream(mzf, writable: false);
                    using var reader = new BinaryReader(stream);
                    TapeRecord record = new MZTFileReader().ReadMzfRecord(reader);
                    record.Profile = profile;
                    record.MetadataOrigin = MetadataOrigin.CreatedOrModifiedInAdvanced;
                    records.Add(record);
                    header = null;
                    pendingStage = PendingStage.Body;
                    blockData.Clear();
                    decoder.BeginHeader();
                }
            }

            if (failedDataCopy is SharpMzDecoderEvent checksumFailure)
            {
                throw ChecksumException(checksumFailure);
            }
            if (header != null)
            {
                ushort expectedBody = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2));
                throw new InvalidDataException(
                    $"The tape waveform contains a header without its {expectedBody}-byte data block.");
            }
            if (records.Count == 0)
            {
                throw new InvalidDataException("The tape waveform does not contain a complete Sharp MZ file.");
            }
            return records;
        }

        private static InvalidDataException ChecksumException(SharpMzDecoderEvent decoderEvent) =>
            new($"Tape checksum mismatch: expected {decoderEvent.CalculatedChecksum:X4}, " +
                $"found {decoderEvent.RecordedChecksum:X4}.");

        internal static bool TryRecoverIntercopyHeader(
            byte[] encoded,
            out byte[]? recovered,
            out TapeProfile profile)
        {
            recovered = null;
            profile = TapeProfile.Normal1_1;
            if (encoded[0] != 0xBB || encoded[24] != 0x01)
            {
                return false;
            }
            profile = encoded[25] switch
            {
                0x4D => TapeProfile.Ic1_1,
                0x20 => TapeProfile.Ic1_2,
                0x16 => TapeProfile.Ic1_3,
                0x11 => TapeProfile.Ic1_4,
                _ => throw new InvalidDataException($"Unsupported Intercopy speed marker ${encoded[25]:X2}.")
            };
            recovered = (byte[])encoded.Clone();
            recovered[0] = 0x01;
            encoded.AsSpan(26, 6).CopyTo(recovered.AsSpan(18, 6));
            recovered.AsSpan(24).Clear();
            return true;
        }

        internal static bool IsTurboCopyHeader(byte[] header) =>
            header.Length >= TapeRecord.HeaderLength &&
            header[0] is 0x01 or 0x76 &&
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(18, 2)) == 90 &&
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(20, 2)) == 0xD400 &&
            BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(22, 2)) == 0xD400 &&
            // The first seven bytes are the stable TurboCopy signature. Older
            // TC/Intercopy headers may keep a workspace value in the eighth byte.
            header.AsSpan(24, TurboCopyTag.Length - 1)
                .SequenceEqual(TurboCopyTag[..^1]);

        internal static byte[] RecoverTurboCopyHeader(
            byte[] encodedHeader,
            byte[] loader,
            out TapeProfile profile)
        {
            // The checksummed loader block reaches this method only after the
            // decoder reports BlockValid. Its static template is the final proof
            // that the seven-byte header signature identifies TurboCopy.
            if (!SharpTapeProfileEncoder.IsTurboCopyLoader(loader))
            {
                throw new InvalidDataException("Invalid TurboCopy loader template.");
            }
            profile = loader[0x4B] switch
            {
                0x52 => TapeProfile.Tc1_1,
                0x29 => TapeProfile.Tc1_2,
                0x1B => TapeProfile.Tc1_3,
                _ => throw new InvalidDataException($"Unsupported TurboCopy speed marker ${loader[0x4B]:X2}.")
            };
            byte[] recovered = (byte[])encodedHeader.Clone();
            recovered[0] = loader[0x4C];
            loader.AsSpan(0x4D, 13).CopyTo(recovered.AsSpan(18, 13));
            recovered[31] = 0;
            return recovered;
        }

        internal static bool TryRecoverMz700FastHeader(byte[] encoded, out byte[]? recovered)
        {
            recovered = null;
            if (encoded[0] != 0x01 ||
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(18, 2)) != 0 ||
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(22, 2)) != 0xD080)
            {
                return false;
            }

            int sizeInstruction = FindInstruction(encoded, 24, 0x21, 0x22, 0x02, 0x11);
            int loadInstruction = FindInstruction(encoded, 24, 0x21, 0x22, 0x04, 0x11);
            int jump = -1;
            for (int index = 24; index <= encoded.Length - 7; index++)
            {
                if (encoded[index] == 0xAF && encoded[index + 1] == 0xD3 &&
                    encoded[index + 2] == 0xE4 && encoded[index + 3] == 0xC3)
                {
                    jump = index + 3;
                    break;
                }
            }
            if (sizeInstruction < 0 || loadInstruction < 0 || jump < 0 || jump + 3 >= encoded.Length)
            {
                return false;
            }

            recovered = new byte[TapeRecord.HeaderLength];
            recovered[0] = 0x01;
            recovered[17] = 0x0D;
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(18, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(sizeInstruction + 1, 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(20, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(loadInstruction + 1, 2)));
            BinaryPrimitives.WriteUInt16LittleEndian(
                recovered.AsSpan(22, 2),
                BinaryPrimitives.ReadUInt16LittleEndian(encoded.AsSpan(jump + 1, 2)));

            int nameLength = Math.Min(16, encoded.Length - (jump + 3));
            for (int index = 0; index < nameLength; index++)
            {
                if (!SharpTapeProfileEncoder.TryDecodeQadcn(encoded[jump + 3 + index], out byte value))
                {
                    break;
                }
                recovered[1 + index] = value;
            }
            return true;
        }

        private static int FindInstruction(
            byte[] data,
            int start,
            byte opcode,
            byte followingOpcode,
            byte addressLow,
            byte addressHigh)
        {
            for (int index = start; index <= data.Length - 6; index++)
            {
                if (data[index] == opcode && data[index + 3] == followingOpcode &&
                    data[index + 4] == addressLow && data[index + 5] == addressHigh)
                {
                    return index;
                }
            }
            return -1;
        }

        private static TapeProfile ProfileFromTone(
            long shortPhysicalLowX8,
            long shortPhysicalHighX8,
            long longPhysicalLowX8,
            int leaderPulses,
            TapeSignalSource source)
        {
            long normalized = NormalizeShortX8(shortPhysicalLowX8, source);
            long d1 = Math.Abs(normalized - 120);
            long d2 = Math.Abs(normalized - 57);
            long d3 = Math.Abs(normalized - 44);
            long d4 = Math.Abs(normalized - 39);
            long normalizedHigh = NormalizeShortX8(shortPhysicalHighX8, source);
            long normalizedLongLow = NormalizeShortX8(longPhysicalLowX8, source);

            // LEP's 50 us units quantize the physical-LOW short half-wave of
            // NORMAL 1:2, 1:3 and 1:4 to the same two-unit value. Use the
            // independently measured HIGH short half-wave to identify 1:2,
            // then the LOW long half-wave to distinguish 1:3 from 1:4.
            if (source.Format == TapeSignalFormat.Lep && normalizedLongLow > 0 &&
                d3 <= d2 && d3 < d1)
            {
                if (normalizedHigh >= 63)
                {
                    return TapeProfile.Normal1_2;
                }
                return normalizedLongLow >= 88
                    ? TapeProfile.Normal1_3
                    : TapeProfile.Normal1_4;
            }

            if (leaderPulses is >= 8000 and <= 13000 &&
                d4 < d3 && d4 < d2 && d4 < d1)
            {
                // Native-unit quantization can bias the integer LOW IIR toward
                // the 1:4 reference. The independently averaged HIGH half-wave
                // keeps QDTool's 1:3 metadata distinct without entering Sharp
                // pulse classification.
                if (normalizedHigh >= 61)
                {
                    return TapeProfile.Normal1_3;
                }
                return TapeProfile.Normal1_4;
            }
            if (d2 < d1 && d2 <= d3)
            {
                return TapeProfile.Normal1_2;
            }
            if (d3 < d1 && d3 < d2)
            {
                return TapeProfile.Normal1_3;
            }

            // MZ-700 and MZ-800 Normal 1:1 have the same decisive physical-LOW
            // half-wave. Keep classification LOW-only and use the independently
            // observed physical-HIGH half-wave solely as a metadata tie-breaker.
            return normalizedHigh >= 130
                ? TapeProfile.Mz700_1_1
                : TapeProfile.Normal1_1;
        }

        internal static TapeProfile ProfileFromWavTone(
            long shortPhysicalLowX8,
            long shortPhysicalHighX8,
            int leaderPulses,
            uint sampleRate) =>
            ProfileFromTone(
                shortPhysicalLowX8,
                shortPhysicalHighX8,
                0,
                leaderPulses,
                new TapeSignalSource(Array.Empty<SignalRun>(), TapeSignalFormat.Wav, sampleRate));

        private static long NormalizeShortX8(long shortX8, TapeSignalSource source) =>
            source.Format switch
            {
                TapeSignalFormat.L16 => shortX8,
                TapeSignalFormat.Lep => ((shortX8 * 50) + 8) / 16,
                TapeSignalFormat.Wav when source.SampleRate != 0 =>
                    ((shortX8 * 62500) + (source.SampleRate / 2)) / source.SampleRate,
                _ => shortX8
            };

        private static TapeSignalSource ReadEdgeRuns(byte[] bytes, TapeSignalFormat format)
        {
            var runs = new List<SignalRun>(bytes.Length);
            foreach (byte raw in bytes)
            {
                int signed = unchecked((sbyte)raw);
                if (signed == 0)
                {
                    // Original MZ-SD2CMT LEP/L16 convention: 0x00 is not an
                    // edge. It extends the preceding physical level by the
                    // maximum positive interval (127 format units). Multiple
                    // zero bytes therefore continue the same level.
                    if (runs.Count == 0)
                    {
                        throw new InvalidDataException(
                            "A LEP/L16 continuation byte cannot appear before the first interval.");
                    }

                    SignalRun previous = runs[^1];
                    runs[^1] = previous with
                    {
                        DurationUnits = checked(previous.DurationUnits + 127)
                    };
                    continue;
                }

                // Positive means physical connector HIGH; negative means LOW.
                // Equal neighbouring levels have no physical edge between them,
                // so they form one edge-to-edge interval.
                AddRun(runs, signed > 0, Math.Abs(signed));
            }
            return new TapeSignalSource(runs, format);
        }

        private static TapeSignalSource ReadWavRuns(byte[] wav)
        {
            if (wav.Length < 12 ||
                !wav.AsSpan(0, 4).SequenceEqual("RIFF"u8) ||
                !wav.AsSpan(8, 4).SequenceEqual("WAVE"u8))
            {
                throw new InvalidDataException("The file is not a RIFF/WAVE stream.");
            }

            ushort format = 0, channels = 0, bits = 0, blockAlign = 0;
            uint sampleRate = 0;
            ReadOnlySpan<byte> data = default;
            int position = 12;
            while (position <= wav.Length - 8)
            {
                ReadOnlySpan<byte> id = wav.AsSpan(position, 4);
                int length = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(position + 4, 4)));
                position += 8;
                if (length < 0 || position > wav.Length - length)
                {
                    throw new InvalidDataException("The WAV chunk extends beyond the end of the file.");
                }
                if (id.SequenceEqual("fmt "u8) && length >= 16)
                {
                    ReadOnlySpan<byte> chunk = wav.AsSpan(position, length);
                    format = BinaryPrimitives.ReadUInt16LittleEndian(chunk);
                    channels = BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]);
                    sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]);
                    blockAlign = BinaryPrimitives.ReadUInt16LittleEndian(chunk[12..]);
                    bits = BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]);
                }
                else if (id.SequenceEqual("data"u8))
                {
                    data = wav.AsSpan(position, length);
                }
                position += length + (length & 1);
            }

            if (format != 1 || channels == 0 || sampleRate == 0 || blockAlign == 0 ||
                bits is not 8 and not 16 || data.IsEmpty)
            {
                throw new NotSupportedException("Only non-empty 8-bit or 16-bit PCM WAV input is supported.");
            }

            var runs = new List<SignalRun>();
            bool? level = null;
            long samples = 0;
            bool digital8BitLevel = false;
            for (int offset = 0; offset <= data.Length - blockAlign; offset += blockAlign)
            {
                bool current;
                if (bits == 8)
                {
                    byte sample = data[offset];
                    if (!digital8BitLevel && sample >= 155)
                    {
                        digital8BitLevel = true;
                    }
                    else if (digital8BitLevel && sample <= 100)
                    {
                        digital8BitLevel = false;
                    }
                    current = digital8BitLevel;
                }
                else
                {
                    current = BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2)) >= 0;
                }
                if (level == current)
                {
                    samples++;
                    continue;
                }
                if (level.HasValue)
                {
                    AddRun(runs, level.Value, samples);
                }
                level = current;
                samples = 1;
            }
            if (level.HasValue)
            {
                AddRun(runs, level.Value, samples);
            }
            return new TapeSignalSource(runs, TapeSignalFormat.Wav, sampleRate);
        }

        private static TapeSignalSource ReadFlacRuns(string filePath)
        {
            using var reader = new FlacPcmStreamReader(filePath);
            var runs = new List<SignalRun>();
            bool? level = null;
            long samples = 0;
            bool digital8BitLevel = false;
            reader.ReadFrames((_, left, _) =>
            {
                bool current;
                if (reader.Format.BitsPerSample == 8)
                {
                    if (!digital8BitLevel && left >= 1769472)
                    {
                        digital8BitLevel = true;
                    }
                    else if (digital8BitLevel && left <= -1835008)
                    {
                        digital8BitLevel = false;
                    }
                    current = digital8BitLevel;
                }
                else
                {
                    current = left >= 0;
                }

                if (level == current)
                {
                    samples++;
                    return;
                }
                if (level.HasValue)
                {
                    AddRun(runs, level.Value, samples);
                }
                level = current;
                samples = 1;
            });
            if (level.HasValue)
            {
                AddRun(runs, level.Value, samples);
            }
            return new TapeSignalSource(runs, TapeSignalFormat.Wav, reader.Format.SampleRate);
        }

        private static void AddRun(List<SignalRun> runs, bool physicalHigh, long durationUnits)
        {
            if (runs.Count > 0 && runs[^1].PhysicalHigh == physicalHigh)
            {
                SignalRun previous = runs[^1];
                runs[^1] = previous with
                {
                    DurationUnits = checked(previous.DurationUnits + durationUnits)
                };
            }
            else
            {
                runs.Add(new SignalRun(physicalHigh, durationUnits));
            }
        }
    }
}
