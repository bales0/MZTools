using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace MZTools;

internal static class DskIssueHelp
{
    internal static string SeverityMeaning(DskIssueSeverity severity) => severity switch
    {
        DskIssueSeverity.Error => "Error: invalid, inconsistent or unsupported structure, or a preserved controller error flag. Some data may not be readable or mapped correctly. This is not proof that every file is damaged.",
        DskIssueSeverity.Unsafe => "Unsafe: metadata is not reliable enough for safe reconstruction or editing. This is included in the Errors total. Keep the original and avoid saving a rebuilt image until the cause is understood.",
        DskIssueSeverity.Warning => "Warning: unusual or suspicious metadata. The image may still work; intentional nonstandard layouts and copy protection can produce warnings. Check the affected location before changing it.",
        _ => "Info: an observation about layout or allocation, not an error. It may describe omitted tracks, extra bytes or a candidate filesystem. A candidate is diagnostic evidence, not confirmed recovery."
    };

    internal static string FilterMeaning(int filter) => filter switch
    {
        1 => SeverityMeaning(DskIssueSeverity.Error) + " Unsafe findings are also included in this filter.",
        2 => SeverityMeaning(DskIssueSeverity.Warning),
        3 => SeverityMeaning(DskIssueSeverity.Info),
        4 => SeverityMeaning(DskIssueSeverity.Unsafe),
        _ => "All findings: Errors include Unsafe; Warnings indicate suspicious metadata; Info describes the layout. Analysis is read-only, is not a hardware read test and does not automatically repair anything."
    };

    internal static (string Meaning, string Impact, string Action) Explain(string code) => code switch
    {
        "DSK_PARSE_FAILED" or "DSK_TRACK_COUNT" or "DSK_TRACK_SIZE" or "DSK_SECTOR_OUTSIDE_TRACK" =>
            ("Declared container geometry, sizes or boundaries cannot be reconciled with the stored data.", "Part of the image may be missing or mapped at an incorrect position.", "Compare the image length and header/track descriptors with a trusted copy. Preserve the original; do not guess missing bytes."),
        "DSK_SECTOR_SIZE" =>
            ("Stored byte length differs from the sector size code N, the declared length, or the supported size range.", "Reads or rebuilds may interpret sector boundaries incorrectly; some special formats intentionally use unusual sizes.", "Inspect N, stored length and raw bytes in Hex view block; compare with the expected disk format."),
        "DSK_EXTENDED_ZERO_LENGTH" =>
            ("An Extended DSK sector descriptor stores a zero length in bytes +6/+7, despite having payload recovered by MZTools from size code N.", "The explicit stored length does not describe the recovered payload. Readers can therefore disagree about sector boundaries and available data.", "Regenerate using the corrected writer or repair an explicit copy by filling only zero length fields from the verified payload sizes. Existing images are never silently normalized on open/save."),
        "DSK_DUPLICATE_SECTOR_ID" =>
            ("More than one descriptor has the same C/H/R address.", "Logical sector lookup is ambiguous. The layout may be damaged or intentionally protected.", "Compare the duplicate physical indices and bytes. Do not delete a duplicate solely because its address repeats."),
        "DSK_TRACK_CH" or "DSK_SECTOR_CH" =>
            ("Cylinder/side metadata disagrees with the physical track position or its enclosing track header.", "Address-based lookup may differ from descriptor order; the mismatch can be intentional.", "Check C/H/R and physical indices against the target machine's layout before normalizing metadata."),
        "DSK_FDC_STATUS" =>
            ("The sector descriptor contains preserved controller status bytes ST1/ST2 from the image capture.", "Error flags may indicate unreadable/missing data or a CRC error during capture, but may also represent intentional protection. This analysis does not recalculate a physical sector CRC.", "Review the decoded flags and sector bytes; compare with another capture or trusted image. No flags or data are repaired automatically."),
        "DSK_MISSING_TRACK" =>
            ("The image explicitly declares an absent track with tsize=0.", "No sector bytes exist for that track. This may be a legitimate sparse or partially formatted image.", "Compare with the expected geometry; do not infer missing sector contents from neighboring tracks."),
        "DSK_TRAILING_DATA" =>
            ("Bytes remain after the declared track data.", "They are not assigned to sectors by the container parser; they may be padding or additional preserved data.", "Retain the original bytes and inspect their purpose before removing or converting them."),
        "FS_DETECTION_FAILED" or "FS_LAYOUT_INVALID" or "FSMZ_BLOCK_MAP" or "CPM_PHYSICAL_MAP_MISMATCH" =>
            ("Filesystem detection or logical-to-physical mapping could not be completed.", "The physical image may remain inspectable, but ownership, free space or file extraction can be incomplete.", "Check the supplied diagnostic, filesystem geometry and required sector IDs. Use the raw block view without assuming that unmapped space is free."),
        "FS_LEGACY_WARNING" =>
            ("The filesystem reader reported this additional diagnostic.", "The impact depends on the exact message and severity; Unsafe means rebuilding could change or lose data.", "Review the original diagnostic below and compare the relevant filesystem metadata with a trusted image."),
        "CPM_LAYOUT_CANDIDATE" or "FSMZ_LAYOUT_CANDIDATE" =>
            ("Remaining metadata suggests a filesystem that the normal reader did not accept.", "The displayed mapping is a diagnostic candidate, not proof of a valid filesystem or successful recovery.", "Verify directory records and allocation ranges independently before relying on recovered names or owners."),
        "FSMZ_DINFO_INVALID" or "FSMZ_DIRECTORY_MARKER" =>
            ("FSMZ DINFO boundaries or the expected directory signature are invalid.", "File-area boundaries or directory interpretation may be unreliable.", "Inspect DINFO and directory block 16; compare their declared ranges/signature with a trusted FSMZ image."),
        "FSMZ_DUPLICATE_NAME" or "MRS_DUPLICATE_FILENAME" or "CPM_DUPLICATE_EXTENT" or "CPM_INVALID_FILENAME" =>
            ("Directory naming or grouping is invalid or ambiguous.", "Files may be difficult to identify, or multiple records may be grouped incorrectly.", "Compare user area, names, extent groups and raw directory records before renaming or deleting entries."),
        "FSMZ_FILE_OUT_OF_RANGE" or "FSMZ_BITMAP_OUT_OF_RANGE" or "CPM_BLOCK_OUT_OF_RANGE" or "MRS_BLOCK_OUT_OF_RANGE" =>
            ("A file/allocation entry refers outside the valid image or declared file area.", "Referenced data may not exist; extraction or allocation can fail or use an incorrect region.", "Check the reported block pointer and declared disk limits. Do not replace out-of-range pointers with guessed values."),
        "FSMZ_OVERLAPPING_FILES" or "CPM_CROSSLINKED_BLOCK" or "CPM_DUPLICATE_ALLOCATION" or "CPM_PHYSICAL_MAP_ALIAS" =>
            ("Multiple allocation claims resolve to the same block or physical byte range.", "Editing or deleting one owner may affect another file; an incorrect mapping can alias otherwise separate allocations.", "Inspect all owners and the physical mapping. Keep a backup and avoid allocation-changing edits until ownership is resolved."),
        "FSMZ_BITMAP_MISMATCH" or "FSMZ_USED_COUNTER_MISMATCH" =>
            ("Directory usage, the allocation bitmap or the stored used-block counter disagree.", "Reported free space may be unreliable; bitmap conflicts can permit accidental reuse of live file data.", "Compare directory claims with DINFO allocation bits and counters. Analysis does not automatically update them."),
        "FSMZ_ORPHAN_BLOCK" or "MRS_FAT_ORPHAN_BLOCK" =>
            ("An allocated block has no matching directory owner.", "It may contain lost-file data or stale allocation metadata; it is not automatically safe to reuse.", "Inspect the block and preserve its contents before considering any allocation repair."),
        "CPM_DIRECTORY_ALLOCATION" or "CPM_DIRECTORY_DATA_OVERLAP" or "CPM_SYSTEM_DATA_OVERLAP" or "MRS_RESERVED_DATA_OVERLAP" =>
            ("Directory/system reservation does not match the allocation map, or file data claims a reserved region.", "Allocation edits can overwrite directory, boot or system data.", "Verify the DPB/FAT reservations and file pointers against the expected disk format; avoid writes while the conflict remains."),
        "CPM_USER_METADATA" =>
            ("A directory record uses a user/status value other than a normal file entry.", "Known metadata records can be informational; unrecognized values may indicate damaged or nonstandard directory data.", "Review the exact record type and bytes instead of treating every non-file record as a deleted or corrupt file."),
        "CPM_BROKEN_EXTENT" or "CPM_SIZE_ALLOCATION_MISMATCH" or "CPM_EXTENT_GAP" =>
            ("CP/M extent fields, record counts or allocation lists cannot describe a consistent complete file.", "File size or segment order may be wrong, and data may be missing. Rebuilding an Unsafe extent can lose data.", "Compare EX/S2/RC and allocation pointers for every extent of the same user/name; preserve raw directory records."),
        "PCPM_SYSTEM_FILE" =>
            ("Native SHARP P-CP/M80 IPL reads directory entry 0 and requires user 0 PCPM.SYS there.", "The file elsewhere in the directory cannot boot with this loader. A SYS flag alone does not verify its version or runtime bootability.", "Install native IPL and PCPM.SYS from a trusted matching source. Preserve entry 0's old file extent by relocating it; system data may use any safe free blocks."),
        "PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY" =>
            ("PCPM.SYS exists, but it is not the first physical CP/M directory entry.", "The native MZ-2Z047 IPL compares entry 0 only and will report No system file even when a later entry has the correct filename.", "Use the native installer to place PCPM.SYS at entry 0 and safely relocate its previous occupant. This is a native boot constraint, not a general CP/M filesystem error."),
        "PCPM_SYS_INVALID_EXTENTS" =>
            ("The native system file has multiple, duplicate or noninitial directory extents.", "The native IPL follows only the first directory entry's allocation list. Multiple entries must not be merged or replaced heuristically.", "Keep the original image and use a consistent single-extent PCPM.SYS from a trusted native reference. Installation rejects the inconsistent source/target."),
        "CPM_FREE_DERIVED" =>
            ("CP/M has no stored allocation bitmap; free blocks are computed from directory extents and reserved DPB blocks.", "Arbitrary bytes in an unclaimed block are not enough to prove an orphan file or occupied allocation.", "Use the directory-derived allocation view, but retain unclaimed bytes when preserving or recovering an image."),
        "MRS_FAT_COVERAGE" or "MRS_INVALID_FAT_MARKER" =>
            ("The MRS FAT does not cover the image or uses a metadata marker in the data area.", "Allocation ownership may be incomplete or reserved blocks may be misinterpreted.", "Inspect FAT length, markers and data-area boundaries before relying on its allocation map."),
        "MRS_INVALID_FILE_ID" or "MRS_DUPLICATE_FILE_ID" =>
            ("MRS directory IDs are invalid or shared by multiple files.", "FAT blocks cannot be assigned unambiguously to their directory owners.", "Compare every directory ID with the FAT; do not assign new IDs without verifying the associated data."),
        "MRS_BLOCK_COUNT_MISMATCH" =>
            ("The directory's declared file-block count differs from the FAT's allocation count.", "The file's size/completeness is uncertain and rebuilding could discard or reassign blocks.", "Inspect all FAT blocks for the file ID and compare with the declared size before editing."),
        _ => ("The analyzer reported the specific condition shown below.", "Interpret the supplied evidence together with its severity and location.", "Keep the original image and inspect the affected metadata before making changes.")
    };

    internal static string DetailedText(DskAnalysisIssue issue)
    {
        var help = Explain(issue.Code);
        return $"{issue.Severity}: {issue.Code}\nLocation: {(string.IsNullOrEmpty(issue.Location) ? "Image / no specific block" : issue.Location)}\n" +
            $"{issue.Description}\n\nSeverity meaning:\n{SeverityMeaning(issue.Severity)}\n\nWhy this was reported:\n{help.Meaning}\n\nPossible impact:\n{help.Impact}\n\nRecommended check:\n{help.Action}\n\nEvidence:\n" +
            (string.IsNullOrEmpty(issue.Detail) ? issue.Description : issue.Detail) + ControllerFlags(issue);
    }

    private static string ControllerFlags(DskAnalysisIssue issue)
    {
        if (issue.Code != "DSK_FDC_STATUS") return "";
        var match = Regex.Match(issue.Detail, @"ST1=([0-9A-Fa-f]{2}), ST2=([0-9A-Fa-f]{2})");
        if (!match.Success) return "";
        byte st1 = byte.Parse(match.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        byte st2 = byte.Parse(match.Groups[2].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        string Flags(byte value, string?[] names) => string.Join(", ", Enumerable.Range(0, 8).Where(bit => (value & (1 << bit)) != 0)
            .Select(bit => names[bit] ?? $"reserved bit {bit}")) is { Length: > 0 } text ? text : "none";
        return "\nDecoded ST1: " + Flags(st1, ["missing address mark", "not writable", "no data", null, "overrun", "data/CRC error", null, "end of cylinder"]) +
            "\nDecoded ST2: " + Flags(st2, ["missing data address mark", "bad cylinder", "scan not satisfied", "scan equal hit", "wrong cylinder", "data/CRC error", "control/deleted-data mark", null]);
    }
}
