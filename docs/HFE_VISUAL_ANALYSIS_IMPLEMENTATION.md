# HFE visual analysis and variants — 2026-10-11

Approved scope: DSK outside-click deselection, black QD spiral gaps, HFEv1/v2/v3
read/write/conversion/batch and the shared Visual Inspector.

## Source specification

[Official HxC specification](https://hxc2001.com/floppy_drive_emulator/HFE-file-format.html)
and primary HxC sources (`hfe_loader.c`, `exthfe_writer.c`, `hfev3_loader.c`,
`hfev3_writer.c`, `hfev3_trackgen.h`) were checked. v1/v2 share HXCPICFE with
revision 0/1; v3 uses HXCHFEV3 revision 0. The nominal v3 divisor follows the
reference implementation's 36 MHz clock / (2 × divisor). See THIRD_PARTY_NOTICES.

## Preservation

Same-version copy returns original bytes including unused header/padding bytes.
Forced rewrite verifies reopened cells, weak masks, index, effective v3 timing,
encoding, RPM and v2 controls. V2 instructions are bit-reversed on disk, four bytes,
and never generated across 256-byte side-buffer boundaries. BREAK BUFFER skips
padding without creating cells. Weak values are deterministic inspection samples.

V2 BAUDRATE stores a PIC SPBRG register; no oscillator is specified in the public
format description. Registers, reserved bytes, positions, index states and WRITE
MODE are retained exactly. Local Hz is deliberately not inferred from SPBRG.
Cross-version conversion is refused when controls have no faithful equivalent.
V2 output requires byte-aligned cells/index and whole weak bytes, without v3 timing
divisors. V1 requires common bitrate, equal side lengths, no weak bits or variable
timing. Unknown encoding permits raw inspection and original copy only; no guessed
FM/MFM decoder or encoder is attached. These are intentional format/preservation
limits rather than silent fallback. There is no writable magnetic-cell editor.

DSK conversion uses the existing synthetic MFM encoder. HFE→DSK explicitly reports
loss of raw cells, gaps, encoding, physical offsets, timing, RPM, index, weak data
and v2 controls, and refuses tracks without complete supported decoded sectors.
Save As is atomic, verified, and cannot replace the source path.

## Shared UI

HFE uses DskVisualSnapshot with a physical source and the existing inspector.
Surface and Track/Sector share multi-selection and blue borders. The lower detail
loads existing physical tracks through the bounded track cache; it never calls the
DSK encoder for HFE. Decoded field regions use real HeaderCell/DataCell positions,
including wraparound. HEX appears only in known fields at their actual byte phase;
unknown regions retain raw signal inspection. Orange marks weak cells; magenta
marks index/timing/control offsets. Surface angles remain schematic.

CRC columns are actual recalculated header/data CRC outcomes. Floating details
show encoding, cells, weak counts, declared rate/RPM, index and control counts.
Statistics and TXT/CSV/JSON exports distinguish HFE stored cells from DSK synthetic
MFM and identify track-cell units. Files attach only from a validated CRC-valid,
weak-free private Sharp filesystem projection. CP/M user values are preserved.
Files and sectors highlight each other; context menus export native files or
selected decoded sector bytes. HFE editing does not expose the private projection.

Batch offers HFE, HFEv2, HFEv3 targets and HFE catalog/integrity/conversion/extract.
Unsupported semantic operations remain limited to DSK.

## Other requested UI changes

DSK background clicks clear the selection through the same navigation and pending
HEX guard as sector changes. Drags pan without clearing. Esc remains supported.
QD spiral ribbons now render over a black disk, making the turn gaps black while
keeping the outside and spindle background unchanged.

## Verification

Independent v2 vectors cover all seven instructions, truncated/unknown opcodes,
BREAK BUFFER padding, control retention, exact copy, verified rewrite and refused
lossy cross-version conversion. v1/v2/v3 generated images reopen with preserved
sector payloads/cells. Existing independent v1/v3, FM, unknown encoding, CRC and
filesystem tests remain included. Batch tests cover v2 catalog, conversion and
extraction without changing sources. Shared-inspector tests verify physical source
identity, cell ranges, field projection, reports and CP/M user ownership.

WPF smoke tests exercise all three versions, actual lower cell signal and HEX,
source-byte identity, dirty-HEX background deselection, black QD gaps, existing
selection/navigation, viewport render bounds and 100/150/200% layout scaling.
Screenshots are emitted to the temporary MZTools-DskVisualUiSmoke directory.

## Editable disk documents and formatting — 2026-10-11

Recognized CRC-valid, weak-free HFE files now open in the main disk filebrowser.
`DskDocument` keeps its sector projection for filesystem/boot services and retains
`SourceHfe` plus the original projection baseline separately. `Serialize()` remains
sector-container output for existing services; `SerializeContainer()` prepares the
physical HFE for Save. Clone/Reopen preserve the physical source and baseline.
Save/Save As retains the HFE version and uses existing atomic verified output;
source/baseline/dirty state advance only after successful write. A .dsk destination
is refused for an HFE document; conversion remains an explicit operation.

A sector edit writes MFM/FM data and recalculated CRC directly into existing cells,
including the next MFM clock dependency. Sector identities, lengths and FDC status
cannot silently change; geometry changes require explicit Format. Index, timing,
weak masks, cell count and gaps remain unchanged. Changed tracks are decoded and
checked against every intended sector payload and CRC before serialization; the
whole output is reopened and physical metadata verified. Untouched cylinder blocks
are copied exactly. Untouched sides in a changed cylinder retain original streams
where possible (v2 streams must be instruction-aligned if padding changes).
Prepared output is cached by projection fingerprint to avoid repeated re-encoding
on UI refresh. Physical Visual Analysis follows the current edited HFE cells.
Write-protected HFE is browsable with editing disabled.

`DiskFormatting` uses existing validated disk factories and `DskNewDialog` geometry
controls. Existing DSK (including raw/read-only filesystem), recognized HFE and
unknown HFE in the physical inspector can be reformatted. The complete intended
output is built and validated before the preview opens. An explicit erase confirmation
is required to replace the in-memory document. No file is written until Save.
Format deliberately replaces all tracks/boot/files/timing/weak regions with generated
MFM; v1/v2 use one common bitrate, v3 may retain generated per-track rates. Filesystem
formatting is distinct from installing CP/M; existing compatible Boot / System
services work on HFE projections and write back through the same sector updater.

Tests cover file insertion/rename/delete and repeated Save for all three versions,
byte-identical untouched cylinders, CRC, variable timing/index retention, exact cells
outside changed fields, boot-system installation with CP/M user 15 retained, common
formatting with changed geometry/filesystem, raw DSK formatting, protected HFE,
and atomic rejection of unintended geometry changes. WPF smoke tests open all three
HFE variants through MainWindow, verify editable filebrowser/Format commands, save
back as HFE and open physical Visual Analysis on the saved document.

## New images and IPL — 2026-10-11

The shared New disk dialog separates container (DSK/HFE/HFEv2/HFEv3) from
filesystem/geometry. All eleven DSK presets remain available for each container.
`DiskFormatting.CreateNew` uses the same factories as DSK and adds a physical
container with no source path. Custom geometry, sector order/IDs and filler pass
through unchanged. The Format existing disk dialog hides the new-container control
and retains the existing document's container as before.

Single and Multi IPL drafts retain the selected HFE version before the first
program is added. First build creates the physical container; subsequent rebuilds
use the existing physical sector updater and retain the save destination. Save As
offers HFE for those documents. Their verified sector projections can reopen in
the main dedicated IPL editor: generic filesystem read-only status alone no longer
rejects supported IPL filesystems. CRC/weak-cell, identity and analyzer-error checks
remain enforced; multi IPL must support configuration. Physical write protection
still prevents changes. The generated multi IPL menu uses `MZTools Multiboot`.

Regression coverage compares every preset's decoded HFE sectors against its DSK
source for all three HFE versions, checks custom IDs and filler, and saves/reopens/
rebuilds Single and Multi IPL while comparing all sector data and CRCs (including
boot/menu data). WPF smoke checks the actual New disk dialog and main-window path,
all four container choices and all eleven presets, and both IPL editor modes for
HFEv1/v2/v3 through first build, rebuild and Save. These are data/structure/UI checks;
an external emulator boot session is not part of this validation.

Validation: 148 focused regression cases passed in Release (no failures or skips),
including all HFE variants/presets, IPL round trips, existing HFE/DSK tests and Save
As protection. The expanded WPF smoke run passed. Release build: zero warnings
and errors. Sample `DSKA0000.HFE` and `converted.hfe` were not modified.
