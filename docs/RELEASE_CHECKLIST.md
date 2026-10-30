# MZTools desktop release checklist

- [ ] Archive final Windows/Linux/macOS x64/arm64 TRX with exact source/package hashes.
- [ ] Run actual native window workflows on every target; mark skipped platform picker explicitly UNTESTED.
- [x] Verify deterministic media outputs and integer FLAC PCM on Windows/Linux x64; macOS/ARM execution remains pending.
- [x] Validate synthetic recording under UI load, stop/start/cancel/disconnect and archive/preview loss separation on Windows/Linux x64.
- [ ] Physically test each OS input backend, actual formats, processing provenance, busy/unplugged inputs and a 10–30 minute recording.
- [ ] Verify all required real monitor DPI combinations, keyboard, external OS drag/drop and screen reader.
- [x] Review native libraries, package/content hashes, original licences, source/relink information and matching CPU architecture; native vulnerability scope is documented separately.
- [x] Build and inspect all six self-contained developer archives with file checksums; execute extracted Windows/Linux x64 runtime diagnostics.
- [ ] Sign Windows/macOS distributions and notarize macOS using release-owner credentials; no unsigned build is labelled signed.
- [ ] Complete feature/UI parity acceptance before retiring the stable WPF frontend.

The scripts and CI prepare reviewable artifacts. Unavailable hardware, signing
credentials or unexecuted remote jobs must remain UNTESTED, not inferred PASS.
WPF removal is a separate retirement step after parity is demonstrated; preserving
the existing build avoids silently replacing a proven Windows tool with an
unverified platform release.
