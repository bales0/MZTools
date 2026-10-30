# F6 · offline Audio Analyzer a sdílené grafy

**Implementace odsouhlaseného rozsahu F6, 30. 10. 2026. Skutečný vzhled je připraven k posouzení.**

[Galerie skutečného UI](ui-design-system/avalonia-f6/index.html) ·
[měřené výsledky a hashe](F6_VALIDATION_2026-10-30.json) ·
[schválený rozsah](F6_AUDIO_ANALYZER_PLAN_2026-10-30.md).

F6 připojuje offline analýzu do společné Avalonia aplikace. WPF zůstává dostupné.
F7 Recorder, jeho backendy, FLAC integrace F8 a odstranění WPF nejsou tímto výsledkem implementovány.
Pracovní strom obsahuje i předchozí F1–F5 a uživatelské změny; nebyl resetován,
staged ani commitován. Výchozí HEAD je `fb0c49cd69e487d218ac9e5d279d2b3111413303`.

## Výsledek a ovládání

Open WAV nebo dokončeného `.wav.json` otevře **read-only Audio workspace**.
Analýza běží mimo UI s průběhem a rušením; vstup není otevřen jako capture zařízení.
Po úspěchu se zobrazí skutečné Core bloky, regiony, nálezy, úrovně a shoda detektorů.
Neúplný manifest, změněný segment, prázdný vstup a nepodporovaný FLAC mají explicitní chybu.
Odmítnuté otevření zachová existující dokumenty.

| Oblast | Hotové workflow |
| --- | --- |
| Výběr a waveform | Původní frame souřadnice `[start,end)`, L/R/overlay/average, Shift+drag, přesné zadání frame, wheel zoom, pan, Fit, Fit selection, navigace z bloků/regionů/nálezů |
| Vícenásobný výběr | Sjednocení bloků/regionů; překryvy se měří jednou. Oddělené intervaly podporuje Measure selection. WAV export, FFT, transform preview a deeper analysis požadují jeden souvislý interval; mezery se tiše nedoplňují ani nezřetězují |
| Měření pulzů | Used for decoding se zachovaným preprocessing/detector/channel kontextem; explicitní porovnání Zero crossing / Schmitt / Adaptive midpoint |
| Histogram / scatter / drift | Původní Core duration bins, naměřené páry a drift windows, skupiny SHORT/LONG High/Low, zoom/pan, built-in i session-local reference a jejich původ |
| Úrovně | Peak/RMS/DC v FS a detail dBFS, clipping, asymetrie, odhadovaný šum; odhad není vydáván za laboratorní SNR |
| Preview | Pravý posun a inverze, mix, Apply preview / Cancel edits; Gain a Auto scale mění pouze zobrazení |
| Spektrální pohledy | Offline FFT spektrum a spektrogram; společná čistá Capture matematika, přesné intervaly/N/hop/agregace a frekvenční viewport |
| Trigger | Nejnovější centrovatelný skutečný průchod prahem v omezeném PCM kontextu kolem viewportu; nejvýše 2 s zobrazených dat a 4 s hledaného kontextu. Nenalezený trigger zachová viewport |
| Deeper analysis | Kompletní program nebo payload-only se známou/neurčenou délkou, kanály, polarita, detektory, threshold/time scale, fixed/fuzzy/adaptive, reference/tolerance/±0.5 sample a bounded diagnostics |
| Recovery | Programs, candidates a jejich platnost, passes a konkrétní spotřebovaný pulse stream, diagnostics, rejected headers, failures, hex a porovnání passů |
| Výstupy | Selected PCM WAV, původní TXT/CSV/JSON reporty, recovery/comparison JSON, raw candidate, programy MZF/M12/MZT a přenos do nového F4 Tape/QuickDisk dokumentu |

Grafický detail používá stejnou `AudioChart` a session jako vložený pohled.
Inspector používá stejnou komponentu v panelu i samostatném okně. Malé viewporty
mají posuvný obsah; nejde o zmenšení fontů nebo další nezávislý design.
Stav waveformu, výběru, kanálů a typů grafu náleží konkrétnímu dokumentu.
Bodový Core nález může navigovat na jeden skutečný frame; původní evidence
nadále uvádí svůj původní bodový rozsah.

## Původ algoritmů a úzká změna Capture

Audio adapter volá původní `AudioSignalAnalysisService`, `AudioRangeInspection`,
`WavHeuristicAnalyzer`, recovery služby, `AudioSelectionWaveExporter` a F4 exportéry.
Nevznikl druhý decoder ani katalog pulzů. Počáteční dekódované záznamy jsou interně
předány do audio session; pro získání stejného programu se znovu nedekóduje soubor.
Původní exportované Core JSON schéma touto interní kolekcí nerozšiřujeme.
`AnalyzeSelection` dostal volitelný PCM transform; původní výchozí chování zůstává stejné.

Z `LiveSignalAnalyzer` byla vyčleněna čistá periodic-Hann/FFT/bin matematika
a hledání průchodu prahem do `MZTools.Capture/SpectralAnalysis.cs`.
**12 skutečných snapshotů před vyčleněním má přesně shodné parsované hodnoty po změně**:
silence, DC, bin tone, off-bin, Nyquist, clipping, vždy mono a stereo při 48 kHz/N=4096.
Nejde o tvrzení shody všech možných vstupů.

Při kontrole krajních hodnot byla navíc opravena **frekvenční osa při vysokém rate/N**:
součin indexu FFT binu a sample rate se počítá v double ještě před násobením,
aby nepřetekl Int32/UInt32. Nové testy 192/384 kHz při N=32768 ověřují monotónní osu,
Nyquist i správný dominantní bin. Tato oprava nemění FFT amplitudy ani archivní PCM.
Je výslovně odlišena od pouhého vyčlenění matematiky.

Capture `StartAsync` a `Process` mají shodná těla jako před F6. Ostatní původní
Capture pipeline a Windows backend soubory jsou beze změn, stejně jako decoder,
heuristiky, pulse measurement engine, reference catalog, recovery algoritmy,
goldeny a boot data. Kontroluje to `validate_f6.py`.

## Spektrální význam a meze paměti

Spektrum používá posledních N skutečných frame z deklarovaného souvislého výběru,
periodic Hann a coherent-gain korigovanou amplitudu dBFS s podlahou −160 dBFS.
N je mocnina dvou 256–32768. Kratší interval nevyrobí doplněné vzorky ani FFT bins.
Výsledek není PSD; sample rate ani frame souřadnice se nemění.

Spektrogram streamuje všechny úplné FFT windows s hop=N/2. Pro zobrazení agreguje
maximum amplitudy přes deklarovaná okna a frekvenční bins; výchozí limit je
128 sloupců × nejvýše 1024 pásem na kanál. Metadata uvádějí počty oken,
skutečné frame hranice, grouping i nepokrytý koncový zbytek. Zbytek se nedoplňuje.
Nevzniká interpolované měření ani tvrzení o flux.

Session má LRU limit 24 MiB spektrálních dat; prostor do plánovaných 32 MiB
zůstává pro aktuální snapshots a omezené bitmapy. Renderer nečte PCM, nedekóduje
a nepočítá FFT. Waveform používá Core overview a při detailním zoomu streamovaný
omezený envelope; přesný sample zoom obsahuje skutečné PCM frame, nikoliv
domyšlené hodnoty z min/max overview.

Žádosti náhledu ruší starší žádosti a hlídají jejich generaci. Výsledek starého
výběru se nesmí připojit k novému. Zavření audio dokumentu zruší a vyčká vlastněné
viewport úlohy. Plovoucí graf se zavře při odstranění jeho dokumentu.

## Ochrana zdroje a výstupů

Save i Save As jsou u audio dokumentu nedostupné. Každý export vytváří samostatný
artefakt a zachovává vazbu na původní WAV/manifest. Jsou chráněny všechny segmenty
manifestu a zdroje ostatních otevřených dokumentů, včetně detekovaných aliasů.
Zdrojová sada má SHA-256 před/po otevření a před/po přípravě PCM exportu;
znovu se ověřuje před commitem. Běžné náhledy používají původní Core kontroly zdroje.

WAV se streamuje do vlastní dočasné staging složky, znovu otevře a ověří podle
frame count, kanálů, sample rate a bit depth. Potom jej společná atomická transakce
přenese do zvoleného cíle. Celý WAV se nedrží jako byte array v RAM.
Zrušení/selhání uklidí vlastní staging, zachová dokument a nepublikuje neověřený výstup.

Raw candidate neznamená platný program. Synthetic header vyžaduje dostupný
matching payload checksum a explicitní uživatelské přiznání name/type/LOAD/EXEC.
Vytvořená metadata mají původ `CreatedOrModifiedInAdvanced`; původní hlavička
není prohlášena za obnovenou. Přenos úplných záznamů do Tape/QD používá F4 modely.
Zavření s preview draftem nebo neexportovanými výsledky nabízí Discard/Cancel;
audio draft není vydáván za soubor k přepsání přes Save.

## Skutečné ověření

| Úplná sada | PASS | Selhání | Fixture skip |
| --- | ---: | ---: | ---: |
| Windows původní aplikace | 1202 | 0 | 3 |
| Windows Core | 411 | 0 | 1 |
| Windows Capture | 224 | 0 | 0 |
| Windows Capture.Windows | 52 | 0 | 0 |
| Windows Desktop | 219 | 0 | 0 |
| Linux Core | 411 | 0 | 1 |
| Linux Capture | 224 | 0 | 0 |
| Linux Desktop | 219 | 0 | 0 |

**2962 úspěšných spuštění případů, 0 nevyřešených selhání, 5 fixture skipů.**
Součet obsahuje opakování přenositelných případů na druhém OS, nejde o počet
unikátních testů. Finální audio UI subset má dalších 8 PASS na každém OS po opravě
rastru; nezapočítává se podruhé. Celkem je nových 39 Desktop a 14 Capture případů.
Finální buildy použité pro UI ověření proběhly bez chyb a upozornění.

**`.gitignore` testy nevylučuje.** Testovaly se i dostupné ignorované lokální zdroje.
Přeskočené případy postrádají externí data: tři úplné QD reference v `specification`
(tentýž případ ve Windows legacy/Core a Linux Core, tedy tři spuštění) a pět
CP/M referencí v sousedním `_SFD800` (dva legacy případy). Ignorované soubory,
které existují, jsou do sestavení/testů zahrnuty podle projektových položek.

Nové testy ověřují přesné union intervaly, read-only lifecycle, změněný source,
kompletní/nekompletní manifesty a export přes hranici segmentů, zdrojové kolize,
skutečné PCM a kanály, gain bez změny dat, recovery a původ synthetic metadata,
sdílený decoder context, FFT normalizaci/okna/agregaci, cache/cancel/stale request,
trigger a sample zoom. UI testy obsluhují skutečné tabulky, myš a klávesnici,
sdílené detaily a více velikostí/render scales. Pixelový test brání regresi
prázdného spektrogramu způsobené nerozpoznaným immutable štětcem.

**10 binárních výstupů má shodné SHA-256 ve Windows i Linuxu**: čtyři kanálové
varianty selected PCM WAV a MZF/M12/MZT s jejich exportovanými metadata soubory.
Textové reporty se zdrojovými absolutními cestami nejsou vydávány za portable golden.

Nativní scénáře na každém OS mají 15 shell/picker + 14 Tape/QD + 16 diskových
+ 16 audio kontrol, tedy **122 PASS dohromady**. Audio scénář vytvoří vlastní
dekódovatelný WAV, otevře jej, změří pulzy, zobrazí FFT/spektrogram, exportuje PCM,
provede recovery a přenese program do Tape i QD. Původní soubory porovnává byte po byte.
Picker cancellation je ověřena systémovým pickerem; ostatní scénáře používají
kontrolované soukromé fixture cesty.

Všechny čtyři původní WPF smoke běhy prošly: Audio, Report, DSK/QuickDisk a Recorder.
Recorder má 114 syntetických UI kontrol; fyzické nahrávání tím není otestováno.
Statický F6 audit má 352 PASS, 0 selhání a kontroluje 108 source hashů, zachování F5/WPF a původních
algoritmů, pipeline hranice, důkazy a nezměněné obrázky. F2 token generator potvrzuje
shodu s kanonickými tokeny. Historický F2 validator stále označuje dvě již schválené
změny WPF z F4/F5; F6 jejich aktuální obsah ověřuje proti schválenému F5 hashi,
nepřepisuje původní auditní baseline.

## Neověřené prostředí a další hranice

Windows x64 a Debian 13/WSLg x64 používají .NET 10.0.303; Linux má již schválenou
trvalou instalaci SDK v `/home/ales/.dotnet` a oddělené sestavovací artefakty.
macOS, ARM64, samostatný běžný Linux desktop, screen reader, externí OS drag/drop,
všechny kombinace skutečných monitorových DPI a fyzický audio/disk hardware
zůstávají **UNTESTED**. Render scale 150/200 % je simulovaný test, ne certifikace DPI.

FLAC je v Avalonia explicitně nepodporovaný do platformní fáze F8; dosavadní WPF
chování se nemění. F6 neotevírá CaptureSession ani backend a neimplementuje F7.
Dalším krokem je posouzení skutečného F6 vzhledu/workflow; poté lze připravit
konkrétní rozsah F7 k samostatnému schválení.

## Opakování kontrol

```powershell
dotnet test tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj
dotnet test MZTools.Capture.Tests/MZTools.Capture.Tests.csproj
dotnet run --project tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj -- TestResults/F6/windows/native
python tests/MZTools.Desktop.Smoke/summarize_f6.py
python tests/MZTools.Desktop.Smoke/validate_f6.py
python docs/ui-design-system/tools/build_avalonia_tokens.py --check
```

Úplné autoritativní TRX a původní logy jsou uvedeny s hashi v měřeném JSON;
historické neúspěšné diagnostické pokusy nejsou započítány jako finální PASS.
Pro regenerování obrázků/binárních kontrol nastavte `MZTOOLS_F6_OUTPUT` na
odpovídající vlastní adresář ve `TestResults/F6`; surové testovací artefakty
zůstávají lokální, review PNG a měřený report jsou archivovány v `docs`.
