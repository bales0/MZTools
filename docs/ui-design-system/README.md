# MZTools UI Design System

**F2 — schváleno uživatelem 29. 10. 2026**, včetně pravidel a změn C01–C09.
Viz [záznam schválení](APPROVAL.md). Tento adresář obsahuje dokumentaci a lokální
prototyp. Schválení F2 nezahajuje implementaci F3; její konkrétní rozsah předkládá
[samostatný návrh](../F3_AVALONIA_SHELL_PLAN_2026-10-29.md).
Níže uvedená pravidla řídí následnou realizaci všech modulů.

## Co posoudit

Otevřete [lokální prototyp](prototype/index.html) nebo [galerii návrhů](gallery.html).
Prototyp funguje offline přes `file://`; nevyžaduje server ani instalaci balíčků.
Pět referenčních kompozic používá stejný shell a komponenty:

| Obrazovka | Náhled | Hlavní posuzované chování |
| --- | --- | --- |
| Tape | [01-tape.png](previews/01-tape.png) | pořadí, metadata/profil, modified, společný inspector |
| QuickDisk | [02-quickdisk.png](previews/02-quickdisk.png) | logická/fyzická data a význam výběru |
| DSK/HFE | [03-disk-map.png](previews/03-disk-map.png) | mapa, soubory, CHRN/FDC, hex a bezpečná oprava |
| Audio Analyzer | [04-audio-analyzer.png](previews/04-audio-analyzer.png) | waveform, regiony, histogram/spektrum, nezměněný zdroj |
| Recorder / Head Alignment | [05-recorder.png](previews/05-recorder.png) | explicitní start, archiv/preview, relativní A/B a nezávislá session |

Jména souborů, bajty, grafy a metriky jsou **modelová data**, nikoli analýza média.
Grafy jsou SVG kompozice, nikoli druhý dekodér, FFT ani pulse catalog. Náhledy nejsou
screenshoty Avalonia aplikace. Prototyp nečte soubory, nevolá zařízení a nic neukládá.
Jeho demonstrační Save/Save As a Recorder mění jen paměťový model prohlížeče.
Detailní záložky naznačují hierarchii/sekce; nejsou implementací všech jejich funkcí.

## Schválený závazný směr

- Jediná finální Avalonia aplikace pro Windows, Linux a macOS; jednotná navigace,
  stejný význam akcí, stejný vizuální systém. WPF slouží pouze jako migrační reference.
- Jeden dokumentový shell: aplikační menu, společná command bar, otevřené dokumenty,
  pracovní plocha, kontextový inspector a status/progress. Modul nevytváří vlastní shell.
- Levá navigace aktivuje poslední dokument příslušného typu nebo prázdný workspace.
  Sama neotevírá file picker, soubor ani audio zařízení. Prototyp aktivuje modelové příklady.
- Panely detailu sdílejí výběr se seznamem a grafem. Změna modulu ani focusu nesmí
  smazat výběr, viewport, nastavený gain nebo rozepsaný stav bez výslovného rozhodnutí.
- Recorder je samostatně vlastněná session. Jeho vložený a případný samostatný
  Avalonia pohled používají stejné komponenty. Zavření browseru jej nesmí zahodit.
- Stávající schopnosti, formáty, omezení hostů, komprese a bezpečnost zůstávají zachované.
  Nevzniká nový decoder, paralelní Capture pipeline ani další historická timing tabulka.

## Paleta a tokeny

Jediný zdroj hodnot je [tokens.json](tokens.json). [tokens.css](prototype/tokens.css)
se generuje skriptem `tools/build_tokens.py`; hodnoty se nemají kopírovat do obrazovek.
F3 převede schválené tokeny na Avalonia resources, nikoli na nezávislou druhou paletu.
Návrh obsahuje pouze světlé téma. Tmavé téma vyžaduje samostatné schválení tokenů.

| Role | Token | Hodnota / použití |
| --- | --- | --- |
| Pracovní pozadí | canvas | `#f4f6f9` |
| Povrch / oddělení | surface / subtle / border | bílý panel, jemná hlavička, dekorativní okraje |
| Hlavní text / vedlejší text | text / muted | `#172b46` / `#52647a` |
| Primární akce / focus | accent / focus | `#195dcc` |
| Výběr | selected + accent | světlé vyplnění, výrazný okraj a textový kontext |
| Ovládací hranice | control-border | `#8191a5`; dekorativní border ji nenahrazuje |
| Healthy | healthy + healthy-bg | zelený text + ikona a konkrétní evidence |
| Repairable / warning | warning + warning-bg | oranžový text, popis, explicitní preview |
| Error / incomplete / recording | error + error-bg | společná výstražná rodina; význam musí odlišit text a symbol |
| Read-only | readonly + readonly-bg | zámek, důvod omezení, dostupné exporty |
| Waveform | wave-l / wave-r | modrá Left, fialová Right; vždy také legenda L/R |
| Pulse groups | pulse-sh / pulse-sl / pulse-lh / pulse-ll | Short HIGH/LOW a Long HIGH/LOW; také názvy a rozlišení čar/markerů |
| Disk map | disk-system / disk-directory / disk-file / disk-free | role nesmí nahrazovat skutečný stav, chyby ani read-only |

Hover používá accent-soft a ovládací okraj; focus má 2px obrys s 3px odsazením.
Disabled zachová čitelný název a důvod nedostupnosti. Modified má `*` u názvu a textový
stav. Recording má text i symbol; blikání není nutné. `Incomplete` nesmí vypadat jako
úspěšně dokončený archiv. Vysvětlení musí být dostupné i klávesnicí, nejen hoverem.

Textový kontrast cílí minimálně na 4.5:1, důležité grafické/ovládací hranice na 3:1.
Čísla jsou návrhová akceptační kritéria; samotný screenshot neprokazuje přístupnost.
Viz [W3C Contrast Minimum](https://www.w3.org/WAI/WCAG22/Understanding/contrast-minimum)
a [Non-text Contrast](https://www.w3.org/WAI/WCAG22/Understanding/non-text-contrast).

## Typografie, rozměry a formátování

- UI font: systémový sans-serif s fallbacky Segoe UI / Noto Sans; žádný pevný Windows-only
  font jako jediná možnost. F2 nedistribuuje font ani nepřidává jeho závislost/licenci.
- Body 13, sekundární text 12, captions 11, titulek 22, oddíl 16; normální text není bitmapa.
  Kritické informace nejsou odkázané na drobný caption. Monospace má vlastní fallbacky.
- Kontrolní výška 32, kompaktní 28; řádek tabulky nejméně 32 v normálním režimu.
  Rozestupy 2/4/8/12/16/24/32; radius ovladače 6 a panelu 8. Větší font neuseknout pevným řádkem.
- Převládají tabulky a pracovní plochy; nepřidávat dekorativní karty, velké hero plochy nebo
  individuální styly oken. Ikony prototypu jsou vlastní jednoduché SVG, nikoli nová ikonová knihovna.
- Hex adresy mají prefix `0x`, velká písmena A–F a šířku podle kontextu. CHRN musí uvést
  základ čísel; fyzický slot a uložené CHRN se nesmějí směšovat. Bajty mají dvě hex číslice.
- `B` je přesný počet bajtů; `KiB` = 1024 B, `kB` = 1000 B. Zaokrouhlený souhrn má přesný
  počet v detailu/tooltipu. Tisícové oddělovače a desetinné znaky řídí jedna UI kultura.
- Časy, Hz/kHz, µs, samples/frames, FS a dBFS používají jednotné formattery. Graf a inspector
  zobrazují tentýž interval `[start, end)`. Zaokrouhlený čas nesmí měnit zdrojové frame indexy.
- dBFS je amplitudová metrika; FFT musí uvést okno a N/rozlišení, nikoli tvrdit PSD.
- Texty návrhů jsou anglicky jako stávající UI; specifikace je česky. Budoucí lokalizace
  patří do UI resources/formátování, nikoli do modelů nebo změny serializace.

## Komponenty a struktura

Katalog je v [COMPONENTS.md](COMPONENTS.md), konkrétní ovládání v
[INTERACTIONS.md](INTERACTIONS.md), workflow v [WORKFLOWS.md](WORKFLOWS.md).
[PARITY.md](PARITY.md) mapuje funkční rodiny a vědomé změny; podkladová
[wpf-controls.csv](wpf-controls.csv) a [wpf-inputs.csv](wpf-inputs.csv) obsahují zdrojové
captiony, tooltipy, input handlery a navržené komponenty. Inventura není runtime coverage;
zejména dynamicky vytvářené hodnoty a disabled větve vyžadují pozdější funkční kontrolu.

Navržené F3 členění: `Theme/`, `Controls/`, `Views/`, `ViewModels/`, `Renderers/`,
`Platform/`. View řídí focus/layout, ViewModel stav a příkazy, Renderer kreslí snapshot,
Platform vlastní picker/clipboard/shell/backend factory. Core/Capture se kvůli vzhledu nemění.
Bez schválené technické fáze se nevybírá MVVM knihovna, DataGrid balíček ani verze Avalonia.

Výsledky návrhových kontrol a následná OS/DPI akceptační matice jsou v
[VALIDATION.md](VALIDATION.md).

## Schvalovací brána

Posoudit pět kompozic, světlou paletu, hustotu tabulek a oddělený inspector, společné
Save/Save As/Export, grafové ovládání, kompaktní rozložení a recorder lifecycle. Seznam
vědomých změn C01–C09 je v PARITY.md. Výsledek uživatel potvrdí nebo připomínkuje.
Teprve potom lze navrhnout F3 a vyžádat nový souhlas s implementací shellu.
Stav nyní: **návrh vytvořen, schválení vizuálního směru zatím neuděleno**.
