# Deep Analyze – skutečný vstup a oddělení Normal/Deep

Výchozí HEAD: `9b471ddcbf0508d80bba64303de363243ac86601`. G0 audit a návrh předcházely implementaci; uživatel je schválil odpovědí „souhlasím“. Schválené ověření: cílené buildy a FAST regrese. Pozdější výslovný požadavek uživatele nahrazuje návrh volby Overlay/Mono v Normal: hlavní okno nemá přepínač kanálů, stereo automaticky zobrazuje overlay L/R, mono jeden průběh.

## Výsledek auditu a schválená sémantika

Původní `ChannelMode` řídil především graf (L/R/Overlay/Average), zatímco recovery formulář předával jinou volbu `options.Channel` a samostatný Mix. Overlay při měření vybíral L. Baseline `AudioSignalAnalysisService.Analyze` zpracovává původní fyzické kanály. Core `AudioPcmTransform` již obsahuje bezpečné zarovnání a aritmetický průměr; jeho změna nebyla potřebná. WPF zůstává referencí, nikoli místem této opravy.

| Vstup Deep | Výpočet a označení |
| --- | --- |
| Left | Pouze L; offset a inverze R se při přepnutí vynulují, ovladače R se znepřístupní. |
| Right | Pouze zarovnané R, v původní referenční časové ose L. |
| Average | Skutečné celočíselné `(L[n] + R[n-offset])/2`, po případné inverzi R. |
| Both / Overlay | Dva nezávislé Core decode běhy; jejich programy se nesestavují napříč kanály ani vzájemně nededuplikují. Tabulky a JSON rozlišují Left/Right. Pulzní měření zachová obě samostatné sady. |
| Mono soubor | Jeden funkční vstup Mono; ovladače R nejsou použitelné. |

Offset je v celých zdrojových framech, UI nabízí také ms, krok ±1 a reset, rozsah ±2 s. Platí `R_aligned[n] = R[n-offset]`: kladný posun R zpozdí. Používá se pouze společný platný interval `[max(start,0,offset), min(end,N+min(0,offset)))`, bez přetáčení nebo doplňování nul. Indexy kandidátů, waveformu a exportu zůstávají na původní referenční ose. Core používá 24bitovou PCM reprezentaci, součet přes `long` a omezený kruhový buffer podle posunu, nikoli kopii celé nahrávky.

## Datový tok a změněné části

- `Documents/DeepAnalysisInput.cs`: neměnná identita signálu, offsetu, polarity a rozsahu; popis aktuálního/dokončeného vstupu a označení OUTDATED.
- `AudioSession.cs`, `SelectedPreview`: Deep má vlastní relaci, životnost, nastavení, výběr a výsledky; sdílí pouze zdrojové PCM a vstupní metadata. Nové Deep začíná s nulovým posunem. Normal baseline decode se nepřepočítává změnou Deep.
- `AudioSession.Recovery`: snapshot vstupu → `AudioRecoveryOptions.Transform/Channel` → `WavHeuristicAnalyzer.AnalyzeRange`; Both spouští oddělené běhy. Publikování hlídá generaci nastavení/výběru, Cancel a identitu vstupu. Filtruje se vystavené pulzní měření na skutečně zvolený fyzický kanál. Souhrnná kandidátní/diagnostická evidence má omezený rozpočet.
- `AudioSession.Analysis`, `WpfViewport`: stejná transformace pro PCM náhled, pulzní měření, FFT a trigger. Both měří oba kanály; FFT graf a jeho souhrn respektují vybraný vstup. Klíč cache obsahuje kanál i transformaci, interval a FFT velikost; změna nastavení zahodí zobrazené měření/spektrum a rozpracovaný výsledek se nepublikuje. FFT data zůstávají omezená původním 24 MiB cache rozpočtem.
- `RecoveryWindow`, `AudioWorkspace.axaml`, `AudioChart.WpfWaveform`: skutečný vstup vedle offsetu; rozsah a znaménko, synchronizace duplikovaných ovladačů, stav OUTDATED. Nové měření pulzů se zobrazí i po zastarání staré recovery. Starý waveform není prezentován jako právě požadovaná transformace. Hlavní Normal okno podle následného pokynu nemá volbu Overlay/Mono.
- `ShellAudioRecovery`: i formulářový Deep vstup z Normal vytvoří samostatnou relaci; Cancel/chyba ji bezpečně uvolní.
- `AudioRows`, `AudioOperations`, `ShellAudioCommands`: zdroj v tabulkách a JSON; zastaralé programy/raw kandidáty/syntetické hlavičky nelze exportovat jako aktuální. Report uchová popis dokončeného vstupu a `isCurrent`. Výslovná volba WAV exportu Left/Right/Stereo/Average má přednost před Deep režimem; zachovává zvolený interval a zarovnání. Gain a Auto scale zůstávají pouze zobrazovací.

Zdrojová WAV/FLAC i segmentované nahrávky zůstávají read-only. Ověření SHA-256 a původní exportní transakce/Preview/Confirm/Cancel se zachovávají. Import a recorder workflow se v této úpravě nemění. Předchozí rozpracované změny fáze 5 zůstaly zachovány.

## Ověření

Nové `DeepAnalysisInputTests` používají malé deterministické WAV/MZF fixture: L/R/Both/Average, dva různé programy, jeden tichý kanál, opačná polarita, skutečné vzorky průměru u kladného i záporného posunu, zarovnání zpožděného R před dekódováním Average, mono, zrušený opakovaný běh, přepnutí nastavení během FFT, zastarání výsledků a nezávislý stereo export. UI regrese kontrolují skutečné vzorky po stisku posunu, oddělení rodiče a Deep, histogram po zastarání recovery a automatické zobrazení mono/stereo bez přepínače.

Finální ověření po posledním požadavku uživatele:

| Kontrola | Windows | Linux / WSL Debian |
| --- | --- | --- |
| Desktop FAST | 203 PASS, 0 FAIL, 0 SKIP; 9,731 s testů | 203 PASS, 0 FAIL, 0 SKIP; 10,550 s testů |
| Core raw/patch FAST | V této etapě nespouštěno | 17 PASS, 0 FAIL, 0 SKIP; 863 ms testů |
| Překlad | 0 chyb / 0 upozornění; 4,450 s | Desktop 13,459 s; Core 4,706 s; 0 chyb / 0 upozornění |
| Kopie korpusu | 0 souborů / 0 B | 0 souborů / 0 B |

Finální počty a časy jsou v `TestResults/PickerPolicy/<host>/fast/desktop.trx`, `summary.json` a `timings.json`; konzolové záznamy v `TestResults/DeepInput-2026-11-09/*-fast.log`. Příkazy: Windows `python tools/test_picker_policy.py`; WSL Debian `python3 tools/test_picker_policy.py --core`. Runner vždy překládá aktuální zdroje, explicitně vypíná automatické publikování a FAST nekopíruje mediální korpus. Dřívější přerušený synchronizační test není důkaz PASS: fixture byla opravena na alespoň 16 FFT oken a čekání má timeout.

Obsahový guard `TestResults/DeepInput-2026-11-09/original-source-guard.json`: 203 původních Core/WPF/Dsk/Media zdrojů a projektů, žádná odchylka proti HEAD po normalizaci konců řádků.

Nativní UI, dlouhé WAV/FLAC, plný mediální korpus a nová distribuce nebyly v této etapě spuštěny. Paměťové chování dlouhého vstupu je doloženo čtením streamovací implementace a omezenými buffery/cache, nikoli dlouhým benchmarkem. FAST není úplná sada testů; runner uvádí přesné třídy a výjimku pro pomalou HFE serializaci. Obsah `bin/win-x64` a `bin/linux-x64` se touto etapou neaktualizuje; aktuální testovací sestavení jsou v `obj/test-runs/<host>/fast`.
