using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;

namespace MZTools;

// Only a bounded UTF-8 stream crosses the OLE boundary, never live CLR objects.
internal sealed record DragTapeRecord(byte[] Header, byte[] Payload, byte[] Trailing,
    TapeProfile Profile, MetadataOrigin Origin);
internal sealed record WorkspaceDragPacket(int Version, byte[]? Disk, CpmDpb? Layout,
    string[] Keys, DragTapeRecord[] Records);

internal static class WorkspaceDragTransfer
{
    internal const string Format = "MZTools.WorkspaceCopy.v1";
    internal const int MaximumBytes = 64 * 1024 * 1024;
    private static string LocalFormat(Guid owner) => "MZTools.LocalDrag." + owner.ToString("N");
    internal static bool IsLocal(IDataObject data, Guid owner) => data.GetDataPresent(LocalFormat(owner), false);
    internal static bool IsPresent(IDataObject data) => data.GetDataPresent(Format, false);

    internal static DataObject Create(Guid owner, WorkspaceDragPacket packet)
    {
        var data = new DataObject();
        data.SetData(Format, new MemoryStream(Encode(packet), writable: false), false);
        data.SetData(LocalFormat(owner), new MemoryStream(new byte[] { 1 }), false);
        return data;
    }

    internal static WorkspaceDragPacket Capture(DskDocument disk, IEnumerable<DskFileEntry> files)
    {
        if (!CrossDiskTransferService.Supports(disk)) throw new InvalidDataException("Copy requires an FSMZ, CP/M or MRS filesystem.");
        return new(1, disk.Serialize(), disk.AttachedDpb, files.Select(f => f.Key).ToArray(), []);
    }

    internal static WorkspaceDragPacket Capture(IEnumerable<TapeRecord> records) => new(1, null, null, [],
        records.Select(r => new DragTapeRecord(r.GetSerializedHeader(), (byte[])r.Body.MzfBody.Clone(),
            (byte[])(r.Body.TrailingData ?? []).Clone(), r.Profile, r.MetadataOrigin)).ToArray());

    internal static byte[] Encode(WorkspaceDragPacket packet)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(packet);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Selection exceeds the 64 MiB drag transfer limit.");
        return bytes;
    }

    internal static WorkspaceDragPacket Read(IDataObject data)
    {
        if (data.GetData(Format, false) is not MemoryStream stream || stream.Length > MaximumBytes)
            throw new InvalidDataException("Invalid or oversized workspace copy data.");
        return Decode(stream.ToArray());
    }

    internal static WorkspaceDragPacket Decode(byte[] bytes)
    {
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversized workspace copy data.");
        var packet = JsonSerializer.Deserialize<WorkspaceDragPacket>(bytes)
            ?? throw new InvalidDataException("Empty workspace copy data.");
        if (packet.Version != 1 || packet.Keys == null || packet.Records == null ||
            (packet.Disk == null ? packet.Records.Length == 0 || packet.Keys.Length != 0 || packet.Layout != null :
                packet.Keys.Length == 0 || packet.Records.Length != 0 || packet.Keys.Distinct().Count() != packet.Keys.Length))
            throw new InvalidDataException("Unsupported workspace copy data.");
        return packet;
    }

    internal static DskDocument OpenDisk(WorkspaceDragPacket packet)
    {
        if (packet.Disk == null) throw new InvalidDataException("Drop disk files onto a disk workspace; drop tape/QuickDisk records onto a tape/QuickDisk workspace.");
        var disk = DskDocument.Open(packet.Disk);
        if (packet.Layout != null) disk.AttachCpmLayout(packet.Layout);
        if (!CrossDiskTransferService.Supports(disk)) throw new InvalidDataException("Unsupported source filesystem.");
        return disk;
    }

    internal static TapeRecord[] OpenRecords(WorkspaceDragPacket packet)
    {
        if (packet.Disk != null) throw new InvalidDataException("Drop disk files onto a disk workspace; drop tape/QuickDisk records onto a tape/QuickDisk workspace.");
        return packet.Records.Select(r =>
        {
            if (r == null || r.Header == null || r.Header.Length != TapeRecord.HeaderLength || r.Payload == null || r.Trailing == null ||
                !Enum.IsDefined(r.Profile) || !Enum.IsDefined(r.Origin)) throw new InvalidDataException("Invalid tape record.");
            if (BinaryPrimitives.ReadUInt16LittleEndian(r.Header.AsSpan(18, 2)) != r.Payload.Length)
                throw new InvalidDataException("Tape payload length does not match its header.");
            using var reader = new BinaryReader(new MemoryStream(r.Header.Concat(r.Payload).ToArray()));
            var record = new MZTFileReader().ReadMzfRecord(reader);
            if (reader.BaseStream.Position != reader.BaseStream.Length) throw new InvalidDataException("Tape payload length does not match its header.");
            var body = record.Body; body.TrailingData = (byte[])r.Trailing.Clone(); record.Body = body;
            record.Profile = r.Profile; record.MetadataOrigin = r.Origin;
            return record;
        }).ToArray();
    }
}
