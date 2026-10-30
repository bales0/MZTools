# Ověření a akceptační checklist — F2

## Co F2 ověřuje

F2 posuzuje návrh a lokální HTML/SVG model: konzistenci kompozice, tokeny, texty,
základní interakce, source inventory a přístupnost návrhu. Nenahrazuje funkční,
binární nebo hardwarové testy aplikace. Production testy z F1 se kvůli dokumentačnímu
prototypu znovu nespouštějí; žádný aplikační zdroj ani závislost se v F2 nemění.

Výsledky tohoto běhu jsou uvedené ve spodní části dokumentu. Strojové logy jsou
lokálně v `TestResults/F2/`; nepočítat přerušený renderer jako úspěšný screenshot/test.

## Reprodukce lokálního návrhu

```powershell
py -3 docs/ui-design-system/tools/build_tokens.py
py -3 docs/ui-design-system/tools/inventory_wpf.py
& docs/ui-design-system/tools/render_review.ps1
py -3 docs/ui-design-system/tools/validate_design.py
```

Renderer používá již nainstalovaný Edge a standardní Python, izolovaný dočasný profil,
headless proces a DevTools pouze na loopbacku. Nepublikuje web a nepoužívá profil uživatele.
Pět hlavních PNG má 1440×960. `render_review.ps1 -ChecksOnly` nebo `-ScreensOnly`
oddělí již provedené kontroly od opakovaného exportu. Python CDP klient je implementační
pomůcka F2; není součást budoucí desktopové aplikace ani nová produkční závislost.

## Objektivní kontrola návrhu

- [ ] Ve všech pěti kompozicích jsou stejné command bar, dokumentové záložky, navigace,
  title/state, lokální views a inspector. Akce mají stejný význam a dostupnost.
- [ ] Tabulky mají viditelné hlavičky, jména, units, selection a přístupný scroll.
  Focus jiného panelu nezruší zvýraznění ani zvolený kontext.
- [ ] Save As přepne dokument až po ověřeném zápisu; Export nikdy. Cancel nic neaplikuje.
  Repair originál a odkazy zůstávají chráněné. Healthy disk nemá opravný dialog.
- [ ] Zoom/pan/Fit mají stejný směr, anchor a indikaci. Výběry a viewport přežijí změnu view.
- [ ] Recorder se sám nespustí. Stop/finalize je dostupný i na kompaktní ploše, má
  správný lifecycle a neplete archive loss s preview loss. A/B je podmíněné kvalifikací.
- [ ] Každé důležité sdělení má text/symbol vedle barvy; focus a disabled reason jsou čitelné.
- [ ] České/polské/ukrajinské názvy se zobrazí, nejsou oříznuté a mají font fallback.
- [ ] Běžný stav neblokuje modalitou; citlivá akce má preview a konkrétní rozhodnutí.
- [ ] Vizuální návrh, interakční pravidla a C01–C09 uživatel výslovně schválil.

Checkboxy jsou schvalovací/native akceptační checklist, nikoli automaticky označené
PASS podle existence HTML. Automatické návrhové kontroly jsou vypsané v logu níže.

## Následná nativní matice pro F3–F9

| Rozměr | Povinné kombinace / důkaz |
| --- | --- |
| OS | Windows, Linux, macOS; přesná verze OS, Avalonia, renderer a fonts v reportu |
| Reference | 1366×768 a 1920×1080 logický viewport; recorder také 920×640 |
| Scale | 100/125/150/200 %; zaznamenat logické rozměry i fyzický PNG rozměr |
| Screenshots | všech pět schválených pohledů; nejméně 100/150/200 % na všech OS, 125 % jako layout/focus kontrola |
| Stavy | clean/modified/read-only, repairable/ambiguous, error/incomplete, pending/recording/finalizing, empty/cancel |
| Vstup | myš, klávesnice/focus/Tab, Primary/Shift selection, drag/drop a clipboard mezi podporovanými dokumenty |
| Životnost | open/close/reopen, zrušené async operace, active recorder při close/quit, device loss a cleanup |
| Data | stejné fixture soubory a golden výstupy jako WPF; žádná změna očekávaných bajtů kvůli vzhledu |

Snímky normalizovat na tentýž logický viewport, kulturu a dataset. Geometrie významných
panelů se má lišit nejvýš o 2 DIP, text nesmí být oříznutý a akce se nesmějí přesunout
mimo dostupný toolbar/overflow. Rozdíly rasterizace fontů maskovat pouze v textech a
jejich 1px okraji při stejných line boxes; hraniční tolerance antialiasingu není dovolení
jiného přetečení/řádkování. Žádný celoplošný procentní pixel threshold nesmí skrýt
chybějící ovladač, jiný selection stav nebo ztracenou diagnostiku. U font fallbacku je
povinné vedle snímku zkontrolovat obsah, baseline a dostupnost ovládání.

Headless Windows browser s emulovaným DPR **není** důkaz native DPI ani portu na Linux
nebo macOS. Layout simulation, skutečné OS screenshots a hardware evidence jsou tři
oddělené kategorie. Totéž platí pro diagnostické diagramy prototypu vs decoder výsledky.

## PASS / FAIL / UNTESTED výsledky F2

| Kontrola | Výsledek / důkaz |
| --- | --- |
| Interakce prototypu | PASS: 252 kontrol = 6 konfigurací × 42 případů; 0 failures |
| Statické kontroly | PASS: 155 kontrol; tokeny, kontrasty, lokální odkazy, PNG a source hash |
| Inventura | 78 auditovaných zdrojů; 453 záznamů ovladačů/popisků/tooltipů; 64 input evidence řádků; bez nezmapovaných UI zdrojů |
| Vizuální návrhy | Pět skutečně vykreslených PNG 1440×960; ručně prohlédnuté Tape, QD, Disk, Audio a Recorder |
| Prohlížeč / prostředí | Windows, headless Edge 156.0.4314.50, lokální file URL + izolovaný DevTools |
| Aplikační UI zdroje | SHA-256 všech 78 auditovaných zdrojů nezměněno proti inventuře F2 |
| Nativní Avalonia / OS UI / hardware | UNTESTED; v F2 se neimplementují |

| Logický viewport | Emulované DPR | Výsledek |
| --- | --- | --- |
| 1440×960 | 1 | 42 PASS, 0 FAIL |
| 1366×768 | 1 | 42 PASS, 0 FAIL |
| 1920×1080 | 1 | 42 PASS, 0 FAIL |
| 920×640 | 1 | 42 PASS, 0 FAIL |
| 1366×768 | 1.5 | 42 PASS, 0 FAIL |
| 1366×768 | 2 | 42 PASS, 0 FAIL |

Ověřené interakce: společná command bar a aktivní workspace, pojmenovaná tlačítka,
absence celostránkového horizontálního přetečení, výběr/inspector a zoom přes změnu
modulu, filter, read-only Save, Cancel/Apply repair a ochrana původního jména při
Save As, oddělené dokumentové stavy, mapový výběr a roving keyboard entry, dostupný
Stop na malé ploše, explicitní demo Monitor/Record/Stop, zachování session při změně
workspace a odmítnutém close, explicitní A/B potvrzení.

Při ověřování se opravila příliš široká reakce navigace na klik, vazba potvrzení A/B
a kontrast okrajů volných sektorů. První neúspěšné exportní pokusy/mezivýsledky nejsou
započítané jako PASS. Poslední kontroly mají nula nevyřešených selhání.

Tato čísla nejsou výsledky produkčních unit/golden testů ani potvrzení funkční parity
Avalonia portu. Emulované DPR není nativní OS DPI. Podrobné klávesové/focus a screen-reader
ověření, skutečné drag/drop, file pickery, HW a screenshoty na Linux/macOS zůstávají UNTESTED.
