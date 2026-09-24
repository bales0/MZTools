using System;
using System.Collections.Generic;
using System.IO;

namespace QDTool
{
    internal sealed class SingleGameIplFileSystem : IDskFileSystem
    {
        private readonly Mz800IplDskReadResult content;
        private readonly DskFileEntry entry;

        private SingleGameIplFileSystem(DskImage image)
        {
            content = Mz800IplDskReader.Read(image.Serialize());
            entry = new DskFileEntry
            {
                Key = "single-ipl",
                Name = content.BootName,
                Extension = "MZF",
                FileType = 1,
                Size = content.Record.Body.MzfBody.Length,
                LoadAddress = content.Record.Header.MzfStart,
                ExecuteAddress = content.Record.Header.MzfExec,
                StartBlock = content.StartBlock,
                Blocks = content.ProgramSectorCount,
                Notes = "Complete single-game IPL program"
            };
        }

        public DskFileSystemType Type => DskFileSystemType.SingleIpl;
        public string DisplayName => "MZ single-game IPL";
        public bool IsReadOnly => true;
        public long UsedBytes => (content.ProgramSectorCount + 1L) * Mz800DskImage.SectorSize;
        public long FreeBytes => content.DiskInfo.FreeCapacityBytes;
        public IReadOnlyList<string> Warnings => Array.Empty<string>();

        internal static bool TryOpen(DskImage image, out SingleGameIplFileSystem? result)
        {
            if (image.TrackCount != Mz800DskImage.CylinderCount || image.SideCount != Mz800DskImage.SideCount)
            {
                result = null;
                return false;
            }
            try { result = new SingleGameIplFileSystem(image); return true; }
            catch (InvalidDataException) { result = null; return false; }
        }

        public IReadOnlyList<DskFileEntry> ReadDirectory() => [entry];
        public byte[] Extract(DskFileEntry selected) => selected.Key == entry.Key
            ? (byte[])content.Record.Body.MzfBody.Clone()
            : throw new FileNotFoundException("The single-game IPL entry was not found.");
        public void Insert(string name, byte[] data, byte fileType = 1, ushort loadAddress = 0, ushort executeAddress = 0, int user = 0) =>
            throw new NotSupportedException("Single-game IPL images are currently read-only.");
        public void Delete(DskFileEntry selected, bool force = false) =>
            throw new NotSupportedException("Single-game IPL images are currently read-only.");
        public void Rename(DskFileEntry selected, string newName) =>
            throw new NotSupportedException("Single-game IPL images are currently read-only.");
    }
}
