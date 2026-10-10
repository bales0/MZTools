# Audio decoder failure analysis — 2026-10-10

## Tested source and scope

HEAD is `0ef87e798139c8a7dcc09cbca4bd0ad46ff2c65b`; results include the current uncommitted audio implementation. This report distinguishes the initial audit from the first implementation block approved by the user. No deglitch, bit repair or additional decoder was introduced.

Fixture: `C:\333\AudioStarB03A_selection.wav`, 94,011,518 bytes, mono PCM 44,100 Hz / 24 bit, 31,337,158 frames (710.593152 s). Its SHA-256 before and after diagnosis is `B051AB477A227385B76BE223C96A3A35A66E3DBADE139BA0479B22B8265DCFEC`. Independent golden MZF files mentioned in the requirements were not provided; equivalence to those external files remains unverified.

## Confirmed failure and correction

The existing pulse decoder already emitted checksum-valid HeaderValid events for all three programs. `SharpTapeCandidateScanner.AcceptHeader` subsequently rejected them because byte 17 was neither CR nor zero. Their names terminate earlier, and the remaining bytes contain original padding/memory contents. No expected body length was then registered, so no matching body scanner was started. Heuristic and manual results misleadingly reported that no valid header had been detected.

Ordinary header validation now checks the entire 17-byte name field for its terminator. It retains the 128-byte header length and nonzero type checks and preserves all original bytes after the terminator. Intercopy/MZ700/TurboCopy structure handling remains ahead of ordinary header validation.

| Program | Header complete sample (Schmitt, normal) | Time (s) | Original byte 17 | Body bytes | LOAD / EXEC | Body checksum modulo 65536 |
|---|---:|---:|---:|---:|---|---:|
| Exploding Fist | 342340 | 7.762812 | A6 | 37744 | 10F0 / 10F0 | 19836 |
| MANIC MINER 800 | 12439084 | 282.065397 | 4D | 36864 | 2000 / 2000 | 16306 |
| S-BASIC | 22861147 | 518.393356 | 20 | 27552 | 1200 / 7D79 | 28919 |

## Before / after on the real fixture

Manual rows use adaptive classification, both polarities and the original mono channel. No mix, shift or PCM rewrite was applied.

| Mode | Before | After |
|---|---|---|
| Standard | Rejects unsupported 24-bit WAV | Reads 24-bit PCM; still reports no complete Sharp MZ file for this analog recording |
| Heuristic | 0 programs / 0 candidates | 3 programs / 24 candidates / 0 unresolved failures |
| Manual ZeroCrossing | 0 / 0 | 3 programs / 12 candidates / 0 failures |
| Manual Schmitt x1 | 0 / 0 | 3 programs / 12 candidates / 0 failures |
| Manual Schmitt x0.65 | 0 / 0 | 3 programs / 12 candidates / 0 failures |
| Manual AdaptiveZeroCrossing x1 | 0 / 0 | 3 programs / 12 candidates / 0 failures |
| Local manual Schmitt x1 | Valid decoded headers rejected | One correct program in each selected interval, 0 failures |

The diagnostic direct-decoder experiment before implementation recovered all three programs using existing filtered ZeroCrossing/Schmitt extraction, bypassing candidate policy without changing pulse decoding. Complete global MZF bytes matched across both detectors and polarities. The fixed/reference experiments before implementation also failed at header acceptance; an exhaustive all-profile parameter sweep was not performed.

After the fix, heuristic and local manual outputs match the complete MZF fingerprints from that direct-decoder baseline, preserving header metadata and payloads. Local regions include leader context: [0,276), [276,512), [512,end) seconds for the regression test. A separate audit also selected each header interval dynamically. This does not establish equivalence for an arbitrary restart halfway through a block.

| Program | Payload SHA-256 | Complete original-header + body MZF SHA-256 |
|---|---|---|
| Exploding Fist | F8A2749D98DD0C90789C368103702DB2E3186EB5FF8E9AD323616E22809083FC | 68533B2931F667F7FCCE53F6D16606F8391B2C1F73D7242A0BBCCBF7D6951F25 |
| MANIC MINER 800 | 1000363F9D391424EAA68C1893036098833558E6C6804F6F793E55F9DBF47B92 | B1E23A8CB5A56AA26C45F88E7309FCA78E3108E027CFBAFB1AD43E1273162F15 |
| S-BASIC | 97BC721288AF4C33CA28C73B507CCB3D89692D2861B495EFDFA06D4A556A9437 | 19C1F5172674D527042E9E26B7F9F32E01E27517AD6007672A9F47B8F8A3D327 |

## Bounded diagnostics

Checksum-valid headers rejected by ordinary validation now have a separate structured diagnostic: exact completion sample, channel, polarity, detector, rejection reason, recorded/calculated checksums and SHA-256 of the original header. At most 128 examples are retained per operation; total omitted events remain counted. These are decode events, potentially repeated across copies/passes, not a count of unique programs. Import report Messages/Properties and manual Messages show them. If every decoded header was rejected, the failure states that explicitly instead of claiming no header was decoded. Missing/invalid payload assembly reasons remain separate.

This block does not add a complete per-pulse parser timeline, invalid-header byte preview or deglitch controls. The neighborhood of 66.143 s was traced in the initial audit but is not the demonstrated cause of MZTools' shared heuristic/manual failure. Filtered zero-cross successfully recovered the bodies in the direct experiment; it is distinct from independent raw-PCM zero-cross.

## Standard PCM import

WAV now streams through WavPcmStreamReader and shares PCM-to-run collection with FLAC. Its 8-bit 155/100 hysteresis and 16-bit sign threshold semantics are retained; 24-bit uses the same sign rule. PCM is not globally resampled, filtered or converted to heuristic detection. Standard WAV retains arbitrary positive sample-rate support; the visual/heuristic reader continues to validate its documented sample rates. The existing Standard decoder may still fail on analog noise, as this fixture demonstrates. Synthetic programs verify identical original headers/bodies at 8/16/24-bit input.

## Histogram navigation

Before: [0,1021.5] us -> zoom out x2 at anchor 0.9 -> [0,2043] -> zoom in x0.5 at anchor 0.9 -> [919.35,1940.85]. Clamping the left edge changed the duration used as the next zoom anchor. The source histogram counts did not change.

After: repeated zoom at a stationary pointer retains its original duration anchor, including when bounds clamp the viewport. Fit, explicit range changes and pan reset that anchor. Wheel zoom also rebases the captured drag range and pointer origin, so the next MouseMove cannot restore a stale viewport. STA UI tests cover 30 out/in cycles, a real captured drag followed by zoom/MouseMove, unchanged histogram counts, existing pan/zoom/Fit and retained waveform selection.

## Regression execution

Deterministic tests cover early CR with trailing 20/4D/A6 bytes, unchanged complete headers/payloads, explicit rejection of a checksum-valid unterminated name, bounded event retention and Standard 8/16/24-bit equivalence.

The optional fixture test runs only when a local recording is configured; otherwise its real-file section is not executed:

```powershell
$env:MZTOOLS_AUDIO_RECOVERY_FIXTURE = 'C:\333\AudioStarB03A_selection.wav'
dotnet test MZTool.Tests/MZTools.Tests.csproj --filter FullyQualifiedName~RealSelection_HeuristicAndLocalManualMatchValidatedOriginalBytes
```

It checks three programs, lengths, LOAD/EXEC, modulo checksums, full MZF fingerprints, exact local-versus-global header/body bytes, and an unchanged source hash. Its fingerprints come from the documented direct-decoder audit, not unavailable independent golden files. Diagnostic JSON and original audit outputs remain under ignored `acceptance/audio-audit-results` and `acceptance/audio-audit-after`.

Validation: 209 audio/tape backend tests passed with the real fixture configured, including existing exporter/profile regressions. The Release build and synthetic/710.59-second real WAV STA UI checks passed, including histogram boundary zoom and captured-drag navigation. The original fixture remains unchanged.


## Manual histogram and adaptive gate follow-up

Manual histogram measurements now originate from the actual Deep analysis detector callbacks. Completed passes retain separate channel/polarity distributions and bounded problem spans; a pass selector exposes each threshold/profile. The waveform preview consumes only PCM, with no pulse detector invocation. Settings changes mark stored measurements outdated until a scan succeeds; histogram zoom, group visibility and comparison-guide edits do not initiate analysis. SHORT/LONG grouping is a comparison of original measured durations with the pass targets, not a record of decoder bit acceptance.

Adaptive peak-midpoint detection accepts an optional minimum symmetric zero deadband (UI: ±% FS, default 0). It supplements the existing dynamic gate with `max(dynamicGate, deadband)`, preventing small alternating near-zero lobes from generating transitions. Interpolation reads original samples and timestamps; automatic heuristic import is unchanged.

Regression checks compare exact pulse totals and duration sums against detector events for two separate Schmitt thresholds, verify normal/inverted measurements stay separate, ensure waveform-only preview has no histogram pulses, suppress synthetic near-zero ringing without changing the valid pulse spacing, and reject invalid deadband values. STA UI checks cover an initially empty histogram, outdated status, unchanged stored measurements after preview refresh, pass selection, deadband provenance, and retained waveform/histogram zoom and WAV export.

Follow-up verification: the 211-test audio/tape suite passed with the local 710.59-second WAV fixture enabled. After adding silent-channel retention and channel-label consistency, all 45 recovery tests and the synthetic STA UI check passed again. The real WAV STA UI check also passed. Release compilation reported zero warnings/errors. The fixture SHA-256 remains B051AB477A227385B76BE223C96A3A35A66E3DBADE139BA0479B22B8265DCFEC. The runnable build is under ignored `acceptance/audio-deep-histogram`.
