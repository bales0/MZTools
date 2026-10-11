# Save As destination audit — 2026-10-11

The physical HFE inspector previously combined conversion/export and Save As in
one command. It wrote a new image without changing its captured original image or
path. Formatting afterward adopted that original path; a subsequent Save could
therefore replace the original while the exported copy remained unchanged.

The inspector now has a separate Save As command backed by `HfeDocumentSession`.
Only a successful verified write advances both the active HFE and absolute path.
Format and compare read that session at invocation time. The window title displays
the active path. Conversion commands are explicitly named Export converted image;
they retain the active source and refuse its path as a conversion destination.

## Project-wide review

| Entry point | Destination and active document |
| --- | --- |
| Physical HFE inspector Save As | Verified byte-identical save in the current HFE version; switches active image/path. Format subsequently adopts the new path. |
| DSK/HFE editor Save As, including IPL editors | `DskDocument.Save(destination)` writes and verifies output before updating FilePath, physical baseline and dirty state. Paths are now normalized to absolute paths. |
| Main window native tape/QD Save As | MZF, M12, MZT, MZQ, QDF and all three QD writers call SetCurrentDocumentAfterSave only after successful output. Sidecar binding and QD source snapshot follow the new file. |
| Main window waveform/IPL generation | Previously offered in Save As without switching the active document. These formats now belong to the existing Export flow; the Save As filter offers only formats that can bind the current tape/QD document. Typing an export-only extension is rejected before writing. QD filter indices remain 3/4/5; M12 moves to 8. |
| DSK filesystem conversion and DSK→HFE | Separate output with source-path protection; labels now say Export converted image instead of Save As. The source document remains active. |
| HFE variant conversion / HFE→DSK | Separate export with source-path protection; no change to active image/path. |
| Sector/range/region/file exports in DSK/QD views | Explicit exports of selected content; no document rebinding. |
| Audio waveform selection, recovered program/candidate, synthetic-header MZF exports | Explicit exports; no document rebinding. |
| Reports, comparisons, batch catalogs, patches, bootstrap and native COM exports | Explicit artifacts; no document rebinding. |

The audit covered Save As handlers and every SaveFileDialog site in project C#
sources, including all export dialogs above. No user sample image was overwritten
as part of the investigation or regression checks.

## Save state

Main tape/QD and disk filebrowser Save buttons require the existing modified flag
and a supported bound path. IPL Save also requires valid prepared content and no
pending layout generation. Save As remains available on clean documents. Successful
save refreshes the UI with the cleared flag; cancellation and write failures do not
clear the flag. Save tooltips display the current destination.

## Verification

`SaveAsDestinationTests` covers all HFE versions through Save As → CP/M80 format →
Save, untouched original bytes, DSK/HFE edits after Save As, failed-write destination
and dirty-state retention, native filter mapping, and clean/dirty IPL button policy.
Existing HFE, native tape, QD and sidecar regression tests cover serialization and
metadata bindings. WPF smoke tests exercise the actual physical inspector Save As
and title, HFE Save button transitions, native tape/QD save handlers and subsequent
Save for every writable tape/QD document format, cancelled tape options and failed
writes. All test output images are confined to temporary test directories.

Validation result: 92 focused regression tests passed (no skips), the expanded WPF
smoke run passed, and the Release build completed with zero warnings and errors.
The verified build is in `bin/Review/Release/net10.0-windows`.
