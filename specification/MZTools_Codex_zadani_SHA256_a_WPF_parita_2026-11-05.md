# MZTools – zadání pro Codex
## Etapa A: integrita a výkon velkých WAV/FLAC; etapa B: sjednocení Avalonia s referenčním WPF

**Datum zadání:** 5. listopadu 2026  
**Repozitář:** https://github.com/bales0/MZTools  
**Poslední prohlédnutý commit:** `d3dc601292101e4dd82bcc2022fa981301c75837` (4. 11. 2026)  
**Platformy:** Windows, Linux, macOS; .NET 10; Avalonia; původní WPF jako funkční a vizuální reference.

> **ZÁVAZNÉ PRAVIDLO – žádné změny bez schválení:** Neprováděj žádné úpravy zdrojových souborů, testů, projektových nastavení, dokumentace v repozitáři, build skriptů, balíčků ani vzdáleného repozitáře, dokud uživatel výslovně neodsouhlasí konkrétní rozsah implementace. Nejprve pouze čti, audituj, navrhni a předlož plán včetně dopadů. Ani příprava automatických fixů nebo commitů není schválena tímto zadáním. Po schválení realizuj jen odsouhlasenou etapu a změny; před každou další etapou znovu vyžádej souhlas.

## 0. Povinný začátek – ověř výchozí stav, nic neměň

1. Zkontroluj aktuální HEAD na GitHubu a místní pracovní větev. Porovnej je s uvedeným commitem; nespouštěj žádný příkaz, který přepisuje pracovní soubory nebo mění větev. Zaznamenej případné lokální změny. Pokud se repozitář od auditu změnil, nejprve ověř, zda níže uvedené problémy přetrvávají.
2. Za referenční chování ber původní WPF MZTools ze stejného repozitáře, nikoliv staré screenshoty či dřívější seznamy chyb. Před každým návrhem zkontroluj, zda už funkce nebyla implementována v nejnovější Avalonia verzi.
3. Přečti stávající auditní dokumenty a testovací manifesty, zejména `docs/WPF_PARITY_AUDIT_2026-11-04.md`, `docs/DSK_ANALYZER_PARITY_2026-11-04.md`, `docs/SELECTED_AUDIO_ANALYSIS_2026-11-04.md`, `docs/RELEASE_CHECKLIST.md` a odpovídající `*_VALIDATION_2026-11-04.json`.
4. Zpracuj tabulku **ověřená chyba / reprodukovatelné riziko / jen rozdíl UI / již opraveno / nelze ověřit**, vždy s konkrétními soubory a reprodukčními kroky. Nedomýšlej neověřené chyby.
5. **První výstup pouze:** audit, časový a výkonnostní plán měření, bezpečnostní rozbor navržených změn a seznam souborů, které by se po schválení měnily. **Potom zastav a vyžádej schválení.**

---

## ETAPA A – optimalizovat kontrolu integrity u velkých WAV/FLAC, aniž by se oslabila bezpečnost

### A1. Kontext a cílové použití

Uživatel běžně analyzuje cca **30minutové kazetové záznamy** WAV a FLAC. Soubor může mít stovky MB, typicky přibližně:

| 30 minut PCM | Přibližná velikost |
|---|---:|
| 44,1 kHz, 16 bit, mono | 159 MB (desítkových) |
| 44,1 kHz, 16 bit, stereo | 318 MB |
| 44,1 kHz, 24 bit, stereo | 476 MB |
| 48 kHz, 16 bit, stereo | 346 MB |
| FLAC | proměnná velikost podle komprese |

**Cíl:** zachovat rychlé zoomování, posun výřezu, stereo korekce ±1/±10 vzorků, invertování R, mix, hlubší analýzu a bezpečný export. Nepočítat SHA-256 celého souboru při každé interaktivní operaci. Nesnižovat ochranu zdrojových souborů, výstupů, segmentů a transakčního zápisu.

### A2. Co je v revidovaném commitu přítomno – ověř znovu

- `src/MZTools.Desktop/Documents/AudioSession.cs`: `LoadAsync()` počítá `AtomicFileTransaction.Hash(...)` pro zdroj(e) **před a po** úvodní analýze; `VerifySources()` hashuje celý zdrojový soubor či jednotlivé segmenty.
- `src/MZTools.Core/Media/AudioRangeInspection.cs`: `VerifySource()` před i po zpracování rozsahu používá primárně velikost a `LastWriteTimeUtc`. U manifestu `.wav.json` vytváří `SegmentedPcmStreamReader`.
- `src/MZTools.Desktop/Documents/AudioSession.cs`: `RefreshViewportCoreAsync()` používá uvedenou rychlou kontrolu před/po čtení výřezu.
- `src/MZTools.Desktop/Documents/AudioOperations.cs`: `StageWaveAsync()` volá `session.VerifySources()` **před i po** vytvoření výstupního WAV. Ověř skutečné pořadí dalších I/O operací.
- `src/MZTools.Core/Media/SegmentedPcmStreamReader.cs`: kontroluje SHA-256 segmentů a má cache závislou na cestě, velikosti, čase změny a očekávaném hashi. Detailně posuď platnost tohoto cache klíče při nahrazení obsahu beze změny metadat.
- `src/MZTools.Desktop/Documents/AtomicDocumentStore.cs`: u bezpečného zápisu používá otisky a atomický postup. Nezaměňovat ochranu proti souběžným změnám cílového souboru za opakovaný hash zdrojového WAV. Rozlišit ukládání dokumentů od exportu audia.

### A3. Povinná měření před návrhem optimalizace

Po výslovném souhlasu s diagnostickým měřením připrav *neinvazivní* měření nad kopiemi souborů nebo dočasnými testovacími daty (nikdy nepřepisuj originál). Zjisti zvlášť:

1. Otevření 30min WAV a FLAC: čas hashování, čas dekódování/analýzy, celkový čas, CPU, peak RAM a objem diskových čtení.
2. Dvacet rychlých zoom/pan operací na malém výřezu: ověř, že nevzniká úplné čtení zdroje kvůli SHA; zaznamenej latenci a přerušování rozpracovaných preview.
3. Opakované posuny R o ±1 a ±10 vzorků, invertování, mix a návrat k původnímu stavu: stejná měření i shoda výstupu.
4. `Analyze selected audio`, `Run deeper analysis`, histogram, výběr rozsahu 10 s / 60 s / celé médium.
5. Export výběru do WAV a export celého půlhodinového WAV: zdokumentuj počet úplných průchodů zdrojem, čas zpracování a všechny bezpečnostní kontroly.
6. Test běžného SSD i pomalejšího média, alespoň s rozlišením „cold“ a „warm cache“, pokud to místní prostředí umožní. Porovnej Windows a Linux; macOS označ **NETESTOVÁNO**, není-li k dispozici.
7. WAV 16/24bit, mono/stereo a FLAC; výstupní PCM musí zůstat bitově shodný, pokud daný formát a transformace bitovou shodu umožňují.

Zveřejni reálné výsledky a metodiku, nikoliv odhadované „zrychlení“. Nepoužívej nereprezentativní několikasekundový soubor jako jediný benchmark.

### A4. Kandidátní optimalizace – nejprve posoudit, pak teprve implementovat

1. **Interaktivní operace:** zachovat kontrolu identity/velikosti/mtime, nikoli opakovaný SHA celého média. Doplň bezpečné řešení výměny souboru během práce, například rozumnou kombinací metadata snapshotu a otevřeného handle, pokud je multiplatformně použitelná. Opatrně posuď edge cases u FLAC a síťových disků.
2. **Otevření WAV/FLAC:** ověř možnost získávat SHA při jednom z již potřebných sekvenčních čtení místo samostatného předběžného a následného průchodu. Nesmí se tím ztratit kontrola změny během analýzy; při závodě s externím zápisem musí program bezpečně odmítnout nebo opakovat podle definované politiky.
3. **Export WAV:** zhodnoť, zda opakovaná `VerifySources()` před/po exportu může být zefektivněna *bez oslabení záruky*, že export pochází z odpovídajícího zdroje. Pouhý cache hash podle mtime+size **není ekvivalent SHA-256**. Pokud bezpečnost nelze zachovat, ponech dodatečný průchod a dokumentuj jeho cenu.
4. **Segmentovaná nahrávka:** audituj chování `.wav.json` a cache. Zachovej ověřitelnost všech segmentů, jejich pořadí, délky a hashů. Nesmí se potichu použít segment se změněnými bajty ani zaměněným souborem.
5. **Paměť:** používat streamovaný hash, bez načítání stomegabajtových souborů do RAM. Neukládat neomezené cache položky. Optimalizovat I/O průchody, ne na úkor fragmentace paměti.
6. **Prostředky:** správně uvolňovat streamy, rušit operace přes `CancellationToken`, neblokovat UI thread, zachovat průběh a chybové zprávy.
7. Rozlišuj režim analýzy, operaci exportu a nebezpečný zápis do existujícího dokumentu. Nezaváděj zbytečné uživatelské přepínače „rychlý/nebezpečný“ bez odůvodnění.

### A5. Regresní a bezpečnostní scénáře

Otestuj změnu zdroje **mezi prvním ověřením, analýzou, exportem a posledním ověřením**, včetně: jiné velikosti, změny při stejném čase, stejná velikost + obnovené mtime, nahrazení souboru jiným souborem, odebrání/odpojení disku, dočasně nedostupný FLAC, poškozený manifest, změněný jediný segment a uživatelské Cancel. Každou situaci označ jako *detekovatelnou podle navržené garance*, nebo přiznej nedetekovatelný případ. Neoznačuj metadata-only kontrolu za kryptografickou integritu.

**Akceptace etapy A:** při opakovaném zoom/pan žádný nechtěný úplný SHA průchod; při exportu nedojde k tiché záměně zdroje; časy a I/O jsou změřené; žádný regresní rozdíl v PCM, vzorcích, rozsazích, diagnostice, výstupních bajtech a ochraně originálů. Implementaci provést jen po schválení konkrétního návrhu.

---

## ETAPA B – další sjednocení Avalonia s původním WPF MZTools

**Začátek až po samostatném odsouhlasení etapy B.** Nejprve vytvoř podrobnou paritní matici všech reálně dostupných nabídek, editorů, dialogů, tabulek, detailních oken, kontextových menu a klávesových zkratek. Porovnej chování na stejných testovacích datech WPF vs. Avalonia, nikoli pouze názvy tříd.

### B1. P0: chyby ovlivňující výsledek nebo interpretaci dat

**B1.1 – Nový DSK/HFE: výchozí geometrie podle profilu**

- Reference: `DskNewDialog.cs` (WPF), zejména `UpdateFields()` a `UpdateCapacity()`.
- Avalonia: `src/MZTools.Desktop/ViewModels/ShellDiskCommands.cs`, metoda `ChooseDiskOptionsAsync()`; Core `src/MZTools.Core/Dsk/DiskFormatService.cs`, `src/MZTools.Core/Dsk/DskDocument.cs`.
- V Avalonia je `tracks` výchozí `40` pro všechna zvolená schémata a změna profilu automaticky neupraví geometrii. Oproti WPF může nevědomé potvrzení vytvořit například LEC DD **360 KiB místo 720 KiB**, LEC HD **720 KiB místo 1,44 MiB**, MRS **360 KiB místo 720 KiB**.
- Navrhni WPF-paritní dynamickou volbu profilu, přepočet geometrie a zobrazené kapacity; rozliš pevné a uživatelské geometrie. Zachovej platnou možnost záměrně nestandardní geometrie, pokud ji Core bezpečně podporuje a UI to jasně označí.
- Otestuj 320 KiB, 400 KiB, 720 KiB, 1,44 MiB, fyzické HFE varianty, IPL single/multi, P-CP/M80, SDS/400, FSMZ/IPLDISK, MRS, Lemmings a custom raw včetně pořadí sektorů, ukládání a znovuotevření.
- Žádné tiché změny historických formátů nebo zavádění neověřených CP/M konfigurací.

**B1.2 – `Analyze selected audio`: konzistence diagnostických overlayů**

- Reference: `AudioRegionRecoveryPreview.cs`, zejména `RefreshPreview()`, `RefreshCompletedHistogram()` a `ApplyScanFindings()`; WPF vrací diagnostické oblasti jen při shodném transformu a intervalu.
- Avalonia: `src/MZTools.Desktop/Documents/AudioSession.WpfViewport.cs` (`ApplyTransformKeepingViewportAsync`), `AudioSession.SelectedPreview.cs` (`SetRawSelectedPreview`, `UseSelectedRecoveryMeasurement`), `src/MZTools.Desktop/Views/RecoveryWindow.cs`.
- **Hypotéza k reprodukci:** po `Run deeper analysis` může další waveform-only refresh zavolat `SetRawSelectedPreview()` a nahradit nalezené chyby/checksum kandidáty a regiony jen základním PCM náhledem. Ověř, co přesně uživatel vidí po změně R offsetu, polarity, mixu, rozsahu a po přepnutí histogramového kanálu.
- Odděl **aktuální PCM náhled** od **historických výsledků poslední hlubší analýzy**, jasně zobraz `OUTDATED`, pokud se nastavení liší, a nespojuj nekompatibilní výsledky. Při stejném rozsahu a transformaci zachovej přiměřené diagnostické informace WPF. Nerozbíjej výběr a zoom ani izolaci samostatného okna od hlavní analýzy.
- Testuj skutečné checksum chyby, přerušené bloky, reset detektoru a kandidáty; rozliš přetrvávající data v session od toho, co vykresluje GUI.

### B2. P1: funkční/UI parita, nikoli pouhé vzhledové úpravy

**B2.1 – HEX Browser pro Tape/QuickDisk**

- Reference `HexBrowser.xaml.cs`, Avalonia `src/MZTools.Desktop/Views/HexViewWindow.cs`.
- Doplň po schválení smysluplné označení **MZF header / data / trailing**, přímý sloupec **SHASCII (EU)** a zvýraznění QDF prefixu a CRC tam, kde je relevantní. Zachovej úplný obsah včetně trailing bajtů; současná Avalonia je již čte, pouze je neodlišuje.

**B2.2 – DSK HEX editor a práce s metadaty**

- Reference `DskHexEditorWindow.cs`, `DskEditorWindow.xaml.cs`, `DskFilePropertiesDialog.cs`.
- Prověř rovnocennost zamčeného fixed-size editoru, HEX/ASCII/SHASCII, náhledu změn, potvrzení dopadu na filesystem, zrušení úprav a navázání na vybraný sektor či CP/M blok.
- V Avalonia `DiskWorkspace.axaml` je zjednodušené pole HEX; nesmí vzniknout druhá nezávislá editační logika v Core. Znovupoužij existující ochrany a preview.
- WPF: **F2 = přímé přejmenování**, kde je dovoleno. Avalonia v `DiskWorkspace.axaml.cs` místo toho otevírá inspektor. Urči požadovanou paritu včetně potvrzování a návratu při chybě.

**B2.3 – Dialogy a výběr parametrů diskových operací**

- V Avalonia `src/MZTools.Desktop/Platform/DesktopDiskDialogs.cs` a dalších adaptérech je řada univerzálních formulářů. Porovnej se specializovanými WPF dialogy, zejména vytvoření/formátování, vlastnosti souborů, filesystem, oprava, boot/system, export/import sektorů a porovnání.
- Každou rozdílnou akci zaznamenej: předvyplnění, dynamická pole, dostupnost při konkrétním typu disku, validace, náhled dopadu, `Cancel`, výchozí fokus.
- Nevymýšlej nový design: reprodukuj funkční chování WPF s multiplatformními ovládacími prvky.

**B2.4 – Audio pulzní reference a jejich prokazatelnost**

- Reference `AudioPulseReferenceControl.cs`, `AudioRegionRecoveryWindow.cs`, `AudioRegionRecoveryPreview.cs`; Avalonia `RecoveryWindow.cs`, `ShellAudioCommands.cs`, Core `PulseReferenceCatalog.cs`.
- Zachovej **jediný společný katalog**: zobraz profil, stroj, software/verzi, zdroj, odvození, polaritu, čtyři časy SHORT H/L a LONG H/L v µs a kontext/omezení.
- Sjednoť výběr jedné či více referencí, vlastní referenci platnou v session, náhled histogramů/scatter/drift, barevné skupiny, viditelnost overlayů, správné channel/polarity a výslovné aktivování reference testů. Nepoužívej zastaralou „compatibility“ referenci ani odhad namísto katalogu.
- Před jakoukoli změnou výpočtů předlož důkaz, že rozdíl není pouze v UI; validované pulzní algoritmy nepřepisuj.

**B2.5 – Batch Process a hlavní menu**

- Porovnej `BatchProcessDialog.cs` vs. `BatchWorkspace.axaml` a `ShellBatchCommands.cs`: režim soubory/složka, dynamické volby podle operace, target, compression, heuristic, scan extensions, možnost replace + backup, náhled, průběh, stop, reporting a klávesová navigace.
- `MainWindow.Wpf.cs` skrývá klasické `File`/`Tools` menu a nahrazuje je `Tools ▾`. Zkontroluj dostupnost všech funkcí včetně importu MZF/M12 jako COM na CP/M disk, všech kontextových nabídek a klávesových zkratek. Rozhodnutí o výsledném layoutu předložit ke schválení; nepřidávat dvě duplicitní nabídky.

**B2.6 – Visual Inspector a stavy bez filesystemové projekce**

- DSK/HFE a QuickDisk vizuální nástroje jsou dle posledních dokumentovaných testů z velké části sjednocené. **Neimplementuj je znovu.** Pouze regresně porovnej Track/Side/Width/Layer, 6 vrstev, filtry nálezů, splitter, floating details, MFM, sector selection, QD osm řádků a 32závitovou spirálu.
- Prověř nabídkový příkaz `Import selected sectors` v `DiskVisualWindow.cs` při read-only / fyzických obrazech bez filesystemové projekce: nesmí předstírat dostupnou funkci, která pak neprovede nic. Vysvětli její důvod deaktivace.

### B3. P1/P2: desktop / multiplatformnost

- Testuj malá okna a různé DPI; některá okna Avalonia mají pevné min. rozměry, zatímco WPF používá rozměry pracovní plochy.
- Zaměř se na table scrollbars, zobrazení plného rozsahu, horizontální posun, splittery, vybírání více položek Ctrl/Shift, chování `Esc`, `F2`, `Enter`, `Delete`, `Ctrl/Cmd+C/V`, F6 a tab order.
- Prověř více současně otevřených waveform oken se společným session/viewport refresh, stálý přepočet grafu a zrušení starších úloh; odliš věrnost zobrazení od správnosti PCM.
- Platformy: nejprve Windows/Linux desktop; macOS a ARM až podle dostupného skutečného runtime. Fyzické audio zařízení, odpojování během nahrávání a 10–30min záznam jsou samostatná ověřovací brána, ne automaticky „PASS“.
- Kde WPF vykazuje vlastní chybu, neopakuj ji pro paritu; založ zvláštní položku s důkazem a bezpečnou variantou řešení.

### B4. Matice parity – povinná forma výsledku

Pro každou oblast zaznamenej: `funkce`, `WPF chování`, `Avalonia chování`, `rozdíl`, `dopad na data`, `priorita`, `důkaz (soubor/metoda + reprodukce)`, `návrh`, `regresní test`, `stav schválení`.

Nedělej závěry pouze ze starého seznamu rozdílů: aktualizovaný DSK Visual Inspector, QD mapa, PCM stereo posun ±1/±10, zachování zoomu, vybraná audio analýza a COM nástroje už mají implementaci i testy. Každý údaj znovu ověř proti aktuálnímu HEAD.

---

## 3. Schvalovací brány a požadované dodávky

**Brána G0 – bez zásahů:** Dodej pouze analýzu a návrhy pro etapy A i B. Ve zprávě jasně odděl:

- **potvrzená závada**, s reprodukcí;
- **pravděpodobné riziko**, dosud nereprodukované;
- **UI rozdíl proti WPF**, bez důkazu chybného výstupu;
- **již implementováno**, jen regresně ověřit;
- **netestováno / omezení prostředí**.

Uživatel musí explicitně povolit konkrétní etapu a soubory.

**Brána G1 – až po odsouhlasení:** Implementace a testování **etapy A**. Předložit srovnávací benchmark, podrobný rozbor integrity, jednotkové i integrační testy a přesný seznam změn. Všechny vstupní WAV/FLAC a záznamové manifesty ponechat beze změny.

**Brána G2 – až po novém odsouhlasení:** Implementace priorit **etapy B**, vždy po logicky uzavřených skupinách. Po každé skupině provést build i konkrétní regresní testy, porovnat s WPF na shodných datech. Nepřepisovat úspěšně přenesené funkce jen kvůli refaktoringu.

**Po schválené implementaci a úspěšném dokončení:** Pokud uživatel požaduje balík, připrav **ZIP obsahující pouze celé změněné soubory s jejich relativními cestami**, nikoliv patch/diff a nikoliv celý repozitář; přidej krátký soupis změn, commit/HEAD, výsledky buildů a testů, neověřené platformy a známá omezení. Není-li implementace odsouhlasená, žádný balík s upravenými projektovými soubory netvoř.

### Zásadní omezení

- Referenční **WPF musí zůstat beze změny**; nesmazat původní projekt.
- Zachovat .NET 10, multiplatformnost a sdílený Core.
- Nezměnit pulsní klasifikátory, datové formáty, CP/M DPB, MZF/M12/COM loader, QD logické/fyzické formáty nebo boot postup bez doložené nutnosti a zvláštního souhlasu.
- Zákaz tichého zápisu do zdrojových obrazů, WAV/FLAC a manifestů; zachovat transakční Save As / Export.
- Žádné falešné tvrzení „100% WPF parity“, „all platforms tested“ či výkonnostních zisků bez reálných měření.
- Nikdy necommitovat, nepushovat ani nevytvářet PR bez výslovného schválení.

**První odpověď Codexu musí obsahovat pouze výsledky statické analýzy a návrh pořadí řešení; následuje čekání na explicitní schválení změn.**
