# Katalog společných komponent — návrh F2

Každá komponenta má jednu implementaci stylu/chování pro všechny moduly a OS.
Vlastní vykreslování je povolené pro data média, nikoli jako náhrada běžného formuláře.

| Komponenta | Zodpovědnost a kontrakt | Použití |
| --- | --- | --- |
| ApplicationShell | menu, command bar, dokumenty, navigation, focus regions, status | všechny moduly |
| DocumentTab / DocumentHeader | název/path, `*`, modified/read-only/incomplete, capability příkazů | každý dokument |
| CommandBar / CommandMenu | stejné příkazy, priority, názvy, tooltip/shortcut/disabled reason | shell i kontextové akce |
| WorkspaceFrame | sdílený header, lokální views, splitter, inspector | Tape/QD/Disk/Audio/Recorder |
| DataTable / TreeList | columns, selection, keyboard, empty/filter state, virtualizace, retained highlight | záznamy, soubory, sektory, metriky, issues |
| SelectionInspector | jediný kontext výběru; property groups, přesné hodnoty | všechny grafické moduly |
| PropertyForm / ValidationSummary | label, input, units, validace u pole, inline chyba, Apply/Cancel | metadata, formáty, profiles, zařízení |
| SplitPane / DetailWindowHost | resize, minimální čitelné rozměry, return focus, sdílený stav | mapy, hex, report, recorder detail |
| ChangePreview | origin/current/result evidence, změněné rozsahy, riziko, Apply/Cancel | repair, format, boot, hex, transfer |
| CollisionResolver | konkrétní cíl, overwrite/rename/cancel; ochrana zdroje | import/export/batch |
| FilePickerAdapter | společná format/capability politika a Cancel, nativní OS picker | Open/Save As/Export |
| ErrorBanner / ReportViewer | kód, dopad, akce, tabulky + TXT/CSV/JSON | každý modul |
| BackgroundTaskProgress | fáze/progress/cancel, vlastník, pending/failure cleanup | conversion, analysis, batch |
| HexEditor | fyzický/logický kontext, offset, bytes, pending edits, sensitive-range preview | Tape/QD/Disk/candidates |
| GraphToolbar / ViewportModel | zoom anchor, pan, Fit, channel/gain, selection state a units | waveform/histogram/mapy |
| SignalPlot / SpectrumPlot | bounded render snapshot, min/max buckets, FS/dBFS, textový ekvivalent | offline a recorder |
| HistogramPlot / ScatterPlot / DriftPlot | měřicí interval, N/µs, guides/context, click-to-locate | offline/live pulses |
| DiskMapRenderer / SurfaceRenderer | fyzické sloty vs CHRN, ownership, status, virtualizovaný detail | DSK/HFE |
| QuickDiskMapRenderer / PhysicalRenderer | logical records, framing, decoded/raw host units | QD/QDF/MZQ |
| PulseReferencePicker | společná catalog projection, provenance, user values, guide visibility | offline + recorder |
| CompressionOptions | Keep/uncompressed/ZX0/ZX7/Auto podle cíle; stejný význam a disabled reasons | Tape/QD/COM/IPL |
| RecorderControls / IntegritySummary | explicit Monitor/Record/Stop/Discard, actual format, archive/preview losses | recorder |
| HeadAlignmentView / RelativeEdgeDial | kvalifikace, manual zero, invalidation, srovnatelný A/B kontext | recorder |

## Implementační hranice pro F3 a následující fáze

```text
MZTools.Desktop/
  Theme/       # schválené tokens, styly, původní SVG ikony
  Controls/    # komponenty tabulky, formu, preview, reportu, grafového toolbaru
  Views/       # kompozice; žádný vlastní parser nebo encoder
  ViewModels/  # dokumentový a command/capability stav
  Renderers/   # kreslení existujících Core/Capture snapshotů
  Platform/    # picker, clipboard, shell, permissions, backend composition
```

View vlastní UI lifecycle, focus a dispatcher. ViewModel nesmí zobrazovat systémový
picker ani kreslit. Renderer nesmí číst soubor/PCM, spouštět FFT nebo měnit decoder.
Core/Capture vlastní stávající výpočty; stejná operace nesmí vzniknout v „Avalonia“ verzi.
AudioRecorderViewModel a bounded pipeline se znovu nenavrhují. DTO/context projection
se může přesunout do adaptéru bez kopírování katalogových hodnot.

Konkrétní toolkit verze, MVVM/DI knihovna, DataGrid/TreeGrid či native renderer/backend
nejsou rozhodnutím F2. Jakákoli nová závislost má před implementací uvést verzi, licenci,
RID/nativní obsah a dopad. Použít standardní Avalonia controls, kde vyhoví kontraktu;
nezavádět wrapper pro každý jednotlivý label nebo kontrolu.

## Kompaktní rozložení

Referenční pracovní viewporty jsou 1366×768 a 1920×1080 logických jednotek; recorder se
musí vejít do referenčního 920×640. Na úzkém okně se navigace změní na popsané ikony,
inspector se přesune do vyvolatelného detailu, command overflow zachová všechny akce.
Centrální formulář/graf může vertikálně scrollovat; Stop/finalize a kritický stav zůstávají
v pevné horní liště. Tabulky mají vlastní horizontální scroll a nastavitelnou šířku sloupců.
Nesmí scrollovat celá aplikace vodorovně ani mizet akce bez dostupné overflow cesty.

DPI matice rozlišuje logický viewport, fyzické pixely a škálování. Není možné vydávat
1366 fyzických pixelů při 200 % za 1366 logických jednotek. Pokud dostupný viewport
klesne pod referenční minimum, implementace musí použít reflow/scroll/overflow a projít
zvláštní kontrolou. Nativní potvrzení na všech OS je až implementační brána.

## Přístupnost

Každý graf má čitelný název, textové metriky a přesný interval. Symbol + text doplňuje
barvu. Focus pořadí a ovládání bez myši patří do komponenty, nikoli do každého dialogu
zvlášť. Tooltip má obdobu v detailu/accessible description. Disabled akce mají čitelný
důvod dosažitelný i bez hoveru. Čísla nejsou součástí bitmapového popisku.

Prototyp předvádí základní HTML focus, jména tlačítek a lokální interakce; neprokazuje
Avalonia AutomationPeer, screen reader, drag-and-drop mezi procesy, native file picker,
platformní font fallback nebo skutečné DPI. Tyto položky se vedou jako UNTESTED.
