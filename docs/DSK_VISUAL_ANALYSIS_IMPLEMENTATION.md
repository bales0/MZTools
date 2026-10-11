# DSK visual analysis implementation — 2026-10-10

Baseline HEAD: `2e575acc5b6e68f4cfe8f93880c626767ebd5013`. Existing local audio changes were retained; the working tree was not reset or updated from the network. The complete DSK inspector scope was approved by the user before implementation.

## Existing coverage and reuse

| Existing code | Responsibility retained |
|---|---|
| `Dsk/DskImage.cs` | Extended CPC DSK parsing, raw descriptors, serialization, missing physical slots. Added raw rate/mode getters and private inspection clones; parsing/writing rules are unchanged. |
| `Dsk/DskAnalyzer.cs`, `Dsk/DskLayoutModel.cs` | All container checks, filesystem interpretation, confidence findings, logical/allocation mappings, owners and issues. The inspector consumes the editor's existing layout; it never runs a second filesystem analysis. |
| `DskDiskMapControl.cs`, `DiskMapVisuals.cs` | Existing map stays available; shared sector identity, metadata/data/selection colors and related-file outline semantics. |
| `DskEditorWindow.xaml(.cs)`, `DskHexEditorWindow.cs` | Existing Files/Blocks/Issues selection and editable Hex remain authoritative. Inspector requests go through `SelectLayoutSector` / `ConfirmHexSwitch`. Existing Issues navigation now checks dirty hex before clearing selection, and track-level issues do not pick an arbitrary sector. |
| `DskStructureService`, physical-property, raw-range and repair services | Existing inspection/edit/repair workflows remain separate. No calls from the new inspector to write or repair services. |
| `FloppyTrackCodec`, `PhysicalTrack`, physical HFE viewer | Existing encoder/decoder produce and validate generated MFM. Optional cancellation was added to the codec; HFE data meanings and default encoding remain unchanged. |
| `DskIssueHelp`, compare and Extended-length repair | Existing issue explanations are reused. No new diagnostics or repair heuristics were introduced. |

Missing presentation was supplied by `DskVisualCanvas.cs`, `DskVisualInspectorWindow.cs`, the editor partial `DskEditorVisualAnalysis.cs`, and the projections `DskVisualModel.cs`, `DskGeneratedProjection.cs`, `DskVisualReport.cs`.

## Entry points and interaction

In the DSK editor, **Tools → Visual analysis… / Track / Sector View… / Disk Surface…** opens one resizable modeless inspector per editor. The same commands are available in the context menus of Disk Map, Blocks, Issues and Files (including IPL files).

The inspector now contains Disk Surface (first/default), Track / Sector, Issues, Statistics and Tracks. Disk Surface includes compact summary counts; its findings count opens Issues with filters cleared. Selection/finding details use a modeless floating window, and generated MFM uses the surface's unrolled pane exclusively. Track/Sector and the MFM pane have draggable dividers. Tables are read-only, sortable, resizable and virtualized. Inactive selections remain highlighted. CP/M owners include user 0–15. Earlier sections below retain the implementation history; the final consolidation supersedes previous tab layouts.

Click a sector to select it in the editor. Double-click or Enter opens the existing Hex workflow; the inspector itself does not edit. Left/Right move between descriptors, Home fits, +/- or wheel zooms, and dragging pans. **Jump to issue** fits the descriptor view, including missing/empty tracks. **Select owner file** lists all owners instead of choosing one implicitly. Files/Blocks selection highlights related sectors across views. A rejected dirty-hex switch leaves the previous selection and edit buffer intact. Accepted discard reverts only the unapplied UI buffer; it does not write the image.

The circular surface uses physical container slots, with C0 outermost and descriptor order clockwise from 12 o'clock. Side selection is independent of declared track C/H, so malformed metadata does not move a sector to another ring. Missing/empty tracks have dashed rings and no invented sectors. Spindle, outside area and descriptor gaps do not select arbitrary sectors. Sector identity is `(physical track, descriptor index)`, preserving duplicate CHRN entries.

## Truth layers and reporting

- **CONTAINER / DESCRIPTOR ORDER:** original CHRN, stored flags, declared/stored lengths, descriptor/data/end offsets, padding, trailing bytes and descriptor sequence. Interleave is described as inferred descriptor order, never measured rotation.
- **LOGICAL / FILESYSTEM:** only mappings and issues from the existing analyzer. Overview aggregates severities, side/track findings, sizes, roles, stored flags and mapped allocation IDs. Allocation summaries do not infer a capacity or independent free-space map. Existing overlap/confidence findings remain in Issues.
- **SYNTHETIC / GENERATED MFM:** generated on demand by `FloppyTrackCodec.Encode`; region offsets come from the resulting encoded track. GAP, sync, IDAM, CHRN, header CRC, DAM/deleted DAM, DATA and data CRC use generated cell positions. Nominal bit rate and 300 RPM describe synthesis. Generated CRC validates generated bytes, independently of stored ST1/ST2.

Surface provenance and the common legend stay outside the zoomable canvas. Generated MFM has a permanent synthetic label. Flux, actual rotational timing, physical CRC and weak bits are **Not available in DSK**; zero status flags never imply measured integrity.

**Export report…** writes TXT, CSV (track/sector/issue rows) or JSON with units and provenance. All formats explicitly distinguish schematic/container data from synthetic MFM. Reports describe MFM availability on demand; they do not claim to include a captured/generated track dump. Export works in the background, the common **Cancel operation** button is accessible from every tab, and a temporary file protects an existing destination until completion.

## Performance and lifecycle

One custom WPF drawing element per canvas; no element per sector or bitcell. Frozen circular geometries and the polar hit-test model are cached; off-viewport sectors are skipped during drawing. Work is bounded by the parser's 204 physical tracks and 29 descriptors per track. Tables virtualize rows/columns. No full bitcell graphic is built.

Snapshots copy sector bytes, metadata and ownership. A separate private raw clone supplies the existing MFM encoder: serialization is deliberately not required to inspect a malformed in-memory track. Reanalysis replaces the snapshot, invalidates geometry/generated caches and cancels pending work. Version/track checks discard stale generation results. Window closure cancels background work. JSON serialization is bounded by the snapshot and cancellation is checked before writing its result; it is not interrupted inside the serializer itself.

## Validation

Baseline, before the new DSK implementation:

```powershell
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~Dsk|FullyQualifiedName~Cpm|FullyQualifiedName~Hfe' --verbosity minimal
```

418 passed, 2 existing skips, 420 total (39 s).

New backend tests:

```powershell
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~DskVisualAnalysisTests --verbosity minimal
dotnet run --project MZTool.Tests/DskVisualUiSmoke/Smoke.csproj -c Release
dotnet build -c Release --no-restore
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Release --no-restore --verbosity minimal
```

14 new backend cases pass. They cover disordered/duplicate IDs, exact offsets/bytes, missing and empty tracks, zero declared length, stored FDC flags, mixed sizes, padding/trailing bytes, track C/H mismatch, malformed in-memory lengths, snapshot isolation, one/two sides, polar inverse hit-testing, descriptor gaps, generated payload/CRC/region offsets, cancellation and unknown ownership. The 204 × 29 case checks a 15-second/256-MB bound. Three optional real-image cases were executed locally with `P-CPM80.dsk`, `multi2.dsk`, and `CPMv41 Hry COM A.dsk`; byte preservation and agreement with the existing analyzer pass. When those optional repository fixtures are absent, those cases return without claiming fixture coverage; synthetic cases always run.

STA UI smoke passes shared selection, rejected dirty hex navigation (both inspector and existing Issues), accepted navigation, missing-track issue navigation, pan/zoom/Fit, side/filters, resizable read-only tables, actual Generated MFM, report formats, cache invalidation and unchanged document bytes/modified flag. A surface screenshot is written to `%TEMP%/MZTools-DskVisualUiSmoke/surface.png`.

Release build succeeds with zero warnings/errors. The full Release suite passes: **1,071 passed, 3 existing skips, 1,074 total** (65 s), including DSK/CPM/IPL/HFE and audio regressions. The skips are the two existing registered-CP/M-system cases (`DescriptorChangesNeverRemainRegisteredOrInstallIntoCanonicalTarget`, `RealRegisteredSystemsRemainIdentifiedWithDifferentComAndGameFiles`) and the existing QuickDisk host-reference case. No new tests are marked skipped. The final accepted-discard UI adjustment was separately rechecked by the STA smoke.

## Limits

The existing parser still rejects fundamentally invalid on-disk lengths/signatures before an editor document can be opened. This change does not introduce a permissive alternate parser. Parseable anomalies and malformed in-memory sector metadata remain inspectable; unsupported generated tracks report a reason while the descriptor view continues to work. Generated MFM currently accepts the codec's supported N=0…3 with matching payload lengths. This inspector does not add flux capture, weak-bit simulation, repair/editing, PNG export UI, or new filesystem detection. Schematic sector weights use a minimum visible unit for zero-byte sectors, rather than claiming a physical duration.

## Approved graphical and selection refinement — 2026-10-11

Overview now shows an interactive circular surface alongside statistics/tracks and summary cards. Technical sector details collapse independently. Disk Map's right-hand Blocks/Issues list and details share constrained star rows with a draggable horizontal divider; collapsing details returns their space to the list. The UI smoke checks the list's bounds at a 600-pixel window height.

`DskSectorSelection` defines Ctrl toggle, Shift range and Ctrl+Shift extension in descriptor display order. Track/Generated views range within the physical track; Surface ranges over the displayed side; Disk Map uses its physical/logical display order. Shared selection stores physical descriptor addresses, so duplicate sector IDs remain distinct. Extended selection in Blocks and the inspector sector table synchronizes with every graphical view. Escape and blank-map clicks clear through the dirty-hex guard.

File selection highlights every mapped physical sector. Sector selection highlights related file rows and adds a Selected sectors column with selected / total physical sector counts and All/Partial coverage. Logical fragments belonging to the same physical sector count once. Related highlighting does not select files for delete/export commands. Sector changes, clear and rejected hex discard preserve the complete previous selection and pending hex buffer. Opening Hex from an already selected sector retains the remaining selected sectors.

Two backend tests cover toggle/ranges and partial/full ownership counts. The expanded STA smoke covers multi-selection across editor/inspector, Ctrl/Shift, file-to-sector selection, partial row coverage, dirty-switch and Escape rejection, immutable bytes, compact layout and detail collapse. Screenshots: `%TEMP%/MZTools-DskVisualUiSmoke/disk-map-selection.png` and `graphical-overview.png`.

Release builds with zero warnings/errors and the expanded Release STA smoke passes. The running application holds the usual Release DLL, so the review build is `bin/Review/Release/net10.0-windows/MZTools.exe`. Some existing fixture tests compute repository paths relative to their output directory; running the full suite in this extra output level mislocates fixtures. Full regression verification therefore uses the normal Debug output layout.

Full Debug regression suite: **1,079 passed, 3 existing skips, 1,082 total**, zero failures (1 min 46 s). The final Escape guard is additionally exercised by the passing Release STA smoke. `git diff --check` passes.

## Approved Both/All sides and Overview findings correction — 2026-10-11

The previous All sides option removed the side filter, but the surface renderer still drew only the active track's side. It now passes an explicit all-sides mode to the canvas. Rendering and hit testing divide the available width into separate labeled surfaces, with independent physical-side models and geometry caches keyed by layout/side. Single-side mode occupies the full area. Pan/zoom applies to the whole canvas and inverse hit testing uses the matching side center/radius. The same behavior applies in Overview and Disk Surface. Single-sided images retain one surface.

Overview lists every finding counted by its summary card, including informational and global findings. This table deliberately shows the complete snapshot instead of inheriting the Issues tab's filters. Severity, location and description are immediately visible. Selection expands the explanation; double-click navigates through the existing dirty-hex guard, or shows global details without choosing an arbitrary sector. The findings card focuses the table. The findings area shares height with the graphical overview and remains inside a 600-pixel window with details expanded.

Validation: **53 targeted Debug tests pass** (`DskAnalyzerTests`, `DskVisualAnalysisTests`, `DskSectorSelectionTests`). Release STA smoke passes simultaneous-side hit identity in both views, explicit side selection, Ctrl selection across sides, rejected dirty-hex switching across sides, inverse hits after zoom/pan, spindle exclusion, single-sided images, Overview finding count/detail/global navigation, track-level navigation and compact layout. Existing generated-MFM, byte-preservation, ownership and selection checks also pass. Release review build has zero warnings/errors. Updated screenshots are written to the existing smoke artifact directory.

## Approved full-page layout redesign — 2026-10-11

This supersedes the earlier embedded Overview surface and bottom detail expander. Bounds-only checks had missed the unusably small row viewport beneath the fixed 180-DIP detail/report headers. The inspector now has a four-row root: short summary, grouped/wrapping toolbar, one expanding tab area and short status. Initial dimensions are capped to desktop work area. Provenance, legend and interaction help remain in tooltips.

Overview contains summary cards and a full-width/full-height findings table. Disk Surface owns the circular graphics. Selection details, Statistics and Tracks have separate full-page tabs; opening an explanation no longer subtracts height from every other view. Finding details buttons open the dedicated details tab, while double-click still navigates to Track/Sector through the dirty-hex guard. Global findings open details without inventing sector coordinates. Sector selection remains synchronized even when switching pages.

Track/Sector and Generated MFM share a reusable grid with two star rows and an explicit PreviousAndNext row splitter. Tables initially receive twice the graphic height. `ReportView.CompactHeader` hides the redundant count summary, reduces the title size and gives property names a 220-DIP column; its default presentation and TXT/CSV/JSON exports remain unchanged. Statistics use a 180-DIP category column and 2:3 weighted value/explanation columns with wrapping. Detailed tracks/sectors retain horizontal scrolling and manual column sizing.

Validation: **59 targeted tests pass**, including DSK analyzer/projection/selection and report presentation. Release DSK STA smoke verifies at least six completely visible data rows (or all rows for shorter reports), a usable viewport, full-width tables, property-name width and statistics column widths at a compact logical size with simulated 100/150/200% layout scaling. These are layout simulations, not a physical-monitor DPI switch. It exercises real WPF splitter DragStarted/DragDelta/DragCompleted events and verifies increased table space. Both split views, desktop size limits, full-page explanations, navigation, cross-side Ctrl selection, dirty hex cancellation and immutable document bytes pass. The existing Report UI smoke also passes default report rendering, export and Apply/Close behavior.

Screenshots in `%TEMP%/MZTools-DskVisualUiSmoke`: `details-full-page.png`, `statistics-full-page.png`, `track-full-page.png` and the retained `graphical-overview.png` filename (now summary/findings only). Review executable: `bin/Review/Release/net10.0-windows/MZTools.exe`.

## Approved floating details and progressive MFM surface — 2026-10-11

This follow-up restores the interactive both-side surface to Overview, above findings with a draggable divider. It preserves the full-page detail/statistics/track tabs and compact report headers from the previous redesign. The findings viewport has an explicit minimum, and UI checks verify that its rows remain inside the window.

Floating details is an owned, resizable, modeless window. It mirrors the current selection report without activating or blocking the map and preserves manually chosen position and size. Closing it disables automatic reopening; the toolbar button or checkbox enables it again. CP/M user numbers appear in owner labels, filters, sector tables, selected-owner summaries, tooltips and reports. Equal filenames in different users remain distinguishable; non-CP/M ownership does not acquire an invented user 0.

Selecting a sector and enabling Unrolled MFM detail, or zooming the surface to 6×, requests that physical track from the existing encoder. Its circular band uses generated cell positions for GAP, sync, address marks, header, data and CRC; other tracks remain schematic. The adjustable unrolled pane progresses from colored regions to decoded HEX bytes and then individual clock/data cells. Values come from actual encoded cells, with guarded descriptor navigation, pointer-anchored zoom, drag pan and fit track/sector. These are synthetic nominal-RPM cells, not captured flux or original physical CRC evidence. The approach follows the decoded-byte/track-view principles described by the [HxC author](https://hxc2001.com/floppy/forum/viewtopic.php?t=4272), without importing HxC code.

Generation is debounced, cancellable, serialized and lazy, with an eight-track snapshot-scoped LRU cache. Track/version checks discard stale results; snapshot replacement and window closure cancel pending work. Drawing visits only visible byte/cell ranges instead of constructing a full-track bit graphic.

Validation: **62 targeted Debug tests passed**, covering analyzer/projection/selection, report presentation, cache reuse/eviction/cancellation, actual encoder header/payload bytes and identical filenames across all CP/M users 0–15. Release DSK STA smoke passed live modeless detail updates and preserved position/size, users 0/15, restored Overview hit testing, circular and unrolled MFM selection, stale-generation/cache invalidation, immutable source bytes and readable tables at simulated 100/150/200% scaling. Twenty deep-zoom renders remained below the smoke's five-second bound; visible byte/cell counts are bounded by viewport width. Existing Report UI smoke also passed. Screenshots include `graphical-overview.png` and `mfm-deep-detail.png` in the smoke artifact directory. These checks simulate layout scaling rather than switching a physical monitor's DPI.


## Approved tab consolidation — 2026-10-11

Removed Overview, Generated MFM and Selection details tabs and their unused duplicate canvases, table and generation handler. Disk Surface is first/default with compact sector/track/missing/finding counts. Its findings button opens Issues and clears filters so all counted findings are accessible. Existing editor entry points open the surface. Navigation indexes, Shift-range selection and Statistics/Tracks routing follow the five-tab layout.

Selection and finding explanations open only in the modeless floating window. Open Hex and Select owner file actions moved into that window. It retains position/size, live CP/M user information and close-to-disable behavior. Surface MFM generation, cache, cancellation and unrolled HEX/bit-cell detail remain available; changing track clears the obsolete circular MFM projection before requesting another.

Validation: Release DSK STA smoke passed an exact five-tab/order assertion, default surface and four summary counts, floating finding explanation without a tab switch, both sides, multi-selection, dirty-hex guard, users 0/15, generated circular/unrolled MFM, stale cache invalidation, bounded rendering and table readability at simulated 100/150/200% scaling. Release review build and whitespace check are run after these changes.


## Approved Tools consolidation — 2026-10-11

Tools now contains Visual analysis, Export / Import Selected Sectors and MZF/M12 → COM. The standalone Hex view, Track / Sector View, Disk Surface and Physical Properties menu entries and their obsolete handlers were removed. Inspection context menus likewise expose one Visual analysis shortcut. Main-editor Hex remains available. The inspector uses its Floating selection details checkbox without a duplicate toolbar button; finding explanation actions still open the modeless report.

Release DSK STA smoke passed explicit assertions for the Tools menu contents and the checkbox without a Floating details button, plus existing selection, dirty-hex, users, MFM, rendering and layout checks. The review executable remains bin/Review/Release/net10.0-windows/MZTools.exe.


## Approved contextual sectors and COM collisions — 2026-10-11

Tools now contains only Visual analysis and MZF/M12 → COM. Sector export/import moved into Disk Map/Blocks and inspector surface/track/sector/MFM context menus. The action passes the shared physical selection into DskRawRangeDialog rather than requiring selection from scratch. Pending hex edits are guarded and read-only disks disable raw import. Main Add/Export/Delete commands disappear outside Files and when browsing raw-sector entries.

NativeComImportService previews default to collision refusal; explicit Replace or Rename policies operate on a clone and preserve reopen/payload validation. User-0 collisions enable the Import button and present Overwrite / Rename / Cancel on click. Rename suggests a free 8.3 name and accepts an edited name without silently overwriting another collision. Protected files cannot be replaced. Equal names in other CP/M users remain unchanged; canceled/failed previews cannot modify the source disk.

Validation: 42 NativeComTests passed, including transactional replacement/rename, suggested-name collisions, user-15 preservation and protected-file refusal. Release DSK STA smoke passed Tools contents, contextual sector action, hiding/restoring file toolbar actions, two-sector dialog preselection, all three collision dialog decisions and existing navigation/MFM/layout/dirty-hex checks.


## Approved integrated verification and QD detail layout — 2026-10-11

Removed the standalone Verify Image item/handler from the DSK Disk menu. Existing open/refresh parsing, DskAnalyzer and DskBootInfo inspection remain the shared validation path. Refresh computes the current serialized-image SHA-256 and Creator-padding canonicality; the inspector Statistics and disk-information tooltip expose both. The main info panel gains no extra height. Inspector updates replace these rows rather than accumulating duplicates, and hashes include current unsaved bytes. Normalize remains explicit. ImageVerificationService remains available for batch verification and existing HFE workflows/tests.

QD Disk Map no longer forces its right report into 110 DIP. Blocks and details use 2:3 star rows, minimum heights and an explicit PreviousAndNext horizontal splitter. The detail ReportView uses CompactHeader and opt-in SingleLineHeading (ellipsis with full-title tooltip); other report views preserve their default wrapping. Tables scroll within the constrained pane.

Validation: 59 targeted QuickDiskMapTests/PhaseEWorkflowTests passed. Release DSK STA smoke passed removed-menu and current-hash/statistics checks, existing DSK layout/navigation checks and actual QD detail viewport/in-window bounds at simulated 100/150/200% scaling, splitter resizing and source preservation. Release Report UI smoke passed default table presentation, bindings, export, Apply/Close and unchanged source. Scaling checks are layout simulations, not physical-monitor DPI changes.

## Approved MFM only in the lower panel - 2026-10-11

This supersedes the generated circular-band projection described above. Disk Surface keeps descriptor-based sector wedges, role/selection colors and descriptor hit mapping at every zoom. Generated GAP/sync/header/data/CRC details and bytes/cells appear only in the lower unrolled MFM panel. Loading or clearing generated output must not change the circular map. Existing lower-panel generation/cache/cancellation and navigation remain intact. The STA UI smoke compares the rendered circle with and without generated cells at large zoom and verifies stable sector hits and the available lower detail.

## Approved graphical sector transfer and blue selection - 2026-10-11

Selected Sectors Raw Export / Import now uses a colored physical-sector map above a full-width extended-selection table, separated by a draggable row divider. Header totals track sector/byte/track counts. Physical descriptor and within-track sector-ID transfer orders are named explicitly; the table follows the chosen transfer order and column sorting cannot silently change that meaning. Map and table selections synchronize through existing Ctrl/Shift range logic, with clear selection and Esc support. Selected sectors are scrolled into view. Export/import are disabled for empty selections; read-only images allow export only. Existing raw-byte service, exact-length validation, filesystem-impact preview/confirmation, transactional apply and editor dirty-hex guard are retained. Selection frames use blue rather than white; circular selection preserves the sector role fill and outlines in screen-sized blue, including deep zoom.

UI validation covers preselection, synchronized map/table Ctrl/Shift, selected byte totals, transfer order, disabled empty actions, read-only export/import behavior, source preservation and readable map/table layouts at simulated 100/150/200 percent scaling. Screenshot: sector-transfer.png in the existing STA smoke artifact directory. QD selected-region export is explicitly verified to remain context-menu-only; report export is a separate action.

## HFE and deselection — 2026-10-11

Background clicks now clear selection with the pending HEX guard; drags do not.
The shared inspector also hosts HFE stored cells; see [HFE implementation](HFE_VISUAL_ANALYSIS_IMPLEMENTATION.md).
