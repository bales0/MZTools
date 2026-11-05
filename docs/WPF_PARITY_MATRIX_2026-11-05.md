# WPF / Avalonia — matice a inventář, 5. listopadu 2026

Výchozí HEAD d3dc601292101e4dd82bcc2022fa981301c75837. Matice rozlišuje statickou kontrolu, konkrétní regrese a dosud neověřenou úplnou paritu. WPF produkční zdroje zůstávají beze změny.

| Funkce | WPF chování | Avalonia chování | Rozdíl / stav | Dopad na data | Priorita | Důkaz a reprodukce | Návrh / provedení | Regresní test | Stav schválení |
|---|---|---|---|---|---|---|---|---|---|
| Nový disk / format | DskNewDialog.UpdateFields/UpdateCapacity: profilová geometrie, fixed/custom, kapacita | DiskFormatForm + stávající Core factory | Opraveno: 40/80, fixed/raw a sektorová ID | Původně nechtěná menší geometrie | P0 | Skutečný dialog: vybrat LEC HD, ověřit 80×2 a 1440 KiB; Cancel; vytvořit MRS | Hotovo v odsouhlaseném rozsahu | ActualDiskDialogUsesProfileGeometryAndCancelPreservesDocuments; ProfileDefaultsCreateExpectedCapacity | A+B schváleno |
| Selected audio diagnostika | AudioRegionRecoveryPreview.RefreshPreview/RefreshCompletedHistogram připojuje jen kompatibilní scan | AudioSession.SelectedPreview drží scan transform a interval odděleně od PCM | Opraveno: zachování kompatibilních markerů, izolace OUTDATED | Interpretace problémů a regionů | P0 | Run deeper analysis; stejný refresh; posun/inverze/rozsah; histogramový stream | Hotovo; historický histogram zůstává viditelně označen | ActualChecksumFailureSurvivesMatchingPreviewRefresh; MatchingRecoveryOverlaysSurviveRefreshAndNeverAttachToChangedPcm; nativní audio workflow | A+B schváleno |
| Tape/QuickDisk HEX | HexBrowser.xaml.cs: MZF header/data/trailing, SHASCII, QDF prefix/CRC | HexViewWindow rozlišuje oblasti a odvozený QDF header ze společného encoderu | Opraven UI rozdíl; kompletní data zachována | Žádné změny médií | P1 | MZF s trailing bajty; přepnout QDF header (derived) | Hotovo; původ odvozených bajtů je výslovný | HexBrowserIncludesTheEntireRecordAndTrailingBytes; HexBrowserRendersDerivedQdfHeaderAndCompleteRecord | A+B schváleno |
| DSK HEX | DskHexEditorControl: read-only TextBox + FixedHexInput, unlock, preview, Cancel | DiskWorkspace používá tentýž FixedHexInput a DskHexEditService; ASCII/SHASCII preview | Opraven fixed-size/lock; layout zůstává multiplatformní | Stejná Core ochrana délky a filesystem impact | P1 | Sektor nebo Open CP/M block; typing před unlock; unlock; přepis; odmítnout Apply; Cancel | Hotovo; rozdělení samostatného preview/apply dialogu zůstává odlišné | ActualHexTypingDraftCancelAndCoreApply; Core hex testy | A+B schváleno |
| F2 přejmenování | DskEditorWindow.xaml.cs začíná editaci názvu v povoleném sloupci | DiskWorkspace inline TextBox; Enter Core preview; Esc návrat | Opraveno; Enter mimo editaci zůstává inspektor | Jen výslovně potvrzená změna kopie v paměti | P1 | F2 nad souborem; Esc; neplatný název; odmítnout a potvrdit preview | Hotovo v UI a společném metadata workflow | RenameCancelAndInvalidNamePreserveOriginalThenValidRenameApplies; DiskUiTests | A+B schváleno |
| Diskové specializované dialogy | Původní specializované dialogy zůstávají referencí | Část operací používá DiskFormAsync a společný Core preview | Zbývající UI rozdíl; všechny focus/Cancel větve nejsou runtime porovnány | Nová chyba výstupu neprokázána | P1 | Níže je inventář; pro každý profil otevřít stejná data v obou frontendech | Nepřepisovat Core; přenášet další doložené rozdíly | DiskDocumentTests a nativní disk workflow nejsou důkazem všech dialogů | A+B schváleno; úplná parita NETESTOVÁNA |
| Pulzní reference | AudioPulseReferenceControl: provenance, časy a kontext | Jediný PulseReferenceCatalog; doplněné software/version/polarity/limitations; scatter/drift | Doplněné UI; výpočty a klasifikátory nezměněny | Žádné přepočty pulzních algoritmů | P1 | Vybrat ROM/IC/TC a session reference; kanál/polarita; show reference | Hotovo pro doložené mezery; úplné porovnání všech katalogových voleb zbývá | AudioDocumentTests.ReferencesAreSessionLocalAndProvenanceRemainsExplicit; katalogové Core testy | A+B schváleno |
| Batch Process | BatchProcessDialog má dynamické volby podle operace | BatchProcessForm přepíná input/folder, target, backup, auxiliary, rate, extract | Doplněná dynamika; stávající preflight/stop/report zachovány | Core output policy nezměněna | P1 | Analyze, ConvertFormat, ExtractAll; files/folder; replace+backup; Cancel | Hotovo pro dynamické parametry; úplná klávesová parita neprokázána | BatchDocumentTests; native-media workflow | A+B schváleno |
| Hlavní menu / COM | WPF MainWindow a DskEditor menu | Tools ▾ + kontextová disková menu; MZF/M12 → COM a Import Native COM existují | Již implementováno; výsledný layout ponechán bez duplicit | Chybějící výstupní cesta neprokázána | P1 | Tools → MZF/M12 → COM; CP/M disk → Import Native COM | Bez nového duplicitního menu; inventář níže | Nativní workflow, NativeCom tests; úplná dosažitelnost všech kombinací není potvrzena | A+B schváleno |
| DSK/HFE Visual Inspector | Track/Side/Widths, šest vrstev, filtry, MFM, selection/floating details | Existující DiskVisualWindow/DskVisualCanvas | Již implementováno, znovu se nepřepisuje | Zachované snapshoty a Core MFM | P1 | Stejné DSK/HFE fixture; vrstvy, filtry, sector selection, opakované floating details | Pouze regrese | DskVisualParityTests; nativní disk: 23 kontrol | A+B schváleno |
| Import vybraných sektorů | Pouze povolené editovatelné obrazy | CanEditHex + žádný draft/busy; tooltip i na disabled položce | Doplněno vysvětlení; bez projekce nebyl import dostupný již před změnou | Bez tichého no-op | P1 | Physical obraz bez bezpečné projekce; read-only; pending hex draft | Hotovo; zachován Core preview/import | DiskVisualWindow menu a stávající read-only testy; fyzické zařízení NETESTOVÁNO | A+B schváleno |
| QD mapa / spirála | Osm řádků a 32závitová spirála | Existující přenos render/hit-test modelů | Již implementováno; žádný nový renderer | Nezměněná interpretace QD | P1 | Disk Map, multi-select, spirála, floating details | Pouze regrese | WpfParityTests/DeepWpfParityTests, Core QuickDiskVisualTests; nativní media | A+B schváleno |
| Malá okna / DPI | WPF vychází z pracovní plochy | DesktopWindowSizing omezuje vybraná okna podle skutečného working area/scaling | Doplněno; BatchWorkspace již nemá vynucených 720 DIP | Bez změny médií | P1/P2 | Native 1440×960, 1366×768, 920×640, 640×480; dialogy podle pracovní plochy | Hotovo pro vybraná okna; všechny monitory/DPI NETESTOVÁNY | Nativní layout smoke a nové render testy | A+B schváleno |
| Klávesnice a více waveform oken | Referenční Esc/F2/Enter/Delete/Ctrl a samostatný viewport | Stávající generation/cancellation a independent session; doplněné F2/HEX chování | Částečně ověřeno; úplný tab order, všechny clipboard a focus cesty NETESTOVÁNY | PCM algoritmy beze změny | P1/P2 | Ctrl/Shift výběry, F6, zoom, R ±1/±10 a nezávislá okna | Zachovat stávající implementaci, další chyby jen s reprodukcí | SelectedAnalysisWindowTests, AudioUiTests, DiskUiTests, native | A+B schváleno |

## Inventář příkazů a formulářů

Následující inventář dokládá přítomnost cest v aktuálním kódu. Neznamená, že u každé položky byla samostatně ověřena všechna předvyplnění, fokus, dostupnost pro všechny filesystémy a Cancel ve WPF i Avalonia.

### Diskové operace

- Container repair — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Normalize container — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Filesystem repair — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Defragment — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Format disk — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Compare image — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Convert filesystem — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Export converted container — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Physical properties — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Open CP/M block — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Raw export — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Raw import — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Export patch — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Apply patch — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap export — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap import — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap header export — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap header import — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap clear — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Bootstrap metadata — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Install boot/system — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- CP/M layout — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Manage profiles — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- IPL add programs — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- IPL rename / compression — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- IPL move up — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- IPL move down — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- IPL delete programs — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Import Native COM — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.
- Copy to Tape — `ShellDiskCommands.RunDiskToolAsync()` / `ShellDiskDetails` a příslušná Core služba.

### Pojmenované parametrické formuláře

| Formulář | Avalonia soubor | Klíče polí z deklarace | Stav úplného WPF porovnání |
|---|---|---|---|
| Select exact audio frames | ShellAudioCommands.cs | start, end | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Export selected waveform | ShellAudioCommands.cs | scope | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Export analysis report | ShellAudioCommands.cs | format | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Export recovered programs | ShellAudioCommands.cs | scope, format | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Synthetic MZF header | ShellAudioCommands.cs | name, type, load, exec, description, ack | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Send recovered programs | ShellAudioCommands.cs | scope | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Session-local pulse reference | ShellAudioCommands.cs | name, sh, sl, lh, ll | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Offline waveform trigger | ShellAudioCommands.cs | level, edge | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Deeper audio analysis | ShellAudioRecovery.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Batch process · preflight | ShellBatchCommands.cs | Víceřádková deklarace v souboru | Dynamika doplněna; úplné WPF pořadí/fokus NETESTOVÁNO |
| Export batch report | ShellBatchCommands.cs | format | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Export files | ShellDiskCommands.cs | format, scope | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Copy to disk | ShellDiskCommands.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Disk format | ShellDiskCommands.cs | format, container, tracks, sides, sectors, size, filler, order, ids | Skutečný dialog a Cancel ověřeny |
| Physical track properties | ShellDiskDetails.cs | creator, c, h, gap, filler, sectors | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Install boot/system | ShellDiskDetails.cs | source | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| CP/M layout | ShellDiskDetails.cs | profile | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| CP/M DPB parameters | ShellDiskDetails.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Profiles | ShellDiskDetails.cs | action | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Save layout profile | ShellDiskDetails.cs | name, description | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Save boot profile | ShellDiskDetails.cs | name, description | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Delete profile | ShellDiskDetails.cs | profile | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| IPL program options | ShellDiskDetails.cs | name, mode, direction, quick, embedded, skip | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Native COM options | ShellDiskDetails.cs | target, mode, compression, name, collision | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Copy from another disk... | ShellDiskWpfCopy.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| Import native program into CP/M | ShellNativeComTools.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |
| MZF/M12 → COM · CP/M import | ShellNativeComTools.cs | Víceřádková deklarace v souboru | Existující Core workflow; úplná dialogová parita NETESTOVÁNA |

### Hlavní toolbar a menu

Tools: Batch Process, Visual analysis, Analyze Audio, Record Audio / Live Tape Analysis, MZF/M12 → COM, New tape, New QD, New disk, Record properties. Toolbar: Open, Add, Export, View, Delete, Compression, Save, Save As, Close, posun řádků nahoru/dolů, New QD, New disk a QD Format podle aktivního dokumentu. Deklarace: `MainWindow.Wpf.cs`, diskové nabídky v `DiskWorkspace.axaml` a `DiskWorkspace.WpfMenus.cs`. Klasické skryté menu se znovu nezobrazuje; nevzniká druhá duplicitní nabídka.

### Platformní mezery

Windows native smoke používá skutečné desktopové ovládací prvky a Skia render. Původní WPF smoke se spouští samostatně nad zachovanými zdroji; žádný snímek se nevydává za pixelově shodné celé GUI. Linux runtime, macOS, ARM, všechny DPI/monitory, screen reader a fyzická audio zařízení nejsou tímto inventářem prokázány.
