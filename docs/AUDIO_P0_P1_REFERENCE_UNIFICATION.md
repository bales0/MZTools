# P0/P1 audio reference and measurement audit — 2026-10-10

Baseline HEAD: `2e575acc5b6e68f4cfe8f93880c626767ebd5013`. Results below apply to the working tree implementing P0/P1. No source recordings are included in the change.

**Current catalog update, 2026-10-28:** separately approved IC cleanup phase 2 removes
the three old IC compatibility rows from the active catalog, manual All/Selected
profiles and comparison UI. There are now 15 built-in comparison references. Exact
IC writers, IC receiver windows, TC compatibility and FAST3 remain. The original
P0/P1 counts and IC compatibility descriptions below are retained as history; see
[the phase-2 audit](IC_PROFILE_NAMING_PHASE2.md).

## Baseline and fixture availability

Before production changes, 231 targeted audio tests passed. The baseline captured SHA-256 for all 13 existing PWM profiles × WAV/LEP/L16 (39 files). The same 39 outputs after migration match byte for byte; the permanent golden is [export-fingerprints.json](../MZTool.Tests/ReferenceGolden/export-fingerprints.json). The generator uses the existing deterministic MZF test header, body `00 FF 55 AA 01 02 03 04`, default MZ800 machine and default sample rate. It does not regenerate its golden on a normal test run.

| Fixture | Availability and validation |
|---|---|
| Original `AudioStarB03A_selection.wav`, SHA-256 `B051AB477A227385B76BE223C96A3A35A66E3DBADE139BA0479B22B8265DCFEC` | **SKIP: unavailable.** The file now at `C:\333\AudioStarB03A_selection.wav` is 47,784,960 bytes and hashes to `6777D575BA1DEB5490395D7D7DE7032DAB06A5268278E180BEF036601ADD517A`; it is not the original 94,011,518-byte capture. No matching-size original was found in C:\333 or the repository WAV fixture directory. The three-program real-capture replay cannot be claimed from this file. |
| `C:\333\explo_payload.wav` | Available: 48,386,908 bytes; stereo PCM 44100 Hz / 16 bit, 12,096,716 frames. SHA-256 `68B2AFF62A90A85DF7336CF4C2D0EA763804AC018CD542D1D9385C8E5C52C362` verified before/after recovery. Right / Schmitt ×0.4 / known length 37744 recovers checksum 19836, body SHA-256 `F8A2749D98DD0C90789C368103702DB2E3186EB5FF8E9AD323616E22809083FC`. |
| Original external MZF files | Not supplied. Payload golden above comes from the previous documented capture audit, not an independent original MZF. |

The earlier Exploding Fist failure remains an early-terminated name validation bug, not a proven glitch at 66.143 s. Existing synthetic tests retain the original 128-byte header, including padding after CR/zero, and reject unterminated names. See [decoder audit](AUDIO_DECODER_FAILURE_ANALYSIS.md) and [headerless audit](AUDIO_HEADERLESS_HEURISTIC_ROOT_CAUSE.md).

## Catalog and migration map

`Media/PulseReferenceCatalog.cs` is the single numeric authority. Version: `sharp-mz-pulse-catalog-v1/2026-10-10`. `AudioPulseReferences` and `SharpZ80TimingTable` are compatibility facades, not independent tables. Units are typed as microseconds (`AudioPulseGuide.Microseconds`), clocks as Hz, ROM paths as T-states; the API does not accept arbitrary unit strings.

Every entry carries machine/region, software/mode, stage, kind, evidence, source file/section, derivation, clock where applicable, contextual guides, polarity, permitted uses and limitations. USER references stay in the window and cannot mutate built-ins. The catalog browser includes receiver constraints even though they cannot be selected as writer waveforms.

| Old symbol / purpose | Original values or formula | Catalog ID / evidence / source | Compatibility expectation |
|---|---|---|---|
| `Mz800Normal`, ROM800 comparison guides / decoder reference | 844/918/908/896/906/1664/1728/1716/1726 T, PAL 3546875 Hz | `rom800`, WriterExact, CMT_TIMING_REFERENCE §6.4 | Exact writer paths unchanged; comparison rounds to 0.001 µs as before. LOW explicitly distinguishes leader, mark, S→L, S→S, L→L, L→S. |
| MZ700 PAL ROM guides | 861/946/934/938/951/1667/1740/1757/1770 T, PAL clock | `rom700pal`, WriterExact, §5.4 | Separate opt-in regional writer; existing export default unchanged. |
| MZ700 NTSC ROM guides | 861/946/934/938/951/1661/1770/1787/1800 T, NTSC 3579545 Hz | `rom700ntsc`, WriterExact, §5.4 | Distinct clock and LOW paths; never relabel PAL as NTSC. |
| Service/hardware guides | MZ800 240/278/470/494 µs; MZ700 240/264/464/494 µs | `service800`, `service700`, ServiceNominal, §§6.5/5.5 | MZ700 existing writer/decoder compatibility default retains service values; not ROM-exact. |
| Intercopy1200 / Ic1_1 | 832/936/1664/1755 PAL T; 234.573/263.894/469.145/494.802 µs | `ic1200`, WriterExact, CMT_INTERCOPY_TURBOCOPY_TIMING_REFERENCE §6.1 | Existing rounding and loader unchanged. |
| Intercopy2400 / Normal1_2 / Ic1_2 | 403/494/832/923 T; 113.621/139.278/234.573/260.229 µs | `ic2400`, WriterExact, §6.2 | Byte-exact existing output. |
| Intercopy2800 / Normal1_3 / Ic1_3 | 312/442/624/793 T; 87.965/124.617/175.930/223.577 µs | `ic2800`, WriterExact, §6.3 | Project alias 1:3 does not redefine historical 7:3 speed. |
| Intercopy3200 / Normal1_4 / Ic1_4 | 273/416/559/637 T; 76.969/117.286/157.604/179.595 µs | `ic3200`, WriterExact, §6.4 | Project alias 1:4 does not redefine historical 8:3 speed. |
| Tc1 / Tc2 / Tc3 | floor(480/ratio)+71; MODE3 ceil/floor SHORT halves; LONG count; nominal CKMS 1.10 MHz | `tc1`/`tc2`/`tc3`, TimerOutput, §16 | Existing nominal timer-based exporter unchanged; not a reconstruction of polling delays on cassette WRITE. |
| Old IC/TC comparison-only compatibility tuples | IC 144/112/256/224, 112/96/224/192, 112/80/176/160; TC 144/144/288/288, 112/112/204/204 µs | `iccompat2/3/4`, `tccompat2/3`, ProjectCompatibility, §§8/19 | Explicit compatibility overlays; never substituted for writer or receiver constraints. |
| MZ700Fast3 | 80/80/160/160 µs | `fast3`, ProjectCompatibility, CMT_TIMING_REFERENCE §9.3 | Existing default unchanged. |
| ROM800/700 receive decision windows | 1233–1285 PAL T; 1237–1299 PAL T; 1345–1407 NTSC T | `rx800`, `rx700pal`, `rx700ntsc`, ReceiverDecision, §§6.6/5.7 | Constraint metadata, never writer durations. Adaptive acquisition unchanged. |
| IC / TC / FAST3 receive windows | IC 171+14N … 223+14N (N32/22/17); TC 85+14N … 137+14N (N41/27); FAST3 379–431 NTSC T | `rxic2/3/4`, `rxtc2/3`, `rxfast3`, ReceiverDecision, §§7.4/8.3/9.2 | Constraint metadata only; loader bytes and receiver logic unchanged. |
| Framing and interstage delays | Existing header/data leaders 11000/5500, marks40/20, final2, trailing2/TC98; IC345 ms, TC110 ms, FAST3 400 ms | `framing`, Project framing, copier reference §21 | Preserve current MZTools policy; native 22000/11000 recommendation remains distinct. |
| Ultra | ROM pretransfer/short leader comparison only | ROM catalog projections + explicitly short-only decoder references | Live WRITE/SENSE handshake remains rejected for static WAV/LEP/L16 export. |

Encoder LOW generation calls `GetRomLow` with actual region and next-bit context. The explicit choices **MZ-700 PAL — ROM 1Z-013A** and **MZ-700 NTSC — ROM 1Z-009A** apply ROM-derived timing to every NORMAL 1:1 and MZ700 1:1 record, including imported records with explicit metadata. Other loader/speed profiles retain their timing. The original MZ800 and MZ700 default choices retain their output. Four regression cases export imported native profiles with both regional choices and verify the recovered header, payload and MZ700 1:1 classification.

Manual reference selection stays accessible with reference tests switched off. Selecting a profile enables reference tests and selects the Selected profiles scope; Ctrl/Shift supports multiple references. The enable checkbox is inside the reference settings section. Reference tests do not force the output loader identity.

### Standard / heuristic native-machine parity follow-up

Standard WAV/FLAC import now applies the same `ClassifyNative1xTiming` decision as heuristic import after a native header and payload have passed checksum validation. It replaces the old header-only HIGH-duration cutoff for PCM native 1:1 records. IC/TC/FAST3 recognition, byte decoding, checksum recovery and LEP/L16 metadata rules are unchanged. Standard import retains unrounded leader/mark averages for this metadata decision; its integer pulse classifier is unchanged. Physical connector LOW maps to logical HIGH in these measurements.

Both paths compare MZ800 with MZ700 service nominal, PAL ROM and NTSC ROM timing from the catalog. One MZ700 reference must fit both blocks, and both blocks must independently satisfy the existing ratio and fit margins. This fixes a reproduced PAL 22.05 kHz case where service-only comparison had insufficient fit margin. Missing or contradictory evidence remains NORMAL; the result identifies MZ700 1:1 without claiming a verified PAL/NTSC machine region. Regression coverage includes PAL/NTSC imported records at 44.1 kHz, regional output at 22.05 kHz, original MZ800/MZ700 output, mixed native profiles in one WAV, exact header/payload preservation and incomplete/conflicting timing evidence.

Follow-up validation: 255 audio, pulse-decoder, exporter and catalog tests passed, including the existing export fingerprint baseline. Release build completed with zero warnings and errors.

### Standard import recovery after an invalid header

`MZTool.Tests/WAV/Barbarian2_700.wav` (21,855,863 bytes; SHA256 `65FDB57BCE4B97A5C9E38A86E1BFC8798CA7A19749F7E348E8AAACA26D09B901`) reproduced a distinct acquisition failure: Standard returned two records while heuristics recovered five, including three NORMAL 1:4 records. A checksum-invalid header after the TC program left Standard committed to conventional duplicate-copy framing, preventing discovery of later framed headers.

After `HeaderInvalid`, Standard now keeps the original duplicate-copy decoder alive and starts one bounded parallel header search. A checksum-valid fresh header can replace the stalled decoder; a valid conventional duplicate still takes precedence when both complete on the same interval. The search resets after each rejected header and is discarded as soon as either decoder accepts a header. Pulse thresholds, integer timing, PCM preprocessing, byte decoding and checksum requirements remain unchanged. No heuristic multi-pass scan is called by Standard.

The capture regression compares all five profiles, serialized headers and payloads against heuristic import. Self-contained generated WAV tests cover both a corrupted header followed by a compact duplicate and a corrupted header followed by a separately framed faster program. The large capture is optional outside this workspace; the synthetic recovery tests always run.

Validation in this workspace: all 258 audio/decoder/export/catalog tests passed, including the actual Barbarian capture and unchanged export fingerprints. Release build: zero warnings and errors.

Removed duplicate number tables: encoder ROM/IC/TC/MZ700 profiles, decoder reference table and visual overlay table. Remaining minimum leader counts, search limits, classifier tolerances, DC filtering, envelope gates and half-sample tests are algorithm parameters, not historical writer timing. Loader instruction bytes and speed operands are executable data and remain unchanged. `WavSink` still quantizes accumulated time as before; LEP/L16 retain existing duration encoding.

## Shared metrics and actual decoding provenance

`AudioPulseMeasurementEngine` now supplies Visual blocks, arbitrary selected ranges and Manual Deep measurements. Input records PCM path, rate, exact interval, preprocessing start, channel, detector, polarity, threshold/deadband, reference ID, scale, context and pass ID. No PCM is changed. All paths use the same exact weighted duration counts/moments/quantiles, nearest-relative-reference grouping, 35% scaled residual outliers, 256-half-pulse drift windows and deterministic high/low scatter. Fractional measured widths stay fractional; half-sample acceptance never alters them.

This intentionally unifies diagnostic grouping: the old Visual absolute-distance grouping and independent drift calculations no longer compete with Manual's relative-reference grouping. A diagnostic colour/count can therefore change near a class boundary; decoder classification and accepted bytes do not change.

Storage limits: 20000 scatter points with deterministic thinning, 4096 drift windows, 4096 localized issue spans, explicit omitted counts. Counts and global sums continue across the whole interval after visualization caps. Adjacent-window comparisons continue using the actual previous window after the retained-window cap. Visual issue navigation uses localized spans from the shared engine; its existing >1% outlier warning policy remains in effect. Empty groups return unavailable metrics, not invented zero measurements. Range unions count overlapping samples once and exclude gaps. The selected waveform bounding extent is not a claim of contiguous statistical coverage.

Visual defaults to **Used for decoding** independently for each header/payload candidate. Candidate provenance captures the original preprocessing start and gate. Selective/non-default passes replay that same preprocessing interval and detector, instead of measuring an unrelated ZeroCrossing stream. Reconstructed candidates without an unambiguous origin are explicitly labelled **Mixed / unknown candidate provenance; comparison stream only**. Comparison Zero crossing/Hysteresis/Adaptive peak is opt-in and does not decode again. Arbitrary waveform ranges are labelled comparison measurements; transformed Mix is explicit.

Manual histograms still consume only pulses captured by the latest completed Deep analysis. No old-pass selectors or independent histogram scan were restored. Contextual reference comparisons can use every documented LOW alternative; parity requires the same reference targets as well as detector, preprocessing start, gate, scale, polarity and interval. Tests compare actual exported PCM through both Visual range and Manual collectors for all three detectors, and four sample rates through the shared accumulator.

Reports include catalog version, candidate pulse source and provenance. TXT/CSV include reference kind/evidence/source/formula; JSON includes structured source metadata. Reference UI exposes machine/stage/kind/evidence/context and opens the complete catalog. Overlay edits remain comparison-only unless selected explicitly for manual reference tests.

## Decode Pass Comparison

The Manual **Pass comparison** tab compares two passes of the latest completed scan. It lists parameters, retained decoder observations, stage coverage and omitted diagnostics; double-click navigates to the observed location, JSON exports the comparison. Histogram selection remains automatic.

One-to-one event alignment uses stage/channel/polarity and sample proximity (1 ms, minimum 2 samples). Mismatched retained widths, states/reasons, byte index, expected length or checksum evidence produce **First observed mismatch**, never an invented absolute first bad byte. Missing events, incompatible transforms or unpaired observations produce **Unable to align**. The comparison distinguishes preprocessing, edge detection, classification, leader/mark, header acceptance, length, checksum and assembly coverage. Unobserved stages are explicitly not certified. Assembly diagnostics are collected per pass, and the final union assembly is labelled Cross-pass final assembly rather than assigned to the last pass. Coverage counts are stage-specific; unobserved edge/classification stages are labelled Unobserved. Decoder logs retain state and reset/checksum evidence where emitted; they are not an exhaustive per-edge trace. The global 512-event cap and 16 events per code/channel/polarity/detector/pass prevent unbounded diagnostic storage. Cancellation is checked while comparing; diagnostics do not influence decoder decisions.

Limit: retained events can show a local divergence but cannot establish the earliest physical cause across omitted events or a missing per-edge neighborhood. The unavailable original capture prevents a new real Exploding Fist header-rejection replay. Existing early-terminator and rejection diagnostic regressions preserve the proven root cause.

## Regression matrix and commands

| Area | Validation |
|---|---|
| Standard PCM | Existing streaming 8/16/24-bit tests and generated import cases; analog recovery limitations unchanged. |
| Heuristic | Profile roundtrips, checksums, polarity/channels, discovery/selective recovery, early name terminator, malformed names and original header preservation. |
| Manual | Known/unknown headerless recovery, fixed/fuzzy/reference/adaptive detectors, transformed channels, half-sample tolerance, diagnostics bounds, source unchanged/cancellation. Available real payload hash verified. |
| Visual | Existing range/selection tests; new actual-PCM Visual/Manual parity tests for ZeroCrossing, Schmitt ×0.65 and AdaptiveZeroCrossing; catalog/report provenance. |
| Export | 39 existing-profile WAV/LEP/L16 golden fingerprints unchanged; explicit regional writer tests; existing format roundtrips. |
| Shared engine/catalog | Four sampling rates, exact numeric/context vectors, invalid catalog rejection, session USER isolation, million-pulse storage bound, retained-event comparison. |

Run from the repository root (PowerShell):

```powershell
$env:MZTOOLS_REFERENCE_FINGERPRINTS = "$PWD\acceptance\audio-p0-p1\after-fingerprints.json"
$env:MZTOOLS_P0_HEADERLESS_FIXTURE = 'C:\333\explo_payload.wav'
dotnet test MZTool.Tests/MZTools.Tests.csproj -c Debug --no-restore --filter 'FullyQualifiedName~PulseReferenceCatalogTests|FullyQualifiedName~AudioReferenceBaselineTests|FullyQualifiedName~AudioHeaderlessRecoveryTests|FullyQualifiedName~AudioRegionRecoveryTests|FullyQualifiedName~AudioSignalAnalysisTests|FullyQualifiedName~WavHeuristicAnalyzerTests|FullyQualifiedName~SharpMzPulseDecoderTests|FullyQualifiedName~SharpTapeExporterTests|FullyQualifiedName~TapeDocumentTests|FullyQualifiedName~AudioSelectionTests' --verbosity minimal
dotnet build MZTools.csproj -c Release --no-restore
dotnet run --project MZTool.Tests/AudioSignalUiSmoke/Smoke.csproj -c Debug --no-restore -- MZTool.Tests/FLAC/AudioStarB03A.flac acceptance/audio-p0-p1/ui
```

The optional real-fixture tests return without execution when their environment path is absent. Their framework count is therefore not a real-fixture pass: availability is explicitly recorded above. The original selection fixture must only be enabled after checking its required hash.

Final validation results and export hashes follow below.

Final audio regression: **243 passed**, zero failed. The optional unavailable original-selection replay was not executed; the available real headerless payload replay was explicitly enabled. Catalog/shared-metric additions contribute 12 cases. The million-pulse test verifies exact counts, bounded scatter and 4096 retained drift windows. Diagnostic enable/disable produces identical headers and payloads for all three manual detectors. Multiple separated intervals retain scatter/drift for every interval without creating a pair across a gap.

Release build: **0 warnings, 0 errors**. WPF smoke: **PASS** on both generated PCM and `MZTool.Tests/FLAC/AudioStarB03A.flac` (1870.56 s, 18 decoded blocks). The long-capture run rendered 12 pans in 51 ms with 501 visible buckets from 65536; this is a local rendering measurement, not a portable performance guarantee. Smoke covers reference editing, provenance default, pass selector refresh, histogram bounds/zoom, selection/navigation, inactive highlight, actual-format WAV export, cancellation, headerless recovery and reports. Local screenshots/reports reside in `acceptance/audio-p0-p1/ui`, outside source control. Before/after fingerprint JSON files are also retained there and are identical.

## Original versus migrated exports

| Profile / format | SHA-256 (before = after) |
|---|---|
| Ic1_1/L16 | `74E5E7C13180CC031E368FE60E6BB6A9AEE73E8F891328872D4A361266CB583D` |
| Ic1_1/Lep | `8FE3198A7BA4629455149C5335815B6D18C80FB85D1D4E551C7AD2CA66F29A8A` |
| Ic1_1/Wav | `5777F2F93C2EAA8894CD494F2490EC7C7031D68308F9A36FA385CA82201DD8C8` |
| Ic1_2/L16 | `BCA22847A57824DE461F9B951BE98E06E927BDDE4F31BF65DF86E465A568DADE` |
| Ic1_2/Lep | `D9F3382787C3CC6E78D852CEE2FDBD1ED432513AF3D72A168488E74E55C006B7` |
| Ic1_2/Wav | `7CEC6A7151194194DCF5403E44DC94E0F2C0ECB28C69246BAFAAF6835FFDFFE3` |
| Ic1_3/L16 | `B6D1BC633473011F90ED0502D254DAC542053C3ACE24335C89CE3E11461AFA2D` |
| Ic1_3/Lep | `E50B41D297D2221424BC713CBF3B20125CE8C3BC0DC92FA311730B64A722EF80` |
| Ic1_3/Wav | `CC785BC79A2A2C97CB1386C02DA33E18B4D0E8C1148540C72539864D8EF5592B` |
| Ic1_4/L16 | `12164987113C4BDA801C986A9BAA3CBA30E8F8B7D8494F933FE199A1A562C43B` |
| Ic1_4/Lep | `D813EE284AEA344199AE530ECA7A04506D0FFE6C206543BE0205AFB7EA40263A` |
| Ic1_4/Wav | `DA743B01F32FF74F8F08B047258F23D22C4CBF697FC0ABDE5CFCC8ADDADDA7E2` |
| Mz700_1_1/L16 | `77AFF601EE961B445B8373050C093B1E387EFE624104A4070D19BA8C16D2CD88` |
| Mz700_1_1/Lep | `6EEA4C873E3B29366A4221C27361E505211485C5B4CDF391FCD0EBA077143A53` |
| Mz700_1_1/Wav | `667CDE3FB0878CE9B5F1C754E15B8145ED072DF65526B4123535D71E61CEABA0` |
| Mz700_1_3/L16 | `EFEFE6F91CD871B0CEEB7446EEDEF75EFEE16E497F53FB7E3EBBCD777FB41587` |
| Mz700_1_3/Lep | `B0E57E5EA88EEF3B4C3CDC545189D4858ACCEE578E941BCA46CB7FE82DF8C624` |
| Mz700_1_3/Wav | `83F3908B46EE5305365FA3EC50BD397A9122BD84D1FB967E4E451B12C01E48F5` |
| Normal1_1/L16 | `1B4BB6214F52F3B9C255BE7DC65985BC6CE73E17F696E1FC32D535D820CAF962` |
| Normal1_1/Lep | `6EEA4C873E3B29366A4221C27361E505211485C5B4CDF391FCD0EBA077143A53` |
| Normal1_1/Wav | `724E6117260C9F3583A5E675EE8AF3B4B35C1D9EF4B107777CD3232B4F839B1A` |
| Normal1_2/L16 | `01C1E2986B1E51C1139ACDE23781AE70D605702DA797B86E899F77BADAD80CE9` |
| Normal1_2/Lep | `28ACD0F16D1B371F0810A5C0B5D8686837C47D01CBF247720DE5245007A0AABE` |
| Normal1_2/Wav | `7D23E1D934B43ADE67E919F2CF21ED4069FA23BF7AD6D82C931EAB9BF51F74B4` |
| Normal1_3/L16 | `E34D6891E37DCECAE1A66B2F2E547CB9F8716EC4346897AAF5776C6DD6BFFD8A` |
| Normal1_3/Lep | `10875CDA4A156F7E73A3F4F96E63EEB0D7547EA5669017D748879719A0B199C3` |
| Normal1_3/Wav | `F0103A338D625778660B47143C126539616CD845709E2A3E299202111B1229CD` |
| Normal1_4/L16 | `43DEEC016991BDFCE1E03181F79E581818D4EE94A0AD9A1B2D35037B6F84DB7A` |
| Normal1_4/Lep | `4B9089ABE9916093DADA889771E2590586F4202B2BF224F468A98DCE7EA807C1` |
| Normal1_4/Wav | `16EE785073257A3867C8F38FA24225552FE226189B15179267A502B5252DB5F0` |
| Tc1_1/L16 | `6D5DDB94564324E33DBBB963236B2AF990FE9796D15DCA388466A7FB06329F71` |
| Tc1_1/Lep | `3FF082B40421113665845922136A2DB4F33B3DE970F1E70C8A0A680ED7790D7C` |
| Tc1_1/Wav | `87C8F6EFC22DDDEE228AFEB57099A43CB73E56A7F913CDABC283879BB415B530` |
| Tc1_2/L16 | `6AB3C35F808D282B577DEBDF156E55AC26E1A660BE7491C9F50108B1385B6C77` |
| Tc1_2/Lep | `027508633D653299551F5082888E59A0E82D362FB0ACAF9A312841A0C2C1EF95` |
| Tc1_2/Wav | `7EEADC4F29BF6AF14AAE142B078036F8DF2170432514F6897F2DBF7B94BD42A6` |
| Tc1_3/L16 | `28FD8B3FC9C3E08FC980445FF29ABE41A2EEEECE4196146E7259EDF064E4362A` |
| Tc1_3/Lep | `9311B392786D7AAD5F5EB87A79089845C142DEC49FF61AE7A5360F87401CD365` |
| Tc1_3/Wav | `18EDC596619F3D9BF8CFA4C87A8737B3CA87EB94FD5D91530007428E5D57A9DA` |
