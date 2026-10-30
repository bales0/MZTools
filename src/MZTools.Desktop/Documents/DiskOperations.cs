using System.Text;

namespace MZTools.Desktop.Documents;

internal sealed record DiskPreview(DskDocument Candidate,long Revision,string Report);
internal static class DiskOperations
{
    internal static DiskPreview Preview(DiskSession session,Action<DskDocument> operation,string report,bool allowReadOnly=false)
    {
        if(session.HasDraft)throw new InvalidOperationException("Apply or Cancel disk edits first.");
        if(session.IsReadOnly || !allowReadOnly && !session.CanEdit)throw new InvalidOperationException(session.SaveDisabledReason);
        var before=session.Model.Serialize();var candidate=session.Model.Clone();operation(candidate);
        // Validate HFE sector updates before exposing Apply.
        _=candidate.SerializeContainer();
        return new(candidate,session.Revision,new DskOperationPreview(before,candidate.Serialize(),report,session.Model).Report);
    }
    internal static MediaConversionPreview ConvertContainer(DiskSession session,string format)
    {
        if(format=="DSK")return MediaConversionService.HfeToDsk(session.PhysicalImage??throw new InvalidOperationException("This image is already DSK."));
        var version=Enum.Parse<HfeVersion>(format.Replace("HFE ",""));
        if(session.PhysicalImage is not { } physical)return MediaConversionService.DskToHfe(session.Model,version);
        byte[] bytes=physical.Serialize(version);HfeImage.VerifyPhysical(physical,HfeImage.Parse(bytes));
        return new(bytes,"Convert the stored HFE physical image to "+version+". Core refuses variants that cannot preserve the existing timing, weak, control and index properties. DSK projection is not used for this conversion.",false);
    }
    internal static DiskPreview Delete(DiskSession session)
    {
        var keys=Selected(session).Select(f=>f.Key).ToArray();
        return Preview(session,c=> {foreach(string key in keys)c.FileSystem.Delete(c.FileSystem.ReadDirectory().Single(e=>e.Key==key));c.MarkModified();},$"Remove {keys.Length} files. The image on disk remains unchanged until Save.");
    }
    internal static IReadOnlyList<DiskFileRow> SelectedOrEmpty(DiskSession session) => session.FileSelection.Count!=0?session.Files.Where(f=>session.FileSelection.Contains(f.Key)).ToArray():session.SelectedFile is { } row?[row]:[];
    internal static byte[] ExportFilesForRecord(DiskSession session,DiskFileRow row)
    {
        if(session.Model.FileSystem is SingleGameIplFileSystem single){var r=single.GetRecord();return r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray();}
        if(session.Model.FileSystem is MultiGameIplFileSystem multi){var r=multi.GetInputs()[session.Model.FileSystem.ReadDirectory().ToList().FindIndex(e=>e.Key==row.Key)].Record;return r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray();}
        return DskMzfConverter.Serialize(row.Entry,session.Model.FileSystem.Extract(row.Entry));
    }
    internal static IReadOnlyList<DiskFileRow> Selected(DiskSession session)
    {
        var selected=session.FileSelection.Count!=0?session.Files.Where(f=>session.FileSelection.Contains(f.Key)).ToArray():session.SelectedFile is { } row?new[]{row}:[];
        if(selected.Length==0)throw new InvalidOperationException("Select at least one file.");return selected;
    }
    internal static DiskPreview Import(DiskSession session,IReadOnlyList<string> paths,CancellationToken ct)
    {
        var names=new List<string>();
        return Preview(session,c=>
        {
            if(c.FileSystem is RawDskFileSystem raw)
            {
                if(paths.Count!=1 || session.SelectedFile is null)throw new InvalidOperationException("Select one raw sector row and one input file, or use Raw import for a map range.");
                raw.ReplaceSector(c.FileSystem.ReadDirectory().Single(e=>e.Key==session.SelectedFile.Key),File.ReadAllBytes(paths[0]));c.MarkModified();return;
            }
            foreach(string path in paths)
            {
                ct.ThrowIfCancellationRequested();var fingerprint=AtomicFileTransaction.Hash(path);
                if(Path.GetExtension(path).Equals(".mzf",StringComparison.OrdinalIgnoreCase))
                {
                    var r=new MZTFileReader().ReadStandaloneMzf(path);
                    string name=DskImportPolicy.GetImportName(c.FileSystem.Type,path,SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname));
                    c.FileSystem.Insert(name,r.Body.MzfBody,r.Header.MzfFtype,r.Header.MzfStart,r.Header.MzfExec);names.Add(name);
                }
                else {c.FileSystem.Insert(Path.GetFileName(path),File.ReadAllBytes(path));names.Add(Path.GetFileName(path));}
                if(AtomicFileTransaction.Hash(path)!=fingerprint)throw new IOException("Import source changed: "+path);
            }
            c.MarkModified();
        },$"Import {paths.Count} files. Core validates names, collisions and capacity; all files apply together.\n"+string.Join("\n",paths));
    }
    internal static bool CanCopy(DiskSession session)=>session.HasProjection&&(CrossDiskTransferService.Supports(session.Model)||session.Model.FileSystem is SingleGameIplFileSystem||session.Model.FileSystem is MultiGameIplFileSystem {CanConfigure:true});
    internal static WorkspaceDragPacket CapturePacket(DiskSession session)
    {
        var rows=Selected(session);
        if(session.Model.FileSystem is SingleGameIplFileSystem or MultiGameIplFileSystem)
        {var directory=session.Model.FileSystem.ReadDirectory();var inputs=IplInputs(session);return WorkspaceDragCodec.Capture(rows.Select(r=>inputs[directory.ToList().FindIndex(e=>e.Key==r.Key)].Record));}
        return WorkspaceDragCodec.Capture(session.Model,rows.Select(r=>r.Entry));
    }
    internal static TapeRecord[] RecordsFromPacket(WorkspaceDragPacket packet)
    {
        if(packet.Disk is null)return WorkspaceDragCodec.OpenRecords(packet);
        using var session=new DiskSession(WorkspaceDragCodec.OpenDisk(packet));
        var rows=session.Files.Where(r=>packet.Keys.Contains(r.Key)).ToArray();
        if(rows.Length!=packet.Keys.Length||rows.Any(r=>!DskExportSupport.CanExportMzf(session.Model.FileSystem.Type,r.Entry)))throw new InvalidDataException("These disk files do not have native tape metadata. Export their raw payloads instead.");
        return rows.Select(r=>{using var reader=new BinaryReader(new MemoryStream(ExportFilesForRecord(session,r)));return new MZTFileReader().ReadMzfRecord(reader);}).ToArray();
    }
    internal static DiskPreview Paste(DiskSession session,byte[] bytes,DiskCollisionPolicy collision,IReadOnlyDictionary<string,string>? names=null)
    {
        var packet=WorkspaceDragCodec.Decode(bytes);
        if(packet.Disk is not null)
        {
            var source=WorkspaceDragCodec.OpenDisk(packet);
            var preview=CrossDiskTransferService.Preview(source,session.Model,packet.Keys.Select(k=>new DiskCopyRequest(k,names is not null&&names.TryGetValue(k,out var renamed)?renamed:null)).ToArray(),collision);
            return Preview(session,c=>preview.Operation.Apply(c),preview.Operation.Report);
        }
        var records=WorkspaceDragCodec.OpenRecords(packet);
        return Preview(session,c=>
        {
            for(int index=0;index<records.Length;index++)
            {
                var r=records[index];
                string name=names is not null&&names.TryGetValue(index.ToString(System.Globalization.CultureInfo.InvariantCulture),out var renamed)?renamed:SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname).Trim();
                DskFileTransferService.ValidateName(name,c.FileSystem);
                c.FileSystem.Insert(name,r.Body.MzfBody,r.Header.MzfFtype,r.Header.MzfStart,r.Header.MzfExec);
            }
            c.MarkModified();
        },$"Copy {records.Length} tape records into this filesystem. Source records are unchanged.\nTape profile, trailing data and raw header description are not disk directory metadata.\nCP/M does not store tape LOAD/EXEC/type fields; disk record padding follows Core. Duplicate names are refused.");
    }
    internal static IReadOnlyDictionary<string,byte[]> ExportFiles(DiskSession session,string path,bool mzf,bool all)
    {
        var rows=all?session.Files:Selected(session);if(rows.Count==0)throw new InvalidOperationException("No files to export.");
        if(mzf && rows.Any(r=>session.Model.FileSystem is not MultiGameIplFileSystem && !DskExportSupport.CanExportMzf(session.Model.FileSystem.Type,r.Entry)))throw new InvalidDataException("Use raw export: this filesystem does not provide a native MZF header.");
        var entries=rows.Select(r=>r.Entry).ToArray();
        var names=DskExportSupport.BuildBatchFileNames(entries,session.Model.FileSystem.Type,mzf);
        var outputs=new Dictionary<string,byte[]>(AtomicFileTransaction.PathComparer);
        for(int i=0;i<entries.Length;i++)
        {
            byte[] data=session.Model.FileSystem.Extract(entries[i]);
            if(mzf)
            {
                if(session.Model.FileSystem is SingleGameIplFileSystem single){var r=single.GetRecord();data=r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray();}
                else if(session.Model.FileSystem is MultiGameIplFileSystem multi){var r=multi.GetInputs()[session.Model.FileSystem.ReadDirectory().ToList().FindIndex(e=>e.Key==entries[i].Key)].Record;data=r.GetSerializedHeader().Concat(r.Body.MzfBody).ToArray();}
                else data=DskMzfConverter.Serialize(entries[i],data);
            }
            outputs.Add(entries.Length==1?path:Path.Combine(Path.GetDirectoryName(path)!,names[i]),data);
        }
        return outputs;
    }
    internal static IReadOnlyList<MultiGameIplInput> IplInputs(DiskSession session)=>session.Model.FileSystem switch
    {
        SingleGameIplFileSystem single=>[new(single.GetRecord(),single.BootName,new(MzfCompressionAlgorithm.None),single.GetRecord().Body.DataSize)],
        MultiGameIplFileSystem multi=>multi.GetInputs(),
        _=>throw new InvalidOperationException("This disk is not a configurable single/multi IPL image.")
    };
    internal static DskDocument BuildIpl(IReadOnlyList<MultiGameIplInput> inputs,bool single)
        =>DskDocument.Open(single?inputs.Count==1?Mz800IplDskWriter.Build(inputs[0].Record,Mz800IplDskWriter.NormalizeBootName(inputs[0].DisplayName)):throw new InvalidDataException("Single IPL requires exactly one program."):Mz800MultiGameIplDskWriter.Build(inputs).Image);
    internal static DiskPreview RebuildIpl(DiskSession session,IReadOnlyList<MultiGameIplInput> inputs)
    {
        if(session.HasDraft || session.IsReadOnly || session.Model.SourceHfe?.WriteAllowed==false)throw new InvalidOperationException("This IPL image cannot be rebuilt in its current state.");
        var rebuilt=BuildIpl(inputs,session.Model.FileSystem is SingleGameIplFileSystem);
        var candidate=session.Model.Clone();candidate.ReplaceContents(rebuilt.Serialize());_=candidate.SerializeContainer();
        return new(candidate,session.Revision,new DskOperationPreview(session.Model.Serialize(),candidate.Serialize(),$"Rebuild IPL: {inputs.Count} programs. Existing Core menu, loader and compression rules apply.",session.Model).Report);
    }
    internal static MultiGameIplInput[] ReadIplFiles(IReadOnlyList<string> paths,CancellationToken ct)
        =>paths.SelectMany(p=>{ct.ThrowIfCancellationRequested();return TapeDocumentIO.Read(p).Records;}).Select(r=>new MultiGameIplInput(r.DeepClone(),SharpMzEncoding.ConvertMzfNameToASCIIString(r.Header.MzfFname).Trim(),new(MzfCompressionAlgorithm.None),r.Body.DataSize)).ToArray();
}
