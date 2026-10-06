# MZTools

MZTools is a utility for converting, inspecting and editing SHARP MZ QuickDisk and tape files.

> **This project is based on the original [QDTool by Martin Lukasek](https://github.com/mlukasek/QDTool).**
>
> MZTools extends the original application with tape waveform, loader, compression,
> IPL floppy and low-level QuickDisk functions in one unified interface.

The goal of this fork is to keep the original application simple for normal MZF/MZT/MZQ/QDF/QuickDisk work, while also providing a more complete toolset for SHARP MZ-700/MZ-800 tape and QuickDisk preservation, conversion and analysis.

<img width="786" height="443" src="/images/MZTools_scr_2026-09-27.png">

## Main functions

MZTools provides:

- opening and saving SHARP MZ QuickDisk and tape files,
- adding files to an existing image or tape,
- deleting files,
- reordering files with the arrow buttons or by dragging one or more selected rows,
- adding files by dragging them from Explorer,
- editing MZF header information,
- a simple file/hex browser,
- creation and editing of MZT multi-file tapes,
- conversion between the supported logical QuickDisk and tape formats,
- standard and extended MZ-800 QuickDisk directory handling,
- tape profiles, waveform conversion, compression, metadata sidecars and IPL floppy tools.

### Tape profiles and loader selection

Each tape record can be assigned a **Loader** and **Speed** profile.

Currently supported profiles include:

- NORMAL 1:1
- NORMAL 1:2
- NORMAL 1:3
- NORMAL 1:4
- MZ700 1:1
- MZ700 FAST3
- IC 1:1
- IC 1:2
- IC 1:3
- IC 1:4
- TC 1:1
- TC 1:2
- TC 1:3
- UL / UL_MZ800 / UL_MZ700 metadata profiles

Multiple rows can be selected with Ctrl/Shift. Changing the Loader or Speed for a multi-selection applies the complete compatible profile to all selected records.

UL profiles use a live WRITE/SENSE handshake and therefore cannot be exported as a static WAV/LEP/L16 waveform.

### Tape waveform import

MZTools can import:

- WAV
- FLAC
- LEP
- L16

WAV and FLAC can be opened using either the normal decoder or the **heuristic audio analyzer**.
Both PCM readers accept native 48 kHz input in addition to the previously supported rates; the signal is analyzed with its declared time base and is not relabeled or implicitly resampled.

The heuristic analyzer is intended mainly for real analogue cassette recordings. It can examine:

- mono and stereo recordings,
- individual left/right channels,
- normal and inverted signal interpretation,
- zero-crossing and Schmitt-trigger pulse extraction,
- duplicate tape copies,
- checksum-valid headers and payloads,
- NORMAL, MZ700, Intercopy and Turbo Copy structures,
- tape timing and pulse statistics,
- selective recovery when a normal decode does not produce a valid payload.

The final result is converted directly into MZTools tape records; no intermediate WAV, MZF or MZT file is required. Polarity is selected independently for every logical program, while its header and payload must use the same polarity. A complete checksum-valid header/payload pair always takes priority over timing score. Selective recovery scans only unresolved program intervals, so one damaged program does not discard records already recovered safely.

If the structured heuristic result contains unresolved programs, MZTools may also try the standard checksum-validating decoder. Format, decoder availability, truncated input and I/O errors do not trigger this fallback. When several WAV/FLAC files are added together, analysis continues after individual failures and one combined report is shown after the complete batch. The import options offer Summary, Detailed and Do not show report modes; Summary is the default for both one file and a batch.

The statistics window shows the selected source, per-record polarity, checksum state, loader/profile evidence, measured timing information and unresolved-program diagnostics.

### Tape waveform export

MZTools can export tape records to:

- WAV
- LEP
- L16

WAV export uses 8-bit mono PCM and offers 44.1 kHz (the default) or 22.05 kHz. The lower rate reduces timing resolution and may not play reliably with very fast loader profiles.

LEP and L16 store signed pulse durations:

- **LEP** - 50 microsecond units
- **L16** - 16 microsecond units

LEP/L16 pulse widths are stored independently. WAV keeps a sample-phase error accumulator so fractional pulse timing is preserved over the complete waveform.

Waveform export uses the Loader and Speed profile assigned to every record.

For multiple selected records, waveform export can create:

- **Union** - one waveform containing all selected records in order,
- **Separate** - numbered output files, one waveform per record.

### MZF and MZT export

Advanced export supports both selected records and the complete document.

- One record can be exported as a single MZF.
- Multiple records can be exported as one MZT.
- Multiple records can also be exported as separate numbered MZF files.
- Record order follows the order shown in the main grid.

### ZX0 and ZX7 compression

`Export...` can create self-extracting MZF/MZT records using integrated
ZX0 or ZX7 compression. No external compressor executable is required. The
available options are:

- ZX0 optimal or quick compression,
- ZX0/ZX7 forward or backward compression,
- ZX7 decoder embedded in the MZF header,
- expert partial compression using an existing prefix (forward) or suffix
  (backward),
- Auto selection of the smallest safe standalone result.

Compression is applied to exported clones and does not modify the open document.
For MZT export the selected policy is evaluated separately for every record and
the preview lists the resolved compression, original size, packed size and ratio
for each item. Records containing a known MZTools ZX0/ZX7 loader are marked as
locked and copied unchanged; they cannot be compressed again or changed to a
different compression. In the Export dialog, recognized compressed records can
either be copied unchanged or decompressed in the exported copy. Full forward
and backward streams are restored to a standard MZF record; the open document is
not modified.
For expert partial/skip compression, only the stored segment can be restored:
the excluded prefix or suffix and the skip count are not present in the packed
MZF. If the compressed stream refers to those external bytes, MZTools rejects
the standalone decompression as unsafe. An embedded ZX7 decoder overwrites part
of the MZF description; those loader bytes
are cleared during decompression, but the original description text cannot be
reconstructed. Packed `.mz0` and `.mz7` files can be opened as self-extracting
MZF files and decompressed during export.

The compression engines and MZF loader builders are ports of
[mz0](https://github.com/bales0/mz0) and
[mz7](https://github.com/bales0/mz7). Attribution and license text are in
`THIRD_PARTY_NOTICES.md`.

### Extended CPC DSK editor

Opening a `.dsk` now switches the main window into an integrated DSK document
mode backed by a shared Extended CPC DSK container model that keeps
the original header and track metadata, physical sector order, sector IDs,
FDC status bytes, padding, missing tracks and variable track sizes. Unchanged
images round-trip byte-for-byte. Sector sizes 128, 256, 512 and 1024 bytes are
supported; structurally valid images with an unknown filesystem open in a raw
sector view instead of being rejected. Boot-only images expose a valid IPLPRO
bootstrap as an exportable MZF entry and the remaining XOR-decoded MZ FDD data
area. Individual physical sectors remain available behind a `Show raw sectors`
switch. The decoded data area does not infer file boundaries when the disk has
no recognized directory filesystem.

The editor detects native single-game and multi-game IPL images, and provides
file operations for FSMZ/MZ-BASIC/IPLDISK,
the LEC CP/M DD (720 KiB) and HD (1.44 MiB) presets, Sharp P-CP/M80
(MZ-2Z047 320 KB and inverted SDS 400K),
and MRS. Native MZTools
multi-game IPL images are displayed as a read-only game list with load/execute
addresses and payload export. The editor can import, export, rename and delete
files, preserve MZF metadata where the filesystem stores it, show filesystem
space information, and replace a selected raw sector with an exactly sized
binary file. The New DSK dialog creates separate standard MZ-BASIC/FSMZ and
extended IPLDISK filesystems, both P-CP/M80 variants, one- or two-sided LEC
CP/M DD/HD and MRS disks with a selectable track count, and the special Sharp
Lemmings geometry. Its custom/raw mode accepts 1 or 2 sides, 1-204 absolute
tracks, 1-29 sectors per track, 128/256/512/1024-byte sectors, a filler byte,
normal/LEC/LEC-HD interleave or an explicit sector-ID map. CP/M and MRS presets
use the Sharp mixed boot/data-track geometry and physical sector interleave
used by `mzdisk`.

CP/M and MRS directory exports preserve their natural `NAME.EXT` names and
always write the original binary payload rather than a synthetic MZF. Batch
export distinguishes CP/M user areas and resolves case-insensitive filename
collisions before writing. MRS stores only a block count, so its last exported
block may contain padding.

Current limitations: attaching a custom CP/M DPB to an unknown image,
filesystem repair/defragmentation, bootstrap metadata editing and a writable in-place hex editor are not yet
exposed by the UI.
The DSK code is a C# port/adaptation of
[mzdisk](https://github.com/bales0/mzdisk); attribution is in
`THIRD_PARTY_NOTICES.md`.

### DSK Disk Map and Analyzer

The integrated DSK editor provides only `Files` and `Disk Map` tabs. Analysis
runs automatically when the image is opened or updated and is integrated into
Disk Map, with an always-visible summary and `Blocks` / `Issues` inspector pages.
Disk Map draws one row per physical track and one cell per sector in the
DSK descriptor order, including interleave, unusual sector IDs, variable track
sizes and explicit missing-track rows. It is a schematic container view, not a
flux/MFM recording or a representation of magnetic timing. Scrolling supports
large images without creating a separate WPF control for each sector; there
is no zoom control. Arrow keys move through sectors in the map or block list.

Selecting a file highlights its sectors. Clicking a map cell shows C/H/R/N,
physical indices, raw byte length, ST1/ST2, the actual sector-data offset in the
current serialized DSK, filesystem roles, allocation blocks and file ownership.
Use the block inspector or `Hex view block` for further inspection. The Hex
buffer contains the raw on-disk bytes, including inversion where applicable.
The optional Logical order sorts mapped sectors within each physical track;
it is unavailable when no logical mapping is known.

The shared, WPF-independent snapshot backend overlays FSMZ DINFO/directory and
bitmap allocation, CP/M DPB and directory extents, MRS FAT file IDs, and known
IPL boot/menu/program ranges. CP/M mappings reuse the filesystem reader's
physical track/sector maps for LEC DD/HD, P-CP/M80 and SDS/400. Ownership records
retain individual 128-byte logical ranges within physical 512-byte sectors.
Unrecognized filesystems retain a physical map with unknown roles. Candidate
CP/M layouts recovered from damaged directories are explicitly identified in
the report; such identification is diagnostic evidence, not guaranteed recovery.

The map's Issues inspector lists structured issues with stable codes, severity,
location and detail, with All/Errors/Warnings/Info filters. Selecting an issue
highlights its map sector and file where known and shows the issue and block
details in the fixed-height inspector. Reports can be copied or saved as text
directly from the map toolbar. There is no separate sector view, Analyze tab or
external Analyze DSK command. Subsequent document edits refresh the snapshot for
the open image. Analysis and map inspection never rewrite sectors or mark an image as
modified; existing edit operations continue to work in Files.

Clickable `All`, `Errors`, `Unsafe`, `Warnings` and `Info` counts open the Issues
inspector with the corresponding filter. Errors includes Unsafe; the Unsafe
count is a subset, not an additional count to add to Errors. Severity help
explains structural/controller errors, edit-risk diagnostics, suspicious
metadata and informational observations. Each finding has a location,
code-specific explanation, possible impact, recommended checks and original
evidence, available in the inspector, its tooltip and the exported text report.
FDC findings also decode preserved ST1/ST2 flags: they are capture metadata,
not a new hardware read test or a recalculated physical CRC. No automatic
repair is performed.

Checks include duplicate addresses, C/H and N/size mismatches, FDC status,
container boundaries and trailing data, FSMZ bitmap/counter/range conflicts,
CP/M cross-links, invalid extents/RC, directory overlaps and physical-map
conflicts, and MRS orphan FAT IDs, block-count mismatches and reserved-area
allocations. CP/M has no persistent free bitmap, so orphan allocations cannot
be inferred from arbitrary nonzero payload bytes. Empty FSMZ variants have no
unambiguous on-disk 63/127-entry discriminator; the existing reader's detection
is used. Custom CP/M DPBs and repair/defrag remain unsupported. Validated raw
hex editing and boot/system installation are described below; analysis never
performs an automatic repair.

### Direct MZ-800 IPL floppy import/export

`Open...` and `Add...` accept compatible single-program MZ-800 IPL
`.dsk` images. MZTools validates the 40-cylinder, double-sided Extended CPC DSK
geometry, sector descriptors, byte inversion and the `IPLPRO` boot sector before
importing its declared payload as a normal OBJ MZF record. The reconstructed MZF
header contains the IPL boot name, SIZE, LOAD and EXEC values; the original MZF
header, tape metadata and compression provenance are not stored in the disk and
cannot be recovered. A compressed or self-extracting payload is imported exactly
as stored and is not automatically decompressed.

Import is intentionally limited to one self-contained IPL payload. Images with
data outside the declared program are rejected as possible multipart or boot-menu
disks. MZTools does not analyze whether the Z80 program later loads data from
another medium. Imported records can be saved/exported as `.mzf` or `.mzt`, or
exported again as an IPL `.dsk`. Multi-record documents can also be saved as a
multi-game `.dsk`.

For an opened IPL DSK, the status area shows the image type and unused
sector capacity in its payload area. The record table also identifies MZTools'
known self-extracting ZX0 and ZX7 loaders, including their direction and the ZX7
embedded-loader variant. `None / unknown` means that no known MZTools compression
loader was detected; it cannot rule out a foreign or custom compression scheme.

When exactly one record is selected, `Export...` offers an
`MZ-800 bootable IPL floppy (*.dsk)` target. It creates a 40-cylinder,
double-sided Extended CPC DSK image using the native MZ-800 IPL/GOPGM path, with
no QDBoot menu or intermediate launcher.

This output supports one self-contained, single-part MZF only. Programs that
load another cassette part at runtime are not supported. If EXEC points into
the standard MZF header workspace at `$10F0..$116F` and contains a direct `JP`,
the jump chain is resolved to its actual target before optional compression.
Other executable header code is staged together with the gap up to the program
body. The prepared program must not exceed 48361 bytes (`$BCE9`). ZX7's
embedded-header loader and partial/skip compression cannot be used for direct
IPL output because the IPL loads only the program body, not the MZF header or an
external prefix/suffix.

### Multi-game MZ-800 IPL floppy export

MZTools reads QDMG metadata both from the menu-program footer and from the
historical IPLPRO comment position (decoded logical block 0, offset `0x20`).
Both are shown as `MZTools multi-game IPL`; the information panel reports the
metadata position and menu entry-table offset. A present menu footer takes
precedence. Signature, version, entry size/count, table bounds, menu dimensions,
payload allocation, overlaps and LOAD/SIZE are validated for both variants.
Unknown compression or flags remain readable/exportable but disable rebuild.

Opening and inspecting either layout preserves the original image. Rebuilding,
or saving the IPLPRO-metadata variant through the configurable multi-program
editor, writes the current canonical menu-footer layout. Payload bytes,
names, LOAD/EXEC and compression metadata are retained; new IPLPRO comment
areas stay clear. This is read/import compatibility; new images always use
the menu footer.

`Save As...` offers
`MZ-800 multi-game IPL floppy (*.dsk)` for the whole document. It opens a
dedicated dialog initialized from the main-table order. There, every program can
be moved with buttons or drag-and-drop, renamed, and assigned `None`, `ZX0`,
`ZX7`, or `Auto` compression. Each row shows original and packed size,
compression ratio, and the 48361-byte per-program staging limit.
The existing `Export...` path uses the same dialog for only the selected rows.

The dialog shows an indeterminate progress bar and identifies each entry while
it is being analyzed or compressed. After every change it automatically displays
menu size, exact content bytes, sector padding, total allocated capacity,
used/free sectors, and used/free byte capacity at the bottom. Saving is disabled
while analysis is running or when the image exceeds 1280 logical sectors. Export
preparation works on clones and does not modify the source MZF records.

Logical block 0 contains `IPLPRO` plus the versioned `QDMG` v1 signature.
Blocks 1 onward contain a menu at `$1200`, its 26-byte-per-entry table, and then
the sector-aligned program payloads. The table records the 16-byte display name,
start block, byte size, LOAD, EXEC, flags and compression type. The menu has nine
direct numeric choices per page and `N`/`P` navigation. The internal maximum is
255 entries, although disk capacity and each program's size normally impose a
lower practical limit.

After a selection, a short final loader stub runs at `$1000`, reads the selected
payload to the ROM staging address `$1200`, and calls the MZ-800 ROM `JGOPGM`
path to relocate it and jump to its original EXEC. This supports LOAD addresses
below, at, or above `$1200`, including overlapping relocation. The stub stays
below the ROM error-stack workspace and is no longer needed while relocation is
executing. The ROM contract was checked against the supplied Sharp 9Z-504M ROM
(SHA-256 `68A91B82517D5642E250CDDDB519DE780FB20BCDE39B2DE8BAEE387CA6BF446A`).

Before each selected-program read, the stub follows the ROM `FDBOOT` sequence:
it checks the controller through `FDCC&` (`$E8D5`), copies the 11-byte `BOOT`
parameter template from `$E4D1`, patches start block/size/destination, calls
`FDDESL` (`$E530`), and then enters `FDREAD` (`$E5A7`).

Every entry must be a self-contained, single-part MZF. Each prepared payload is
limited to 48361 bytes (`$BCE9`), must fit the 16-bit LOAD range, and shares the
same direct-IPL preparation and compression validation as single-program export.
Multipart cassette programs and ZX7 embedded-header/partial compression modes
are not supported. `QDMG` images are recognized on import, but are intentionally
not flattened into a single synthetic MZF record.

The loader has automated binary-layout and supplied-ROM contract coverage, but
is not yet marked as emulator- or hardware-verified. Manual verification plan:

1. Create a disk with at least two self-contained programs and boot it in
   `mz800emu`.
2. Run every menu entry and verify paging plus LOAD values below `$1200`, equal
   to `$1200`, and above `$1200`, with differing EXEC values.
3. Repeat with None, ZX0/ZX7, Auto, and a program close to the staging limit.
4. Repeat the same image and selections on a real MZ-800 before claiming
   hardware verification.

### MFI and MTI sidecars

MZTools supports SD2CMT-style metadata sidecars:

- `.MFI` for a single MZF,
- `.MTI` for an MZT containing multiple records.

Matching sidecars are loaded automatically.

The save dialog can explicitly create MFI/MTI metadata. An existing matching
sidecar is regenerated when necessary so it cannot remain inconsistent with the
saved tape file.

These metadata files are intended for use with **MZ-SD2CMT2-Reborn**. MZTools can create:

- `.MFI` metadata for `.MZF` files,
- `.MTI` metadata for `.MZT` multi-file tapes,
- `.LEP` pulse files,
- `.L16` pulse files.

This makes it possible to prepare tape files and their Loader/Speed metadata directly in MZTools for playback with MZ-SD2CMT2-Reborn.

MZ-SD2CMT2-Reborn repository:

https://github.com/bales0/MZ-SD2CMT2-Reborn

The format used by this project is documented in:

`specification/MFI_MTI_FORMAT.md`

### Trailing MZF data

MZTools allows preservation or removal of trailing data located after the normal MZF body.

### QuickDisk functions

MZTools adds lower-level QuickDisk handling while preserving the original logical editing workflow.

Supported `.qd` image variants are detected from file content and include:

- SHARP/MZ legacy logical QuickDisk images,
- HxC QuickDisk images (`HXCQDDRV`),
- FlashFloppy QuickDisk images.

The application also provides:

- explicit selection of the `.qd` container type when saving,
- creation of an empty MZQ or selected `.qd` image,
- QuickDisk image details,
- formatting of the current `.qd` image without changing its physical container/geometry,
- detection and preservation of imported images containing more than the standard 34 MZ-800 directory entries.

Standard editing uses the normal SHARP MZ-800 limit of 34 directory entries. MZTools can identify and retain compatible imported images containing approximately 35-50 entries without silently discarding their existing contents.

### QuickDisk Disk Map

QuickDisk documents (`.qd`, `.qdf`, `.mzq`) have `Files` and `Disk Map` tabs.
The map wraps the sequential stream over eight rows and distinguishes FNBLK,
file headers, payloads, body framing/CRC, sync/gaps/unassigned data and space
outside the physical data window. Gaps are not automatically free capacity.
Select a region or a block in the list to see its file name, payload size,
LOAD/EXEC and exact position; matching files/regions are highlighted. Arrow
keys move between regions in the map or block list, including tiny regions.
There is no zoom control. The selected list row stays strongly highlighted
even when keyboard focus remains in the map.
Every region has a dark outline. Selected regions/files use a blue fill
with a contrasting black/white frame; body framing/CRC is pale yellow, so
selection is distinct from the normal header, payload and framing colors.
Clearing the file/block selection restores the original role colors. Clicking
the selected region again or clicking outside the track clears its selection;
changing images also discards all old highlights.

DSK and QuickDisk maps share the same map-left, inspector-right layout,
resizable divider, fixed-height scrolling details, blue selection and orange
headers/system metadata. Detail changes do not resize the map. Both offer
`Clear selection` and `Hex view block`; clicking blank space around the map
also clears selection without interfering with toolbar controls or scrolling.
QuickDisk hex view displays original/preview image bytes for logical formats,
decoded MFM bytes for physical file blocks, and packed raw LSB-first bitcells
for physical gaps/outside-window regions. The viewer identifies the position
and encoding. DSK hex view shows raw bytes of the selected sector.

HxC and FlashFloppy maps use actual track bitcell positions (LSB-first), the
container's track offset and data-window boundaries. Frame positions start on
the first data bitcell, not its preceding MFM clock cell. QDF and compact
MZQ/Sharp logical QD maps show byte offsets in the image, not invented physical
sector or track positions. QDF/physical frames use validated CRCs; compact
formats contain literal CRC markers instead.

Opening and inspecting a map never rebuilds the source: the original image
is retained as a read-only snapshot. After edits or for a new document, the map
shows an explicitly labeled **preview of the rebuilt image**, using the same
format/profile as saving. Capacity/format errors clear the map and display an
explanation, without changing the document. Saving refreshes the original-image
snapshot and positions. Non-QuickDisk tape documents do not show the map tab.

## Supported file formats

### QDF

Japanese QuickDisk logical file format used by several SHARP tools and emulators.

### MZQ

European QuickDisk logical format with a simpler structure. It is used by UniCard and several SHARP MZ emulators and utilities.

### QD

QuickDisk image container. MZTools detects supported SHARP/MZ legacy, HxC and FlashFloppy QuickDisk variants from their contents.

### MZF

Single SHARP MZ tape file containing the 128-byte file header followed by the file body.

The same or closely related tape data is also found with extensions such as M12.

### MZT

Multiple MZF records concatenated in tape order.

MZT is useful for preserving multi-part programs and for creating correctly ordered sequential tapes for UniCMT and emulators.

### LEP

Compact signed pulse-duration tape stream using 50 microsecond units.

### L16

Signed pulse-duration tape stream using 16 microsecond units.

### WAV

PCM tape waveform.

MZTools can generate 44.1 kHz or 22.05 kHz 8-bit mono WAV files and analyze supported PCM WAV recordings, including native 48 kHz, at 8/16/24-bit depth in mono or stereo.

### FLAC

FLAC is supported as an **input** format for audio/tape analysis, including native 48 kHz mono/stereo input at the supported 8/16/24-bit depths.

FLAC output is not generated by MZTools.

## Tape timing

Waveform generation and loader recognition are based on SHARP MZ ROM/Z80 timing analysis and on the original accelerated loader implementations.

The generated conventional tape structure uses:

- 11000 SHORT pulses for a header leader,
- 5500 SHORT pulses for a data/loader leader.

NORMAL 1:1 uses timing derived from the MZ-800 1Z-013B ROM, including context-dependent LOW widths.

MZ700 timing is based on the corresponding MZ-700 ROM tape routines.

Accelerated NORMAL/IC timing follows the Intercopy V10.2 writer timing.

Turbo Copy timing follows the Turbo Copy V1.22 loader/writer implementation and its timer model.

The MZ700/NORMAL, IC and TC metadata detected during audio analysis is kept separate from the physical waveform decoder so that structural loader evidence can be used where available.

## QuickDisk limits

A standard SHARP MZ-800 QuickDisk directory contains up to 34 files.

MZTools can recognize some non-standard/extended images containing more entries and preserves their existing contents whenever possible.

The actual usable capacity also depends on the selected QuickDisk image format and physical layout.

## Requirements

MZTools is currently a Windows WPF application.

- .NET 10 Desktop Runtime
- Windows
- C# / Visual Studio 2022 or a compatible .NET development environment

FLAC input uses `NAudio.SoundFile` together with the bundled `libsndfile` Windows runtime.

## Work in progress

Possible future/experimental formats include:

- **RAW** - raw QuickDisk data captured by QDC or similar hardware/software,
- **MFM** - decoded/converted MFM-level QuickDisk data.

These formats should not be considered stable until explicitly listed as supported.

## Project history

### Original QDTool

The original QDTool was created by **Martin Lukasek** as a simple application for converting and editing SHARP MZ QuickDisk and tape files.

Original repository:

https://github.com/mlukasek/QDTool

This fork keeps that application and its workflow as its foundation.

### Extended fork

This repository extends the original project mainly with:

- a unified interface for all supported features,
- additional QuickDisk image variants,
- physical QuickDisk image handling,
- tape Loader/Speed metadata,
- NORMAL/MZ700/Intercopy/Turbo Copy waveform generation,
- IC 1:1 and TC 1:1 support,
- LEP/L16/WAV waveform import and export,
- FLAC audio input,
- heuristic analogue tape analysis,
- MFI/MTI sidecars,
- multi-selection with `Ctrl+A`, batch Delete, and export of the selected rows,
- additional validation and preservation functions.

## License

MZTools is distributed under the GNU General Public License version 3.

Original QDTool copyright:

Copyright (C) 2024 Martin Lukasek  
https://www.8bity.cz/

This project is provided without warranty; see `LICENSE.txt` for the full GPLv3 license text.

## Technical references and source projects

The extended functions in this fork were developed and verified using information, code concepts, file-format behavior and timing data from the following projects and documentation:

- **Original QDTool by Martin Lukasek** - the base project from which this fork was created  
  https://github.com/mlukasek/QDTool

- **MZ-SD2CMT by SHARPENTIERS** - original SD-card CMT implementation for the SHARP MZ family and an important reference for tape loaders, formats and metadata  
  https://github.com/SHARPENTIERS/MZ-SD2CMT

- **MZ-SD2CMT2-Reborn** - development/reference fork used while extending loader profiles, tape timing and MFI/MTI behavior. MZTools can generate `.MFI` metadata for `.MZF`, `.MTI` metadata for `.MZT`, and `.LEP`/`.L16` pulse files for use with this project.
  https://github.com/bales0/MZ-SD2CMT2-Reborn

- **TapeMZ by Michal Hucik** - SHARP MZ tape archive/file-format reference and related tooling  
  https://github.com/michalhucik/TapeMZ

- **SHARP MZ-800 Technical Reference Manual** - memory map, hardware, ROM routines and machine-level behavior  
  https://www.radeksuk.cz/sharp/gdg/dokumentace/MZ800_Technical_reference_manual.pdf

- **Direct Z80/ROM analysis of the SHARP MZ-800 1Z-013B and MZ-700 tape routines** - used to verify NORMAL and MZ700 pulse timing and contextual pulse widths.

- **Intercopy V10.2** - loader/writer code and timing behavior used as a reference for IC and accelerated NORMAL tape profiles.

- **Turbo Copy V1.22** - loader/writer code and timer behavior used as a reference for TC profiles.

- **FlashFloppy Quick Disk documentation** - QuickDisk hardware/image behavior and FlashFloppy QuickDisk compatibility  
  https://github.com/keirf/flashfloppy/wiki/Quick-Disk

- **HxC Floppy Emulator project** - HxC image/container and QuickDisk implementation reference  
  https://github.com/jfdelnero/HxCFloppyEmulator

- **NAudio.SoundFile / libsndfile** - FLAC decoding used by the Advanced audio import path  
  https://www.nuget.org/packages/NAudio.SoundFile/  
  https://libsndfile.github.io/

## Format-aware Install Boot/System

The DSK editor provides one **Disk → Make Bootable / Install CP/M System...** dialog for consistent, recognized CP/M images with a supported Sharp boot track. **Browse...** automatically identifies a registered exact system-area match or a compatible source DSK whose OS/version, loader, transfer mode and bootability remain unverified. There is no manual Verified/Compatible source mode. Each new source is classified independently; failed selections discard the previous source/profile. Profiles are tied to the detected DPB and exact physical layout, including sector descriptor order, C/H/R/N, sector sizes, system tracks, allocation parameters and physical track/sector maps. P-CP/M80 original, SDS/400, LEC DD and LEC HD are distinct layouts, not interchangeable systems.

Choose a trusted source DSK with the identical layout. No bundled CP/M version is assumed and no CP/M system bytes are generated. An empty/fill-only boot track or identification-header-only source is rejected; matching layout does not certify the source OS version or its bootability. Until a compatible source is chosen, no system data is available for installation.

Installation runs on a private buffer. For LEC hidden-track systems, only sector payloads in the system area are replaced; geometry, descriptors, DPB, filesystem and directory/data allocation are retained. Native P-CP/M80 instead installs its IPL and allocates/replaces `PCPM.SYS` as described below. Files are checked after reopening the candidate. Any failure leaves the original document unchanged. Successful installation marks it modified; saving remains a separate action. Analyzer report exports include installer availability.

### CP/M System Profiles and System Builder backend

The UI-independent System Builder backend registers CP/M 2.3 DD polling, CP/M 4.1 DD/HD IRQ and CP/M 4.2 DD/HD polling using **system-area fingerprints**, not whole-image SHA. A fingerprint includes layout, exact geometry/descriptor signature, full DPB/map signature, canonical system physical tracks and SHA-256 of their exact stored payloads and identities. Adding/removing COM programs or games outside the system area does not change system identification. Reference whole-image SHA is retained only as provenance; complete source/target/result hashes remain in transaction reports. Transfer mode is trusted metadata only for an exact registered system-area match, never inferred from opcode patterns or port `DFh`. All five profiles carry only `StaticValidated`, not emulator/hardware verification. System image files are not bundled with MZTools.

Fingerprint encoding v1 concatenates each system physical track in ascending order: track index (Int32 little-endian), descriptor count (Int32 little-endian), then descriptors in physical order, each containing C/H/R/N (one byte each), payload length (Int32 little-endian) and exact stored payload bytes. Geometry and DPB are validated separately. Branding is included without normalization; changed branding produces an unregistered source, which may still pass compatibility validation.

Preflight checks the complete physical descriptor signature, CP/M DPB and maps, system-track list, system-area hash for registered sources, presence of system bytes, allocation separation and Analyzer results. The source remains **strict**: whole-disk Error/Unsafe issues are rejected, even in user data; ordinary warnings are not silently promoted into known system verification. Build operates on a private byte copy, reopens and analyzes the result, verifies that non-system sectors and all target CP/M files remain byte-identical, and only then allows the caller to replace the document. Source user files are never imported. Registered and compatible installation share this transaction backend and report exact changed byte ranges and hashes; compatible reports explicitly mark OS/version/transfer/loader unverified.

**Install into current disk** changes the open document after validation, marks it modified, and refreshes Analyzer, Disk Map and boot information. Nothing is saved automatically. Standard editor **Save / Save As...** is the only image-save workflow; the installation dialog contains no Build-and-Save action. Its report can still be copied or saved as text.

All boot name, logo and displayed-version bytes are preserved from the source. There is no automatic branding normalization/replacement or drive-configuration patch. SDS/400 retains its existing compatible import, but has no registered system profile; this change does not certify native SDS/400 boot support. CP/M 4.2 HD IRQ is also not registered without a concrete verified reference.

### Native SHARP P-CP/M80 (MZ-2Z047), 320 KiB

Native P-CP/M80 is a **file-based boot system**: SHARP ROM → IPL on C0/H1 → compare **directory entry #0** with user 0 `PCPM.SYS` → load the system through that entry's allocation list. The MZ-2Z047 IPL does not search later directory entries; a correct system file elsewhere produces `No system file`. It must not use the LEC hidden-system-track installer. The explicit storage kinds are `HiddenSystemTracks` and `BootTrackPlusSystemFile`.

The creator uses 40 cylinders × 2 sides, ordinary tracks with 8 × 512 B in descriptor order `1,5,2,6,3,7,4,8`, and boot track index 1 with 16 × 256 B in order `1,9,2,10,3,11,4,12,5,13,6,14,7,15,8,16`. The existing PersonalCpm80 DPB and side-0-forward/side-1-reverse map are unchanged. Creating a disk produces **data-only / not bootable** media. Install the system afterwards through the existing Disk menu; no automatic-after-creation option is added.

The registered native fingerprint combines the canonical boot track structure/payload hash with filename `PCPM.SYS`, user 0, SYS attribute, size 14848 B and payload SHA-256 `E1BDED8F1F9E320F76E7732F040279850B939CF8F4EBD4D56B39FC40BB0E1C60`. Full `mz-2z047v10a.DSK` and minimal `P-CPM80_MZ-2Z047_320K_MINIMAL_BOOTABLE.dsk` match the same profile. Other files, whole-image SHA and system-file allocation blocks do not define identity. The documented boot hash `A9AF2A7AE10898918DB860948BB3A17024A780BF40DFF6A0F1891B4E94CF0B0A` hashes the **serialized track including its header**, not just concatenated sector payloads. The canonical descriptor/payload fingerprint hash is `DD95944DF879A10D826FC55C1F96B6751D5937652EC45D978BD3ADB7A6F201B0`.

Installation copies validated IPL payloads only to C0/H1 and inserts `PCPM.SYS` through the CP/M allocator on a private clone, always at **directory slot 0**. An existing user 0 system file is replaced, SYS is set, and RO/ARC are retained from the source. A different file extent previously at slot 0 is moved byte-for-byte to a free directory slot; its data blocks, allocation pointers, user, attributes and extent fields do not change. Other directory slots remain fixed, including any later extents of that file. A replaced system file's old slot may supply space for relocation even in an otherwise full directory. If no slot can be freed, installation is rejected without touching the original. Duplicate/multiple/noninitial system extents are rejected rather than repaired heuristically. All other target files are checked content- and metadata-identical; other source files are never copied. Reference allocations 1–8 are not hardcoded. Insufficient space, incompatible DPB/geometry or Analyzer Error/Unsafe leave the original untouched. The report includes system slot, old first-entry relocation, system allocation blocks and preservation result, plus exact changed byte ranges. Standard Save / Save As is still a separate operation.

An identification header alone, native IPL without user 0 `PCPM.SYS`, or a system file outside slot 0 is not bootable. Analyzer reports `PCPM_SYS_NOT_FIRST_DIRECTORY_ENTRY` for the last case as a repairable boot-layout warning, not a general CP/M corruption error. Duplicate/multiple/noninitial system extents yield `PCPM_SYS_INVALID_EXTENTS` (Unsafe). Exact native IPL + registered file in slot 0 with valid allocation is reported as a structurally recognized system; changed payloads with valid native-loader markers are unverified candidates only if slot 0 is correct. A structurally valid source with a later system entry can still be imported: installation repairs its directory position, without treating the source as already bootable or a registered boot match. Disk Map distinguishes native IPL, directory, the system file, other files and free blocks; editing system-file bytes requires the same sensitive-area confirmation as other system bytes. Transfer mode is unknown, not guessed as polling/IRQ. Verification is static only: relocation/allocation regressions do **not** certify emulator or hardware boot. No BIOS patching, experimental LEC-style 320K conversion or new SDS/400 boot profile is included.

**New DSK** only creates and opens the disk; it contains no system-install choice and never opens the installer automatically. To add a CP/M system afterwards, explicitly use **Disk → Make Bootable / Install CP/M System...**. The P-CP/M identification header remains format metadata, not proof of an installed bootable system. Non-CP/M and single/multi IPL creation workflows are unchanged.

`CpmSystemBuilderTests` includes deterministic synthetic regressions and optional full-image tests using the five documented reference DSKs in the sibling `_SFD800` directory. The optional tests verify provenance, unchanged system identity after adding COM/game files, changed system-byte/descriptor rejection and preservation of target files.

Single/Multi IPL never offer CP/M installation. FSMZ/IPLDISK, MRS, BootOnly and Raw have no compatible installer; their disabled menu item explains why. Installation is not a universal Make Bootable function and does not convert formats. No CLI, automatic repair or defrag is introduced.

## Format conversion

Open **Disk ▾ → Convert Format...**. The original document and its unsaved edits remain unchanged; output is saved as a separate DSK and cannot overwrite the source path.

- **Related conversion:** MZ-BASIC/FSMZ 63 entries ↔ IPLDISK/extended FSMZ 127 entries, retaining physical geometry, volume, native file metadata and payloads. Directory and allocation are rebuilt transactionally in the new image; data beginning at block 24 is relocated if expansion needs block 32. The source's declared block limit is respected. Shrinking more than 63 occupied entries is refused, not truncated. Boot bytes are retained, but a pre-existing loader's support for the new directory variant is not certified.
- **File copy to a new medium:** FSMZ ↔ CP/M, FSMZ ↔ MRS, MRS ↔ CP/M and different supported CP/M layouts. Targets are FSMZ 63/127 (320 KiB), P-CP/M80 original, SDS/400, LEC DD/HD and MRS (720 KiB). This is explicitly a file transfer, not an in-place filesystem conversion. Single/Multi IPL, Raw, BootOnly and inconsistent images do not offer conversion.

Preflight builds and reopens a private target, checks directory entries/extents, filename encoding and length, duplicate names (including CP/M users collapsing into a filesystem without users), actual allocation and capacity, and payload preservation. Strict mode refuses the entire conversion if any file fails. No automatic renaming or partial-copy policy is enabled. The scrollable report explains source warnings, failures and metadata changes before **Convert and Save As...** becomes available. Saving writes a private temporary file first and commits the complete output within the destination directory; failed writes do not produce a partially replaced target.

CP/M-to-CP/M copies retain user, RO/SYS/ARC and bytes; extents/block allocation are rebuilt. CP/M-to-FSMZ/MRS loses user and flags. FSMZ-to-CP/M loses type, lock and LOAD/EXEC; MRS-to-CP/M loses LOAD/EXEC and FAT/file-ID metadata. FSMZ-to-MRS preserves LOAD/EXEC but loses type/lock. MRS-to-FSMZ preserves LOAD/EXEC but loses FAT/file ID. Where no source file type exists, FSMZ explicitly uses generic binary type 1; absent LOAD/EXEC are stored as unset (0), not inferred. These policies appear in preflight. Record/block-based targets can append padding, which is also reported; source payload bytes are never truncated.

File-copy conversion never transfers boot/system code. New targets are data-only; a P-CP/M identification header is not an installed OS. Empty native FSMZ directories do not distinguish 63 from 127 entries: MZTools-produced containers retain the explicit choice in their creator field (`MZTools F63` / `MZTools F127`), not an invented native filesystem field. Other images are detected from their existing layout and occupied extended slots where these do not overlap data.

## Boot and system information

The DSK information panel and exported analysis report show **Bootable** and **System**. Recognized single/multi IPL loaders are distinguished from an operating system. Empty boot/system areas and identification-header-only CP/M images are reported as data-only, even when files exist. Other non-fill boot/system bytes are reported as **Unverified**, with the detected CP/M layout or IPL label where available. Layout recognition alone does not prove bootability or a specific OS version; MZTools does not invent either.

## File properties

Native properties are displayed and edited directly in the **Files** browser rows, without a separate side panel. Confirm a cell with Enter or leave the cell to apply it; Escape cancels editing. Only consistent, writable CP/M and MRS images expose editable native cells. Values are applied through the validated properties service; typing alone does not change the document. Each edit applies to its row, not to all selected files.

- **CP/M:** User area (decimal 0–15) and clickable RO/SYS/ARC checkboxes. Every extent of the logical file is updated consistently. Checkbox bindings never write directory bytes directly: clicks use the same validated transaction as text properties. Filename, extension, RC, extent numbering, allocation pointers and payload bytes are unchanged. Moving to a user area that already contains the same name/extension is refused before any write. CP/M does not expose LOAD/EXEC because those are not native directory fields.
- **MRS:** LOAD and EXEC display as `0x0000`–`0xFFFF`. Enter `0x`-prefixed hexadecimal or plain decimal. The existing native little-endian directory fields are at +0x0C and +0x16 respectively. Editing does not change file ID, block count, FAT ownership or payload. CP/M user/attribute fields are not offered for MRS.

Apply works on a private document, reopens and validates the filesystem/DPB and all file payloads, then replaces the original document only on success. The file list and analyzer/map refresh and the edited file is reselected. A no-op does not mark a previously saved image modified. Saving remains a separate operation.

Boot-install geometry errors report the target and source sector counts, sizes and physical IDs. Such differences still refuse installation: choose a system source for the same detected filesystem, DPB and physical layout. Installation is not a conversion operation.

## Filesystem Structure Inspector

Open **Disk ▾ → Filesystem Structure Inspector...** (the duplicate Disk Map button has been removed). The read-only, resizable window uses a private snapshot including unsaved edits, with four tabs: Filesystem, Directory, Allocation and Raw structure. Selecting a block or directory slot highlights its physical sectors in the inspector and the editor's Disk Map. All sectors of a block are highlighted, not all blocks belonging to the same file. Details include native offsets and physical image offsets. **Hex view metadata...** shows decoded native metadata without enabling writes.

- **FSMZ:** standard 63 / extended 127 directory, DINFO volume, file-area start, used/last block counters, raw DINFO and 2000-bit LSB-first bitmap, every directory slot (including header/unused), and bitmap state plus directory owners per 256-byte block.
- **CP/M:** detected DPB (SPT/BSH/BLM/EXM/DSM/DRM/AL0/AL1/CKS/OFF), block size, directory blocks and physical maps; all raw directory entries, user/file grouping, extent number/group, RC, RO/SYS/ARC and 8-/16-bit allocation pointers. Free/used/reserved blocks follow that DPB and its physical mapping. DPB parameters are not presented as a fabricated on-disk record.
- **MRS:** actual FAT/directory boundaries and data start, reserved blocks, raw FAT and directory area, file IDs, LOAD/EXEC, native padding and each FAT block's owner (including orphan IDs).

Analyzer diagnostics are retained, including for recognized read-only filesystems. Unknown/Raw, BootOnly and dedicated IPL layouts do not offer filesystem decoding; no structures are guessed. Raw metadata buffers are filesystem-decoded (inversion removed where required), while the existing Disk Map sector hex view shows stored physical bytes. This is not a DPB editor, repair or defragmenter.

## Physical QuickDisk host identification

`.QD` is not only a SHARP format. MZTools can inspect physical HxC and
FlashFloppy QuickDisk containers from multiple host systems. The container
representation remains separate from content-derived host identification:
SHARP MZ, Roland, Akai S612/S700 family, Thomson MO5, or unknown physical
QuickDisk. Device names are probable origins supported by evidence, not
hardware guarantees; filenames never influence detection.

Recognized non-SHARP and unknown physical images open read-only in the
existing Files view, with container/profile metadata, confidence, evidence,
warnings, decoded blocks/sectors and separate read-only hex inspection.
Disk Map retains physical positions and host-specific regions. Both views
offer text reports; the inspector offers decoded-block/sector binary export
and MO5 raw 51200-byte export only for 400 unique checksum-valid sectors.
Selecting a host unit shows its structure, framing/CRC ranges and track position;
Roland exposes shared SHARP header fields, window-relative start and inter-block
spacing. Akai shows subformat/marker information; MO5 shows physical/logical IDs
and checksums. Block export includes decoded sync/framing/CRC; sector export
contains only sector payload. No byte-identical image-copy action is offered;
Save As as a format converter remains future work.
MZF add/delete/rename, normal Save/Save As and SHARP rebuilding are disabled.
Non-SHARP support is read-only unless documented otherwise.

Blank physical media have unknown origin, not an assumed SHARP identity.
Formatted zero-file SHARP media are recognized from a validated FNBLK/CRC;
residual syncs and partially valid Roland blocks do not override that evidence.
The explicit Format Quickdisk command may initialize a proven blank image
as SHARP after its existing confirmation; nonblank non-SHARP images cannot
be formatted through that command. Native SHARP editing/writers are unchanged.

Roland can share valid FNBLK/header/body framing with SHARP. Validated
Roland S10 metadata plus the three fixed CRC regions provide more specific
evidence; an otherwise wire-compatible layout with conflicting host evidence
is kept unknown/read-only instead of guessing its origin.

Deterministic synthetic tests run without media fixtures. Optional full-image
integration tests use these local files in `specification` (not automatically
committed): `DSKA0001_Roland.QD`, `DSKA0002_MO5_CQ90-028_formatted.QD`,
`DSKA0003_Akai_formatted.QD`. The test project copies them into `QDReference`
when present. Run `dotnet test MZTool.Tests/MZTools.Tests.csproj --filter
QuickDiskHostDetectionTests`. Equivalent synthetic FlashFloppy wrappers test
container-independent detection, not emulator/hardware compatibility.

## Writable Hex Editor

In **Disk Map**, select a sector or block, then explicitly click **Hex editor...** to open a **separate modeless window**. Selection alone never opens it. Once open, one window follows subsequent sector/block selections. The map retains its full height and remains usable. The buffer selector offers the physical sector and any mapped CP/M allocation blocks; while the window is closed it only chooses what to open. The title identifies the active track/descriptor or allocation block. **Cancel** discards unapplied bytes and closes the window without reopening it. Switching buffers or closing the window with unapplied edits requires confirmation; declining preserves both the active editor and selection. Sector editing shows stored bytes; CP/M block editing shows filesystem-decoded bytes and writes storage inversion back correctly. Dedicated single/multi-program IPL images display sector bytes read-only.

The workflow is **Unlock editing → edit two-digit hex bytes → Preview changes → Apply**. The original and new buffers appear side by side with HEX, ASCII and MZ (SHASCII) columns. Scrolling either buffer scrolls both, vertically and horizontally. Character views update from valid edited bytes; incomplete or invalid HEX is indicated explicitly. MZ uses the existing SHASCII display mapping, not a pixel-exact hardware font. The preview includes exact byte changes, sector role, filesystem re-detection and Analyzer results. Boot/System/Directory/FAT/AllocationMap buffers warn explicitly. Buffer size cannot change. Revert and Cancel leave the document untouched, and changing the text invalidates the previous preview.

In the DSK file browser, select multiple files with Ctrl/Shift and edit a native
property. Clicking RO/SYS/ARC sets the same flag for the whole selection;
editing User or MRS LOAD/EXEC sets that value on all selected files. Other fields
retain their individual values. The complete batch is validated on a private
copy; a collision or invalid value leaves every file unchanged. Renaming remains
a single-file operation.
The selection remains strongly highlighted when the browser loses focus after
editing. Sectors belonging to selected files retain their blue map fill and are
also highlighted in the side Block View, independently of its single active row.

HEX entry uses fixed-position overwrite, not text insertion: only hexadecimal
digits are accepted, separators and buffer length stay unchanged, and the caret
advances across digit positions. Backspace moves back without deleting a byte;
Delete/Enter/Space cannot reshape the buffer. Paste accepts complete bytes
(`AA BB` or `AABB`) starting at the current byte; malformed or oversized input
is rejected atomically. Native cut and drag/drop cannot modify the buffer.

QuickDisk report copy/save actions are available only in Disk Map. The non-SHARP
Files inspector exports a selected decoded block/sector for supported hosts.
MO5 additionally exports a complete logical raw image (51200 bytes), only when
all 400 unique sectors have valid checksums. This is not a container copy or
a speculative logical image conversion for Roland/Akai.

Each edit uses captured private image bytes, modifies only the mapped payload, serializes, reopens and analyzes the candidate before replacing the current document. Container errors are rejected. Filesystem damage or a detection/layout change requires explicit confirmation; the resulting bytes remain available for Save As and raw inspection even if detection changes to Raw/BootOnly. A changed document invalidates a pending hex edit. Apply marks the document modified; saving remains a separate action. The first version does not edit whole images or container descriptors; non-CP/M filesystem blocks are accessible as physical sectors.

## DSK Compare

Use **Disk ▾ → Compare with...** and select a second DSK. Comparison works on snapshots, including unsaved edits in the open document. Comparing and exporting a patch do not modify either image. **Preview patch** and **Export patch...** describe the changes from the left snapshot to the right.

The table and selected-item details show **Left** and **Right** side by side. In **Physical sectors**, `Stored descriptor length (+6/+7)` explicitly identifies differences in the Extended DSK sector length field, separately from the decoded payload length. Such a difference is selected automatically when the window opens. **Hex diff... → Descriptor / raw directory bytes** shows the exact descriptor bytes; the selected-item summary gives their absolute image offsets. The full diagnostic detail is also available in the summary tooltip.

New Extended DSK images store the actual sector length in every descriptor (for example, `00 01` for 256 bytes). A legacy zero length is reported by Analyzer as `DSK_EXTENDED_ZERO_LENGTH`: MZTools can recover bytes from N for inspection, but FlashFloppy treats the stored zero as no sector data. Opening and ordinary saving preserve existing descriptors; they do not silently repair them. Explicit repaired copies change only zero length fields and do not overwrite their source. This behavior follows [FlashFloppy's Extended DSK reader](https://raw.githubusercontent.com/keirf/flashfloppy/master/src/image/dsk.c). Static validation does not certify hardware boot.

### DSK Patch Format

Version 1 `.mzpatch.json` files contain source/target SHA-256, source/target geometry signatures and sector byte ranges addressed by physical track, descriptor index, C/H/R/N and offset. Every range includes expected original bytes, their SHA-256 and equal-length replacement bytes. Matching only a sector ID is never sufficient.

In **Disk Map**, use **Apply DSK patch...** to preview a patch against the current document. Source SHA-256, geometry, original bytes and hashes, descriptor identity, range bounds and overlap are checked. The private result is reopened, serialized, analyzed and verified against the exact target SHA-256 before Apply. Any mismatch leaves the document unchanged; saving remains separate. Filesystem damage is rejected. Version 1 supports only sector payload changes with identical physical layout; header/descriptor, padding, trailing-data and geometry changes cannot be exported. Patch JSON is limited to 16 MiB. Semantic patches are not implemented.

The resizable comparison window has three levels and two physical maps. Scrolling either map moves both maps to the same vertical/horizontal offset; a smaller map stops at its own boundary without pulling the larger map back:

- **Container:** cylinder/side counts, creator, complete container header, physical track/block sizes, missing tracks, raw track headers/descriptors, track padding and trailing bytes.
- **Physical sectors:** paired by physical track index and descriptor index. C/H/R/N, stored descriptor bytes, ST1/ST2, payload length and payload contents are compared at that position. Matching R alone never hides reordered descriptors.
- **Filesystem:** available for the same safely recognized filesystem family without allocation errors. Files are matched by name/extension (and CP/M user). Content, metadata and allocation are reported separately. CP/M includes DPB/maps, raw extent fields, RC, attributes, slots and allocation pointers; FSMZ includes directory variant, DINFO/volume/bounds/bitmap, slots, native metadata and block ownership; MRS includes raw entries, LOAD/EXEC, file ID and FAT ownership. Unknown, inconsistent or ambiguous filesystems retain physical/container comparison with an explicit explanation instead of guessed logical matches.

Rows show **same**, **changed**, **only in left**, **only in right** or **unavailable**. Unchanged rows are hidden initially and can be shown. Moving a CP/M file between users is shown as removal/addition in the two namespaces, not heuristically paired. Selecting a row highlights its sector or file blocks in both snapshot maps and the corresponding left-side region in the main Disk Map; right-only items highlight only the right snapshot. Clicking a map sector selects its physical comparison row.

**Hex diff...** is enabled for selections containing bytes. It shows differing relative offsets and left/right values (`--` for an absent byte). For metadata-only changes it defaults to descriptor/raw directory bytes; payload and native structure views can be switched. The UI shows up to 10,000 differing offsets per view, explicitly reporting the complete difference count; all bytes are compared. There is no writable hex or patch application in this phase.
