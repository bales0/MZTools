# Hloubková revize shody WPF / Avalonia — 4. 11. 2026

Výsledek: předchozí označení rozhraní za shodné bylo příliš široké. Původní testy prokazovaly funkčnost nové implementace, nikoliv shodu všech jejích obrazovek s WPF. Tato revize opravuje konkrétní zjištěné rozdíly a přidává ověření proti původním vykreslovacím algoritmům. Úplná shoda všech oken nadále není prokázána.

## Opravené rozdíly

| Oblast | Nález před revizí | Provedená oprava / kontrola |
|---|---|---|
| QuickDisk Disk Map | Jeden pruh a přidaný kruhový/MFM náhled | Osm řádků, souřadnice 110 / 31 DIP a osm stejných úseků podle QuickDiskMapControl; původní barvy i hit-testing |
| QuickDisk Disk Map | Chyběla tabulka bloků a panel Messages | Sloupce Role, #, File / details, Start, Length*, Payload B, LOAD, EXEC; dělitelná mapa/tabulka a bloky/zprávy |
| QuickDisk příkazy | Jiné reporty a ovládání | Hex view block, Clear selection, Copy report, Save report; stejný QuickDiskAnalysisReport jako WPF; chráněné exporty |
| QuickDisk stav | Chyběl řádek typu image | FlashFloppy / HxC / Sharp legacy a počet souborů / standardní buffer |
| QuickDisk Visual Analysis | Jednoduchý kruh místo skutečné spirály | Přenos původního 32závitového algoritmu, cache geometrie a inverzního převodu bodu na pozici |
| QuickDisk MFM | Jiná kompozice a barvy | Samostatný dělitelný panel; původní QuickDiskVisualCanvas, původní jednotky, zoom a přesné buňky; žádná interpretace jako naměřený flux |
| QuickDisk výběry | Jen jedna oblast, chyběly detaily | Ctrl/Command, Shift rozsah, synchronizace oblastí/souborů, floating details, dvojklik a Escape; kontextový HEX a export více oblastí |
| DSK Disk Map | Všechny stopy stlačeny do výšky panelu | Původní buňky 38×25 DIP, popisek 115 DIP, scroll; původní pořadí dle logical allocation blocks, původní barvy |
| WAV/FLAC PCM | Neprůhledný modrý výběr přes celý záznam | Průhlednost WPF (alpha 35), oddělení implicitního rozsahu exportu od viditelného výběru |
| WAV/FLAC PCM | Jiné výchozí kanály a škálování | Stereo overlay, gain 1, auto scale vypnuto, výchozí vybraný blok bez zúžení implicitního rozsahu analýzy |
| WAV/FLAC graf | Jiná geometrie a způsob kreslení | Původní regiony a názvy nad signálem, osy FS a čas, spojnice jednotlivých vzorků, vyplněná min/max obálka; relativní škála samostatně pro každý kanál |
| WAV/FLAC okno | Chyběl heading a stav, jiný toolbar a boční inspector | Původní pořadí hlavních příkazů, zdroj/format/quality/preferred channel, progress a stav; vlastnosti dostupné výslovně |
| WAV/FLAC záložky | Sloučené úrovně a agreement, jiné pořadí | Records / blocks, Pulse statistics, Channels / levels, Detector agreement, Issues, Regions, Definitions |
| WAV/FLAC tabulky | Vynechané reference, checksumy a měřené hodnoty | Všechny skalární sloupce původních modelů; původní pořadí prioritních sloupců, numerické formáty a checkboxy |
| WAV/FLAC nástroje | Recovery, Spectrum a Report jako další hlavní záložky | Samostatná okna se sdíleným modelem, zachovaný přístup přes kontext waveformu; uzavření při výměně zdroje |
| WAV/FLAC otevření | Obecný picker umožňoval i tape/disk v audio okně | Filtr WAV / FLAC / wav.json a vynucení typu i v loaderu; nesprávný soubor zachová aktivní analýzu |

## Jak byla shoda kontrolována

Původní WPF frontend se skutečně spouští přes MZTool.Tests/WpfParityUiSmoke. Testovací projekt byl rozšířen o FlashFloppy Disk Map, QuickDisk Visual Inspector, WAV a FLAC analýzu. Produkční zdroje původního WPF a algoritmy Core/Capture se nemění. Referenční snímky nejsou retušované ani přeskupené.

Nové regresní testy ověřují každý z osmi řádků, mezery mezi řádky, původní sloupce, obousměrný převod spirálových souřadnic, více výběrů, pevné DSK buňky a logical ordering. Stejné stereo PCM je skutečně zakódováno do FLAC; oba frontendové grafy pak musí mít identické pixelové buffery. Samostatně se kontroluje přítomnost obou kanálů a bílé plochy grafu a nezměněné zdrojové WAV/FLAC/DSK bajty. Nejde pouze o kontrolu názvů ovládacích prvků.

Čerstvé výsledky Windows/Linux, počty nativních kontrol, SHA-256 zdrojů a snímků jsou v [automatickém záznamu](WPF_PARITY_AUDIT_VALIDATION_2026-11-04.json). [Galerie přímého porovnání](ui-design-system/wpf-parity-2026-11-04/index.html) uvádí původ každého snímku. Windows WPF render používá skutečné DPI systému; galerijní CSS jej zobrazuje ve stejném logickém měřítku jako Avalonia. Celé obrazovky nejsou označovány za pixelově identické pouze podle počtu procházejících testů.

## Konkrétní zbývající rozdíly / neověřená shoda

- DSK/HFE Visual Inspector stále nemá celý původní společný toolbar Track / Side / Widths / Layer ani úplné filtry Severity / Type / Role / Owner. Jeho povrch, tabulky a rozložení je nutné dále přenést přímo z DskVisualInspectorWindow / DskVisualCanvas. Opravená běžná DSK Disk Map tuto skutečnost nemění.
- Pulse statistics má zachované výpočty a úplné hodnoty, ale volba Histogram / Scatter / Drift a reference ještě nemá přesně původní skladbu AudioPulseReferenceControl. Recovery používá společné Core funkce, ale jeho specializované dialogy nejsou úplným přepisem AudioRegionRecoveryWindow.
- Obecné formuláře exportu, syntetických hlaviček a některých diskových operací se stále liší od specializovaných WPF dialogů.
- macOS, ARM, všechny skutečné škály DPI, čtečka obrazovky a fyzický audio vstup nebyly při této revizi provozně ověřeny. Font fallback a nativní dialogy se na operačních systémech liší.

Tento seznam je součást výsledku auditu, nikoli potvrzení dokončené úplné migrace UI. Nové archivy v TestResults/WpfParity-2026-11-04/distribution jsou vývojové buildy této konkrétní opravy; starší buildy 2026-11-03 tyto změny neobsahují.
