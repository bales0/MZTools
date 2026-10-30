using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MZTools;

internal sealed record MzNativeComResult(byte[] Bytes, NativeLoaderPlan Plan, string Report);

internal static class MzNativeComBuilder
{
    internal static MzNativeComResult Build(NativeProgramImage image, CpmTargetProfile profile, NativeMachineMode mode,
        bool independent, bool knownMultipart = false)
    {
        var plan = NativeLoaderPlacementAnalyzer.Analyze(image, profile, mode, independent, knownMultipart);
        if (!plan.Supported) throw new InvalidDataException(plan.Report);
        var loader = plan.Loader!; var segment = image.Segments.Single();
        var stage = new List<byte>();
        void Emit(params byte[] bytes) => stage.AddRange(bytes);
        void Word(byte opcode, int value) { Emit(opcode, (byte)value, (byte)(value >> 8)); }
        void Out(byte port, byte value) => Emit(0x3E, value, 0xD3, port);
        void Copy(int source, int target, int length, bool backward = false)
        {
            Word(0x21, backward ? source + length - 1 : source);
            Word(0x11, backward ? target + length - 1 : target);
            Word(0x01, length); Emit(0xED, backward ? (byte)0xB8 : (byte)0xB0);
        }
        // No CALL/PUSH/BDOS, or dependency on the former CP/M stack, after takeover.
        Emit(0xF3, 0xED, 0x56, 0xAF, 0xD3, 0xE6, 0xD3, 0xE0, 0xD3, 0xE1, 0xDB, 0xE1);
        Out(0xCE, 0); // MZ-800 mode while initializing 8255 through IORQ D0..D3
        Out(0xD3, 0x8A); // mode 0 also clears keyboard column and port C outputs
        Out(0xD3, 0x07); Out(0xD3, 0x05); // native monitor PPI state; enable timer IRQ gate
        // Restore the monitor's cascaded 8253 timer setup instead of inheriting CP/M's.
        Out(0xD7, 0x74); Out(0xD7, 0xB0);
        Out(0xD6, 0xC0); Out(0xD6, 0xA8);
        Out(0xD5, 0xA0); Out(0xD5, 0x00);
        Out(0xD7, 0x80); Out(0xD5, 0xFB); Out(0xD5, 0x3C);
        Emit(0xDB, 0xD8); // acknowledge the last floppy status/INTRQ before the game enables IRQs
        Out(0xFC, 7); Out(0xFD, 7); // disable external PIO interrupts
        foreach (byte value in new byte[] { 0x9F, 0xBF, 0xDF, 0xFF }) Out(0xF2, value);
        Out(0xCD, 1); Out(0xCC, 1); // GDG read/write formats, as documented by MZX
        Copy(NativeLoaderPlacementAnalyzer.HeaderSource, NativeHeaderExecution.HeaderAddress, 128);
        Copy(plan.SourceStart, segment.Address, segment.Data.Length, plan.Backward);
        if (mode == NativeMachineMode.Mz700Monitor)
        {
            Out(0xCE, 8);
            Emit(0xDB, 0xE0);
            Copy(0x1000, 0xC000, 0x1000); // font ROM -> CG-RAM; HIGH remains visible
            Emit(0xDB, 0xE1);
        }
        if (mode != NativeMachineMode.Mz800AllRam) Emit(0xD3, 0xE2, 0xD3, 0xE3);
        Word(0x31, loader.End); Word(0xC3, image.EntryPoint);
        if (stage.Count > NativeLoaderPlacementAnalyzer.Stage1Capacity) throw new InvalidDataException("Stage 1 exceeds its reserved code range.");

        int meta = NativeLoaderPlacementAnalyzer.MetadataSource;
        var stage0 = new List<byte>();
        void S(params byte[] b) => stage0.AddRange(b);
        void LoadHl(int address) => S(0x2A, (byte)address, (byte)(address >> 8));
        void LoadDe(int address) => S(0xED, 0x5B, (byte)address, (byte)(address >> 8));
        S(0x3A, 0x05, 0x00, 0xFE, 0xC3, 0xC0); // CP/M BDOS vector must be JP
        LoadHl(6); S(0x7C, 0xB7, 0xC8); // invalid low/zero BDOS pointer
        S(0x11, 6, 0, 0xB7, 0xED, 0x52); // BDOS entry - 6 = conservative boundary
        LoadDe(meta + 10); S(0xB7, 0xED, 0x52, 0xD8); // RET C, original CP/M stack
        S(0xF3); LoadHl(meta + 6); LoadDe(meta + 4);
        S(0xED, 0x4B, (byte)(meta + 8), (byte)((meta + 8) >> 8), 0xED, 0xB0);
        LoadHl(meta + 4); S(0xE9);
        if (0x100 + stage0.Count > NativeLoaderPlacementAnalyzer.Stage0End) throw new InvalidDataException("Stage 0 exceeds reserved code range.");

        var output = new byte[plan.SourceEnd - 0x100];
        stage0.CopyTo(output, 0);
        stage.CopyTo(output, NativeLoaderPlacementAnalyzer.Stage1Source - 0x100);
        image.Header.CopyTo(output, NativeLoaderPlacementAnalyzer.HeaderSource - 0x100);
        "NZC1"u8.CopyTo(output.AsSpan(meta - 0x100));
        void Meta(int offset, int value) => BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(meta - 0x100 + offset, 2), checked((ushort)value));
        Meta(4, loader.Start); Meta(6, NativeLoaderPlacementAnalyzer.Stage1Source); Meta(8, stage.Count);
        Meta(10, Math.Max(plan.SourceEnd, loader.End)); Meta(12, plan.SourceEnd);
        Meta(14, image.EntryPoint); Meta(16, plan.SourceStart); Meta(18, segment.Data.Length); Meta(20, segment.Address);
        Meta(22, (int)mode); Meta(24, plan.Backward ? 1 : 0);
        segment.Data.CopyTo(output, plan.SourceStart - 0x100);
        string hash = ImageVerificationService.Hash(output);
        return new(output, plan, plan.Report + $"\nOutput SHA-256: {hash}\nCOM length: {output.Length} B\nStage 0: {stage0.Count} B; Stage 1: {stage.Count} B, relocated to {loader.Start:X4}\nHeader copied to 10F0 before payload; segments then replace any overlapping header bytes.\nStage 0 returns without takeover if the live BDOS vector leaves insufficient space. No BDOS call after takeover.\nThis is an original uncompressed one-way bootstrap, not a native CP/M application.");
    }
}
