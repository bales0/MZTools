# F1 — portable Core extraction

Date: 2026-10-29. Baseline: `fb0c49cd69e487d218ac9e5d279d2b3111413303`.
Scope: approved F1 only. The desktop application remains the existing Windows WPF
frontend. No Avalonia dependency, new codec, new capture pipeline, dependency-version
change, release, commit or removal of the stable frontend is included.

## Dependency and adapter boundaries

- `MZTools` → `MZTools.Core` → existing `MZTools.Capture`.
- `MZTools` → existing `MZTools.Capture.Windows` → `MZTools.Capture`.
- Core targets `net10.0` and has no production NuGet packages. Its Capture reference
  reuses the existing preprocessor and adaptive detector; it does not duplicate them.
- Internal types and the `MZTools` namespace are retained. Explicit friend assemblies
  allow the existing frontend/tests and the portable test host to use them.
- `CorePlatformServices` accepts host-owned `IAudioFileCodec` and
  `IDskRepairProtectionFactory` implementations once, before operations start. This
  composition boundary preserves existing static service entry points; there is no
  native library probing, frontend assembly discovery or OS backend selection in Core.
- The WPF module initializer registers the existing SoundFile codec and Win32 file
  identity implementation. Registration does not open a file, show UI or start a device.
  Unit-test hosts explicitly initialize that WPF module without constructing Application.
- Without an installed host adapter, FLAC fails explicitly and file-backed repair is
  rejected before mutation. In-memory repair still requires explicit Save As. Linux
  tests do not imply implemented Linux FLAC or file-identity adapters.
- Seven original `boot/*.dsk` files remain in place and are embedded in Core under
  the unchanged `MZTools.Boot.*` logical names. The root project no longer embeds copies.
- The root project excludes `src/**/*.cs` and `tests/**/*.cs` from implicit compilation.

## Extracted mixed responsibilities

| Original | Portable responsibility | Frontend responsibility |
| --- | --- | --- |
| `Media/WorkspaceDragTransfer.cs` | `Media/WorkspaceDragCodec.cs`: same v1 JSON packet, 64 MiB bound, validation, capture and reconstruction | Same WPF/OLE data format and local-owner marker |
| `DiskFormatting.cs` | `Dsk/DiskFormatService.cs`: document/container construction | Dialog, preview, confirmation and UI application |
| `DskNewDialog.cs` | `Dsk/DskNewOptions.cs`: options and format enum | Existing dialog |
| `MainWindow.xaml.cs` | `ListReorder.cs`, `Media/AudioImportPolicy.cs`, `TapeFileNameEncoding.cs` | Existing controls, filters and action handlers |
| `DskEditorWindow.xaml.cs` | `Dsk/DskExportSupport.cs`: existing MZF serialization and export naming | Existing editor and prompts |
| `CompressionOptionsControl.xaml.cs` | `Compression/CompressionDescription.cs`: unchanged option description | Existing configuration control |
| `Media/BatchTapeService.cs` | Batch workflow and byte verification | SoundFile encoder and cancellation wrapper in `WpfCoreAdapters.cs` |
| `Dsk/DskRepairSaveProtection.cs` | Protection contract only | Existing Win32 identities and read leases, unchanged checks |

`Dsk/UserProfileService` already accepts a profile-directory argument; that existing
portable override and the current default are retained. `LivePulseCatalog` remains
in the host as the projection from the single authoritative Core timing catalog to
Capture DTOs. Capture sources and public contracts are unchanged.

## Regression projects and media

The legacy Windows test project still includes every local source via SDK defaults,
including previously ignored files. `.gitignore` now allows its root C# regression
sources to be versioned; it continues to exclude local media and build artifacts.
Nothing was staged or committed.

`tests/MZTools.Core.Tests` links existing portable regression sources rather than
maintaining copies. Mixed WPF/native test files remain in the Windows project. The
QuickDisk decoded-unit UI test is split into a second partial-class file, preserving
its original fully qualified test identity. HFE formatting and drag protocol tests
call the extracted service directly; Windows smoke tests continue to cover adapters.
The new boundary tests check assembly references, boot resource names and safe repair
behavior when no native protection adapter exists.

Test media are copied under their original directory names where needed. The shared
`TestMediaPaths` helper supports both legacy and portable output layouts. Local DSK,
LEP, MZF, QD and WAV files are not added to Git. A clean checkout still requires the
separately supplied media for full corpus coverage; missing fixtures are not fabricated.
Golden JSON is parsed before comparing every expected hash, so CRLF/LF is not mistaken
for a difference in generated binary outputs. Golden files themselves are unchanged.

Windows command:

```powershell
dotnet test MZTools.sln --logger "trx;LogFilePrefix=f1-after" --results-directory TestResults/F1/after
```

Linux commands (separate artifacts prevent Linux/Windows obj/bin collisions):

```bash
dotnet test tests/MZTools.Core.Tests/MZTools.Core.Tests.csproj --artifacts-path "$HOME/.cache/mztools/f1-linux-artifacts" --results-directory TestResults/F1/linux
dotnet test MZTools.Capture.Tests/MZTools.Capture.Tests.csproj --artifacts-path "$HOME/.cache/mztools/f1-linux-artifacts" --results-directory TestResults/F1/linux
```

The WPF AudioSignal, DskVisual, Report and Recorder smoke projects remain separate
processes, including their existing assembly names and STA/Application lifecycle.
Do not run different smoke applications into a common output directory.

## Validation

Baseline Windows: legacy 1202 passed / 3 fixture skips; Capture 210 passed; Windows
Capture 52 passed. Total 1464 passed, 0 failed, 3 skipped.

Final validation (2026-10-29):

| Check | Result |
| --- | --- |
| Final Windows solution build | PASS, 0 warnings / 0 errors |
| Full legacy Windows suite after extraction | 1202 passed, 0 failed, 3 fixture skips; matches baseline |
| Capture, Windows | 210 passed, 0 failed |
| Capture.Windows | 52 passed, 0 failed |
| Core, Windows | Latest outcome of all 412 cases: 411 passed, 1 fixture skip, 0 unresolved failures |
| Core, Debian 13 / WSL | Latest outcome of all 412 cases: 411 passed, 1 fixture skip, 0 unresolved failures |
| Corrected Core harness subset, each OS | 63 passed, 0 failed on each OS |
| Final legacy harness subset | 59 passed, 0 failed |
| Report / DskVisual / AudioSignal WPF smoke | All PASS |
| Recorder WPF smoke | 114 checks passed, synthetic input only |
| Golden files / boot images / Capture production sources | No content changes |
| macOS / physical audio hardware | UNTESTED |

Core totals are explicitly aggregated from a complete run plus a targeted corrective
run, not claimed as a second clean full run. The first portable Windows run had 18
fixture-path failures; the first Linux run had those same 18 plus the JSON CRLF/LF
comparison failure. The corrected subset contains every initially failed case and
all tests affected by the shared media-path/JSON changes. Every such case passed on
both OSs; the final legacy subset passed too. No decoder, encoder or golden value
was changed to make these tests pass. `TestResults/F1/summary.json` records the merged
latest outcome per case, with zero unmatched original failures.

An additional Windows runner launched from temporary artifacts aborted before test
execution due to a testhost connection timeout; it is not counted as a PASS. The
workspace-artifact retry ran all 63 selected cases successfully. The first recorder
smoke build encountered missing generated WPF files before execution; after the
other builds ended, its unchanged source built and all 114 checks passed. Smoke
projects were restored against the new project graph before their successful runs.

The original three legacy skips are the two optional `_SFD800` full-system reference
cases and the optional non-SHARP QD full-image reference theory. Core retains the
latter skip. Existing optional environment-variable branches remain optional; a
passing test that returns without its external input is not hardware/corpus evidence.
Smoke scaling checks are simulated layout checks, not native multi-DPI certification.

Per the user's additional authorization, .NET SDK 10.0.303 is installed permanently
for WSL user `ales` in `/home/ales/.dotnet`, with PATH/DOTNET_ROOT entries in `.profile`
and `.bashrc`. Debian's required `libicu76` was installed; zlib/Kerberos dependencies
were already present. Linux test artifacts are isolated under the user's cache.
These are environment setup changes, not application/native backend dependencies.
No physical audio device is exercised by this phase. macOS remains untested. The
existing recorder adapter contract in `LIVE_TAPE_RECORDER_AVALONIA_ADAPTER.md` remains
binding for subsequent phases. New OS backends, native codecs and original-file
identity implementations require the separately approved platform phase.

## Source move manifest

99 files were moved along the following map. Of those, 93 retained their exact
pre-move SHA-256; six only replace adapter calls/types or the extracted drag codec.
The original source-map hashes are retained locally in
`TestResults/F1/source-map-before.json`. New extraction files are listed above.

| Original path | New path |
| --- | --- |
| `Compression/MzfCompressionOptions.cs` | `src/MZTools.Core/Compression/MzfCompressionOptions.cs` |
| `Compression/MzfCompressionService.cs` | `src/MZTools.Core/Compression/MzfCompressionService.cs` |
| `Compression/MzfDecompressionService.cs` | `src/MZTools.Core/Compression/MzfDecompressionService.cs` |
| `Compression/MzfLoaderBuilder.cs` | `src/MZTools.Core/Compression/MzfLoaderBuilder.cs` |
| `Compression/Zx0Compressor.cs` | `src/MZTools.Core/Compression/Zx0Compressor.cs` |
| `Compression/Zx7Compressor.cs` | `src/MZTools.Core/Compression/Zx7Compressor.cs` |
| `Dsk/BootstrapService.cs` | `src/MZTools.Core/Dsk/BootstrapService.cs` |
| `Dsk/BundledBootSystems.cs` | `src/MZTools.Core/Dsk/BundledBootSystems.cs` |
| `Dsk/CpmFileSystem.cs` | `src/MZTools.Core/Dsk/CpmFileSystem.cs` |
| `Dsk/CpmLayoutService.cs` | `src/MZTools.Core/Dsk/CpmLayoutService.cs` |
| `Dsk/CpmSystemBuilder.cs` | `src/MZTools.Core/Dsk/CpmSystemBuilder.cs` |
| `Dsk/CpmSystemProfiles.cs` | `src/MZTools.Core/Dsk/CpmSystemProfiles.cs` |
| `Dsk/CrossDiskTransferService.cs` | `src/MZTools.Core/Dsk/CrossDiskTransferService.cs` |
| `Dsk/DskAnalyzer.cs` | `src/MZTools.Core/Dsk/DskAnalyzer.cs` |
| `Dsk/DskBootInfo.cs` | `src/MZTools.Core/Dsk/DskBootInfo.cs` |
| `Dsk/DskCapabilityService.cs` | `src/MZTools.Core/Dsk/DskCapabilityService.cs` |
| `Dsk/DskCompareService.cs` | `src/MZTools.Core/Dsk/DskCompareService.cs` |
| `Dsk/DskContainerRepairService.cs` | `src/MZTools.Core/Dsk/DskContainerRepairService.cs` |
| `Dsk/DskDefragmentService.cs` | `src/MZTools.Core/Dsk/DskDefragmentService.cs` |
| `Dsk/DskDocument.cs` | `src/MZTools.Core/Dsk/DskDocument.cs` |
| `Dsk/DskExtendedLengthRepair.cs` | `src/MZTools.Core/Dsk/DskExtendedLengthRepair.cs` |
| `Dsk/DskFilePropertyService.cs` | `src/MZTools.Core/Dsk/DskFilePropertyService.cs` |
| `Dsk/DskFileTransferService.cs` | `src/MZTools.Core/Dsk/DskFileTransferService.cs` |
| `Dsk/DskFilesystemRepairService.cs` | `src/MZTools.Core/Dsk/DskFilesystemRepairService.cs` |
| `Dsk/DskGeneratedProjection.cs` | `src/MZTools.Core/Dsk/DskGeneratedProjection.cs` |
| `Dsk/DskGeneratedTrackCache.cs` | `src/MZTools.Core/Dsk/DskGeneratedTrackCache.cs` |
| `Dsk/DskHexEditService.cs` | `src/MZTools.Core/Dsk/DskHexEditService.cs` |
| `Dsk/DskImage.cs` | `src/MZTools.Core/Dsk/DskImage.cs` |
| `Dsk/DskIssueHelp.cs` | `src/MZTools.Core/Dsk/DskIssueHelp.cs` |
| `Dsk/DskLayoutModel.cs` | `src/MZTools.Core/Dsk/DskLayoutModel.cs` |
| `Dsk/DskOpenCoordinator.cs` | `src/MZTools.Core/Dsk/DskOpenCoordinator.cs` |
| `Dsk/DskPatchService.cs` | `src/MZTools.Core/Dsk/DskPatchService.cs` |
| `Dsk/DskPhysicalPropertyService.cs` | `src/MZTools.Core/Dsk/DskPhysicalPropertyService.cs` |
| `Dsk/DskRawRangeService.cs` | `src/MZTools.Core/Dsk/DskRawRangeService.cs` |
| `Dsk/DskSectorSelection.cs` | `src/MZTools.Core/Dsk/DskSectorSelection.cs` |
| `Dsk/DskStructureService.cs` | `src/MZTools.Core/Dsk/DskStructureService.cs` |
| `Dsk/DskVisualModel.cs` | `src/MZTools.Core/Dsk/DskVisualModel.cs` |
| `Dsk/DskVisualReport.cs` | `src/MZTools.Core/Dsk/DskVisualReport.cs` |
| `Dsk/FixedHexInput.cs` | `src/MZTools.Core/Dsk/FixedHexInput.cs` |
| `Dsk/FsmzFileSystem.cs` | `src/MZTools.Core/Dsk/FsmzFileSystem.cs` |
| `Dsk/MrsFileSystem.cs` | `src/MZTools.Core/Dsk/MrsFileSystem.cs` |
| `Dsk/MultiGameIplFileSystem.cs` | `src/MZTools.Core/Dsk/MultiGameIplFileSystem.cs` |
| `Dsk/PersonalCpmSystemInstaller.cs` | `src/MZTools.Core/Dsk/PersonalCpmSystemInstaller.cs` |
| `Dsk/SingleGameIplFileSystem.cs` | `src/MZTools.Core/Dsk/SingleGameIplFileSystem.cs` |
| `Dsk/UserProfileService.cs` | `src/MZTools.Core/Dsk/UserProfileService.cs` |
| `Media/AudioAlignedPcm.cs` | `src/MZTools.Core/Media/AudioAlignedPcm.cs` |
| `Media/AudioDecodePassComparison.cs` | `src/MZTools.Core/Media/AudioDecodePassComparison.cs` |
| `Media/AudioHeaderDiagnostics.cs` | `src/MZTools.Core/Media/AudioHeaderDiagnostics.cs` |
| `Media/AudioPulseMeasurementEngine.cs` | `src/MZTools.Core/Media/AudioPulseMeasurementEngine.cs` |
| `Media/AudioPulseReferences.cs` | `src/MZTools.Core/Media/AudioPulseReferences.cs` |
| `Media/AudioRangeInspection.cs` | `src/MZTools.Core/Media/AudioRangeInspection.cs` |
| `Media/AudioRecoveryDiagnostics.cs` | `src/MZTools.Core/Media/AudioRecoveryDiagnostics.cs` |
| `Media/AudioRecoveryHistogram.cs` | `src/MZTools.Core/Media/AudioRecoveryHistogram.cs` |
| `Media/AudioRecoveryMeasurements.cs` | `src/MZTools.Core/Media/AudioRecoveryMeasurements.cs` |
| `Media/AudioRegionRecovery.cs` | `src/MZTools.Core/Media/AudioRegionRecovery.cs` |
| `Media/AudioSelectionWaveExporter.cs` | `src/MZTools.Core/Media/AudioSelectionWaveExporter.cs` |
| `Media/AudioSignalAnalysis.cs` | `src/MZTools.Core/Media/AudioSignalAnalysis.cs` |
| `Media/AudioSignalAnalysisService.cs` | `src/MZTools.Core/Media/AudioSignalAnalysisService.cs` |
| `Media/AudioSignalPassOrigin.cs` | `src/MZTools.Core/Media/AudioSignalPassOrigin.cs` |
| `Media/AudioSyntheticHeader.cs` | `src/MZTools.Core/Media/AudioSyntheticHeader.cs` |
| `Media/BatchMediaPolicy.cs` | `src/MZTools.Core/Media/BatchMediaPolicy.cs` |
| `Media/BatchProcessService.cs` | `src/MZTools.Core/Media/BatchProcessService.cs` |
| `Media/BatchTapeService.cs` | `src/MZTools.Core/Media/BatchTapeService.cs` |
| `Media/FloppyTrackCodec.cs` | `src/MZTools.Core/Media/FloppyTrackCodec.cs` |
| `Media/HfeDocumentSession.cs` | `src/MZTools.Core/Media/HfeDocumentSession.cs` |
| `Media/HfeImage.cs` | `src/MZTools.Core/Media/HfeImage.cs` |
| `Media/HfeSemanticInspection.cs` | `src/MZTools.Core/Media/HfeSemanticInspection.cs` |
| `Media/ImageVerificationService.cs` | `src/MZTools.Core/Media/ImageVerificationService.cs` |
| `Media/ManualPulseDetection.cs` | `src/MZTools.Core/Media/ManualPulseDetection.cs` |
| `Media/MediaConversionService.cs` | `src/MZTools.Core/Media/MediaConversionService.cs` |
| `Media/MediaFormats.cs` | `src/MZTools.Core/Media/MediaFormats.cs` |
| `Media/MzNativeComBuilder.cs` | `src/MZTools.Core/Media/MzNativeComBuilder.cs` |
| `Media/MzfCpmCompatibilityAnalyzer.cs` | `src/MZTools.Core/Media/MzfCpmCompatibilityAnalyzer.cs` |
| `Media/NativeComConversionService.cs` | `src/MZTools.Core/Media/NativeComConversionService.cs` |
| `Media/NativeComFeedback.cs` | `src/MZTools.Core/Media/NativeComFeedback.cs` |
| `Media/NativeComImportService.cs` | `src/MZTools.Core/Media/NativeComImportService.cs` |
| `Media/NativeComSourceCompression.cs` | `src/MZTools.Core/Media/NativeComSourceCompression.cs` |
| `Media/NativeProgramImage.cs` | `src/MZTools.Core/Media/NativeProgramImage.cs` |
| `Media/PhysicalTrack.cs` | `src/MZTools.Core/Media/PhysicalTrack.cs` |
| `Media/PulseReferenceCatalog.cs` | `src/MZTools.Core/Media/PulseReferenceCatalog.cs` |
| `Media/SegmentedPcmStreamReader.cs` | `src/MZTools.Core/Media/SegmentedPcmStreamReader.cs` |
| `Media/TapeCopyPreview.cs` | `src/MZTools.Core/Media/TapeCopyPreview.cs` |
| `Mz800DskImage.cs` | `src/MZTools.Core/Mz800DskImage.cs` |
| `Mz800IplDskReader.cs` | `src/MZTools.Core/Mz800IplDskReader.cs` |
| `Mz800IplDskWriter.cs` | `src/MZTools.Core/Mz800IplDskWriter.cs` |
| `Mz800MultiGameIplDskWriter.cs` | `src/MZTools.Core/Mz800MultiGameIplDskWriter.cs` |
| `Mzf.cs` | `src/MZTools.Core/Mzf.cs` |
| `QuickDisk.cs` | `src/MZTools.Core/QuickDisk.cs` |
| `QuickDiskFormatLabels.cs` | `src/MZTools.Core/QuickDiskFormatLabels.cs` |
| `QuickDiskHostDetection.cs` | `src/MZTools.Core/QuickDiskHostDetection.cs` |
| `QuickDiskLayout.cs` | `src/MZTools.Core/QuickDiskLayout.cs` |
| `QuickDiskUnitStructure.cs` | `src/MZTools.Core/QuickDiskUnitStructure.cs` |
| `QuickDiskVisualSnapshot.cs` | `src/MZTools.Core/QuickDiskVisualSnapshot.cs` |
| `ReportPresentation.cs` | `src/MZTools.Core/ReportPresentation.cs` |
| `SharpMzPulseDecoder.cs` | `src/MZTools.Core/SharpMzPulseDecoder.cs` |
| `TapeFormat.cs` | `src/MZTools.Core/TapeFormat.cs` |
| `Utility.cs` | `src/MZTools.Core/Utility.cs` |
| `WavHeuristicAnalyzer.cs` | `src/MZTools.Core/WavHeuristicAnalyzer.cs` |
| `WavPcmStreamReader.cs` | `src/MZTools.Core/WavPcmStreamReader.cs` |
