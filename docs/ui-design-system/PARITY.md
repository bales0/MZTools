# Funkční parita a vědomé změny — návrh F2

Stav všech budoucích Avalonia obrazovek v této tabulce: **Design-mapped / Not yet
implemented**. F2 nevydává modelový prototyp za funkční port. Algoritmický baseline je
[report F1](../F1_CORE_EXTRACTION_2026-10-29.md) a současný pracovní strom.

## Funkce, cílové workflow a ochranné brány

| Současná schopnost | Cílový UI kontext | Co musí zůstat zachované / jak ověřit |
| --- | --- | --- |
| MZF/M12/MZT header, metadata, sidecars, pořadí | Tape → Records / Metadata / Hex | původní bytové header/payload/trailing a enum serializace; sidecar + golden testy |
| NORMAL/MZ700/IC/TC, loader, WAV/LEP/L16 | Tape → profile a Export | původní HIGH/LOW, framing a loader; 39 golden fingerprintů, žádný nový encoder |
| ZX0/ZX7 forward/backward, Auto, partial/embedded omezení | sdílené CompressionOptions podle cíle | stejné schopnosti a disable reasons; nezměněné algoritmy/loadery |
| MZF/M12 → COM, Keep source vs Uncompressed | Tools → Native COM | unknown není „uncompressed“; textové stavy a 49 golden COM kombinací |
| QD/QDF/MZQ a fyzické varianty | QuickDisk → Directory / Physical map / Hex | logical/physical rozdíl, order, CRC evidence, decoded/raw export odděleně |
| SHARP/Roland/Akai/Thomson/Famicom a další existující hosty | QD host inspector | jen skutečně existující capability; inspekce neznamená povolený přepis hostu |
| DSK a HFEv1/v2/v3, CHRN/ST1/ST2 | Disk → map / surface / track detail | slot vs uložená metadata, fyzické/logické pořadí, timing/weak/index evidence beze změn |
| CP/M variants, FSMZ/MRS/IPL, HD/DD | Disk → Files / Structure | původní allocator, user/attributes, read-only a boot gates; fixture comparison |
| Sector/raw/hex properties, sensitive ranges | shared HexEditor + ChangePreview | pending edit guard, přesné changed ranges, Apply odděleně od Save |
| Format / compare / repair / defrag / interleave | Disk/Filesystem → samostatné akce | preview, postvalidation, no silent writes, origin protection |
| DSK Open Repair | Open inspection + ContainerRepairPreview | healthy bez dialogu, Cancel zachová aktivní dirty doc, deterministic copy + Save As, file-link ochrana |
| Filesystem repair | Filesystem → Guided Repair | nikdy nesplývá s container repair; typová omezení a před/po report |
| Boot/system install a profiles | Boot / System → preview | ověřený zdroj, geometry/DPB a slot0 pravidla; žádné nové BIOS/format konverze |
| Single/multi IPL editor | Disk → prepared program list | komprese/limity/header chain, stale preview cancellation, order a HFE variant |
| WAV/FLAC a segmented `.wav.json` | Audio → Signal / Regions | streamování, stejné frame intervaly a source protection, všechny recording segmenty |
| Standard/heuristic/manual/headerless recovery | Audio → Recovery | původní detector a reference, checksum evidence, preview/export, bez změny PCM |
| Histogram/scatter/drift, L/R alignment, mix/polarity | společné grafy + inspector | přesné units, intervaly, selection sync, reference provenance; žádné vizuální přepisování algoritmu |
| Recorder Shared/Exclusive / RAW / native/converted archive | Recorder → Input / Archive | explicit start a fallback, skutečný formát/provenance, no accidental device start |
| FFT, levels, live pulses, Head Alignment A/B | Recorder → views nad snapshots | bounded data, archive priority, kvalifikace/zero invalidation, bez absolutního úhlu/skóre |
| WAV+JSON+SHA-256, partial, segmentace, disconnect | Recorder → Recordings / Diagnostics | správná incomplete evidence, archiv vs preview loss, lifecycle a buffer ownership |
| Drag/drop, clipboard, cross-disk/tape copy, collisions | shared transfer preview | stávající omezený serializovaný packet, žádné live CLR objects, explicit overwrite/rename/cancel |
| Batch import/export/conversion/integrity | BatchWorkspace | preflight, jasný output scope, progress/cancel, dílčí výsledky bez přepsání zdroje |
| Reports TXT/CSV/JSON | ReportViewer | neztratit neznámé diagnostiky, přesné rozsahy, source/hash evidence, oddělený export |
| Save/Save As/Close, dirty/pending/read-only | centrální CommandPolicy | význam podle existujícího Save As auditu, source identity, potvrzení zavření |

## Vědomé změny vyžadující schválení vizuálního návrhu

| ID | Původní stav | Navržené sjednocení | Dopad |
| --- | --- | --- | --- |
| C01 | hlavní browser a jednotlivá okna mají vlastní navigaci | jeden shell, dokumentové záložky, návrat do posledního workspace | nový shell/focus model, schopnosti dokumentů stejné |
| C02 | různé inspectors, tabulky a properties dialogy | společný pravý inspector a jeden vzor detailního okna | méně různých oken, zachovaný kontext a detail |
| C03 | různé kroky zoomu: DSK 1.2, některé QD 1.5/2, audio 2 | faktor 1.5, kurzorový anchor, společný Fit a pan | vědomá změna UI gesta; data i souřadnicový model zůstávají |
| C04 | lokální handlery Ctrl+A/Delete/F2, Ctrl+C/V, Home/+/-/Escape | Primary podle OS a společné globální dokumentové příkazy; F6 oblasti | nové/fixované zkratky, textové vstupy mají přednost |
| C05 | mix modálních informací, progress oken a reportů | běžný stav inline/status, jeden progress/report a modal pouze pro rozhodnutí | neblokující běžná práce; citlivé Apply zůstává explicitní |
| C06 | stejná schopnost dostupná různými cestami v modulech | stejné názvy a capability řízení Save/Save As/Export/Apply | žádná změna již opravené save/export sémantiky |
| C07 | recorder jako samostatné WPF okno | společný Avalonia view vložený do shellu nebo oddělený podle jednotného vzoru | session zůstává nezávislá; main close nesmí zahodit archiv |
| C08 | různé pevné rozměry a lokální styly | stejné tokens, kompaktní nav/inspector/overflow, konzistentní focus | nová adaptivní kompozice; bez skrytých příkazů |
| C09 | lokální barvy a rozdílné grafové legendy | společná semantická paleta, text + symbol + legenda, společná čísla/units | vědomá vizuální změna, nikoli algoritmická nebo serializační |

Undo/redo, nový clipboard datový formát, nové formáty média, platformní backendy,
nové kompresní/dekodovací algoritmy a tmavé téma nejsou schválením F2 automaticky povoleny.

## Současné zkratky a tooltipy

[wpf-inputs.csv](wpf-inputs.csv) zachycuje konkrétní zdrojové handlery, zejména:
Tape Ctrl+A/Delete/F2 a Ctrl/Shift výběr; disk Ctrl+C/V/F2 a Escape; mapy Home/+/- a
šipky/Enter; waveform wheel/pan/Shift+drag; live pulse drag/zoom/follow. Cílová pravidla
jsou v INTERACTIONS.md. `InputGestureText` je jen hint, nikoli důkaz funkční vazby.

[wpf-controls.csv](wpf-controls.csv) obsahuje caption/tooltip a dostupný handler ze
XAML i programově vytvářeného UI. Dynamické popisky a conditional capability mají
odkaz na zdroj a vyžadují runtime kontrolu. Tooltip se přenáší obsahově: názvy, jednotky,
riziko a důvod disabled musí zůstat dostupné, ale nemusí zůstat původní nekonzistentní formát.

## Úplná mapa auditovaných UI zdrojů

Následující mapa je generovaná z `source-inventory.json`; související partial soubory
jsou uvedené jednotlivě. Historické `MZTFileReader.cs`, `MZQFileReader.cs` a
`QDFFileReader.cs`, které root projekt nekompiluje, jsou z inventury vyloučené.
Theme registration metadata není samostatná obrazovka.

| Zdroj | Modul | Cílová komponenta |
| --- | --- | --- |
| `App.xaml.cs` | Application | Theme / app resources |
| `AssemblyInfo.cs` | Application | Theme registration metadata |
| `AudioPulseReferenceControl.cs` | Audio / Recorder | PulseReferencePicker |
| `AudioRecorderNavigation.cs` | Recorder | RecordingNavigation |
| `AudioRecorderWindow.cs` | Recorder | RecorderView |
| `AudioRegionRecoveryComparison.cs` | Audio recovery | RecoveryComparison |
| `AudioRegionRecoveryExport.cs` | Audio recovery | RecoveredArtifactExport |
| `AudioRegionRecoveryHeaderless.cs` | Audio recovery | HeaderlessRecovery |
| `AudioRegionRecoveryPreview.cs` | Audio recovery | CandidatePreview |
| `AudioRegionRecoveryWindow.cs` | Audio recovery | RecoveryView |
| `AudioSignalAnalyzerExport.cs` | Audio | ExportPreview |
| `AudioSignalAnalyzerSelection.cs` | Audio | SelectionCoordinator |
| `AudioSignalAnalyzerWindow.cs` | Audio | AudioAnalysisView |
| `AudioSignalCharts.cs` | Audio | SignalPlot / HistogramPlot / DriftPlot |
| `BatchFileDetailsDialog.cs` | Batch | ReportDrawer |
| `BatchProcessDialog.cs` | Batch | BatchWorkspace |
| `BootstrapDialog.cs` | Disk boot | BootstrapEditor |
| `CompressionOptionsControl.xaml.cs` | Shared conversion | CompressionOptions |
| `CompressionSettingsDialog.cs` | Shared conversion | CompressionDialog |
| `CpmLayoutDialog.cs` | Disk filesystem | CpmLayoutEditor |
| `CpmSystemBuilderDialog.cs` | Disk boot | BootInstallPreview |
| `CrossDiskTransferDialog.cs` | Disk transfer | TransferPreview |
| `DiskFormatting.cs` | Disk formatting | ChangePreview |
| `DiskMapVisuals.cs` | Shared | SelectionStyles |
| `DskCompareWindow.cs` | Disk compare | CompareView / HexDiff |
| `DskConversionDialog.cs` | Disk conversion | ConversionExportPreview |
| `DskDiskMapControl.cs` | Disk / HFE | DiskMapRenderer |
| `DskEditorSectorSelection.cs` | Disk / HFE | SelectionCoordinator |
| `DskEditorVisualAnalysis.cs` | Disk / HFE | InspectorCoordinator |
| `DskEditorWindow.xaml.cs` | Disk / HFE | DiskWorkspace |
| `DskFilePropertiesControl.cs` | Disk properties | PropertyEditor |
| `DskFilePropertiesDialog.cs` | Disk properties | PropertiesDialog |
| `DskHexEditorWindow.cs` | Disk hex | HexEditor |
| `DskMfmDetailControl.cs` | Disk physical | TrackCellRenderer |
| `DskNewDialog.cs` | Disk creation | NewDiskDialog |
| `DskPatchPreviewWindow.cs` | Disk changes | ChangePreview |
| `DskPhysicalPropertiesDialog.cs` | Disk properties | PhysicalPropertyEditor |
| `DskRawRangeDialog.cs` | Disk transfer | RawRangeTransfer |
| `DskRepairDialog.cs` | Disk repair | ContainerRepairPreview |
| `DskStructureWindow.cs` | Disk filesystem | StructureView |
| `DskVisualCanvas.cs` | Disk physical | DiskSurfaceRenderer |
| `DskVisualInspectorDetails.cs` | Disk inspection | InspectorDetails |
| `DskVisualInspectorWindow.cs` | Disk inspection | InspectorHost |
| `HeadAlignmentPanel.cs` | Recorder | HeadAlignmentView |
| `HeadEdgeAdjustmentControl.cs` | Recorder | RelativeEdgeDial |
| `HexBrowser.xaml.cs` | Shared hex | ReadOnlyHexView |
| `IplDskOptionsDialog.xaml.cs` | Single IPL | SingleIplOptions |
| `LivePulsePanel.cs` | Recorder | LivePulseView |
| `LiveSignalCanvas.cs` | Recorder | SignalPlot / SpectrumPlot |
| `MainWindow.xaml.cs` | Tape / QuickDisk | DocumentWorkspace |
| `MainWindowQuickDiskVisual.cs` | QuickDisk | QuickDiskWorkspace |
| `MapInteraction.cs` | Shared | SelectionPolicy |
| `MultiGameIplDskOptionsDialog.xaml.cs` | Multi IPL | MultiIplEditor |
| `NativeComDialog.cs` | Native COM | NativeComDialog |
| `PhysicalTrackViewerWindow.cs` | Physical media | PhysicalTrackView |
| `QuickDiskInspectorControl.cs` | QuickDisk hosts | HostInspector |
| `QuickDiskMapControl.cs` | QuickDisk logical | QuickDiskMapRenderer |
| `QuickDiskSurfaceCanvas.cs` | QuickDisk surface | QuickDiskSurfaceRenderer |
| `QuickDiskVisualCanvas.cs` | QuickDisk physical | QuickDiskPhysicalRenderer |
| `QuickDiskVisualInspectorWindow.cs` | QuickDisk inspection | InspectorHost |
| `ReportView.cs` | Shared reports | ReportViewer |
| `SaveOptionsDialog.xaml.cs` | Tape / QuickDisk | SaveExportOptions |
| `SeparateExportDialog.cs` | Shared export | MultiExportPreview |
| `UserProfilesDialog.cs` | Profiles | ProfilesView |
| `WavAnalysisProgressWindow.xaml.cs` | Audio progress | BackgroundTaskProgress |
| `WavAnalysisStatisticsWindow.xaml.cs` | Audio statistics | AudioStatistics |
| `WavImportOptionsWindow.xaml.cs` | Audio import | AudioImportOptions |
| `App.xaml` | Application | Theme / app resources |
| `CompressionOptionsControl.xaml` | Shared conversion | CompressionOptions |
| `DskEditorWindow.xaml` | Disk / HFE | DiskWorkspace |
| `HexBrowser.xaml` | Shared hex | ReadOnlyHexView |
| `IplDskOptionsDialog.xaml` | Single IPL | SingleIplOptions |
| `MainWindow.xaml` | Tape / QuickDisk | DocumentWorkspace |
| `MultiGameIplDskOptionsDialog.xaml` | Multi IPL | MultiIplEditor |
| `SaveOptionsDialog.xaml` | Tape / QuickDisk | SaveExportOptions |
| `WavAnalysisProgressWindow.xaml` | Audio progress | BackgroundTaskProgress |
| `WavAnalysisStatisticsWindow.xaml` | Audio statistics | AudioStatistics |
| `WavImportOptionsWindow.xaml` | Audio import | AudioImportOptions |
