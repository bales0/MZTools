# Operation state and MZF compression cache — 2026-11-19

## Authorization and baseline

The user approved G1 implementation and G2 tests/builds after the read-only G0 report in this conversation. Specification: `specification/MZTools_Codex_G0_Systemova_Oprava_Falesneho_Working_2026-11-19.md`. HEAD remains `12c1e8415167ec94210fb28f67b9c1576d2ff034`. Existing Description/HEX/version/project changes and the user's deleted specification were retained. No commits were made.

## G0 findings and scope

The confirmed shell cause is an operation that awaits a modal form or confirmation without entering the existing input-wait scope. `IsBusy` then truthfully owns the document lock, but the old `IsWorking = IsBusy && !IsAwaitingInput` misrepresents that passive wait as computation. Native input/output pickers already used the wait scope. `ShellViewModel.OnDocumentStateChanged` and `RefreshCommands` do notify `WpfStatus`; a stale binding after a fully completed operation was not demonstrated.

Reviewed areas: DocumentCoordinator; ShellViewModel and ShellWpfPresentation; MainWindow normal/compact presentations; DesktopDialogs/Media/Disk/QuickDisk/AudioImport adapters; MediaOperations and MzfCompressionService; IplDskOptionsDialog, SaveOptionsDialog, CompressionSettingsDialog, MultiGameIplDskOptionsDialog/MultiGameIplRow and DskEditorWindow; BatchProcessWindow/BatchWorkspace/BatchProcessDialog; AudioSession viewport/recovery, RecoveryWindow, AudioSignalAnalyzerWindow/Selection, AudioRegionRecoveryWindow/Preview; DiskVisualWindow/Details, DskVisualInspectorWindow/Details, QuickDiskVisualInspectorWindow; RecorderModel and both recorder hosts; WavAnalysisProgressWindow; existing ShellPresentation/DocumentLifecycle tests.

| Scope | Owner / lifecycle | Change |
| --- | --- | --- |
| Avalonia shell, normal and compact | DocumentCoordinator, one operation CTS, finally cleanup | Explicit Running/AwaitingInput/Cancelling/Completed/Failed/Cancelled presentation; actual completion releases the lock. |
| Modal forms, confirmations, New QD | Existing document operation remains owner | Input-wait scope hides working animation and working Cancel while retaining the conflict lock; exception/close unwinds the scope. |
| Avalonia Batch | Independent window coordinator; scan generation and pending scans | Progress follows IsWorking, Stop follows CanCancel, instead of IsBusy. |
| Avalonia Deep Analysis | Independent coordinator and local preview CTS | Cancel reflects the actual owned task, progress follows work state; closing awaits idle without polling. |
| Avalonia disk analysis | Local detail/report tasks; explicit document operation for export | Window tracks and drains its tasks before close, cancels its own report rather than unrelated work, reports cancellation completion. |
| Recorder | Independent capture lifecycle | Cancel offered for cancellable input/start work; hidden during noncancellable Stop/finalize and Discard. Cancelling retains busy until completion. |
| WPF compression/COM previews | Each request owns its CTS until its worker finishes | Replaced request cancelled; finally cannot clear a newer request; no premature CTS disposal. |
| WPF Batch | Local work CTS and per-run generation | Numeric progress disables indeterminate mode; queued callbacks cannot affect another run or overwrite Stopping. |
| WPF audio progress/recovery | Local CTS and generation/identity checks | Late progress ignored after cancellation; Cancel disabled after request, completion/cancellation retains a truthful result. |

Atomic write/rollback, source fingerprint and document revision checks remain in place. There is no timer that clears live busy state, no global singleton busy shared between independent windows, and no change to compression algorithms or codec output rules. The existing preview debounce timers retain their original purpose. The WAV progress window icon uses its explicit assembly URI so that the real dialog also loads in the smoke host.

## MZF cache

`MzfCompressionCache` is an in-process, thread-safe LRU with a 32 MiB accounting budget. The key contains SHA-256 and length of a byte snapshot and every `MzfCompressionOptions` field. Only compressed stream bytes and delta are cached. Each cache consumer receives independent bytes. Failed/cancelled computations are not cached, and cancelled requests cannot return cached results. Oversized entries are not retained.

Target-independent payload reuse deliberately excludes source metadata and the target from the payload key: source validation, target validation, address layout, loader and header construction still execute for the current request. Thus changing name/LOAD/EXEC/Description/provenance never returns a stale TapeRecord. Auto candidates use the same payload cache, so Auto and later explicit choices reuse completed variants. Concurrent unfinished requests may compute independently; only completed results are reused. Cache does not persist across app restarts.

Both frontends display packed versus original program-data size and percentage/saving. The comparison includes the loader in packed data and excludes the 128-byte MZF header and trailing data from both program-data counts. For a loaded recognized compressed record the reference is the decoded size stored in its loader; with partial/Skip compression this describes the restored portion, not omitted source bytes. Unknown compression never invents an original size.

## Regression matrix and evidence

Tests use controllable completion for running/cancelling/modal states and queued progress; no speed-based timeout is used to decide whether an operation has completed. Coverage includes success/failure/retry/reentrancy, nested modal input, real compression/confirmation/New QD dialogs, old callbacks during waiting/cancelling/another generation/idle, independent coordinators, recorder enumeration cancellation and a gated Stop that must not advertise Cancel.

Cache tests cover A-B-A, all option fields, changed input, cancelled/faulted computations, independent output arrays, LRU eviction, oversized entries, current header/address reconstruction, decompression round-trip and target validation even after a cache hit. Existing reference COM compression fingerprints are checked separately; algorithms were not replaced.

Evidence lives in `TestResults/WorkingCompression/`:

- `windows-targeted*.trx`, `linux-targeted*.trx`: operation/cache/lifecycle/shell/parity regressions.
- `windows-desktop-final.trx`, `linux-desktop-all.trx`: broad Desktop suite.
- `wpf-final.log`: WPF smoke including the actual compression dialog and delayed WAV progress after Cancel.
- `windows-core*/`, `linux-core/` and logs: extended Core verification.
- `publish.log`, `windows-runtime.json`, `linux-runtime.json`: self-contained distributions and native runtime checks.

Broad Desktop has one previously known unrelated failure: `BatchWindowAndHexCursorTests.FolderScanRejectsStaleGenerationAndKeepsUnsupportedInputsVisible` expects four scan entries including unsupported `.txt`, while unchanged `BatchInputSelection` filters it and returns three. This task does not change that policy or relax the test. The optional thirty-minute media benchmark is skipped unless `MZTOOLS_SHA256_BENCH` is explicitly configured.

The initial Windows Core run used FAST output without copied media, causing missing-fixture failures. Available ignored DSK/HFE/MZF/M12/QD/LEP/ROM/ReferenceGolden inputs were subsequently copied to test output only; the affected cases are rerun separately. Source media were not modified. Optional absent reference media remain explicit skips, not successes. All eight missing-fixture failures passed on the two focused reruns; the original failed run is retained as evidence.

Current runnable application files are published only to `bin/win-x64` and `bin/linux-x64`. Build/compiler/evidence data remain under obj/TestResults, outside distributions. Both published runtimes validate .NET 10.0.11 and libsndfile 1.2.2 without starting capture.

## Final results

| Verification | Passed | Failed | Skipped | Notes |
| --- | ---: | ---: | ---: | --- |
| Final related Desktop, Windows | 130 | 0 | 0 | After the final local Cancel and initialization-reentrancy fixes. |
| Final related Desktop, Linux | 130 | 0 | 0 | Same final source and cases. |
| WPF smoke | 128 | 0 | 0 | Synthetic input; no hardware capture. |
| Extended Core, Windows, unique outcomes after fixture reruns | 483 | 0 | 2 | Initial full run: 475 pass, 8 missing-fixture failures, 2 skips. All 8 pass on focused reruns; not a claim that the initial run was green. |
| Extended Core, Linux | 487 | 0 | 1 | Available small fixtures supplied before starting. |
| Broad Desktop, Windows | 508 | 1 | 1 | Known Batch scan expectation; optional thirty-minute benchmark skipped. |
| Broad Desktop, Linux | 516 | 1 | 1 | Same known Batch failure and optional benchmark. |

The broad Desktop runs precede the final local Cancel/reentrancy hardening; the final 130-case related runs verify those final changes. The unchanged Batch failure was not hidden, skipped or rewritten. Core codec output remained byte-exact against the stored COM compression reference and the PWM export reference.

The final Windows/Linux publishing completed successfully. Both resulting canonical executables passed runtime validation again after the final publish. Machine-readable evidence: `TestResults/WorkingCompression/summary.json`; final build log: `publish-final.log`. `git diff --check` passes. No changes were committed.
