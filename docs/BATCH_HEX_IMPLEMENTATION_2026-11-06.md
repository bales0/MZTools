# Batch 1:1 a společné HEX ovládání

Výchozí HEAD: `bb39bc4175a75f3ec71eb6de3446f243434dd4fa`; pracovní strom navíc obsahuje předchozí schválené A1–A7 a opravu MZF/QDF kontextu. Tyto změny se zachovávají. Aktuální přímý požadavek uživatele je „oprav dle zadání, zároveň sjednoť hex viewery a editory …“; dokument se posuzuje jako specifikace, nikoli jako samostatný pokyn uživatele. Žádný commit, push ani úprava původní WPF produkce se neprovádí.

## Ověřený rozdíl a návrh

| Oblast | WPF | Aktuální Avalonia | Návrh |
|---|---|---|---|
| Batch vstup | BatchProcessDialog, jeden živý formulář | AnalysisWindow(true) → DiskFormAsync → BatchWorkspace | BatchProcessWindow: parametry nahoře ve scrollu, osm WPF sloupců uprostřed, summary/progress/akce/Stop dole |
| Operace | TestIntegrity první, Analyze vynecháno | Analyze první, enum identifikátory | TestIntegrity první; popisky BatchMediaPolicy.Label; Info / catalog jako poslední samostatná volba |
| Výběr | Opakované Add, folder, předběžný seznam | Picker po formuláři | Viditelný seznam, deduplikace, Clear; oddělené režimy, debounce a generace scanů |
| Parametry | Trvalé hodnoty, enabled stavy, inline compression | Většina neaktivních řádků skryta; druhý compression dialog | Specializované prvky podle WPF; compression uvnitř okna; Skip=0 |
| Snapshot | Invalidate při každé změně | Výsledkový dokument nezávislý na původním formuláři | Zneplatnit snapshot i Execute/report/details; žádné spuštění starých parametrů |
| Details | BatchFileDetailsDialog, pět karet | Tři vestavěné karty | Samostatné BatchFileDetailsWindow s Contents/Properties/Messages/Integrity/Technical, včetně User a Trailing |
| Bezpečnost | Core preview/execute, explicitní loss/replace, Stop | A1–A7 již opraveno | Zachovat Core a existující potvrzení; při zavření vyčkat na skutečné ukončení tasku |
| HEX viewer | Monospace bajty, offset, barevná pole | Jediný TableView, bez byte kurzoru/RAM | Jednotné mapování pozice, offset a RAM pouze u známého payloadu, souběžně zvýrazněný bajt ve třech reprezentacích |
| HEX editor | Pevná délka, tři sloupce | Tři synchronně scrollující TextBoxy bez adres a souběžné pozice | Zachovat overwrite/lock/Apply; adresní gutter a společná byte selection pro HEX/ASCII/MZASCII |

Měněné oblasti: nové BatchProcessWindow/BatchFileDetailsWindow, vstup z MainWindow a kompatibilita AnalysisWindow(true), BatchSession invalidace a sdílená cesta přípravy v ShellBatchCommands; HexViewWindow, sdílené mapování hex pozic, DiskSession/DiskWorkspace gutter a kurzor; QD regionové call sites a cílené GUI/native testy. Core formáty, dekodéry, kompresory ani transakce se nepřepisují.

Adresní pravidlo: offset je pozice v zobrazeném zdrojovém bufferu. RAM = `(LOAD + offset v payloadu) & 0xFFFF`; hlavička, fyzické QD framing/CRC, trailing data a sektor/CP/M blok nemají automaticky RAM adresu. Pozice kurzoru se promítá do téhož bajtu všech tří sloupců; neznámé mapování se nesmí odhadovat.

Regresní plán: skutečný vstup Tools → Batch bez modal overlay; všechny operace a extrakční volby, invalidace preview, režimy/scan, embedded compression, details pět karet, loss/replace/zdrojové SHA, Cancel a zavření během práce. HEX: klik/klávesnice ve všech reprezentacích, partial row, LOAD wrap, QDF barvy a absence QDF v MZF, adresy sektoru/CPM segmentů, společný scroll, overwrite a Cancel beze změny délky. Windows/Linux build, headless a nativní smoke; macOS/ARM a hardware pouze podle skutečné dostupnosti. Úplná pixelová parita se nepředpokládá bez měření WPF i Avalonie.

## Provedená implementace a měřená parita

`Tools → Batch Process...` nyní přímo otevře `BatchProcessWindow`. Původní `AnalysisWindow(true)` a jeho generické spouštění konfigurace byly odstraněny; `AnalysisWindow()` zůstává audio nástrojem. Starší veřejné Shell Batch příkazy a výsledkový adaptér zůstávají pro ostatní kontexty a původní regrese. Nové okno volá jediný Core Preview a pro Execute znovu používá existující Shell cestu se samostatnými souhlasy loss/replace a ochranami zdrojových SHA. Nový snapshot nahrazuje předchozí BatchSession, aby se starý výsledek nestal druhým otevřeným zdrojem blokujícím další dávku.

| Ovládací prvek / událost | WPF zdroj | Avalonia zdroj | Důkaz a stav |
|---|---|---|---|
| Jediné okno, default TestIntegrity, menu | BatchProcessDialog / MainWindow | BatchProcessWindow / MainWindow.Wpf | OK: skutečný vstup z Tools, žádné OwnedWindows při otevření; native-batch-empty.png |
| Operation, hint, pořadí | BatchProcessDialog.CurrentOperation / Invalidate | OperationChoice / Invalidate | OK pro devět referenčních operací; Analyze je dodatečné explicitní Info / catalog na konci |
| Selected files / folder, Add, Clear | Checked handlery a ShowFolderInputs | SwitchMode / AddInputs / ScanAsync | OK: deduplikace, viditelný unsupported, debounce 250 ms, generace scanu a ochrana snapshotu; vlastní filtr se zachovává jako již schválená vlastnost |
| Conversion target / Same as source | Invalidate | Invalidate | OK: enabled/visible měřeno ve všech kombinacích |
| Extract output format | Invalidate / dryRun | Invalidate / PreviewAsync | OK: devět voleb, všechny přes skutečné nové okno až k ověřeným výstupům |
| Audio rate / heuristic | sampleRate / heuristic | AudioSampleRate / UseHeuristicAnalysis | OK: 44100/22050, stavy měřeny ve všech kombinacích; skutečné 22050 výstupy ověřuje předchozí zachovaná Batch regrese |
| Inline compression | CompressionOptionsControl | BatchCompression | OK: None/Auto (smallest safe)/ZX0/ZX7, rádia Forward/Backward, quick, embedded, Skip=0; Core round-trip čtyř režimů a návrat přes Decompress. |
| Boot source / output folder | Row / Invalidate | Row / Invalidate | Enabled/visible OK ve všech kombinacích; skutečné systémové browse implementace. Add files, input folder, boot/profile a output folder byly na Windows i Linuxu skutečně otevřeny a zrušeny bez změny zdrojů. Kompletní tab order a screen reader NEMĚŘENO. |
| Naming, strict, replace, .bak | Invalidate / dryRun / execute | Invalidate / PreviewAsync / ShellBatchCommands | Funkční ochrany zachovány; při read-only a tape output se replace vypne; samostatné souhlasy a transakční Core regrese |
| Osm sloupců, selection / sort / copy | InputFileList | InputFileList / DskTableBehavior | OK: stejné referenční pořadí; shared sort a Ctrl/Cmd+C; native Preview zůstává ve stejném okně |
| Invalidace | Invalidate | Invalidate + BatchSession.Invalidate | OK: všechny změny zablokují Execute/report/details. Textová pole používají synchronní TextChanging, aby opožděné TextChanged nezneplatnilo právě spuštěný nový Preview. |
| Preview / Execute / Stop / close | Work / Closing | PreviewAsync / ExecuteAsync / CloseSafely | OK: nový preview po změně, jediný coordinator, reálný progress, Cancel a čekání na cleanup včetně scanů; test zastavení při verifikaci před publikací |
| Report TXT / CSV / JSON | report handler | ExportAsync | Stejný Core report a transakční export chránící input/sidecar; systémový Save picker byl skutečně otevřen a zrušen v novém okně na obou OS. Obsah všech tří formátů ověřují Core/regresní testy; jejich kompletní OS klávesnicový průchod NEMĚŘENO. |
| Contents | BatchFileDetailsDialog | BatchFileDetailsWindow | OK: šest základních a dynamické User/Profile/Compression/Trailing/Attributes; testuje se jejich zachování a kopírování |
| Properties / Messages / Integrity / Technical | BatchFileDetailsDialog | BatchFileDetailsWindow | OK: všech pět karet, stejné Core údaje, source SHA, víceřádkové zprávy a explicitní unavailable limity; stejný výběr výchozí karty |
| HEX / ASCII / MZASCII | HexBrowser / DskHexEditor | HexViewWindow / HexPositions / HexEditorCursor | OK: společný font, skupiny po čtyřech, 16 bajtů na řádek, stejné dekódování znaků; synchronní byte pozice z každého sloupce, klik a klávesnice |
| RAM a offset | Explicitní nový požadavek uživatele | Row.Load / PayloadOffset / Region | OK: jen payload, LOAD+offset mod 65536, MZF/MZT/M12 i QD payload. Hlavičky/trailing/CRC/raw recovery bez LOAD a diskové sektory RAM adresu nedostávají. U HFE projekce je uveden offset DSK bufferu, nikoli domnělý fyzický offset v HFE. |

**Přímé měření stavů:** WPF i obě Avalonia implementace byly skutečně spuštěny. Pro devět WPF operací × dva režimy vstupu × 23 cílů × devět extrakčních formátů vzniklo 3 726 konfigurací na platformu. Pro ConversionTarget, ExtractFormat, AudioSampleRate, UseHeuristicAnalysis, BatchCompression, BootSource a OutputFolder se porovnává enabled i visible; všechny tyto hodnoty souhlasí. Jde o měření konkrétních stavů, nikoli o důkaz pixelové shody všech obrazovek nebo všech funkcí.

Autentické reference a rendery jsou v [galerii WPF / Windows / Linux](ui-design-system/batch-hex-2026-11-06/index.html), s platformou, DPI, původem a SHA-256. Batch detail používá na všech platformách totožné MZF fixture bajty (512 B programu, LOAD/EXEC 4000); cesty do dočasných adresářů se přirozeně liší. WPF produkce se nemění, rozšířen je pouze referenční testovací harness `MZTool.Tests/WpfParityUiSmoke/Program.cs`.

## Validace a omezení

Výsledky aktuální úpravy: `TestResults/BatchHex-2026-11-06`. SDK Windows i WSL Debian `.NET 10.0.303`. Core produkce se oproti výchozímu pracovnímu stromu nezměnila; nově znovu proběhlo 86 cílených Core testů Batch/sidecar/report na každém OS, všechny prošly. Předchozí úplná Core sada 487/0/1 z `BatchParity-2026-11-06` je pouze baseline, nikoli nově vydávaný běh této UI úpravy.

Nativní scénáře pokrývají i rozměry 1440×960, 1366×768, 920×640 a 640×480, pět detailních karet, reálný Preview/Execute, invalidaci, bitově shodný MZF a zavření. Core/hash/format operace neběží při změnách ComboBoxu ani při pohybu hex kurzoru.

Nejde o deklaraci 100% pixelové parity. Zůstávají rozdíly nativního vzhledu ovládacích prvků a výše jmenované neměřené picker průchody. macOS/ARM, fyzický audio hardware, screen reader, všechny kombinace DPI/multimonitor a síťové úložiště jsou NETESTOVÁNO. Přeskočený Desktop ThirtyMinuteMediaOperations je explicitně volitelný benchmark; ignore seznam média z testů nevylučuje.

Lokální nepodepsané self-contained binární buildy Windows/Linux x64 jsou v `TestResults/BatchHex-2026-11-06/distribution`. Balík zdrojových změn, commit, push ani publikovaný release nevzniká. Archivy mají vnitřní SHA256SUMS, samostatný SHA-256 manifest a ověření spuštění skutečně rozbalených aplikací.

### Výsledky provedených kontrol, 6. 11. 2026

| Aktuální běh | Windows PASS / FAIL / SKIP | Linux PASS / FAIL / SKIP |
|---|---|---|
| Plná Desktop sada (`finished`) | 362 / 0 / 1 | 370 / 0 / 1 |
| Core Batch / sidecar / report | 86 / 0 / 0 | 86 / 0 / 0 |
| Dodatečné Backward + quick/embedded round-trip volby | 4 / 0 / 0 | 4 / 0 / 0 |
| Nativní shell/media/disk/audio/recorder/Batch kontroly | 127 / 0 / 0 | 127 / 0 / 0 |
| WPF stavové konfigurace (každá má 7 prvků × 2 booleany) | 3 726 shodných / 0 rozdílů | 3 726 shodných / 0 rozdílů |

WPF referenční harness skončil exit=0. Galerie obsahuje 62 původních PNG, kontrolní součty a úplné stavové matice. Windows native rendery vznikly při 150% scale; přesná měření každého okna jsou přiložena v platformních JSON. Výchozí skutečná fixture má SHA-256 `4FA75EB59C34B7961A41DF7305C0635627C07080E87413CE1027A2738D619C7F` na všech třech platformních zobrazeních.

Produkční zdroje od poslední plné Desktop sady zůstaly beze změny; dodatečný test rozšířil pouze kontrolu kompresních voleb Backward/quick/embedded. Zaznamenané pracovní neúspěchy (dvojí rodič hex footeru, odložený TextChanged, staré očekávání názvu SHASCII a dočasný Linux build adresář) nejsou vydávány za PASS; opravy byly znovu ověřeny. Zachovány jsou testovací protokoly všech pokusů.
