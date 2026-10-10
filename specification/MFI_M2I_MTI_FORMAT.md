# MFI / M2I / MTI playback metadata and legacy MZI status

Updated 2026-10-08 for MZTools Phase E. Companion mapping cross-checked against
MZ-SD2CMT2-Reborn, commit `600a788`, firmware guide for **2.0.2**
(`guide/MFI_M2I_MTI_FORMAT.md`, `src/formats/mzi_sidecar.cpp`).
Earlier M12 implementation reference was commit `6453c37`.

External metadata names are explicit:

- `GAME.MZF` -> `GAME.MFI`
- `GAME.M12` -> `GAME.M2I`
- `TAPE.MZT` -> `TAPE.MTI`

There is no `.MZI` fallback. The internal source/API name `mzi_sidecar.*` is
kept only to avoid an unrelated project-wide rename.

## Scope and legacy `.MZI`

| Main file | Companion | Records | MZTools behavior |
|---|---|---|---|
| `.MZF` (also existing `.MZ0` / `.MZ7` aliases) | `.MFI` | one | matching TYPE/SPEED profile |
| `.M12` | `.M2I` | one | same syntax/parser as MFI |
| `.MZT` | `.MTI` | 1-based `RECORD=n` sections | per-record profile |
| any main file | `.MZI` | legacy name only | never read, written, merged or used as fallback |

`mzi_sidecar.cpp` is an internal firmware source filename, **not** an instruction
to generate `.MZI`. MZF and M12 may share a basename without sharing metadata.
Renaming a main extension requires writing the matching companion explicitly;
MZTools never guesses that an old sidecar belongs to the new format.

The firmware PLAY/browser/clock behavior described below belongs to
MZ-SD2CMT2-Reborn. MZTools is the desktop editor and exporter; it does not run
that firmware UI. A sidecar selects tape playback, not CP/M compatibility,
relocation, a COM launcher, or proof that an arbitrary program can run under CP/M.

## MZTools parser and transactional save rules

MZTools trims lines and keys/values, accepts CRLF/LF and case-insensitive TYPE
and keys. Empty lines and full-line `#` comments are ignored. Unknown keys do
not define a profile. Last repeated TYPE/SPEED key wins. NORMAL/MZ700/IC/TC
require an explicitly supported SPEED; UL variants accept absent/empty SPEED
only. An unsupported combination is invalid; no nearest-speed substitution.
Inline comments are not a supported value syntax.

MTI selects positive 1-based `RECORD=n` sections. Text before a valid section
is ignored; an invalid section header has no active record. If a record section
is repeated, the last section replaces the earlier one. Missing/invalid metadata
falls back to NORMAL 1:1 for that record, without affecting other records.
These duplicate/whitespace details describe MZTools' parser; they are not a claim
that every firmware release treats malformed input identically.

Save/export can generate the matching companion. If the destination already has
one, it is regenerated from current profiles together with the main file through
the existing transactional writer. Failure restores the previous pair. Save As
uses the **destination** basename/extension and does not move a source sidecar.
MZT section numbers are regenerated in output record order after reordering or
exporting a selection. Trailing-data preservation is an explicit save option;
TYPE/SPEED sidecars do not describe trailing bytes or multipart dependencies.
Reconstructed IPL exports to MZF/M12 do not create a sidecar automatically.

Phase E Verify, Compare, Normalize and MZF/CP/M analysis do not read a sidecar
as execution proof and do not change playback profiles. Normalize operates on
explicit DSK creator padding only; it never normalizes tape records or metadata.

## MFI for one MZF

Example:

```text
TYPE=NORMAL
SPEED=1:3
```

Intercopy and TurboCopy examples:

```text
TYPE=IC
SPEED=1:1
```

```text
TYPE=TC
SPEED=1:1
```

Loader without a SPEED field:

```text
TYPE=UL_MZ800
```

MFI is used only when PLAY loader selection is `AUTO`. A manually selected
loader/profile always has priority.

## M2I for one M12

M12 contains one Sharp 128-byte tape header followed by the declared payload,
using the same record parser as MZF. M2I shares the MFI single-record TYPE/SPEED
syntax and supported profile table below; it has no `RECORD=n` sections.

```text
TYPE=IC
SPEED=1:2
```

For `GAME.M12`, place this content in `GAME.M2I`. Exact companion lookup permits
`GAME.MZF`/`GAME.MFI` and `GAME.M12`/`GAME.M2I` to coexist. No cross-format fallback
or `.MZI` fallback occurs. In firmware, manual PLAY loader selection takes
priority; M2I is consulted only for AUTO. The extension does not select a machine.

MZTools loads matching metadata when opening/importing M12, retains M12 as a
distinct document format, and saves/exports single records through the shared
MZF writer. The save dialog offers Generate M2I and trailing-data preservation.
Existing target M2I files are regenerated transactionally. Save As does not copy
the source's old sidecar binding to a new basename unless generation is selected
or the target already has its own M2I.

## MTI for an MZT container

Each MZT logical MZF record can use its own loader/profile. Record numbering is
1-based.

```text
RECORD=1
TYPE=NORMAL
SPEED=1:3

RECORD=2
TYPE=IC
SPEED=1:1

RECORD=3
TYPE=TC
SPEED=1:1

RECORD=4
TYPE=UL

RECORD=5
TYPE=UL_MZ800

RECORD=6
TYPE=UL_MZ700

RECORD=7
TYPE=MZ700
SPEED=1:3
```

The MTI parser is streaming. It uses the existing shared SD work buffer and
does not build a record table in SRAM. The SD layer owns one sequential stream,
so AUTO MZT temporarily closes the MZT, reads only the requested MTI section,
then reopens the MZT and seeks back to the saved position.

## Supported TYPE / SPEED combinations

| TYPE | SPEED | Firmware mode |
|---|---|---|
| `NORMAL` | `1:1`, `1:2`, `1:3`, `1:4` | native / historical MZ-800 timing family |
| `MZ700` | `1:1` | native MZ-700 timing |
| `MZ700` | `1:3` | MZ-700 FAST3 loader |
| `IC` | `1:1`, `1:2`, `1:3`, `1:4` | Intercopy/FASTIPL loader + IC payload timing |
| `TC` | `1:1`, `1:2`, `1:3` | Turbo Copy loader + TC payload timing |
| `UL` | none | classic Ultra Fast, automatic LOW/HIGH placement |
| `UL_MZ800` | none | MZ-800 header-only Ultra Fast |
| `UL_MZ700` | none | MZ-700 header-only Ultra Fast |

`TC 1:4` is intentionally not accepted: TurboCopy V1.22 provides the 1:1,
1:2 and 1:3 timing family used by this firmware; no verified TC 1:4 loader
readpoint/writer profile is defined.

### Copier speed bytes used by generated loaders

| Mode | Loader/readpoint byte | Source |
|---|---:|---|
| IC 1:1 | `$4D` | Intercopy V10.2 1200-Bd row |
| IC 1:2 | `$20` | Intercopy V10.2 2400-Bd row |
| IC 1:3 | `$16` | Intercopy V10.2 2800-Bd row |
| IC 1:4 | `$11` | Intercopy V10.2 3200-Bd row |
| TC 1:1 | `$52` | native MZ-800 1Z-013B DLY3 used by TC loader family |
| TC 1:2 | `$29` | TurboCopy loader family |
| TC 1:3 | `$1B` | TurboCopy loader family |

CRLF and LF are accepted. TYPE is compared case-insensitively.

## Selection rules

1. Manual PLAY loader/profile always wins.
2. MZF AUTO: missing/invalid MFI -> `NORMAL 1:1`.
3. MZT AUTO: missing MTI, missing `RECORD=n`, or invalid target section ->
   `NORMAL 1:1` for that record only. The next record is resolved again.
4. M12 AUTO: matching valid M2I; missing/invalid M2I -> `NORMAL 1:1`.
5. `.MZI` is not read or written.

## MZT record selector

Opening an MZT prepares record 1 but does not immediately arm/start the tape.
The PLAY screen first acts as a lightweight record selector:

- `UP` = previous record, with wrap
- `DOWN` = next record, with wrap
- `SELECT` = confirm/start the selected record
- `LEFT` = return to browser

No index array is retained. Every selection rescans the MZT headers and seeks to
the selected record. The LCD shows the selected record number/count, the MZF
header title, loader/profile and the duration of that record.

When an MTI exists, the selector explicitly shows `MTI`; MZT line 0 also marks
the record counter with `I`. For MZF, line 0 explicitly shows `MFI` when its
sidecar exists. M12 similarly shows `M2I` for its matching metadata in the firmware.

## Per-record time

MZT does not display one total duration for the entire container. The active
clock and nominal duration belong only to the current logical record. When the
next MZT record becomes active, elapsed time resets to `00:00` and its own
nominal duration becomes the new total.

- NORMAL 1:1/1:2/1:3/1:4: exact current-record generated waveform duration
- MZ700 1:1: current-record duration
- MZ700 FAST3: current-record generated FAST3 duration including fixed start delay
- IC 1:1/1:2/1:3/1:4: patched header + IC payload duration
- TC 1:1/1:2/1:3: patched header + TC loader + TC payload duration
- UL / UL_MZ800 / UL_MZ700: unknown (`--:--`) because payload timing is governed
  by the live WRITE/SENSE handshake

MOTOR pauses are not included in the nominal record duration.

## UL / UL800 / UL700 boundaries inside MZT

A completed Ultra Fast payload returns control to the loaded program. Therefore
an UL record may **not** automatically feed the next MZT record.

For an MZT containing another record after UL/UL800/UL700:

1. finish the UL payload,
2. park at the next MZT boundary,
3. MOTOR mode requires a real `LOW -> HIGH` cycle before the next record starts,
4. MANUAL mode requires a new `SELECT` press.

This permits several UL records in one MZT as separate LOAD operations without
incorrectly assuming the Z80 is already waiting for the following header.

Every new MZT record is prepared from its original 128-byte header. UL/UL800/
UL700/MZ700 FAST3/IC/TC loader generation and LOW/HIGH placement are therefore
re-evaluated independently for that record, and the loader-visible file end is
clamped exactly to that record's payload.

## Browser behavior

`.MFI`, `.M2I`, `.MTI`, and legacy `.MZI` files are metadata and are hidden from the
normal sorted browser. Filtering happens inside the shared SD browser-entry
filter, so hidden metadata does not count in the visible `N/N` position and does
not participate in previous/next sorting.
