# Batch media workflows

Updated: 2026-10-10.

The Batch Process dialog uses the existing individual disk, tape, audio,
compression and QuickDisk services. A source is either a manually selected file
list or a folder scan, never both. The file picker, folder scanner and service
preflight enforce the operation's and conversion target's supported extensions. Changing the operation
retains selected files but marks unsupported inputs explicitly; strict preflight
blocks the batch if any row fails.

| Operation | Inputs | Result |
| --- | --- | --- |
| Info / test integrity | DSK, HFE, MZF, M12, MZ0, MZ7, MZT, WAV, FLAC, LEP, L16, QD, QDF, MZQ | Combined read-only contents/metadata/findings and container/record/CRC/checksum/recognized decompression validation; Integrity = Passed / Failed / Not verifiable |
| Convert format | DSK/HFE or tape/audio/QD families | Existing disk conversions; tape/audio/QD conversions listed below |
| Compress programs | Tape/audio/QD family | Full ZX0/ZX7 settings, verified restoration of the source program |
| Decompress programs | Tape/audio/QD family | Recognized MZTools ZX0/ZX7 loaders; unknown/custom packed input is explained and skipped |
| Extract all | DSK and all tape/audio/QD inputs | Per-input directory in the selected extraction format |
| Install boot system, patch, defragment, safe repair | DSK | Existing disk services |
| MZF/M12 → COM compatibility analysis | MZF, M12, MZ0, MZ7 | Existing read-only compatibility report |

## Conversion and compression

Tape/audio/QD targets: MZF, M12, MZT, WAV, FLAC, LEP, L16, QD (Sharp),
QD (HxC), QD (uniform), QDF and MZQ. Each input produces an independent output;
the batch does not merge separate source files. Disk targets accept disk inputs;
tape targets accept decoded SHARP tape/QD inputs. Unsupported family/target
combinations produce an explanation in their row.

Multiple records targeting MZF/M12 produce a directory of numbered files and
sidecars. Audio is decoded to complete programs before conversion or compression.
The **Use heuristic analysis (WAV / FLAC)** checkbox explicitly chooses heuristic
analysis versus the standard decoder; it defaults to standard. Heuristic mode
uses the existing timing/channel/checksum recovery service without silently
falling back to a different decoder. The report names the selected decoder.
Audio output is regenerated from the selected records and their tape profiles;
it does not preserve the original analogue recording. WAV/FLAC rates are 22050
or 44100 Hz. Non-SHARP/unknown QD can be inspected but cannot be converted or
compressed as SHARP programs.

Compression exposes None, Auto, ZX0, ZX7, direction, ZX0 quick and ZX7 embedded
loader settings in the shared panel. Skip remains visible but nonzero values
are rejected with an explanation: batch outputs must be independently verifiable
complete programs. Recognized packed input is decoded before recompression;
the decoded bytes, LOAD and EXEC must match after a compression round trip.
The original header description cannot be recovered where an embedded ZX7
decoder previously overwrote it; the row reports this limitation.

Compression/decompression retains M12 and native QD/QDF/MZQ containers; other
single MZF-family inputs produce MZF and MZT/audio inputs produce MZT. A QD
target cannot retain an executable MZF header/embedded decoder. Its 38-byte
description limit and loss of tape profile metadata are reported before execution.

Extract all has a separate format selector: Native, BIN, MZF, M12, MZT, WAV,
FLAC, LEP, L16. Each source file/program is exported independently. Native keeps
disk file names and raw payloads; for tape sources it means MZF with MFI.
WAV/FLAC extraction uses the selected sample rate. Disk sources need native
type/LOAD/EXEC metadata for tape/audio outputs; CP/M raw files cannot acquire a
fabricated MZF header, and receive an explanation to choose Native/BIN instead.
BIN exports the program body and preserves any trailing record bytes separately.

## Metadata and integrity

MZF outputs receive MFI, M12 outputs receive M2I, and MZT receives MTI.
Sidecar profiles are loaded and validated during preflight. Missing and present
sidecars are fingerprinted as dependencies, so creation, deletion or modification
after Preview prevents execution. Program trailing data is preserved in MZF/M12;
MZT container trailing data is preserved in MZT or extracted separately as
`container-trailing.bin`. A target unable to retain trailing bytes is refused.

MZF/M12/MZT have no payload checksum. Their test validates structural lengths,
metadata and any recognized compressed stream, and explicitly states that this
cannot detect every arbitrary data change or certify execution. QD/QDF and audio
use existing frame/block checksum checks. Undecodable/partially recovered audio
is not reported as fully valid and cannot be converted automatically. Disk checks
include analyzer errors/unsafe findings and HFE sector CRC findings.

## Outputs and validation

Preview writes no user outputs. Encoding/reopen checks may use disposable files
under the system temporary directory. Every single media output is reopened and
its records/header/payload compared; waveform and QD outputs undergo their decoder
checks. Originals and sidecar dependencies are checked again at Execute.

Tape/audio/QD operations always use a separate output folder. Replacing originals
remains available only for the existing supported disk operations. Existing output
files, sidecars and cross-input name collisions block writing. Tape/sidecar pairs
are staged, verified and moved without overwrite; a failed sidecar move rolls back
the newly created main file. Existing files are preserved. Multi-program extraction
uses the existing staged-directory writer. Preflight and execution show per-file
progress, and reports include failures as well as successful rows.

Regression coverage includes all twelve tape targets, audio/QD back-conversion,
ZX0/ZX7 round trips and recompression, M2I preservation, changed sidecars, late
output collisions, trailing bytes, damaged QDF CRC, mixed catalog inputs and
operation-specific extension rejection.

## Cancellation and UI state

Batch File Details opens a structured read-only window. Its header identifies the
source, format, processing status, integrity result and file/program count.
Contents lists decoded tape programs or disk directory entries in sortable
columns (name, byte size, type, hexadecimal LOAD/EXEC and applicable tape metadata
or disk user/attributes). HFE contents are listed only when the validated semantic
projection is available. Empty/unreadable sources show properties or errors.
Properties includes container, audio decoder and checksum/finding statistics;
Messages separates result, warning and error rows. The full original report
remains available in Technical details. Tables support Ctrl+C with column headers.
Structured contents and decoder properties are retained in JSON reports too.
Tape profiles use their display names (e.g. TC 1:2 and NORMAL 1:2).
Info / test integrity opens the Integrity checks tab with one row per check, including
scope, outcome and limits. Tape rows cover header/body length, recognized stream
decompression, available tape/frame checksums and complete source recovery.
Disk rows cover container/filesystem findings and available sector CRCs.
Unavailable checks are explicitly marked; program execution is not tested.
The UI exposes one combined catalog/integrity operation, selected by default,
instead of separate entries. Audio is decoded once through the selected
checksum-validating reader; recognized compressed records are then decompressed
for validation. No extra catalog metadata is promised by the integrity test.
Check rows are in JSON too. The legacy internal Analyze operation remains
available for existing non-UI callers; the combined UI uses TestIntegrity.

Stop stays available while Preview or Execute is running. It cancels pending
files, folder enumeration, compression, heuristic/standard audio decoding,
waveform generation and FLAC encoding at their cancellation checkpoints. Disk
service calls finish their current step before the next checkpoint. A final
atomic file/pair/directory commit completes as a unit, and already committed
outputs remain saved. Temporary outputs are cleaned if cancellation occurs
before commit. Cancelled rows are retained for reporting, and a cancelled preview
cannot be executed or resumed; the user must run Preview again.

All input/settings controls, including Clear file list outside the settings
panel, are disabled during processing. The Clear handler also guards against
programmatic invocation while busy. Closing requests cancellation and defers
closing until the active worker has finished cleanup. UI smoke coverage checks
Stop, input locking, heuristic/extraction controls and post-cancellation recovery.
