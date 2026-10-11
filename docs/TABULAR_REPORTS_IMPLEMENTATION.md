# Tabular reports and previews — 2026-10-11

The user approved migration of all text-mode reports and operation previews, including copy, patches, repair/defragmentation, verification, boot/CP/M, conversions, QD, batch and inspector details. Existing pending changes in the workspace were retained.

## Shared presentation

`ReportView` is a resizable table-based WPF control with a heading, compact summary and tabs. Tables support column resizing, sorting, multiple cell selection, header-inclusive Ctrl+C, virtualized rows/columns and a persistent inactive selection highlight. Warning/attention rows have text categories and colored edges; selection colors remain readable. No raw report TextBox is included.

`ReportPresentation` separates presentation from operations. Its compatibility adapter projects existing backend report fields, named sections, messages, changed ranges and individual byte changes into tables. Unrecognized diagnostic lines are retained as messages rather than silently omitted. It does not run validation, reinterpret a filesystem, change capability/Apply checks, or derive new integrity results. Generic summaries count report entries/properties/ranges, not inferred successful/failed checks. Backend reports remain authoritative.

The `Text` property retains the original report for existing export/copy callers and tests; assigning it builds tables instead of displaying a terminal-style report. Shared Export report offers the original TXT, table CSV or structured JSON. Existing file export handlers continue to work. Actual HEX buffers, character views, raw byte browser and short error/status messages remain in their appropriate controls.

## Copy preview

Cross-disk copy exposes structured file changes directly from `CrossDiskTransferService.Preview`:

- **Files:** source/target names, copy/rename/replace/skip, payload verification, source/target byte lengths and user areas.
- **File properties:** source/target type, LOAD/EXEC and attributes. CP/M type/LOAD/EXEC are explicitly not represented, rather than shown as meaningful zero addresses.
- **Metadata changes:** existing backend explanations; an explicit empty state if none.
- **Properties / Notes / Changed ranges:** free space, directory capacity, policies, hashes and byte range evidence.

Per-file text is removed from the generic tabs to avoid duplicating the structured tables, while the full original report remains in TXT export. Preview does not modify source or target. Apply retains the existing source fingerprint, interpretation and validation checks; it changes only the target in memory. Save remains separate. Close does not apply.

## Coverage

| Surface | Migration |
|---|---|
| `DskPatchPreviewWindow` | Shared report view for all 18 preexisting callers: cross-disk and tape copy, patch preview/apply, verify DSK/HFE, normalization, defragmentation, repair, bootstrap/raw import, physical properties, CP/M layouts/profiles, physical comparison and HFE inspection/conversion. Existing action labels/callbacks remain unchanged. |
| `DskConversionDialog`, `NativeComDialog` | Embedded conversion/technical reports use tables; availability messages and export behavior remain. |
| `CpmSystemBuilderDialog` | Compatibility, preflight, installation and technical reports use the shared control. Existing ReportText/export remain compatible. |
| `BatchFileDetailsDialog` | Technical details are tabular too; existing contents/properties/messages/check tables remain. Removed the obsolete instruction to select a separate integrity operation. |
| `QuickDiskInspectorControl` | Container/analysis report and selected-unit details are tabular; decoded export and Hex Browser remain. |
| Main QD map | Selected region details use the shared control. |
| DSK editor, structure, compare and visual inspector | Sector/structure/left-right/issue details use tables. Existing graphical maps and shared selection/dirty-hex checks remain. |
| `PhysicalTrackViewerWindow` | Selected sector/region details use tables. Physical map and source provenance remain. |
| `DskHexEditorControl` | Byte diff/impact becomes a report with a Byte changes table. Actual original/editable HEX buffers remain. Filesystem-impact confirmation now opens a table-based review dialog; explicit acceptance is still required. |
| `SaveOptionsDialog` | Detailed compression preview uses tables with bounded width and height. Existing compression controls and preview progress remain. |

Already tabular audio import summaries, batch contents/checks and visual/manual audio analysis tables were retained. Their explanatory text and brief status messages are not terminal report views. Editable paths, selected file paths, names, reference values and address inputs remain input controls.

## Verification

```powershell
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Debug --no-restore --filter FullyQualifiedName~ReportPresentationTests --verbosity minimal
dotnet run --project MZTool.Tests/ReportUiSmoke/Smoke.csproj -c Release
dotnet run --project MZTool.Tests/DskVisualUiSmoke/Smoke.csproj -c Release
dotnet run --project MZTool.Tests/AudioSignalUiSmoke/Smoke.csproj -c Release
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Release --no-restore --verbosity minimal
dotnet build -c Release --no-restore
```

Six new backend tests pass: report/unknown-diagnostic preservation, exact range and byte-change columns, empty states, 5,000 ranges, typed copy rename/skip, metadata visibility and unchanged preview documents. STA report smoke checks actual WPF cell binding, no raw report TextBox, columns/sorting/copying, TXT/CSV/JSON, preview read-only behavior, Apply/Close and source preservation. Screenshot: `%TEMP%/MZTools-ReportUiSmoke/cross-disk-copy.png`.

DSK inspector STA smoke and audio analysis STA smoke also pass. The full Release suite passes **1,077 tests, 3 existing skips, 1,080 total** (64 s). No new tests are skipped. The existing skips are two CP/M system-reference cases and one QD host-reference case. Final presentation-only sizing/summary refinements are rechecked with report/UI tests and Release build.

## Compatibility limits

Most backends still provide their established textual report contract. The shared adapter makes this tabular without changing that contract; these reports have property/message/section tables, rather than pretending every free-form sentence is a typed file record. Cross-disk file and metadata tables use actual backend data. Existing report truncation limits, if any, remain explicitly reported. No raw text-mode report pane is offered as a fallback.
