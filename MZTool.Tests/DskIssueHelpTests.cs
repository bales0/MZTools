namespace MZTools.Tests;

public class DskIssueHelpTests
{
    [Fact]
    public void EveryAnalyzerCodeHasSpecificExplanationAndChecks()
    {
        string[] codes = [
            "DSK_PARSE_FAILED", "FS_DETECTION_FAILED", "CPM_LAYOUT_CANDIDATE", "FS_LAYOUT_INVALID", "FS_LEGACY_WARNING",
            "DSK_TRACK_COUNT", "DSK_MISSING_TRACK", "DSK_TRACK_CH", "DSK_TRACK_SIZE", "DSK_SECTOR_CH", "DSK_SECTOR_SIZE",
            "DSK_FDC_STATUS", "DSK_SECTOR_OUTSIDE_TRACK", "DSK_DUPLICATE_SECTOR_ID", "DSK_TRAILING_DATA", "DSK_EXTENDED_ZERO_LENGTH",
            "FSMZ_LAYOUT_CANDIDATE", "FSMZ_DINFO_INVALID", "FSMZ_DIRECTORY_MARKER", "FSMZ_BLOCK_MAP", "FSMZ_DUPLICATE_NAME",
            "FSMZ_FILE_OUT_OF_RANGE", "FSMZ_OVERLAPPING_FILES", "FSMZ_BITMAP_MISMATCH", "FSMZ_BITMAP_OUT_OF_RANGE",
            "FSMZ_ORPHAN_BLOCK", "FSMZ_USED_COUNTER_MISMATCH", "CPM_DIRECTORY_ALLOCATION", "CPM_SYSTEM_DATA_OVERLAP",
            "CPM_PHYSICAL_MAP_ALIAS", "CPM_PHYSICAL_MAP_MISMATCH", "CPM_USER_METADATA", "CPM_INVALID_FILENAME",
            "CPM_BROKEN_EXTENT", "CPM_DUPLICATE_EXTENT", "CPM_SIZE_ALLOCATION_MISMATCH", "CPM_BLOCK_OUT_OF_RANGE",
            "CPM_DIRECTORY_DATA_OVERLAP", "CPM_DUPLICATE_ALLOCATION", "CPM_CROSSLINKED_BLOCK", "CPM_EXTENT_GAP",
            "CPM_FREE_DERIVED", "PCPM_SYSTEM_FILE", "PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY", "PCPM_SYS_INVALID_EXTENTS", "MRS_FAT_COVERAGE", "MRS_INVALID_FILE_ID", "MRS_DUPLICATE_FILE_ID", "MRS_DUPLICATE_FILENAME",
            "MRS_RESERVED_DATA_OVERLAP", "MRS_FAT_ORPHAN_BLOCK", "MRS_INVALID_FAT_MARKER", "MRS_BLOCK_COUNT_MISMATCH", "MRS_BLOCK_OUT_OF_RANGE"
        ];
        foreach (string code in codes)
        {
            var help = DskIssueHelp.Explain(code);
            Assert.DoesNotContain("specific condition shown below", help.Meaning);
            Assert.True(help.Meaning.Length > 30, code);
            Assert.True(help.Impact.Length > 30, code);
            Assert.True(help.Action.Length > 30, code);
        }
    }

    [Fact]
    public void SeverityMeaningsExplainUnsafeSubsetAndNonErrorObservations()
    {
        Assert.Contains("not proof", DskIssueHelp.SeverityMeaning(DskIssueSeverity.Error));
        Assert.Contains("included in the Errors total", DskIssueHelp.SeverityMeaning(DskIssueSeverity.Unsafe));
        Assert.Contains("may still work", DskIssueHelp.SeverityMeaning(DskIssueSeverity.Warning));
        Assert.Contains("not an error", DskIssueHelp.SeverityMeaning(DskIssueSeverity.Info));
        Assert.Contains("Unsafe findings", DskIssueHelp.FilterMeaning(1));
        Assert.Contains("Unsafe:", DskIssueHelp.FilterMeaning(4));
    }

    [Fact]
    public void CountsDoNotDoubleCountUnsafeAndReportsContainDetailedEvidence()
    {
        var model = new DskLayoutModel();
        model.Issues.Add(new("DSK_SECTOR_SIZE", DskIssueSeverity.Error, "Size mismatch", "N=2, bytes=128", 3, 2));
        model.Issues.Add(new("CPM_BROKEN_EXTENT", DskIssueSeverity.Unsafe, "Bad extent", "RC=255", FileKey: "file"));
        model.Issues.Add(new("DSK_TRACK_CH", DskIssueSeverity.Warning, "Different C/H", "", 1));
        model.Issues.Add(new("DSK_MISSING_TRACK", DskIssueSeverity.Info, "Missing", "", 4));
        Assert.Equal(4, model.Issues.Count);
        Assert.Equal(2, model.Errors); Assert.Equal(1, model.Unsafe);
        Assert.Equal(1, model.Warnings); Assert.Equal(1, model.Information);
        string report = model.Report();
        foreach (var issue in model.Issues)
        {
            Assert.Contains(issue.Explanation, report);
            Assert.Contains("Severity meaning:", issue.Explanation);
            Assert.Contains("Possible impact:", issue.Explanation);
            Assert.Contains("Recommended check:", issue.Explanation);
            Assert.Contains(issue.Location, issue.Explanation);
        }
        Assert.Contains("N=2, bytes=128", report);
    }

    [Fact]
    public void FdcDetailsDecodeStoredFlagsWithoutClaimingNewCrcVerification()
    {
        var issue = new DskAnalysisIssue("DSK_FDC_STATUS", DskIssueSeverity.Error, "Preserved status", "ST1=25, ST2=71", 2, 0);
        Assert.Contains("Decoded ST1: missing address mark, no data, data/CRC error", issue.Explanation);
        Assert.Contains("Decoded ST2: missing data address mark, wrong cylinder, data/CRC error, control/deleted-data mark", issue.Explanation);
        Assert.Contains("does not recalculate", issue.Explanation);
        Assert.Contains("ST1=25, ST2=71", issue.Explanation);
        var warning = issue with { Severity = DskIssueSeverity.Warning, Detail = "ST1=40, ST2=00" };
        Assert.Contains("reserved bit 6", warning.Explanation);
        Assert.Contains("Decoded ST2: none", warning.Explanation);
    }

    [Fact]
    public void UnknownCodesAndMissingLocationsHaveSafeFallback()
    {
        var issue = new DskAnalysisIssue("NEW_CODE", DskIssueSeverity.Warning, "Specific evidence", "");
        Assert.Contains("Image / no specific block", issue.Explanation);
        Assert.Contains("Evidence:\nSpecific evidence", issue.Explanation);
        Assert.Contains("Keep the original image", issue.Explanation);
    }
}
