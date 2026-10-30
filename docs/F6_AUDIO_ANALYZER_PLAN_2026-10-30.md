# F6 – offline Audio Visual/Manual Analyzer a sdílené grafy

**Rozsah F6 byl následným samostatným souhlasem uživatele schválen k implementaci, 30. 10. 2026.**
Předchozí souhlas se týkal vzhledu F5 a přípravy tohoto plánu; následný souhlas
zahájil implementaci F6. F7/F8 tím schváleny nejsou.
Předchozí výsledek: [report F5](F5_DSK_HFE_2026-10-30.md),
[galerie skutečného UI](ui-design-system/avalonia-f5/index.html).

## Výchozí stav a cíl

Read-only ověření: `main`, HEAD `fb0c49cd69e487d218ac9e5d279d2b3111413303`.
Pracovní strom obsahuje F1–F5 i předchozí uživatelskou práci; není čistý a není
určen k resetu, hromadnému přesunu nebo vracení změn. Dosavadní schválený F2
vzhled, všechny diskové/Tape/QD adaptéry a referenční WPF zůstávají zachované.

F6 připojí skutečnou offline analýzu do stejného shellu: Open WAV nebo dokončený
segmented recording manifest → streaming analýza → záznamy/bloky/regiony/úrovně
→ společný výběr a grafy → případný manual recovery preview → explicitní export
nebo přenos získaných záznamů do Tape/QD. Původní audio se při prohlížení,
transformaci preview ani recovery nepřepisuje.

Audit rozlišuje existující funkce od nových požadavků zadání. Offline WPF
`AudioSignalAnalyzerWindow` a `AudioSignalCharts` již mají waveform, histogram,
scatter a drift, ale nemají vlastní FFT spektrum ani spektrogram. Přenositelné
FFT spektrum a trigger existují v `MZTools.Capture/LiveSignalAnalyzer.cs`.
F6 proto zahrne níže výslovně vymezené nové offline spektrální pohledy; nebudou
vydávány za pouhý port hotové WPF funkce.

## Funkční mapa a autoritativní služby

| Dosavadní schopnost / požadavek | Konkrétní výstup F6 a zdroj |
| --- | --- |
| Open/analyze | Audio session nad `AudioSignalAnalysisService`, `WavPcmStreamReader` a `SegmentedPcmStreamReader`; skutečné progress/cancel, analýza mimo UI, metadata a původ zdroje |
| Records / blocks | Společná tabulka s record/block/profile/channel/checksum/recovered/polarity/detector a přesným intervalem; `AudioSignalAnalysis`, `AudioSignalPassOrigin`, `AudioHeaderDiagnostics` |
| Regions / issues / detector agreement | Tabulky, filtry, detail a navigace v témže výběru; skutečné Core regiony a diagnostics; odlišit decoder trace, neidentifikované audio a odhadovaný šum |
| Waveform a levels | L/mono/R/overlay/average podle Core; display-only Auto scale/Gain, clipping a FS/dBFS, sample-level detail versus min/max buckets; `WaveformEnvelopeBuilder`, `AudioChannelLevel` a původní čtečky |
| Výběr a měření | Half-open frame/sample intervaly, Shift+drag, více bloků/regionů s překryvem počítaným jednou, explicitní channel/detector; `AudioRangeInspection` a `AudioPulseMeasurementEngine` |
| Histogram / scatter / drift | Původní přesné duration bins, omezené páry a drift/short-term variation/global speed offset; `AudioRecoveryHistogram`, `AudioRecoveryMeasurements`, `AudioPulseReferences`; nové vykreslení, stejné výpočty |
| Reference pulzů | Built-in ROM/IC/TC i uživatelské reference, follow selected block, show/hide reference, tolerance a ±0.5 sample test podle existující politiky; jediný `PulseReferenceCatalog` |
| Manual/deeper analysis | Výběr intervalů, detektorů, polarity, channel/mix, threshold/time scale, adaptive/fixed/fuzzy a reference tests; `AudioRegionRecovery`, `ManualPulseDetection`, `AudioRecoveryDiagnostics` |
| Transform preview | Existující `AudioPcmTransform`, aligned PCM a range preview: shift, pravá polarita, kanál/mix a další podporované volby; preview není editace zdrojového souboru |
| Recovery výsledky | Programs, candidates, passes, messages/problems, comparison a hex; `AudioDecodePassComparison`, diagnostické a headerless Core služby; odlišit valid checksum, recovered, candidate a neúplný vstup |
| Headerless recovery | Známá/neurčená délka a existující hledání payloadu, validace a sestavení synthetic MZF přes `AudioSyntheticHeader`; uživatelský header není důkaz, že byl načten z nahrávky |
| Export | Selected waveform WAV přes `AudioSelectionWaveExporter`, podporované reports, candidate raw a validní program MZF; přenos záznamů do F4 Tape/QD přes stejné modely a ochrany |
| Spektrum / spektrogram | Nové offline pohledy nad jediným sdíleným FFT výpočtem popsaným níže; Hz/dBFS, přesný analyzovaný interval, N/window/hop a žádné tvrzení PSD či měřeného flux |
| Trigger a grafická navigace | Offline trigger je zobrazení/nalezení existujícího průchodu prahem; frame polohy se neposouvají a při nenalezení se nevyrobí trigger. Společný viewport/selection/zoom/pan/fit pro pozdější F7 renderer |

Výchozí WPF mapa zahrnuje `AudioSignalAnalyzerWindow.cs`, `AudioSignalCharts.cs`,
`AudioSignalAnalyzerSelection.cs`, `AudioSignalAnalyzerExport.cs`,
`AudioPulseReferenceControl.cs` a celou sadu `AudioRegionRecoveryWindow`,
`AudioRegionRecoveryPreview`, `Headerless`, `Comparison`, `Export`.
Čisté formátovací/validační části smí být úzce vyčleněny a používány oběma hosty;
WPF dialogy, brush typy, dispatcher a file pickery se do Core nepřenesou.

## Nové spektrální pohledy a rozsah změny Capture

Doporučený a tímto návrhem předkládaný postup je **úzké sdílení čisté FFT matematiky**:
vyčlenit současnou periodic-Hann/window/coherent-gain/FFT/bin normalizaci z
`LiveSignalAnalyzer` do přenositelné služby, například
`MZTools.Capture/SpectralAnalysis.cs`. Stávající LiveSignalAnalyzer ji bude volat
se svými opakovaně používanými scratch buffery. Offline Core ji použije nad
omezeným PCM oknem. Nevznikne druhá FFT, paralelní decoder ani druhý pulse catalog.
Vnější kontrakty snapshotů a dosavadní výsledky Capture se zachovají; nové API
se omezí na čistý výpočet a jeho vstupy/výstupy, bez zařízení či session stavu.

Toto je výslovně navržená změna produkčního Capture výpočtového souboru, nikoliv
změna capture pipeline: žádné úpravy callbacku, front, archivace, backendů,
vyjednávání formátu, Stop/Finalize, timestampů nebo životnosti půjčených bufferů.
Výchozí kontrakt z [recorder boundary](LIVE_TAPE_RECORDER_AVALONIA_ADAPTER.md)
byl přečten a zůstává závazný. Před vyčleněním se zachytí referenční výstupy FFT,
po něm se porovnají a proběhne úplná Capture regrese i synthetic recorder smoke.

Spektrum bude uvádět periodic Hann, amplitudovou dBFS normalizaci a podlahu
−160 dBFS podle současného výpočtu, nikoliv PSD. Velikost N zůstane ve stávajících
validovaných mocninách dvou 256–32768. Nesmí se zaměnit waveform, level a spectrum
měřicí interval ani zaměnit amplitudová FS/dBFS metrika za výkonovou hustotu.

Spektrogram je nová časová kompozice těchto stejných FFT oken. Výchozí N=4096,
hop=N/2, se zobrazeným skutečným hopem a přesnými hranicemi oken. Výpočty budou
pro vybraný interval/viewport, mimo UI, s progress/cancel. Případná agregace
časových/frekvenčních sloupců nebo deterministická decimace bude přiznaná v
legendě/hover detailu včetně rozsahu; žádná interpolace přes chybějící audio.
Cache se řídí byte limitem, navrženě maximálně 32 MiB na audio session; render
dostane omezený raster/bin snapshot podle šířky obrazovky, ne celou historii.

Offline trigger bude používat stejnou prahovou/crossing politiku jako současný
analyzér. Je-li třeba zpřístupnit čistou trigger funkci, vyčlení se stejným
způsobem bez změny algoritmu; nespustí se `CaptureSession` ani backend.
Trigger/Follow live ovladače recorderu se v F6 nevydávají za implementovaný F7.

## Jeden dokumentový a grafický workflow

Audio bude samostatný typ dokumentu ve stávajícím `DocumentCoordinator`.
Opakovaný Open stejného zdroje použije existující kartu. Session drží source
fingerprint, formát/kanály, výsledek, aktivní záložku, výběr, viewport, referenci,
gain a recovery draft; změna modulu tyto stavy nevymaže.

Zdrojová nahrávka je read-only. Globální Save/Save As nesmějí tiše získat význam
„uložit aktuální graf“ nebo přepsat WAV transformovaným preview. Zdrojová audio
session nebude bindable writer; pro výstupy slouží explicitní Export selection,
Export report/program/candidate a Send to Tape/QD. Důvod disabled Save bude
textový. Nový Tape dokument používá již existující F4 Save/Save As význam.
Rozepsaný recovery/header/reference draft má při zavření Apply/export nebo
Discard/Cancel podle své skutečné schopnosti; neexportovatelný draft se neoznačí
za uložený a nebude předstírat neexistující persistence projektu analýzy.

Hlavní waveform zůstane v témže workspace s kontextovým inspektorem. Obsahové
sekce přehledně seskupí Records/Regions, Pulses, Levels, Spectrum/Spectrogram,
Recovery a Report. Detailový recovery/hex pohled bude znovupoužitelná komponenta,
ikoliv vlastní odlišný shell. Na malé ploše se použije posun a stejný vzor
samostatného detailu jako v F4/F5, nikoliv skrytá tlačítka.

Společné grafové transformace budou oddělené od kreslení i měření. Závazné F2
ovládání: wheel zoom s kotvou pod ukazatelem, faktor 1.5, drag pan, Fit all/Fit
selection, viditelný stav Auto scale versus ruční gain, klávesnice, tab/focus,
Ctrl/Command a Shift, zachování výřezu při přepnutí zobrazení. Interní WPF faktor
waveform Zoom(.5)/Zoom(2) se nebude kopírovat jako nový veřejný UX standard.
Audio amplituda a source PCM se při display gain nemění. Výběr/hover a histogram
viewport jsou odlišné stavy; pouhé posunutí grafu nespustí druhý decode pass.

Frame index znamená jeden okamžik všech kanálů, nikoliv počet interleaved hodnot.
Half-open intervaly `[start,end)` a převod frame↔čas budou deterministické, s
explicitním vzorkovacím kmitočtem. Zobrazení v sekundách nemění přesný frame
výběr. Půlsamplová tolerance měření pulzu nesmí posouvat PCM ani timestampy.

## Streaming, bezpečnost a životní cyklus

- Nečíst dlouhý WAV nebo všechny segmenty do jedné RAM array. Zachovat Core
  streaming, overview nejvýše stávajících 65536 buckets a omezený detail podle
  viditelného rozsahu; vykreslování nikdy neotevírá PCM soubor.
- Odděleně vlastnit/cancelovat full analysis, range measurement, preview,
  spectrogram a export. Zastaralý request po změně source/options/selection
  nesmí přepsat novější výsledek nebo zavřenou kartu. Workery neaktualizují
  Avalonia ovladače přímo; refresh/render mají omezenou práci.
- Manifest otevřít jen po rozpoznání `SegmentedPcmStreamReader.IsManifest`;
  ověřit kompletnost, formáty, návaznost a integritu podle Core. Chybějící,
  změněné nebo incomplete segmenty nevyplnit domyšleným tichem a nevydat za
  souvislý validní záznam. Ochrana exportu zahrne manifest i všechny segmenty.
- Waveform export znamená skutečné PCM vybraného intervalu/transformace,
  nikoliv envelope nebo znovu syntetizovanou kazetu. Zachovat dosavadní
  kvantizaci, rounding, kanály, sample rate, podporovanou bit depth a RIFF limity.
  Nepodporovaný vstup se odmítne, bez tiché změny formátu.
- Ztrátový či transformovaný výstup má konkrétní preview. Všechny kolize se
  potvrzují před zápisem, včetně otevřených Tape/QD/DSK a audio source segmentů.
  Velký WAV bude souborově streamovaný staging; použije se společný F4/F5 commit
  mechanismus. Nejdřív source/length/output validace, teprve potom nahrazení cíle.
  Cancel/selhání nesmí poškodit source nebo existující destination.
- Synthetic MZF a raw candidate se označí podle skutečného původu. Core checksum
  a valid header pravidla nejsou obcházena pro vyšší recovery úspěšnost.
- Při close zrušit a vyčkat vlastní operace, uvolnit readery, cache a event handlers;
  pozdní dokončení nesmí znovu vytvořit okno. Žádné automatické audio zařízení.

## FLAC a hranice dalších etap

`AudioSignalAnalysisService.OpenReader` již pro FLAC volá
`CorePlatformServices.OpenFlac`; Avalonia nyní žádný takový kodek nekonfiguruje.
F6 proto bez nových knihoven plně zpřístupní podporované WAV a dokončené
segmented WAV manifesty. FLAC v Avalonia zůstane explicitně nedostupný s důvodem
a návazností na F8; původní WPF FLAC workflow a regrese zůstanou zachované.
Toto není vypuštění finálního požadavku WAV/FLAC parity.

Recorder UI a Head Alignment patří do F7, platformní capture/FLAC backendy a
native balíčkování do F8, zbývající batch UI a distribuce/CI do F9. F6 neinstaluje
NuGet, FFT/plotting balíček, kodek, font ani nativní runtime. Nový veřejný grafický
framework není navržen. Pokročilé QD physical/floating parity položky se při
sdílení grafů nesmějí automaticky přeznačit na hotové; jejich status bude uveden
samostatně. WPF se neodstraňuje a neprovádí se commit/push/publikace.

## Konkrétní oblasti změn při schválení

- `src/MZTools.Desktop/Documents`: audio session, loader, source set/manifest
  fingerprint a staging/export workflow; cílené rozšíření koordinátoru.
- `ViewModels`, `Views`, `Controls`, případně `Renderers`: jednotný audio
  workspace, reusable recovery/reference/hex a grafové transformace/render snapshoty.
- `Platform`: stejné pickery, clipboard, dialogy a report/export policy jako F4/F5;
  žádné UI typy v Core a žádný Windows audio backend v přenositelném frontend API.
- `src/MZTools.Core/Media`: použití existujících analýz/recovery; úzké oddělení
  dosud smíšeného pure formatting/export kontraktu a nový bounded offline
  spektrální adaptér nad sdílenou matematikou. Neměnit decode heuristiky/timingy.
- `MZTools.Capture/LiveSignalAnalyzer.cs` a nová čistá spektrální služba:
  pouze popsané sdílení FFT/trigger výpočtu, při zachování měření a scratch bounds.
- WPF pouze nezbytné delegace téže vyčleněné pure logiky; každá změna se doloží
  oproti frozen F2 inventory a dosavadním F4/F5 oprávněným odchylkám.
- `tests/MZTools.Desktop.Tests`, `tests/MZTools.Desktop.Smoke`, relevantní portable
  Core/Capture testy, README, actual F6 parity/report/validation a skutečná galerie.

## Akceptační brána – dosud plánované testy

1. Reálný Open/Analyze WAV i manifest → records/regions → výběr → měření →
   manual recovery → export/reload nebo transfer do Tape/QD. Source i segmenty
   zůstanou byte-identické; Cancel/invalid source nezmění jiné karty.
2. Stejné Core výsledky bloků, profilů, detektorů, checksumů, regionů, pulzů,
   histogramů, drift a diagnostics jako WPF/reference fixture. Zachovat
   `AudioSignalAnalysisTests`, `AudioReferenceBaselineTests`,
   `AudioRegionRecoveryTests`, `AudioHeaderlessRecoveryTests`,
   `AudioSelectionTests`, `PulseReferenceCatalogTests` a související decoder sady.
3. Source transform/interval boundary/Left/Right/mix/shift/inversion,
   půlsamplová tolerance a provenance: žádné dvojí počítání překryvů, posun
   timestampů ani záměna dekódovacího a srovnávacího detector streamu.
4. FFT před/po vyčlenění: silence, DC, bin-centred/off-bin sinus, Nyquist,
   stereo, clipping a neplatné N. Stejné normalization/window a peak/bin
   výsledky ve stanovené numerické toleranci; golden se nepřepíše pro PASS.
   Spectrogram porovnat po oknech se stejnou FFT, s přesným N/hop/time bounds.
5. Trigger nalezen/nenalezen, rising/falling, channel/level a edges; stabilní
   frame osy a stejné viewport transformace po zoom/pan/fit a změně view.
6. Skutečný raw pointer/keyboard výběr, Ctrl/Command/Shift, tab/focus/F6,
   navigace tabulka↔waveform/regions/issues, reference visibility, Auto/Gain
   a zachování viewportu. Barevné grafy mají textovou alternativu a popisky.
7. Recovery candidate/valid program/headerless synthetic provenance, preview
   cancel, nevalidní název/adresy/délka a přenos do F4. Žádný candidate se
   nestane valid programem pouhou změnou štítku v UI.
8. WAV selection export: přesné PCM/délka/kanály/RIFF padding proti původnímu
   exporteru pro podporované formáty; zdrojový alias, manifest segment jako
   cíl, změněný source, read-only, kolize, cancel a řízené selhání staging/commit.
   MZF/raw/reports ověřit i podle obsahu; textové OS konce řádků oddělit od
   binární shody. Explicitně testovat odmítnuté formáty/RIFF size limit.
9. Dlouhý stereo soubor a segmenty: měřit init, pan/zoom, visible bucket počet,
   resident cache, FFT/spectrogram práci a cancel latency. Znovu použít dlouhý
   synthetic WPF smoke fixture; uvést naměřené výsledky a výřez, nikoliv
   slib výkonu odvozený jen z kompilace. Po zavření nesmí zůstat read handles.
10. Windows/Linux unit + headless + nativní smoke, skutečné obrázky waveform,
    pulses, spectrum/spectrogram, recovery a validace; 1366×768, 1920×1080,
    malý viewport a render 100/125/150/200 %. Native DPI a simulated render
    odlišit, WSLg neoznačit za všechny běžné Linux desktopy.
11. Úplné dostupné legacy/Core/Capture/Windows Capture/Desktop regrese i
    čtyři původní WPF smoke. Capture FFT refaktor nesmí změnit synthetic
    recorder PCM/WAV/JSON ani dropout/preview loss pravidla; ověřit, že F6
    konstrukce a offline Open nikdy nespustí backend či recorder session.
12. Všechny lokálně přítomné zdroje/fixture zahrnout i při gitignore. Chybějící
    reference zůstávají konkrétní skip, macOS/ARM64/externí OS DnD/screen reader
    a skutečný hardware UNTESTED, pokud prostředí zůstane nedostupné.
    Source hashes, binary parity, package audit, tokeny a hranice projektů
    doložit v závěrečném reportu. Neztotožňovat F6 PASS s celkovou audio paritou
    včetně dosud nezapojeného FLAC/recorderu.

Při přípravě tohoto návrhu se pouze četly zdroje a zapisuje tento plán a schválení
výsledku F5. F6 build, testy, FFT refaktor ani produkční UI nebyly provedeny.
Předchozí testy F5 nejsou výsledky F6.

## Pořadí, návrat a další schválení

Po schválení: nejprve source/manifest a dokumentový životní cyklus s baseline
výsledky, potom shared viewport/waveform/tabulky/pulse reference, následně manual
recovery a bezpečný export. FFT baseline a úzké vyčlenění matematiky předcházejí
novým spektrálním pohledům. Nakonec kompletní regrese, výkonové měření, parity
matrix a skutečné obrazovky. Dílčí kroky jsou pořadí uvnitř jedné schvalované F6.

Návrat zasahuje pouze doložené F6 změny včetně pure FFT delegace; zachová F1–F5,
uživatelskou práci, boot/golden/reference data a funkční WPF. Bez reset/clean.
Pokud se objeví potřeba změnit decoder, formát, nativní backend nebo přidat
závislost, předloží se konkrétní odchylka před jejím provedením.
Po F6 se předloží skutečný vzhled/workflow, změněné soubory, testy, rizika a
otevřené parity položky. Teprve další souhlas umožní přípravu konkrétní F7.

**Otázka ke schválení:** Souhlasíte s implementací této F6 včetně nových offline
spektrálních pohledů a úzkého sdílení stávající FFT/trigger matematiky z Capture,
s FLAC backendem nadále vyhrazeným F8 a zachováním WPF i binárních algoritmů?
