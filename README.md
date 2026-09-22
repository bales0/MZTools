# MZTools

MZTools is a utility for converting, inspecting and editing SHARP MZ QuickDisk and tape files.

> **This project is based on the original [QDTool by Martin Lukasek](https://github.com/mlukasek/QDTool).**
>
> MZTools extends the original application with tape waveform, loader, compression,
> IPL floppy and low-level QuickDisk functions in one unified interface.

The goal of this fork is to keep the original application simple for normal MZF/MZT/MZQ/QDF/QuickDisk work, while also providing a more complete toolset for SHARP MZ-700/MZ-800 tape and QuickDisk preservation, conversion and analysis.

<img width="786" height="443" src="/images/MZQDTool_scr_2026-09-19.png">

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

The final result is converted directly into MZTools tape records; no intermediate WAV, MZF or MZT file is required. If heuristic recovery is ambiguous, MZTools also tries the standard decoder and accepts its result only when the tape checksum is valid.

The statistics window shows the selected source, checksum state, loader/profile evidence and measured timing information for recovered records.

### Tape waveform export

MZTools can export tape records to:

- WAV
- LEP
- L16

WAV export uses standard 44.1 kHz, 8-bit mono PCM suitable for playback into SHARP MZ cassette input hardware.

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

Advanced `Export...` can create self-extracting MZF/MZT records using integrated
ZX0 or ZX7 compression. No external compressor executable is required. The
available options are:

- ZX0 optimal or quick compression,
- ZX0/ZX7 forward or backward decompression,
- ZX7 decoder embedded in the MZF header,
- expert partial compression using an existing prefix (forward) or suffix
  (backward),
- Auto selection of the smallest safe standalone result.

Compression is applied to exported clones and does not modify the open document.
For MZT export the selected policy is evaluated separately for every record.
Packed `.mz0` and `.mz7` files can be opened as self-extracting
MZF files; automatic decompression back to the original MZF is not provided.

The compression engines and MZF loader builders are ports of
[mz0](https://github.com/bales0/mz0) and
[mz7](https://github.com/bales0/mz7). Attribution and license text are in
`THIRD_PARTY_NOTICES.md`.

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

MZTools can generate 44.1 kHz 8-bit mono WAV files and analyze supported PCM WAV recordings at several common sample rates and bit depths.

### FLAC

FLAC is supported as an **input** format for audio/tape analysis.

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
