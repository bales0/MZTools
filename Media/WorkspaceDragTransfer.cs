using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;

namespace MZTools;

// WPF/OLE adapter. The wire protocol and all model operations belong to Core.
internal static class WorkspaceDragTransfer
{
    internal const string Format = WorkspaceDragCodec.Format;
    internal const int MaximumBytes = WorkspaceDragCodec.MaximumBytes;
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

    internal static WorkspaceDragPacket Read(IDataObject data)
    {
        if (data.GetData(Format, false) is not MemoryStream stream || stream.Length > MaximumBytes)
            throw new InvalidDataException("Invalid or oversized workspace copy data.");
        return Decode(stream.ToArray());
    }

    internal static WorkspaceDragPacket Capture(DskDocument disk, IEnumerable<DskFileEntry> files) => WorkspaceDragCodec.Capture(disk, files);
    internal static WorkspaceDragPacket Capture(IEnumerable<TapeRecord> records) => WorkspaceDragCodec.Capture(records);
    internal static byte[] Encode(WorkspaceDragPacket packet) => WorkspaceDragCodec.Encode(packet);
    internal static WorkspaceDragPacket Decode(byte[] bytes) => WorkspaceDragCodec.Decode(bytes);
    internal static DskDocument OpenDisk(WorkspaceDragPacket packet) => WorkspaceDragCodec.OpenDisk(packet);
    internal static TapeRecord[] OpenRecords(WorkspaceDragPacket packet) => WorkspaceDragCodec.OpenRecords(packet);
}
