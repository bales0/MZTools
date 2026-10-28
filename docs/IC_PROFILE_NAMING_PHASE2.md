# IC legacy reference removal — phase 2

**Subsequent UI update:** [the profile reference picker](PULSE_REFERENCE_UI.md) now
projects twelve simple profile choices from this retained technical catalog of
fifteen entries. IC/NORMAL aliases share guides and produce nine unique physical
tests. Technical/service variants are visible only in the catalog browser by default.
The phase-2 scope and verification below remain a historical record.

Date: 2026-10-28. Separately approved after variant-A phase 1. The existing working
tree includes the previously approved recorder and phase-1 changes. This phase
removes the three obsolete IC comparison rows; it does not change tape profiles.

## Active catalog and UI

Removed IDs: `iccompat2`, `iccompat3`, `iccompat4`. The following values remain only
in the historical audit, not in executable built-in reference tables:

- 144/112/256/224 µs;
- 112/96/224/192 µs;
- 112/80/176/160 µs.

The active comparison catalog now has **15** entries. Exact `ic1200`, `ic2400`,
`ic2800`, `ic3200` writer rows retain all their values. Independent `rxic2/3/4`
receiver constraints remain. TC exact timer and compatibility rows and FAST3 remain;
the ProjectCompatibility category is still required for those other entries.

`AudioPulseReferenceControl` directly binds the active observable collection again.
The legacy visibility checkbox, filtering, auto-reveal logic and unused naming helper
are removed. Consequently the cleanup applies to Entries/Comparisons/BuiltIn,
comparison dropdowns, automatic nearest-reference selection, manual All/Selected
profiles, measured-pass reference settings, live projections and downstream reports.
No production C# references the removed IDs or the removed toggle/helper. Historical
reports are not rewritten; user-defined references remain explicitly user-defined.

The main document still uses NORMAL 1:x with ROM/direct-IC explanations, IC FAST IPL
1:x for existing loader profiles and a separate TC family. Enum values, identifiers
of retained entries, .mfi/.m2i/.mti encoding, loaders, speed bytes, timings, delays and
framing are unchanged. Phase 1's shared IC writer labels remain.

## Files and provenance

- Media/PulseReferenceCatalog.cs: remove exactly three rows.
- AudioPulseReferenceControl.cs: remove the obsolete checkbox/filter/reveal path.
- Mzf.cs: remove the unused legacy-name helper; keep all stable profile mappings.
- PulseReferenceCatalogTests: assert absent/unknown removed IDs, 15 active comparison
  rows and preservation of IC writers, receivers, TC and FAST3. Exact IC value tests,
  sidecar byte tests, structural loader tests and export fingerprints remain enabled.
- Recorder STA smoke: verify the active collection has no legacy IC rows/toggle.
- Existing audio STA smoke: inspect the actual manual recovery All/Selected profile
  list, confirming the four exact IC writer rows and the shared active collection.
- Historical copier reference: add an archive clarification for the old SD2CMT2 IC
  values, without replacing the original derivation or removing receiver/TC sections.
- Current guide/README and the P0/P1 audit link to this phase; phase-1 results remain
  as history with a clear subsequent-update note.

The user's current SD2CMT2 statement supersedes the old source document's description
of those tuples as current compatibility waveforms. The timing audit remains a
provenance source. Exact timing measurements or additional families were not introduced.

## Verification

Release solution build: zero warnings/errors. Focused catalog/value/fingerprint tests:
33 passed. Recorder STA UI: 74 checks passed. Existing audio-analysis STA smoke also
passed its new direct manual-list check and prior histogram/reference/navigation/
recovery/cancellation/report/zoom checks. Its synthetic observation was 12 pans in
66 ms, 501 visible buckets out of 65,536; not a hardware performance measurement.

Portable capture tests: 191 passed. Windows integration tests: 44 passed. Full legacy
suite: 1163 passed, 0 failed, 3 missing-fixture skips. Combined unit result: **1398
passed / 0 failed / 3 skipped**. All **39** waveform export fingerprints match the
unchanged golden. The eight previously documented optional legacy early returns
remain and are not hardware evidence. Test commands use the existing
dotnet-host workaround; the legacy run clears output-writing environment variables.
The golden export file was not regenerated and retains SHA-256
`7B142B0D300DFDAD731032E9DD8A55F900082F84A9789C41C05310EC0E94EB49`.

Reports: ignored TestResults/IcNamingPhase2/{capture,windows,legacy}.trx.
Recorder STA screenshots: MZTools.Capture.Windows.Tests/UiSmoke/bin/Release/net10.0-windows/
smoke-artifacts/2e29c28392114531b5f18e0ba407416c. Original audio STA artifacts: local temp
mztools-audio-ui-099b6dc4591f4c31a7734a2d1a2363f4. Tests did not start physical capture.
Physical-device/boot acceptance and actual per-monitor DPI remain unverified. No
commit, push, release or version/package change was performed.
