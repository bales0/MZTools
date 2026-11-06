# Zadání pro Codex — MZTools: Batch Process 1:1 podle původního WPF

**Datum auditu:** 6. 11. 2026  
**Repozitář:** https://github.com/bales0/MZTools  
**Ověřený HEAD `main`:** `bb39bc4175a75f3ec71eb6de3446f243434dd4fa` (5. 11. 2026)  
**Cíl:** dokončit **věrnou funkční i vizuální migraci celého nástroje Batch Process** z WPF do Avalonia/.NET 10 na Windows, Linux a následně macOS. Nestačí, že sdílený Core podporuje stejné operace; běžný uživatel musí mít k dispozici stejně sestavený a ovladatelný pracovní postup.

> **Schvalovací brána:** Toto je zadání k revizi, nikoli souhlas ke změně repozitáře. Nejprve proveď read-only audit a předlož přesný návrh souborů a UI. **Žádný zásah do zdrojů, testů, projektových souborů, Git historie ani vydaných balíků bez explicitního schválení uživatelem.** Po schválení implementuj jen odsouhlasený rozsah. Bez samostatného souhlasu necommituj, nepushuj, nevytvářej PR ani neupravuj referenční WPF.

## 1. Závazná reference a současný stav

**Primární WPF reference (zdroj pravdy pro Batch):**

- `BatchProcessDialog.cs` — celé hlavní okno, jeho životní cyklus, nastavení vstupů a výstupů, seznam předběžně vybraných souborů, preflight, průběh, Stop, potvrzení a export reportu.
- `BatchFileDetailsDialog.cs` — samostatné okno detailů, pět záložek, kontextové vlastnosti/integrity, třídění a kopírování tabulek.
- `CompressionOptionsControl` — původní integrované nastavení komprese.
- `src/MZTools.Core/Media/BatchMediaPolicy.cs`, `BatchProcessService.cs`, `BatchTapeService.cs` — sdílená pravidla a implementace operací; před změnou znovu ověř soulad WPF s těmito službami.

**Aktuální Avalonia, kterou je nutné skutečně zkontrolovat:**

- `src/MZTools.Desktop/Views/MainWindow.Wpf.cs` (`Tools ▾` → Batch Process)
- `src/MZTools.Desktop/Views/AnalysisWindow.cs` (`AnalysisWindow(true)`, `PrepareBatchAsync()`)
- `src/MZTools.Desktop/Views/BatchWorkspace.axaml`
- `src/MZTools.Desktop/ViewModels/ShellBatchCommands.cs`
- `src/MZTools.Desktop/Platform/BatchProcessForm.cs`
- `src/MZTools.Desktop/Platform/DesktopDiskDialogs.cs`
- `src/MZTools.Desktop/Documents/BatchSession.cs`
- `tests/MZTools.Desktop.Tests/BatchDocumentTests.cs`, nativní smoke scénáře
- Existující dokumentace `docs/WPF_PARITY_MATRIX_2026-11-05.md`, `docs/RELEASE_CHECKLIST.md`.

**Ověřené strukturální rozdíly v HEAD `bb39bc4`:**

| Oblast | Původní WPF | Aktuální Avalonia | Výsledek požadovaný v zadání |
|---|---|---|---|
| Celý workflow | **Jedno okno Batch Process** obsahuje nastavení, seznam, preflight, spouštění, průběh, výsledek a report | Nejprve generický modální `DiskFormAsync`, potom oddělený `BatchWorkspace`; dialog se neudržuje během výsledků | Jeden souvislý pracovní prostor/okno podle WPF; bez dalšího překryvného generického formuláře |
| Výběr souborů | Radio `Selected files` / `Input folder`, `Add files…`, `Browse…`, `Clear file list`, read-only seznam cest | Výběr až po odsouhlasení formuláře, bez současného viditelného seznamu a jeho údržby | Stejný viditelný seznam a stejné ovládání již před Preview |
| Volba operace | Zobrazuje uživatelské popisky z `BatchMediaPolicy.Label`, začíná `Info / test integrity`; popis operace pod volbou | `Enum.GetNames<BatchOperation>()` s default `Analyze` | WPF pořadí, popisky, výchozí volba a kontextový popis; žádná tichá ztráta existující funkce |
| Dynamika parametrů | Povoluje/zakazuje pole podle operace, cílového formátu a režimu vstupu; mění přípony a tooltipy | `BatchProcessForm` převážně pole **skrývá**; neobnovuje všechny závislosti (např. extract, rate, heuristic) | WPF funkční stavy a aktualizace při změně každé závislé volby |
| Preflight | `Preview / dry-run` aktualizuje tabulku přímo v témž okně | Preview vytvoří dokument `BatchSession` a otevře jiný pracovní panel | Průběžná tabulka „Selected inputs / preview results“ před i po preflightu |
| Tabulka | Input, DetectedFormat, Filesystem, Operation, Result, Integrity, Reason, Output | Input, Output, Operation, Format, Result, Severity, Reason | Referenční sloupce, pořadí, scroll, výběr, přístupnost; případné rozšíření až za WPF sloupci |
| Tlačítka a stavy | Preview, Execute preview, Selected details, Export TXT/CSV/JSON report, Stop | New batch / Preview, Execute prepared batch, Export report; detail jen v záložkách | WPF sémantika, názvy, povolování, zpětná vazba, přerušení |
| Průběh/Stop | Per-file `BatchProgress`, progress bar, text Stage/Completed/Total/File, Stop, bezpečné zavření | Jen obecný `Documents.Status` a globální Cancel, bez per-file průběhu předaného ze služby | Reálný průběh a Stop v Batch okně, správné zacházení se zavřením během běhu |
| Detaily souboru | Samostatný `BatchFileDetailsDialog` s 5 záložkami, kontextovými sloupci, tříděním a clipboardem | Vestavěné 3 záložky `Selected details / Contents / Full report` | Samostatné detailní okno ekvivalentní WPF, bez ztráty dat |
| Potvrzení ztrát | Nezávislý souhlas se ztrátou fyzických/metadatových informací a se replace | Obecné potvrzení; `BatchProcessService.Execute(..., true, ...)` předává souhlas se ztrátou automaticky | Samostatný výslovný souhlas se ztrátou a případně se nahrazením originálů |
| Volby Extract All | `Native`, `BIN`, `MZF`, `M12`, `MZT`, `WAV`, `FLAC`, `LEP`, `L16` | `Native`, `Mzf`, `M12`, `Bin` | Úplné WPF volby, správné case-sensitive hodnoty Core |
| Skenované přípony | Dynamicky `BatchMediaPolicy.Extensions(operation,target)` | Statický text vynechává např. `.m12`, `.mz0`, `.mz7` | Politika Core pro typ operace a cíle, možnost uživatelského filtru a viditelný náhled výsledku |
| Po dokončení | V témž okně zůstane summary, tabulka, report, možnost změnit nastavení a nový preflight | Stav oddělený od vstupního formuláře | Návrat k další dávce bez nutnosti opakovaně otevírat další modální konfiguraci |

**Pozor:** Toto jsou zjištění z porovnání zdrojového kódu, ne důkaz pixelové shody či běhové reprodukce všech stavů. Po schválení musí Codex obě UI skutečně spustit, pořídit autentické snímky a každou větev porovnat. Změna formy ovládání má smysl jen tam, kde přibližuje WPF, ne kvůli libovolnému redesignu.

## 2. P0 — přenést celé hlavní okno Batch Process

1. **Nahradit dvoustupňový workflow** (generické modální nastavení → výsledkový panel) ekvivalentem referenčního `BatchProcessDialog`. Při `Tools ▾ → Batch Process…` otevřít jedno nezávislé okno se všemi prvky od začátku; nesmí okamžitě otevřít další `DiskFormAsync`. Zachovat multiplatformní window owner, zavírání a samostatný životní cyklus. **Nepožadovat WPF knihovny v Avalonia.**
2. Zachovat referenční **vertikální kompozici**: scrollovatelná část parametrů nahoře, roztahovatelná tabulka vybraných vstupů a výsledků uprostřed, stavový text, progress, akční tlačítka a Stop dole. Referenční rozměry WPF: 1120 × 800 DIP, min. 800 × 500; přizpůsobit skutečné pracovní ploše a DPI (malé displeje nesmí ztratit tlačítka). Horizontální/vertikální scroll má fungovat na obou OS.
3. Ovládací prvky v pořadí WPF: `Operation` + popis; `Selected files` / `Input folder`; read-only selected paths + `Add files…`; cesta složky + `Browse…`; `Folder extensions`; recursive; `Conversion target` / read-only „Same as source“; `Extract output format`; integrovaná `CompressionOptionsControl` + vysvětlení; `WAV / FLAC rate (Hz)`; `Use heuristic analysis`; `Boot source / profile` + Browse; `Output folder` + Browse; `Naming template`; `Strict all-or-nothing preflight`; `Replace originals`; `Keep .bak`.
4. Vstupní seznam `Selected inputs / preview results`, `Clear file list` a **osm referenčních sloupců** v pořadí: `Input`, `DetectedFormat`, `Filesystem`, `Operation`, `Result`, `Integrity`, `Reason`, `Output`. Udržet dostupný výběr a samostatné detaily řádku; nepřepisovat seznam po změně pohledu bez důvodu.
5. Výchozí operace dle WPF **`Info / test integrity`**; uživatelské názvy z `BatchMediaPolicy.Label()`, nikoli enum identifikátory. Rozhodni, jak zachovat samostatnou funkci `Analyze` mimo referenční pořadí, pokud má zůstat dostupná: navrhni explicitní UX bez tiché ztráty funkce a předlož ke schválení.
6. Po změně vstupu, operace, targetu, komprese, outputu, template, checkboxů atd. okamžitě **zneplatnit starý preflight**, deaktivovat Execute/report/details, obnovit podporované přípony a vysvětlit proč. Nikdy nespouštět Execute nad výsledky pro jiné parametry.
7. Folder mode: průběžné asynchronní skenování s krátkým debounce, generační ochranou před zastaralými výsledky a správnou filtrací. Souborový režim: opakované Add files, deduplikace, indikace nepodporovaných souborů a Clear list. Přepnutí režimu nesmí použít skryté staré zdroje.
8. Browse souborů, input folder, output folder, boot DSK/profile: plnohodnotné systémové dialogy s odpovídajícími filtry. `Select files` nesmí být nahrazen jediným generickým pickerem otevřeným až na konci formuláře.
9. Zbytek MZTools nesmí ztratit možnost otevřít Batch z hlavního menu; samostatné analýzy a běžné dokumenty musí pokračovat beze změny.

## 3. P0 — preflight, Execute, bezpečnost, průběh

1. Jednoznačně oddělit **pouze čtecí** Preview/dry-run od explicitního Execute: před Execute žádný zápis do uživatelských výstupů ani změna originálů. Preflight zpracovat přes stávající `BatchProcessService.Preview()`.
2. **Preview přímé v tabulce**: zobrazit `Selected`, `Unsupported input`, `Will modify`, `Will analyze`, `Already matching`, `Skipped incompatible`, `Error` apod. včetně důvodu, integrity a výstupní cesty. Pod tabulkou shrnutí. Strict all-or-nothing musí blokovat Execute při invalidních/incompatible zdrojích stejně jako WPF.
3. Execute aktivovat pouze u validního *aktuálního* preview. Znovu ověřit neměnnost vstupů/sidecarů a output paths; při změně zdroje ohlásit nutný nový preflight. Zachovat transakční zápis a ověřování výstupů z Core.
4. **Nikdy nepředat `confirmLoss=true` automaticky.** Pokud jakýkoli prováděný řádek vyžaduje `RequiresLossConfirmation`, zobrazit oddělený souhlas „Accept metadata/physical information losses…“ s konkrétními varováními; odmítnutí nic nezapíše. Při `ReplaceOriginals` navíc samostatně odsouhlasit náhradu a backup policy.
5. Předat skutečný `IProgress<BatchProgress>` do `Preview(...)` i `Execute(...)`; ukazovat stage, completed/total, název souboru, progress bar a Stop, nikoli fiktivní průběh. Bezpečné thread-dispatch do Avalonia; neblokovat UI ani při delším WAV/FLAC.
6. Stop musí rušit `CancellationToken` používaný pro aktuální dávku. Po zrušení nepublikovat nedokončený výstup; již úspěšně dokončené dílčí výstupy se řídí existující politikou Core. Neztratit přehled, které položky byly dokončeny, chybné či zrušené.
7. Při zavření během operace: požádat o zastavení, vyčkat na bezpečné uvolnění operace v asynchronním workflow, **neodpojovat logiku od běžícího tasku**, nezavřít a nepřepisovat výsledky před dokončením cleanup; bez blokování UI smyčkou.
8. Po skončení uchovat summary, tabulku a details/report; nepřipustit opakované spuštění stejného dokončeného preview bez nového explicitního kroku. Chybová hlášení musí být srozumitelná, s konkrétním souborem.
9. `Export TXT / CSV / JSON report…`: v jednom okně, aktivní až po validním preflightu (případně po Execute), obsah podle skutečných `BatchRow` a existujícího `BatchProcessService.Report`; neumožnit přepsat aktuálně otevřené zdroje ani ignorovat ochrany transakčního exportu.

## 4. P1 — úplná parita chování formuláře / podporovaných operací

1. Vytvořit tabulku stavů `Operation × input mode × target × extract format` a pro každou kombinaci porovnat **viditelnost, IsEnabled, default, nápovědu a validaci** ve WPF a Avalonia. WPF většinu polí nechává ve formuláři, ale podle operace je zakazuje; nevyměňovat bezmyšlenkovitě za schovávání a neztrácet souvislosti.
2. Průběžně aktualizovat `Folder extensions` podle `BatchMediaPolicy.Extensions(operation,target)` včetně `.m12`, `.mz0`, `.mz7`; při změně operace/targetu zneplatnit předběžný seznam, znovu filtrovat a zapsat důvod nepodpory.
3. Extrakční volby přesně `Native`, `BIN`, `MZF`, `M12`, `MZT`, `WAV`, `FLAC`, `LEP`, `L16`. Pozor na **case-sensitive** Core (`"Mzf"`/`"Bin"` nejsou totéž co `"MZF"`/`"BIN"`). Nepřidávat převodník, který potichu mění data; správně mapovat UI hodnoty.
4. `Conversion target` vycházející z existující `BatchMediaPolicy.Targets`, včetně HFE/DSK, CP/M a tape/audio/QD; kontrola přípustnosti operace. `Sample rate` povolit pro WAV/FLAC konverzi a audio extrakci; referenční WPF nabízí 44100 a 22050 Hz. Pokud jsou další frekvence již podporované v Avalonia/Core, nepřijít o tuto schopnost bez odsouhlasení — přesné výsledné UI porovnat zvlášť.
5. Heuristic analysis: WPF umožňuje `Use heuristic analysis (WAV / FLAC)` u operací podporujících audio (podle politiky/aktuálního filtru). Doplnit chybějící enable stav v Avalonia, ale nepovolit režim, který Core pro daný vstup neumí bezpečně zpracovat.
6. Zkomprimování/dekomprimování: nastavení `CompressionOptionsControl` dostupné **přímo v hlavním Batch okně** pro `CompressPrograms`; správné mode, direction, quick, embedded, skip a validace, včetně limitu `Skip bytes = 0` pro batch. Nevyžadovat až dodatečný samostatný nečekaný dialog. Znovupoužít stávající sdílenou službu, nepsat nový kompresor.
7. `Replace originals`, `.bak`, output dir, naming template: přesně WPF aktivace pro read-only operace, tape/audio/QD konverze, Extract All a fyzicky/metadata ztrátové převody; nepřipustit neplatný přepis originálu.
8. Operation hint: WPF podrobně popisuje `TestIntegrity` a `ExtractAll` (včetně absence payload checksum u MZF/M12, požadavku na typ/LOAD/EXEC při exportu). Zobrazení musí reagovat na každou změnu operace.
9. Všechny nekompatibilní kombinace musí dostat viditelnou důvodovou zprávu; nikdy tichý skip. Důležitý je přenos stavů a zákaz tiché ztráty metadat.

## 5. P1 — Batch File Details přesně podle WPF

Přenést `BatchFileDetailsDialog.cs` jako samostatné Avalonia detailní okno (může zůstat společný view-model sdílený s BatchSession) otevřené tlačítkem **Selected details…** nebo dvojklikem na řádek. Referenční přibližná velikost **1120 × 700**, minimálně **720 × 420**, s adaptací pracovní plochy.

Zachovat:

- Hlavičku: název a plná cesta vstupu, štítky `Format`, `Status`, `Integrity`, `Files / programs`, důvod výsledku.
- **Contents:** dynamické sloupce `#`, `Name`, `Size (B)`, `Type (hex)`, `Load (hex)`, `Exec (hex)` a podle obsahu `User`, `Tape profile`, `Compression`, `Trailing (B)`, `Attributes`. Nesmí se ztratit `User` a `TrailingBytes`, které jednoduchá současná Avalonia tabulka nezobrazuje.
- **Properties:** operation, filesystem, output, capacity, usage, geometry, layout/DPB, boot/system, profile, CRC chyby, weak bits, RPM, analyzer findings, všechna další `DetailFields` a source SHA-256.
- **Messages (N):** oddělené kategorie `Result`, `Warning`, `Error` s víceřádkovými zprávami a počtem.
- **Integrity checks (N):** `File / scope`, `Check`, `Result`, `Details / limits`; nikdy nepřevádět „Not available/Not verifiable“ na „Passed“.
- **Technical details:** úplné `DetailedReport`, pohodlné scrollování a kopírování.
- Výchozí kartu podle WPF: integrita, jsou-li kontroly; jinak Properties/Messages podle obsahu; `Close` přes tlačítko/Esc. Tabulky: sort podle sloupců, vícenásobný výběr, clipboard Ctrl/Cmd+C včetně hlavičky tam, kde je WPF používá.
- Bez přibližných/falešných hodnot: jen údaje skutečně dostupné v Core `BatchRow` / `BatchCatalog`; neznámá data zobrazit jako nedostupná. Bez změny dekodéru kvůli UI.

## 6. Návrh architektury (ke schválení, nikoli povinné názvy)

- Preferovat specializované `BatchProcessWindow` + `BatchProcessViewModel` / `BatchDialogState` s živým formulářem a `BatchFileDetailsWindow` místo řetězce `AnalysisWindow(true)` → `DiskFormAsync` → `BatchWorkspace`.
- Pro všechny operace nadále volat **jediný** `BatchProcessService` a `BatchMediaPolicy`; zachovat Core, SHA/atomic output kontrolu, re-open validaci a existující formátové chování. Nezdvojovat implementaci preflightu v UI.
- `BatchSession` lze zachovat jako výsledkový view-model/document adaptér, pokud to nevyžaduje sekundární pracovní krok; odlišit **pracovní UI stav** před Preview od **neměnného snapshotu preflightu** po Preview. Při změně parametrů snapshot zneplatnit.
- Zachovat stávající veřejné příkazy, pokud je využívají jiné části aplikace/testy; kde je nutná změna, doložit migrační mapu a aktualizovat navazující testy, neodstraňovat práci s Batch bez náhrady.
- Neodstraňovat původní WPF ani jiné funkce. Vizuální styl mimo Batch měnit jen tehdy, když je to nezbytné a schválené.

## 7. Povinné testy — pouze PASS na skutečně provedených platformách

**A. Porovnání UI (Windows WPF vs. Windows Avalonia + Linux Avalonia):**

1. První otevření Batch: výchozí operation, velikost, pořadí prvků, zobrazení tabulky, footer, důraz na jednu obrazovku.
2. Pro každou operaci: default pole, enabled/visible, tooltip/hint, filtry, všechna tlačítka.
3. Vybrané soubory: Add 1/3, duplikáty, unsupported, Clear, přepnutí folder; složka s podadresáři a `.m12/.mz0/.mz7`; změna targetu během skenování.
4. `Preview` s validním, nevalidním a kombinovaným vstupem; invalidace při změně volby; Strict blokace; výběr a sort tabulky.
5. `Selected details`: každá z pěti karet, prázdné hodnoty, obsah CP/M, QD, tape a WAV/FLAC, víceřádkové warnings, kopírování, klikací sloupce a close.
6. Execute: běžný výstup, ztrátový převod (souhlas ano/ne), replace+backup (ano/ne), existující destination, změněný zdroj/sidecar, opakování, nevalidní přípona.
7. Long run: fáze Preview/Execute s realistickým WAV/FLAC, skutečné progress + Stop/Cancel, zavření okna během běhu, žádný zablokovaný UI thread.
8. Rozměry 1440×960, 1366×768, 920×640 a 640×480, různý DPI scaling a Windows/Linux native file pickers; nedostupné platformní/zařízení označit `NOT TESTED`.

**B. Funkční regrese / integrita:**

- `ExtractAll` s WPF formáty `Native/BIN/MZF/M12/MZT/WAV/FLAC/LEP/L16`, ověření skutečně vygenerovaných souborů; neplatné konfigurace skončí s důvodem, nikoli polovičním výstupem.
- `ConvertFormat`, `CompressPrograms`, `DecompressPrograms`, `TestIntegrity`, `AnalyzeMzfCompatibility`, `InstallBootSystem`, `Defragment`, `SafeRepair`, `Analyze` (pokud zachováno); porovnání stejných vstupů s WPF/Core.
- Vždy ověřit **bitovou shodu výstupních bajtů**, pokud ji příslušný formát/operace zaručují; jinak sémantickou ekvivalenci a důvody ztráty metadat. Zakázat tichý overwrite.
- `MZF/M12/MZT` hlavičky a payloady, programové metadata, sidecary, QD fyzické varianty, DSK/HFE a boot/CP/M struktury, `WAV/FLAC` checksum a formátové parametry.
- Přepsání / Save As / output: rollback, SHA zdroje, nezměněný původní soubor, funkční `.bak`, více vstupů s kolizními názvy a Cancel během transakce.
- Regression pro existující desktopové testy a Core; při přestavbě UI doplnit **nové headless + native UI testy**, nejen unit test enumu.

**C. Obrazové důkazy:**

- Pořídit **skutečné** WPF screenshoty z referenčního `BatchProcessDialog` a `BatchFileDetailsDialog` pro totožné fixture i stavy, vedle skutečných Avalonia Windows/Linux screenshotů. Označit platformu, DPI, vstup a krok; archivovat bez retuší a s hashem. Screenshot není PASS funkce bez klikacích/integračních testů.
- Předložit WPF/Avalonia **matici po jednotlivých ovládacích prvcích**: `OK`, `rozdíl s důkazem`, `neměřeno`, `záměrně odlišné + odsouhlasené`. Slovo „100% parity“ až po skutečně uzavřených povinných bodech.

## 8. Hotovo znamená (akceptační kritéria)

- `Tools → Batch Process` otevře **jediné souvislé WPF-paritní okno**, nikoli hned další generický formulář.
- Uživatel může **v témž okně** spravovat vstupy, parametry, vidět průběžný seznam, spustit Preview, prostudovat řádky/detail, potvrdit rizika, Execute, sledovat průběh, Stop, exportovat report a nastavit další dávku.
- Funkční a vizuální stav ovládání, pořadí a vysvětlující texty odpovídají původnímu `BatchProcessDialog.cs` v rozsahu odsouhlasené parity.
- `Selected details` je bohaté pětikartové WPF-ekvivalentní okno; data `Integrity checks`, `Messages`, `Properties`, `Trailing`, `User` se neztratí.
- Žádné automatické schválení ztrátového převodu, žádný tichý přepis; původní Core a WPF beze změn.
- Regresní výsledky a skutečné screenshoty doloženy pro Windows a Linux; macOS/ARM označeny podle reality.

## 9. Pořadí dodávek a schvalování

**G0 – NYNÍ, POUZE AUDIT:**

1. Zkontroluj vnější HEAD a místní větev, `git status` a případné rozpracované změny. Pokud HEAD není výše uvedený, **nejdřív znovu ověř** všechny nálezy; nepoužívej zastaralou paritní matici jako autoritu.
2. Spusť/read-only porovnej původní WPF a Avalonia (pokud je runtime dostupný), pořiď výchozí snímky podle 7.C; pokud i diagnostický běh generuje soubory, použij samostatný dočasný adresář a bez souhlasu nezasahuj do projektu.
3. Dodej matici současných rozdílů s konkrétními metodami/soubory a **návrh konečného vzhledu/událostí/architektury**, seznam upravovaných souborů, rizika, regresní plán. **Zastav a vyžádej schválení.**

**G1 – AŽ PO SCHVÁLENÍ:** Přestavba Batch hlavního okna do referenčního workflow a závislostí parametrů; nativní UI tests/screenshoty.

**G2 – AŽ PO DALŠÍM SCHVÁLENÍ:** Detailní okno, potvrzování, progress/Stop a dokončení konkrétních funkčních rozdílů; bezpečnostní testy, cross-platform porovnání.

**Závěrečný protokol:** Přesný HEAD/commit, stav WPF parity po řádcích, seznam upravených souborů, real Windows/Linux build+test, screenshoty, počty PASS/FAIL/SKIP, nedostupné platformy, známá omezení a regresní hodnoty. Pokud uživatel později požádá o ZIP, připrav pouze **celé změněné soubory se zachovanými relativními cestami**, bez patchů a bez WPF zdrojů; nic nepublikuj bez souhlasu.

**Zásada:** Nesmí vzniknout „nový hezčí Batch“, který je stále ovládáním odlišný od referenčního WPF. Cílem je **stejný pracovní postup a stejná dosažitelná funkčnost** s multiplatformními komponentami Avalonia; odlišnosti dané OS/DPI musí být výslovně zdokumentované.
