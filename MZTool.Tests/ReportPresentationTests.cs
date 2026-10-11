namespace MZTools.Tests;

public class ReportPresentationTests
{
    [Fact]
    public void AdapterPreservesAllDiagnosticsAndSeparatesRangesAndBytes()
    {
        const string text="Preview\nSource: DISK\nWarnings:\nUnusual but retained evidence\nMetadata changes:\nname: attributes rebuilt\nBefore SHA-256: ABCD\nChanged byte ranges: 1\n0x100..0x1FF (256 B)\n0x0010: 00 → FF\nUnrecognized final diagnostic";
        var report=ReportPresentation.FromText(text);
        Assert.Equal(text,report.ExportText);
        Assert.Contains(report.Tables,t=>t.Title=="Changed ranges" && t.Rows.Single().SequenceEqual(new[] {"0x100","0x1FF","256"}));
        Assert.Contains(report.Tables,t=>t.Title=="Byte changes" && t.Rows.Single().SequenceEqual(new[] {"0x0010","00","FF"}));
        Assert.Contains(report.Tables.SelectMany(t=>t.Rows),r=>r.Contains("Unrecognized final diagnostic"));
        Assert.Contains(report.Tables.Single(t=>t.Title=="Properties").Rows,r=>r[0]=="Before SHA-256");
        Assert.All(report.Tables,t=>Assert.All(t.Rows,r=>Assert.Equal(t.Columns.Count,r.Length)));
    }
    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("\r\n   \n")]
    public void EmptyReportsHaveExplicitEmptyState(string? text)
    {
        var report=ReportPresentation.FromText(text);
        Assert.Single(report.Tables); Assert.Contains("No report",report.Tables[0].Rows[0][1]);
    }
    [Fact]
    public void CopyPresentationIncludesRenamedAndSkippedFilesWithoutChangingDocuments()
    {
        var source=DskDocumentFactory.CreateCpm(false); var target=DskDocumentFactory.CreateCpm(false);
        source.FileSystem.Insert("GAME.COM",[1,2,3]); source.FileSystem.Insert("OTHER.COM",[4,5]); target.FileSystem.Insert("GAME.COM",[9]);
        source.MarkModified(); target.MarkModified();
        var sourceBefore=source.Serialize(); var targetBefore=target.Serialize();
        var request=source.FileSystem.ReadDirectory().Select(e=>new DiskCopyRequest(e.Key)).ToArray();
        var renamed=CrossDiskTransferService.Preview(source,target,request,DiskCollisionPolicy.Rename);
        var presentation=renamed.Presentation();
        var rows=presentation.Tables.Single(t=>t.Title=="Files").Rows;
        Assert.Equal(2,rows.Count); Assert.Contains(rows,r=>r[0]=="GAME.COM" && r[1]=="GAME~1.COM");
        Assert.Contains(presentation.Tables.Single(t=>t.Title=="File properties").Rows,r=>r[2]=="LOAD" && r[3]=="Not represented" && r[4]=="Not represented");
        Assert.Equal(renamed.Operation.Report,presentation.ExportText);
        Assert.DoesNotContain(presentation.Tables.Where(t=>t.Title is "Properties" or "Messages").SelectMany(t=>t.Rows),r=>r.Any(v=>v.Contains("; payload ") || v.Contains("Target metadata:")));
        var skipped=CrossDiskTransferService.Preview(source,target,request,DiskCollisionPolicy.Skip);
        Assert.Contains(skipped.Presentation().Tables.Single(t=>t.Title=="Files").Rows,r=>r[0]=="GAME.COM" && r[2].StartsWith("Skipped") && r[4]=="—");
        Assert.Equal(sourceBefore,source.Serialize()); Assert.Equal(targetBefore,target.Serialize());
        Assert.All(presentation.Tables,t=>Assert.All(t.Rows,r=>Assert.Equal(t.Columns.Count,r.Length)));
    }
    [Fact]
    public void LongReportsRetainEveryRangeAndUnknownMessage()
    {
        var lines=Enumerable.Range(0,5000).Select(i=>$"0x{i*256:X}..0x{i*256+255:X} (256 B)").ToArray();
        var report=ReportPresentation.FromText(string.Join("\n",lines)+"\nNot evaluated: physical timing");
        Assert.Equal(5000,report.Tables.Single(t=>t.Title=="Changed ranges").Rows.Count);
        Assert.Contains(report.Tables.SelectMany(t=>t.Rows),r=>r.Contains("physical timing"));
    }
}
