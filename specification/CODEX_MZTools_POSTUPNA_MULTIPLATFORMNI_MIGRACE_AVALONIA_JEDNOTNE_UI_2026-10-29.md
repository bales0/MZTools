# MZTools – postupná multiplatformní migrace na Avalonia se sjednoceným UI

**Zadání pro Codex – pouze návrh a plán do výslovného schválení**  
**Datum zadání:** 29. 10. 2026  
**Repozitář:** https://github.com/bales0/MZTools  
**Výchozí veřejně ověřený stav:** `main`, commit `ff2ddbba00633a9113fa8957784e4ef3133518d3` (2026-10-28), aplikace 0.9.9.  
**Pozor:** Před prací ověř skutečný `HEAD`, pracovní strom, případné nepushnuté změny, aktivní větve a rozpracované úkoly. Výchozí commit není pokyn k resetu ani checkoutu. Nenahrazuj lokální práci starším stavem.

## 0. Nepřekročitelná podmínka: schvalování všech změn

1. **Nejprve proveď pouze read-only audit a předlož plán.** Před první změnou zdrojového kódu, projektů, adresářů, testů, dokumentace, verzí závislostí, CI, nastavení IDE či git stavu **musíš vyžádat výslovný souhlas uživatele**.
2. Souhlas s jednou fází **neznamená souhlas s další fází**. Po každé dokončené fázi předlož výsledek, změněné soubory, rizika, testy a návrh dalšího kroku; pak se zastav a znovu požádej o souhlas.
3. I uvnitř schválené fáze si vyžádej nový souhlas, pokud vyvstane zásadní změna architektury, rozsahu, datového formátu, závislosti/licence, odstranění či přepsání souborů, nové nativní knihovny nebo nevratný zásah.
4. **Bez schválení neprováděj** editaci, generování nových souborů v repozitáři, instalace, formátování, automatické opravy, `git reset`, `clean`, `rebase`, force push, commit, merge ani publikaci. Pokud build/test vytváří artefakty v pracovním stromu, předem to oznam a vyžádej schválení; read-only prohlídka je povolena.
5. Nedělej skryté změny na pozadí. Neprováděj hromadné přejmenování či automatickou konverzi bez schválené mapy. Nepovažuj toto zadání za autorizaci k implementaci.
6. Nejsou požadovány změny existujícího externího zařízení, firmwaru ani jiných repozitářů.

## 1. Hlavní cíl a zásadní UX požadavek

Postupně převést **celou desktopovou aplikaci MZTools** z Windows/WPF (`net10.0-windows`, `UseWPF=true`) na **jednu skutečně multiplatformní desktopovou aplikaci** s Avalonia + aktuální kompatibilní verzí .NET; cílové OS **Windows, Linux a macOS**. Zachovat plnou funkčnost včetně diskových editorů, QuickDisk, kazetových formátů, grafických analyzérů a audio rekordéru.

> **Nejdůležitější požadavek:** Finální MZTools musí mít **jedno jednotné uživatelské rozhraní**, společný vizuální jazyk, stejnou navigaci, rozložení, význam akcí, odezvu, pracovní postupy a chování ovládacích prvků ve všech modulech a na všech třech OS. Nechceme sadu funkčních, ale vizuálně a behaviorálně různorodých oken.

- Nejde o pouhé nahrazení WPF typů jejich Avalonia ekvivalenty. Součástí je **sjednocení UI/UX** při zachování existujících uživatelských schopností.
- Během migrace může stará WPF aplikace sloužit jako stabilní paralelní Windows frontend a regresní reference. **Finální uživatelské rozhraní ale musí být jedna Avalonia aplikace** na všech OS. Nenabízej na Windows mix WPF oken a Avalonia oken jako konečné řešení.
- Rozdíly OS schovej za platformní adaptéry; měnit se mohou například systémová oprávnění, nativní file picker, systémové klávesové konvence nebo backend audio zařízení, **ne však tok práce a význam funkcí**.
- Migraci rozděl na malé, reverzibilní, explicitně schvalované fáze. Neprováděj přepis algoritmů současně s portováním UI.

## 2. Výchozí technický stav – ověřit znovu na HEAD

Při přípravě zadání bylo ověřeno:

- `MZTools.csproj` je `net10.0-windows`, WPF, verze 0.9.9, odkazuje na `MZTools.Capture` a `MZTools.Capture.Windows` a používá `NAudio.SoundFile` a Windows libsndfile runtime.
- `MZTools.Capture/MZTools.Capture.csproj` už je **přenositelné `net10.0`** bez WPF; obsahuje abstrakce zdrojů zvuku, `CaptureSession`, omezené fronty, archivaci PCM/WAV, živý analyzér, FFT, pulse monitor a head alignment.
- `MZTools.Capture.Windows` používá WASAPI/NAudio a Windows-specific native capture; na Linux/macOS je nutný samostatný backend, nikoli závislost portable core na Windows.
- Stávající dokument `docs/LIVE_TAPE_RECORDER_AVALONIA_ADAPTER.md` definuje hranice budoucího Avalonia adaptéru; **povinně jej přečti a zachovej jeho funkční, integritní a výkonnostní požadavky**.
- Hlavní UI obsahuje rozsáhlý `MainWindow.xaml.cs`, `DskEditorWindow.xaml.cs`, audio/DSK/QD vizualizace a řadu dalších WPF dialogů. Výpočetní modely v `Dsk/`, `Media/`, `Compression/` a kazetové části jsou z velké části běžný C#, ale často jsou stále součástí hlavního WPF projektu.
- WPF vazby se vyskytují i v souborech, které mají směs UI a aplikační logiky (např. `Media/WorkspaceDragTransfer.cs`, `DiskFormatting.cs`). Dělit odpovědnosti, nikoliv slepě přesouvat všechny soubory ze složek.
- Automatické testy existují v `MZTool.Tests`, `MZTools.Capture.Tests`, `MZTools.Capture.Windows.Tests` a UI smoke projektech. Ověřit jejich aktuální seznam, možnost spouštění a referenční soubory.
- Pro FLAC je použit `NAudio.SoundFile`/`libsndfile`; přenositelnost distribuce a právní povinnosti třetích stran je třeba ověřit pro jednotlivé RID/OS.

**Audit musí uvést** aktuální commit, počet a kategorie projektů, WPF závislostí, skutečné smíšené oblasti, kandidáty k přesunu, testovací coverage, veřejná API a přehled závislostí. Neslibuj přenositelnost pouze na základě kompilace projektu `net10.0`.

## 3. Cílová architektura – návrh k posouzení, nikoli pokyn k hromadnému přesunu

Předlož varianty a doporuč jednu přehlednou strukturu. Orientační koncept:

```text
MZTools/
├── src/
│   ├── MZTools.Core/                  # formáty, dekodéry, modely, služby, bez UI
│   │   ├── Tape/                      # MZF, M12, MZT, Intercopy, TurboCopy
│   │   ├── QuickDisk/                 # QD, QDF, MZQ, QD physical
│   │   ├── Floppy/                    # DSK, HFE, CP/M, FSMZ, MRS, IPL
│   │   ├── Audio/                     # offline WAV/FLAC, pulse engine, reference
│   │   ├── Conversion/                # COM, ZX0, ZX7, exports/imports
│   │   └── Common/
│   ├── MZTools.Capture/               # stávající portable recording core
│   ├── MZTools.Capture.Windows/       # stávající WASAPI
│   ├── MZTools.Capture.Linux/         # až po schválení backendu
│   ├── MZTools.Capture.Mac/           # až po schválení backendu
│   ├── MZTools.Desktop/               # JEDINÉ finální Avalonia UI
│   │   ├── Theme/                     # design tokens + styly + ikony
│   │   ├── Controls/                  # opakovaně použitelné UI komponenty
│   │   ├── Views/                     # obrazovky, dialogy
│   │   ├── ViewModels/                # stav/akce, bez přímé vazby na OS
│   │   ├── Renderers/                 # canvas pro audio, DSK, QD, HFE
│   │   └── Platform/                  # soubory, clipboard, shell, backend factory
│   └── MZTools.Wpf/                   # dočasný, beze změn chování
├── tests/
│   ├── MZTools.Core.Tests/
│   ├── MZTools.Capture.Tests/
│   ├── MZTools.Desktop.Tests/
│   └── ...
├── docs/ui-design-system/
└── specification/
```

- Zachovej existující portable `MZTools.Capture`. **Nepřesouvej jej do nového Core a nevytvářej paralelní recording pipeline** bez věcného důvodu.
- `Core` nesmí referencovat `System.Windows`, `Microsoft.Win32` UI, Avalonia, WinForms, dispatcher, WPF bitmapy, systémové dialogy ani OS-specifické capture API.
- `Desktop` smí odkazovat na Core/Capture a příslušné backendy prostřednictvím rozhraní a dependency injection/factory; nesmí spouštět Windows WPF EXE jako své jádro.
- Odděl modely, služby, algoritmy, view-modely, renderer a platform-specific implementace. **Konsoliduj duplicitní logiku**, ale neslučuj nesouvisející třídy pouze za účelem snížení počtu souborů.
- V jádře zůstane **jedna autoritativní sada referencí pulzů** a jedna implementace pro každý formát/algoritmus; nesmí vzniknout paralelní „Avalonia decoder“.
- Zvaž projektové názvy a namespaces s ohledem na stávající build/resource konfiguraci. Zejména prověř implicitní `Compile Include`/`Remove` v kořenovém `.csproj`, které by mohly při přidání nových složek způsobit duplicitní kompilaci.
- Kde je modul již připravený pro přenos (např. `DskImage`, `HfeImage`, `AudioPulseMeasurementEngine`), používej stávající implementaci a regresní testy.

## 4. Jednotný design system – povinně navrhnout a schválit před realizací Avalonia UI

**Tato fáze je samostatná schvalovací brána.** Bez ní neimplementuj produkční Avalonia obrazovky. Vytvoř čitelný návrh `MZTools UI Design System` a ukázkový layout; předlož uživateli ke schválení.

### 4.1 Vizuální konzistence

- Jedna společná **paleta a design tokens**: barvy a jejich význam, světlé/tmavé varianty (pokud budou schváleny), kontrast, typografická stupnice, rozestupy, výšky ovladačů, rádiusy, okraje, oddělovače, stavy focus/hover/disabled/error/warning/modified/recording a jednotné ikony.
- Jedna sada opakovaně použitelných **komponent**: toolbar/command bar, vstupní formulář, split-pane, tab navigation, DataGrid, tree/list, status bar, progress, filtr, výběr souboru, detailní panel, modal confirmation, contextual menu, error/report viewer, properties editor, hex editor, graph toolbar.
- Stejná velikost a význam příkazů `Open`, `Save`, `Save As`, `Export`, `Import`, `Apply`, `Cancel`, `Reset`, `Close` a stavů `Modified` / `Read-only` / `Incomplete` v celé aplikaci. **Nesmí se měnit význam Save podle modulu.**
- Jednotné zobrazování čísel, adres hex (`0x`), bajtů, kB/KiB, vzorkovacích frekvencí, časů, úrovní dBFS, legend a tooltipů; lokalizační možnosti musí být oddělené od modelu.
- Případné odlišnosti formulářových nativních OS dialogů musí být dokumentovány; preferuj konzistentní workflow, zabezpečené přístupy a přístupnost před násilným kopírováním vzhledu OS dialogů.
- Používej dostupné platformní fonty nebo distribuovatelné fonty s vhodnou licencí; žádné pevné Windows-only fonty jako jedinou volbu. Testovat diakritiku, polštinu, ukrajinštinu a různé DPI.
- **Žádné ad-hoc styly v každém okně, žádné barevné kódy roztroušené po souborech, žádné vlastnoručně duplikované controly.**

### 4.2 Jednotné chování

- **Jeden hlavní aplikační shell / workspace**, ze kterého lze přepínat Tape, QuickDisk, DSK/HFE, Audio Analysis a Recorder podle aktuálního dokumentu; menu a navigace musí mít stejnou logiku. Podrobné panely a samostatná okna jsou přípustné jen s předem definovaným jednotným vzorem.
- Sjednoť zobrazení dokumentu, názvu souboru, hvězdičky změn, undo/redo (pouze tam, kde již bezpečně existuje nebo bude zvlášť schváleno), informací o chybách, progressu, kontextových akcí a zkratkách.
- **Navigace a ovládání grafů:** wheel zoom a zoom anchor, drag pan, fit/all, výběr, synchronizace zvýraznění mezi tabulkami a grafikou, kontextové informace, přesné souřadnice a jednotné chování při změně záložek. Rozdíly jen tam, kde jsou funkčně odůvodněné a vysvětlené.
- Výběr řádků/souborů/sektorů, Shift/Ctrl (resp. systémová primární modifikační klávesa), kontextová nabídka, klávesnice, drag-and-drop, dvojklik, scroll a zavírání dialogů musí fungovat konzistentně.
- Jednotná politika hlášení: běžný informační stav neblokuje modalitou; nebezpečné operace vyžadují náhled a souhlas; chyby uvádějí konkrétní problém, dopad a možnost nápravy. Rozlišovat `healthy`, `repairable`, `ambiguous`, `read-only`, `incomplete`.
- Každé okno musí respektovat škálování 100/125/150/200 %, malé displeje a různé platformní velikosti fontů; nevytvářet přetečení ani skryté ovládací prvky. Základní referenční velikosti: např. 1366×768 a 1920×1080, pro recorder minimálně ekvivalent 920×640 podle existující specifikace.
- Přístupnost: čitelné popisky, tooltips, focus pořadí, stav klávesnice, dostatečný kontrast a textová alternativa ke grafickým metrikám; barva nesmí být jediným nositelem informace.
- **Nekopíruj současné nekonzistentní WPF detaily jako normu**: nejprve vytvoř inventuru současného UX, navrhni konsolidaci a schval se seznamem vědomých změn.

### 4.3 Povinné výstupy design fáze

- `docs/ui-design-system/README.md`: závazná specifikace tokenů, komponent a chování.
- Mapa obrazovek a workflow: odkud/kam se uživatel dostane, společná pravidla navigace, error handling a ochrana dokumentu.
- **3–5 schvalovacích vizuálních návrhů/screenshotů prototypu** minimálně: hlavní Tape/QuickDisk workspace, DSK Disk Map, Audio Visual Analyzer a Recorder/Head Alignment. Vzhled musí být jednotný; reálnou implementaci nejprve odsouhlasí uživatel.
- Inventura WPF/nového UI – jaká stará ovládací funkce se kam mapuje, včetně shortcuts a tooltipů.
- Jednotný seznam UI komponent a doporučená struktura View/ViewModel/Renderer, bez implementace nesouvisejících prvků.
- Objektivní checklist vizuálního a funkčního ověření, včetně screenshotů pro Windows/Linux/macOS a tolerancí rozdílů v rasterizaci písma.

## 5. Zachovat veškerou stávající funkcionalitu a binární kompatibilitu

Před migrací sestav **feature inventory / parity matrix**. Následující oblasti jsou minimálně povinné:

1. Tape: MZF/M12/MZT, metadata/header, profily ROM/IC/TC, původní přesné HIGH/LOW timingy Intercopy/TurboCopy, loader, streaming export WAV/LEP/L16, dekódování, rozdělení a pořadí nahrávek.
2. Komprese a převody: ZX0/ZX7 forward/backward, embedded header loader, Auto, MZF/M12 → COM, význam `Keep original compression` vs dekomprese, integrity a nezměněné ROM/loader bajty.
3. QuickDisk: logické a fyzické QD/QDF/MZQ, Sharp/Roland/Akai/Famicom tam, kde je projekt podporuje, hex/physical analyzéry a mapy.
4. Floppy: Extended CPC DSK, HFEv1/v2/v3, CHRN/FDC status, sector/track metadata, CP/M varianty, FSMZ/MRS/IPL, HD/DD, disk map, sector/hex edit, file operations, format/compare/repair/defragment, interleave, boot/system install.
5. **DSK Repair workflow**: při otevření neplatného `.dsk` po bezpečné analýze nabídnout opravu dle schválené změny; zdravý disk otevřít bez dialogu, zachovat originál, opravenou kopii ukládat přes `Save As`; ruční volba v editoru, oddělená filesystem repair. Pokud jde o dosud nerozpracovaný požadavek, před implementací ověř jeho samostatné schválení a aktuální stav.
6. Audio: WAV/FLAC, standardní a heuristické dekódování, ruční a vizuální analýza, histogramy, pulzní reference, výběr a export regionů, panel statistik, zoom a příslušné testovací soubory.
7. Recorder: Shared/Exclusive na Windows; monitor, Record/Stop/Finalize, WAV+JSON/segmentace, detekce dropoutů, snapshoty, FFT, L/R, Head Alignment A/B, pulse analyzér, bez blokování capture při práci UI; přenosná capture pipeline nedotčená.
8. Import/export, batch processing, drag-and-drop, reporty, akce Save/Save As, clipboard, souborové kolize a ochrana originálu.

**Regresní požadavek:** U stávajících deterministických operací nesmí samotná migrace změnit obsah generovaných MZF/M12/MZT/QD/QDF/MZQ/DSK/HFE/COM/WAV/LEP/L16 ani výsledky analýzy. U formátů s časovým kvantováním výslovně zdokumentuj pravidla shody a případné legitimní platformní rozdíly; **nepřepisuj algoritmus pro dosažení vizuální shody**. Využij existující golden fixtures a rozšiř testy jen se schválením.

## 6. Fázový plán – žádná etapa bez nového souhlasu

### F0 – pouze read-only audit a návrh migrace (POVINNÝ PRVNÍ KROK)

- Ověř HEAD/working tree, aktuální stav předešlých úkolů, projektové závislosti, licence, testy, funkční seznam a WPF vazby.
- Rozděl soubory na (A) čistý portable core, (B) smíšená logika/UI, (C) čisté WPF UI, (D) platform-specific backend, (E) testy/assets.
- Urči hlavní rizika přesunu (resource names, embedded disk images, stream APIs, WPF static references, Native library load, drag-and-drop).
- Navrhni konkrétní pořadí souborů k přesunu a mapu projektů. Odhadni dopad na build/testy a zvaž varianty architektury.
- Předlož zprávu a **zastav se pro souhlas**. Žádné změny.

### F1 – rozdělení `MZTools.Core` a regresních testů bez změny UI

- Po souhlasu vyčleň pouze skutečně platformně nezávislé modely, parsing, encoding, codecs, conversions, filesystems, referenční tabulky a analytické algoritmy do `net10.0`.
- WPF musí dál fungovat nad Core; žádná změna uživatelského chování ani bytů exportovaných formátů.
- Vyčleň `WorkspaceDragPacket`/serializaci od WPF `IDataObject`; file dialogs, message boxes, report WPF a shell dej do adaptéru.
- Převáděj existující testy na portable jen tam, kde to nezmění jejich význam; UI smoke testy ponech pro Windows WPF, dokud budou potřeba.
- Brána: core a testy se sestaví minimálně na Windows a Linuxu (na dostupném prostředí; ostatní označ jako neotestované); WPF smoke a binární golden testy beze změny.
- **Zastavit a požádat o schválení F2.**

### F2 – schválený design system a interakční pravidla

- Dokumentovat a schválit tokeny, komponenty, layout, navigaci, gesta, shortcuts, dialogy, feedback a čtyři reprezentativní UI návrhy.
- Vyhnout se implementaci celé aplikace před odsouhlasením vizuálního směru.
- Brána: uživatel výslovně schválí referenční vzhled + interakční specifikaci.

### F3 – Avalonia shell a sdílené UI komponenty

- Založit samostatný Avalonia desktop projekt (nepřipojovat WPF jako závislost) a schválené theme resources.
- Implementovat společný shell, command bar, navigaci, stav dokumentu, Open/Save/Save As/Close, souborové dialogy, tabulky, status/validation, progress, report viewer, keyboard shortcuts.
- Zajistit jeden způsob práce se soubory: potvrzení neuložených změn, bezpečný Save As, kontrolu kolizí, load/close bez poškození.
- Brána: stejný hlavní shell a komponenty na Windows/Linux/macOS se smoke testy, schválené screenshoty.

### F4 – Tape/MZF/M12/MZT + základní QuickDisk

- Připojit společné Core k editoru záznamů a kazet, kompresi a konverzím, import/export, rychlosti a metadata.
- Implementovat QuickDisk logické prohlížení/editor a mapu bez duplikace parseru.
- Brána: typické export/import workflow i golden binární testy; stejné příkazy a layout jako ostatní moduly.

### F5 – DSK/HFE, filesystems, disk map, hex a opravné workflow

- Portovat CP/M, FSMZ, MRS, IPL, DSK/HFE editor, diskové mapy, výběr sektorů, raw/hex editor, compare, repair, format, boot/system.
- Vykreslování má používat existující modely; zachovat selection sync, zoom/pan, CHRN/ST1/ST2, logické/fyzické pořadí, read-only ochranu, bezpečný Save As a zachování originálu.
- Brána: fixture comparison, konzistentní UX a testy operací nad fyzickými médii/obrazy.

### F6 – Offline Audio Visual/Manual Analyzer + grafické komponenty

- Portovat waveform, spektrogram/spektrum, pulse histogram/scatter, úrovně, selection overlays, trend/drift a vazby tabulka↔graf.
- Nespojovat vykreslovací vrstvu s dekódováním či čtením PCM. Společné view-transformace a renderer; konsolidace s rekordérem podle design systemu.
- Brána: deterministické osy v sample/frame jednotkách, shodné intervaly, zoom/pan/trigger a rozumný výkon na dlouhých nahrávkách.

### F7 – Recorder UI a Head Alignment na Avalonia

- **Zachovat** `MZTools.Capture` a jeho stávající bounded capture/analysis/archive pipeline. Avalonia View připojit přes `AudioRecorderViewModel` a již dokumentované DTO/snapshoty.
- Neprovádět FFT, kreslení, diskový zápis ani logování v capture callbacku. UI může při zátěži zahodit preview; archivaci nesmí ovlivnit. Oddělit archivní a preview loss, indikovat incomplete.
- Zachovat explicitní volbu zařízení, start pouze na akci uživatele, Shared/Exclusive u Windows, nativní/převáděný archiv, bezpečné ukončení i při zavírání UI a životnost bufferů.
- Portovat `LiveSignalCanvas`, `LivePulsePanel`, `HeadAlignmentPanel` jako komponenty ve **stejném design systému** jako offline analyzér.
- Brána: synthetic recording bez zařízení, UI zátěž, bitová identita, no accidental device start, cancel/stop/disconnect.

### F8 – Linux/macOS backend, FLAC, OS integrace

- Pro Linux/macOS **nejprve předložit varianty backendu** a jejich licence, dostupnost, schopnost zachování PCM/sample format, latency, callback threading a balíčkování. Teprve po schválení implementovat.
- Vyřešit file dialogs/permissions, copy/paste, drag-and-drop, shell reveal/open, časové zóny, line endings, case-sensitive filenames, UNC/paths, temp soubory a aplikací vlastněná nastavení.
- Ověřit native `libsndfile` runtime/architektury/licence a identickou interpretaci FLAC PCM pro podporované formáty; nepřebírat Windows-only runtime do Linux/macOS buildu.
- Brána: unit + headless + skutečné zařízení; úspěch bez HW testu explicitně označ jako „technicky připravené, HW neověřené“.

### F9 – CI, multiplatformní validace, distribuce a odstranění WPF

- Zřídit či aktualizovat CI matrix Windows/Linux/macOS až po schválení; portable Core tests, feature parity, golden outputs, UI smoke a screenshot regression (s tolerancemi na rozdílné font renderery/OS).
- Dodávat jedno společné UI a verze pro x64/arm64 podle schváleného rozsahu, balíčkování a testů. Ověřit nativní knihovny a licence.
- Starý WPF frontend odstranit **až po písemném schválení** a prokázané funkční/UI paritě; nesmí dojít ke ztrátě testerem používaného stabilního Windows buildu před náhradou.
- Brána: všechny kritické workflows na každém cílovém OS, podepsané testovací výsledky (PASS/FAIL/UNTESTED), release checklist.

## 7. Testovací matice a minimální akceptační kritéria

Testy musí být měřitelné, ne pouze „okno se otevře“:

| Kategorie | Příklady akceptace |
| --- | --- |
| Přenositelné core | `net10.0`, žádný UI/OS závislý assembly reference, testy na Win/Linux/macOS |
| Golden outputs | Shodné bajty pro neměněné zdrojové soubory a deterministické konverze; zdokumentované výjimky |
| Uživatelské rozhraní | Schválené stejné tokeny, rozložení, názvy, ikony, actions, error states pro všechny moduly/OS |
| Vizuální regresní testy | Screenshoty hlavního workspace, DSK Disk Map, Audio Analyzer, Recorder na 100/150/200% DPI (podle schopností CI); bez useknutých ovladačů |
| Ovládání | Stejný zoom/pan/fit, výběry, shortcuts, klávesnice, drag/drop, focus, tabulator |
| Bezpečnost dokumentů | Žádné tiché přepsání originálu při Open/repair/Save As, kontrola změn před Close, otevření read-only médií |
| Audio | Identický PCM archiv při zátěži UI, oddělení preview drop/archival drop, omezené fronty, korektní WAV+JSON a SHA-256 |
| Soubory | Velké DSK/HFE/QD/MZF, neznámé či poškozené soubory, názvy s diakritikou, case sensitivity, read-only a oprávnění |
| Dialogy | Žádný nechtěný file picker; po Cancel se stav nezmění; chybová hlášení stejná na všech OS |
| Životní cyklus | Opakované Open/Close, cancel long task, nahrávání při zavření, zrušené asynchronní operace, úklid handle/bufferů |
| Přístupnost | Taby, focus, čitelné labely, contrast, stavové ikony + text, screen reader kde je to realizovatelné |

**Každý krok:** zapsat porovnávaný původní WPF workflow, nové Avalonia workflow, testované OS, výsledky a známé limity. Screenshotové porovnání nesmí nahrazovat binární testy dekodérů.

## 8. Výkonnost a paměť

- Dlouhé WAV/FLAC nečíst celé do RAM; pokračovat v existujícím streamování, bounded caches a min/max bucket zobrazení.
- Nevkládat UI výpočty do capture callbacku; při práci s mouse/pan/zoom musí nahrávání zůstat archivně prioritní a zjištěný dropout vyznačen.
- Při velkých DSK/QD mapách používat virtualizaci/tabulky a omezené render cache. Po zavření uvolnit nativní zdroje a event handlers.
- Nevyužívat druhé ad-hoc implementace pulzního parseru nebo referenčních tabulek kvůli UI.
- Nedeklarovat „nulový jitter“ bez HW testů; vzorky mají časovou základnu z ADC/driver streamu, ne z UI callback arrival.

## 9. Dokumentace a správa změn

- Aktualizovat README, architektonický přehled, testovací průvodce, uživatelský manuál, release notes a third-party notices **jen v příslušně schválené fázi**.
- V reportech používej `Implemented / Tested / Hardware-tested / Not yet implemented / Blocked` – neoznačovat port dokončený jen protože `dotnet build` prošel.
- Zapsat seznam otevřených úkolů a závislostí mezi fázemi. Respektovat rozpracovanou větev, změny z lokálního pracovního stromu a jiná zadání Codexu (např. názvy IC profilů, zobrazení komprese při MZF→COM, DSK Repair).
- Přehlednost kódu je samostatný cíl: každá veřejná služba jasná odpovědnost, bez zbytečných wrapperů a duplicit, logické složky. Počet `.cs` souborů není KPI, provázanost a srozumitelnost ano.
- Návrhy nových závislostí musí uvádět přesnou verzi až po ověření, licence, nativní runtime, bezpečnost a dopad na cílové platformy.

## 10. Povinný formát výstupu Codexu po F0 (bez implementace)

Předlož uživateli v češtině:

1. **Aktuální stav repo**: HEAD, branch, clean/dirty, test projects, OS dependencies a existující návrhy migrace.
2. **Architektonickou mapu**: Core/Capture/Frontend/Platform, úplný seznam souborů a konkrétní změny navrhované pro F1.
3. **Inventuru funkcí** a mapu starého a nového UI, místa duplicit, rizika integrace jiných rozpracovaných změn.
4. **Návrh jednotného UI**: principy, komponenty, navigace, vzhled, chování, schvalovací vizualizace pro fázi F2.
5. **Plán etap** se závislostmi, orientační pracností, přesnými acceptance gates a rollback strategií.
6. **Seznam testů a jejich výsledků** – oddělit skutečně spuštěné testy od jen navržených.
7. **Výslovnou otázku:** „Souhlasíte s implementací fáze F1 – oddělení MZTools.Core při zachování současného WPF rozhraní a binární kompatibility?“

**V tomto okamžiku zastav a čekej na schválení.** Pokud uživatel souhlas odmítne, nic neupravuj.

---

### Stručné zadání pro připomenutí

**Postupně, bez big-bang přepisu; nejdříve společné jádro, pak SCHVÁLENÝ jednotný design system, pak jedna Avalonia aplikace pro Windows/Linux/macOS. Všechny moduly musí vypadat a chovat se jako jediný promyšlený produkt. Zachovat existující algoritmy, kompatibilitu souborů, recorder integritu a testy. Před každou změnou požádat o výslovný souhlas.**
