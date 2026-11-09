# Phase 5 — funkční Open/Add a Batch audit, G1 + G3 (2026-11-09)

Výchozí HEAD: `9b471ddcbf0508d80bba64303de363243ac86601`. Uživatel schválil G1 (doložené P0/P1) a G3 (cílené buildy/testy). G2, FULL/corpus, distribuce, commit a push nejsou součástí této etapy. Původní WPF a produkční Core se nemění; jejich obsah ověřuje `TestResults/Phase5-2026-11-09/original-source-guard.json`.

## Opravy a opravený závěr auditu

| Bod | Výchozí důkaz WPF → Avalonia | Výsledek | Důkaz / dopad |
|---|---|---|---|
| P0-1 Open WAV/FLAC/manifest | `MainWindow.xaml.cs:2060,2208` ImportAudioFile/AddFile → `Documents/TapeSession.cs` MediaDocumentLoader | OPRAVENO | Hlavní Open vytváří TapeSession MZF/MZT podle počtu programů. Samostatný Analyze Audio používá explicitní `audioOnly=true` a zachovává AudioSession. Malé WAV 1/2 programy, FLAC, manifest, Cancel a Save As mají regrese. |
| P0-2 audio Add | `MainWindow.xaml.cs:2441` + WavImportOptionsWindow → MediaOperations.ImportAsync | OPRAVENO | Jedny volby pro více audio vstupů; Standard/Heuristic, Summary/Detailed/None, FLAC i manifest skutečně dekóduje sdílená služba. Smíšený výběr se aplikuje až po úspěšném načtení všech vstupů a potvrzení preview. |
| P1-3 Open LEP/L16 | `MainWindow.xaml.cs:2324` → MediaDocumentLoader | OPRAVENO | Původní SharpTapeImporter, bez změny pulzů/dekódování; výsledkem MZF/MZT. Save nepřepisuje LEP/L16, Save As vyžaduje platnou tape příponu. |
| P1-4 IPL DSK | WPF AddFile podmínka BootOnly → DiskLoader a sdílený DskFileSystemDetector | PŮVODNÍ ZÁVĚR G0 UPŘESNĚN / ROZDÍL PRO PLATNÝ SINGLE IPL VYVRÁCEN | Platný obraz writeru současný Core rozpozná jako SingleIpl, takže i WPF ho otevře v diskovém editoru. Přesměrování do Tape by zde nově vytvořilo odchylku. DiskLoader a MainWindow.Wpf zůstávají obsahově původní; nová regrese ověřuje diskový program a Cancel poškozeného DSK. |
| P1-5 Batch folder | `BatchProcessDialog.cs:126,232` Scan.Where(Supports) → BatchProcessWindow.ScanAsync/PreviewAsync | OPRAVENO | Sdílený BatchInputSelection.Scan dělá průnik vlastního omezení s operací/cílem. Používá ho i starší ShellBatchCommands. Operace/cíl jsou pro asynchronní scan zachycené předem. Ruční nepodporovaný vstup zůstává viditelný. |
| P1-6 Linux globy | FilePickerPolicy.CaseVariants | ALGORITMUS OPRAVEN; LIMIT PROVIDERU VIZ NÍŽE | Nejvýše lower/upper vzor pro příponu, bez kartézského součinu; Apply patch 4, Audio 6, OpenMedia 60 vzorů v Linuxu. Validate stále porovnává přesné přípony bez ohledu na case; nepřibylo obecné All files/JSON pro audio. |

**IPL podrobně:** `DskDocument.cs:535–536` zkouší MultiGameIpl/SingleGameIpl před ostatními filesystémy. `SingleGameIplFileSystem.TryOpen` používá stejný `Mz800IplDskReader` a požaduje stejnou geometrii jako reader. WPF při SingleIpl do větve BootOnly nevstoupí. G0 označení tohoto případu jako potvrzené runtime chyby bylo příliš široké. Původní BootOnly větev nelze vydávat za důkaz, že se běžný platný IPL otevírá v Tape. Test nefalšuje žádná původní MZF metadata a zdrojový DSK nemění.

## Importní chování a ochrany

- WAV/FLAC mají Standard + Summary jako ve WPF; heuristiku je možné výslovně zapnout. Complete manifest používá heuristiku; volby to uvádějí.
- Orchestrace opakovaně používá stávající SharpTapeImporter, WavHeuristicAnalyzer a AudioImportPolicy. Při neúplném heuristickém výsledku pro WAV/FLAC může standardní decoder nahradit výsledek pouze pokud získal více platných programů, stejně jako WPF. Manifest do nepodporovaného standalone decoderu nevstupuje.
- Report uvádí počet programů, bajty, režim a dostupné heuristické statistiky/failures; Detailed obsahuje skutečné statistiky, nikoli vymyšlené checksumy. Původní důvody decoderu se zachovávají v chybě. Jde o funkční přístup k reportu, ne o G2 pixelovou kopii WPF statistického okna.
- Před/po dekódování se kontrolují zdrojové SHA. U manifestu se chrání report a všechny segmenty. U tape importu se chrání i načtené sidecary včetně jejich původní neexistence. Před Apply po potvrzení preview se zdroje ověřují znovu.
- Cancel a chyba jednoho vstupu nezmění cílové záznamy. Stávající kontrola revision a QD kapacity/read-only hostů zůstává; 35-record import do nového standardního QD nelze částečně aplikovat.
- DocumentCoordinator přijímá ověřený source fingerprint také z dekódované TapeSession, ne pouze z AudioSession. Přímé otevření ani externí cesta proto neobcházejí zdrojovou ochranu.

## Důkazní matice zbývajícího rozsahu G2

MATCH níže platí jen pro jmenovaný kontrakt, nikoli jako potvrzení celé obrazovky. Historická měření se nepřeznačují na nový PASS.

| Oblast / reference | Avalonia cesta | Stav | Dopad / priorita / guard nebo další důkaz |
|---|---|---|---|
| MainWindow Open/Add | MediaDocumentLoader, AudioProgramImport, ShellMediaCommands | MATCH pro testované programové workflow | P0 opraveno; nové Phase5WorkflowTests a skutečné audio volby na Windows/Linuxu. |
| MainWindow Save As/Export | TapeSession.PrepareSaveAsync, MediaOperations | MATCH pro testovaný Save As a source guard; ostatní UI NOT VERIFIED | P1; MZF/MZT round-trip, source a segment SHA, Cancel. |
| BatchProcessDialog / BatchFileDetailsDialog | BatchProcessWindow, BatchFileDetailsWindow | MATCH pro testovaný folder průnik; úplná UI parita NOT VERIFIED | P1; aktuální strict preview, zero-compatible, explicit unsupported; původní loss/replace/Cancel testy v FAST. |
| HexBrowser / rekordový editor | HexViewWindow, HexPositions, RecordWorkspace | MATCH pro zachované MZF header/data/trailing/QDF kontexty v cílených guardech; pixelová shoda NOT VERIFIED | P1; společný kurzor/RAM původní implementace, bez refactoru. |
| DskNewDialog | DiskFormatForm, ShellDiskCommands | MATCH pro testované profilové kapacity | P1; 40/80, 320/720/1440 guard. Všechny dialogové kombinace NOT VERIFIED. |
| CP/M layout/profiles/boot dialogy | ShellDiskDetails, DesktopDiskDialogs | NOT VERIFIED jako úplná UI kopie | P1/G2; parametrické formuláře se nyní nepřepisovaly. |
| DskEditor repair/normalize/compare/raw/patch | DiskLoader, ShellDiskCommands, DiskWorkspace | MATCH pro jmenované existující guardy; úplný workflow NOT VERIFIED | P1; zachován repair Cancel, HEX exact length a source ochrany; cílené Core raw/patch regrese. |
| NativeCom / IPL dialogy | ShellNativeComTools, MainWindow.Tools, ShellDiskDetails | MATCH pro doložené SingleIpl Open; ostatní UI NOT VERIFIED | P1; žádná nová syntetická hlavička ani změna COM builderu. |
| QD osm řádků / spirála / DSK mapy | RegionMap, QuickDiskVisualWindow, DiskVisualWindow | MATCH pro vybraný snapshot/selection guard; pixelová parita NOT VERIFIED | P1/G2; původní renderery zachované. |
| AudioSignalAnalyzer / recovery / reference | AnalysisWindow, AudioWorkspace, AudioSession.* | MATCH pro explicitní analytic Open a vybraný OUTDATED guard; úplná UI parita NOT VERIFIED | P1/G2; výpočty a PCM se nepřepisují. |
| WPF Capture/Live recorder | RecorderModel, RecorderWindow | MATCH pro vybrané syntetické bitové/disconnect guardy; hardware UNTESTED | P1; žádné fyzické audio zařízení se neotevíralo. |
| Fokus, F2, Esc/Enter, DPI, scrolling | Desktop*Dialogs, DiskWorkspace, DesktopWindowSizing | NOT VERIFIED jako celek | P1/P2/G2; aktuální audio options Cancel + historický HEX/name guard nejsou důkaz všech dialogů a DPI. |

## Linux picker: přesný rozsah důkazu

Původních 2048 + 16 patch globů je odstraněno. Nové lower/upper vzory zachovávají běžné lowercase a uppercase. `.MZPATCH.JSON` a ručně zadané `.WaV.JsOn` projdou přísnou suffix validací. **Libovolné mixed-case jméno nemusí být ve case-sensitive GTK seznamu viditelné; je možné zadat cestu ručně.** To je transparentní omezení provideru, ne tvrzení úplné mixed-case vizuální podpory. Validate není oslabené a audio filtr se nerozšiřuje na obecné JSON.

Aktuální WSLg poskytuje `FallbackStorageProvider`; GTK/KDE provider není dostupný, jeho výkon ani zobrazení mixed-case nelze označit PASS. Gtk-specific řešení se nezavádí odhadem. Case-insensitive suffix API GTK4 samo nedokazuje podporu v používaném Avalonia backendu.

| Měření aktuálního provideru | Windows Win32 | Linux fallback |
|---|---:|---:|
| Apply patch / OpenAudio / OpenMedia — součet vzorů v dialogu | 2 / 3 / 30 | 4 / 6 / 60 |
| První zpozorování skutečného pickeru, všechny omezené průchody | cca 201–281 ms | cca 208–276 ms |
| Skutečný picker workflow | Cancel; MZF/DSK/WAV výběr; import | Open/Import/Raw/Compare/Patch/Audio Cancel |
| Skutečná nová audio okna | Standard/Summary, Cancel, Open a Add preview, report | stejné funkční kontroly nad řízenou cestou |

Čas je měřen do detekce okna timerem s intervalem 200 ms, zahrnuje jeho granularitu; není to přesný benchmark prvního vykresleného pixelu ani GTK měření. Samostatně je v JSON zaznamenána doba vytvoření options a celkový picker cyklus. Zdroj a skutečně vrácený soubor na Windows jsou zaznamenány, neodvozují se jen z options.

## Provedené G3 kontroly

SDK `10.0.303`, Windows 11 x64 a Debian 13 / WSL2 x64. Výchozí HEAD je ve výsledném summary. Buildy mají `-p:MZToolsPublishLatest=false`; self-contained publish ani distribuce nevznikly. FAST nekopíruje korpus (0 souborů / 0 B). Žádné procházení uživatelských FLAC/DSK adresářů.

| Kontrola | Windows PASS / FAIL / SKIP | Linux PASS / FAIL / SKIP |
|---|---|---|
| Desktop FAST, aktuální testhost dokončen | 140 / 0 / 0; testy 4,569 s | 140 / 0 / 0; testy 4,497 s |
| Core cílený raw/patch | v této etapě nespouštěno | 17 / 0 / 0; testy 942 ms |
| Historické úzké guardy (geometrie, QD, HEX/name, OUTDATED, recorder bit identity/disconnect) | 15 / 0 / 0; 2,147 s | 15 / 0 / 0; 2,596 s |
| Skutečné omezené nativní UI/picker průchody | exit 0 | exit 0, fallback provider |
| macOS/ARM, fyzický hardware, GTK/KDE, kompletní DPI/screen reader | UNTESTED | UNTESTED |

Nové testy používají 144bajtovou deterministickou MZF fixture (16 B programu, LOAD/EXEC 4000): SHA-256 `C573DA754A538237FC6F8B73491537FF8ECE71D900542688921668639CB8BCA1`. WAV/FLAC/LEP/L16/manifest vznikají výhradně z těchto malých dat. Jejich délky a SHA včetně manifestových segmentů jsou v `TestResults/PickerPolicy/<host>/fast/fixtures`; přesné příkazy, SDK/OS/HEAD, trvání a změněné soubory v summary/timings. Ve sdíleném checkoutu může syrový Linux Git status uvádět rozdíly konců řádků/módů; obsahovou nezměněnost původního WPF/Core doloží samostatný source guard. Budoucí runner status pro tento účel normalizuje bez změny Git konfigurace.

Příkazy z kořene:

```text
python tools/test_picker_policy.py
# Linux:
python3 tools/test_picker_policy.py --core
# Omezené nativní kontroly; na Linuxu změnit host v cestě:
dotnet build tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj -m:1 -nr:false -p:MZToolsPublishLatest=false
dotnet obj/artifacts/windows/bin/MZTools.Desktop.Smoke/debug/MZTools.Desktop.Smoke.dll --pickers TestResults/Phase5-2026-11-09/native-windows
```

Úzké guardy jsou přesným xUnit `-method` výběrem sedmi metod, 15 test cases; jejich názvy jsou v TRX `windows-guards.trx` / `linux-guards.trx`. FULL zůstává samostatně dostupný přes `tools/test_picker_policy.py --mode full --core`, v této etapě nespouštěn. Windows Core ABORTED z předchozí etapy se tímto nevydává za PASS.

Výstupy: `TestResults/Phase5-2026-11-09` (diagnostické pokusy, nativní finální JSON, guards a obsahový source guard), `TestResults/PickerPolicy/<host>/fast` (finální FAST/fixture evidence). První neúspěšný pokus ukázal nepřesné očekávání chybové hlášky a mylné IPL očekávání; opravené testy dokončeně prošly. Žádná fixture absence není novým testem maskovaná `return` nebo falešným PASS.

## Upravené soubory a předání

Produkce:

- nové `Documents/AudioProgramImport.cs`, `Documents/BatchInputSelection.cs`, `Platform/DesktopAudioImportDialogs.cs`;
- `Documents/DocumentCoordinator.cs`, `Documents/MediaOperations.cs`, `Documents/TapeSession.cs`;
- `Platform/FilePickerPolicy.cs`, `ViewModels/ShellMediaCommands.cs`, `ViewModels/ShellBatchCommands.cs`, `Views/BatchProcessWindow.cs`.

Testy/nástroje:

- nový `tests/MZTools.Desktop.Tests/Phase5WorkflowTests.cs`;
- `AudioDocumentTests.cs`, `FilePickerPolicyTests.cs`, smoke `AudioScenario.cs`, `PickerScenario.cs`;
- `tools/test_picker_policy.py` (nová třída + evidence běhu).

`DiskLoader.cs` a `MainWindow.Wpf.cs` nemají obsahovou změnu; nedoložený IPL zásah byl odstraněn. Nejde o plnou 1:1 funkční/pixelovou certifikaci všech ovládacích ploch. G2 zůstává otevřené podle matice. Binární verze v `bin/<RID>` nebyly dle této schvalovací brány přegenerované; aktuálně ověřené zdroje jsou sestavené v `obj/test-runs/<host>/fast`.
