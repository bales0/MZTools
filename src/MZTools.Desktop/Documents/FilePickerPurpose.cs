namespace MZTools.Desktop.Documents;

// Operation identity is independent of the localized window title.
public enum FilePickerPurpose
{
    OpenMedia, OpenAudio, ImportTapeRecords, ImportDiskFiles, ImportCpmDiskFiles,
    ImportIplPrograms, ImportNativeComSource, ConvertNativeComSource, ImportNativeComRecord,
    CopyFromDisk, CompareDisk, PatchBaseline, RawSectorImport, BootstrapImport,
    ApplyDiskPatch, BatchInputs, BootSystemSource, BootSystemOrProfile
}
