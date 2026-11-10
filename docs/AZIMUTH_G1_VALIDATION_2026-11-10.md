# Relativní kvalita fyzických CMT hran — G1, 10. 11. 2026

Výchozí HEAD: `46d3fc470e4a39631a72bd1640eedc1bff072120`. Implementace i cílené buildy/testy byly po G0 samostatně navrženy a uživatelem schváleny. Zadání: `specification/MZTools_Codex_FINAL_AZIMUT_CMT_2026-11-10.md`. Bez commitu/pushe; předchozí změny Deep Analyze a uživatelské odstranění staršího zadání zůstaly zachovány.

## Skutečný tok a změna proti G0

`CapturePipeline.Offer` stále pouze kopíruje a frontuje nativní PCM. Archiv a analytická fronta zůstávají oddělené. `LiveSignalAnalyzer.Process` ukládá normalizovanou kopii do omezené historie. `AudioRecorderViewModel.RefreshAsync` volá `CreateSnapshot` na workeru; po opuštění zámku historie proběhne FFT, původní diagnostika a nová `HeadEdgeAnalysis`. Capture callback ani archivní writer nový algoritmus nevolají.

Původní `HeadAlignmentAnalyzer.Channel` používá společné P5/P95, interpolované 10–90% přechody a souhrnné mediány/MAD. Původní `HeadEdgeAdjustmentModel` porovnával aritmetický průměr mediánů Rise/Fall, bez identit skupin. Opačné změny pouze komentoval. Kopie obou přesných implementací se SHA-256 původních souborů jsou v `MZTools.Capture.Tests/Baseline`; identifikátory tříd jsou přejmenované pro souběžný T0 test. Historické testy modelu jsou výslovně pojmenované Baseline. Nové produkční kontrakty testuje `ContextualHeadEdgeTests`; původní `HeadAlignmentTests` zůstaly zachované.

Nová větev vede dočasný struct pro každý midpoint přechod: čas, fyzický směr, dobu hrany a zpětný pohyb. Oba sousední intervaly a lokální rozsah počítá na workeru. Doby se nepřepočítávají vůči uměle normalizovanému lokálnímu schodu. Symetricky interpolované kvantily chrání ekvivalenci `x → −x`. Počítají se úplné/neúplné, okrajové, nejednoznačné a odmítnuté přechody i všechny tři třídy rozlišení. Důvody odmítnutí a nejednoznačnost se mohou překrývat; `detected − boundary = complete + incomplete`, klasifikované skupiny plus ambiguous pokrývají vnitřní přechody.

Rodiny vysokých a nízkých intervalů se učí zvlášť. Jedna rodina vyžaduje P90/P10 < 1,25; dvě robustní rodiny alespoň osm intervalů každá, poměr center ≥ 1,45 a nepřekrývající se tolerance. Tolerance je maximum 1,5 vzorku, 12 % centra a 4 MAD; příliš široké rodiny jsou odmítnuté. Nejde o rozpoznávání loaderu ani certifikované fyzikální prahy. Maximálně osm skupin/kanál.

`Zero here` ukládá mediány skupin z nezávislých stabilních oken, identitu a tolerance rodin a pevné referenční váhy. Kvalifikace vyžaduje ≥ 3 okna a ≥ 0,5 s; historie má nejvýše 24 souhrnů. Předčasná nula zůstává provisional do nového výslovného nulování. Worker dostává neměnnou referenci přes `LiveSignalViewOptions.LeftEdgeReference/RightEdgeReference`; snapshot nese její ID. Okno se starou klasifikací nemůže aktualizovat novou referenci.

Porovnání používá pevné referenční počty v logaritmické doméně, s poloviční vahou Rise/Fall. Ztráta skupiny nikdy nepřerozdělí váhy. Rozpor skupin, nejednoznačnost, velká změna přijaté populace/lokálního rozsahu, výrazná změna úrovně, clipping, ringing, nevyřešené hrany a skok přes faktor 2 vedou na HOLD. Malá změna musí překročit konzervativní příspěvek `Ts/min(t0,t)`; není to dokázaná univerzální mez chyby. FFT ani poměr HF/LF neřídí tuto ručičku. Změny capture identity, formátu, kanálu, parametrů nebo kontinuity nulu ruší. Damping mění pouze kvalifikovanou prezentaci.

WPF a Avalonia používají stejný model a oddělené reference L/R. HOLD ponechá starou ručičku šedě a odstraní čerstvé procento; důvod zůstává viditelný. Podrobné počty a skupiny jsou v rozbalovací sekci. Původní A/B a stereo diagnostika zůstaly zachované. Jedna oprava smoke testu výslovně vybírá uložený WAV před navigací; předtím test závisel na náhodném automatickém výběru DataGridu. Produkční navigace se neměnila.

## Reprodukovatelné ověření

Před změnou: plná Capture sada **224/224 PASS**. TRX: `TestResults/Azimuth-2026-11-10/baseline/azimuth-baseline.trx`.

Příkazy (Linux/WSL má instalované SDK):

```sh
dotnet test MZTools.Capture.Tests/MZTools.Capture.Tests.csproj -m:1 -nr:false -p:MZToolsPublishLatest=false --logger "trx;LogFileName=capture-final.trx" --results-directory TestResults/Azimuth-2026-11-10/final
```

T0 ukládá pro stejná float32 PCM okna jejich SHA-256, frame bounds, čas, původní i nový stav/procento a původní počty/doby hran i nové počty odmítnutí. Původní algoritmus má jen souhrnné počty dokončených hran; nezpětně mu nevymýšlíme počty identit, které neměřil. JSON evidence se zapisuje do `TestResults`, nikoli do distribučních buildů.

T1: oba skutečné FLAC, šest úseků každého souboru, samostatně L/R při původních 44,1 kHz; začátek, dokumentovaný úsek 24/300 s, 25/50/75 % a konec. Jde o stratifikované výřezy, nikoli anotaci všech loaderů na celé kazetě. Spárovaný test přidává Butterworth LP 3 kHz stejného PCM. SHA-256 obou zdrojů před/po zůstává stejný. Souhrny a průběhy: `testAudioStarB03A.json`, `SonyHF90V2A.json`.

T2–T4: **135 kombinací** skutečného katalogu (ROM800, ROM700 PAL, IC 1:1–1:4, TC 1:1–1:3), Fs 22,05/44,1/48/88,2/96 kHz a LP 12/6/3 kHz. Interní mřížka 384 kHz, HP40, Butterworth LP 2. řádu, 129tap windowed-sinc antialias převzorkování. Shodná deterministická data pro původní i nový produkční model. Dalších **18 kombinací** používá skutečné bajty payloadu existujícího `Tc122.mzf`; soubor zůstává beze změny. TC 1:4 se nevymýšlí. Nejde o nový validátor dekodérů ani o zvýšení fyzického rozlišení FLAC.

T3: analytická 45µs RC hrana, 201 fází pro každé Fs. P95 absolutní chyby mediánu Rise v tomto testu: 22,05 kHz 61,13 %, 44,1 kHz 21,38 %, 48 kHz 21,09 %, 88,2 kHz 6,30 %, 96 kHz 6,75 %. Odlišná konstrukce proti externímu experimentu, proto čísla neslučujeme. Pod 2Ts je ResolutionLimited, 2–4Ts LowConfidence, od 4Ts pouze Candidate. Výsledek podporuje konzervativní kvalifikaci; 4Ts není fyzikální certifikát ADC. U 88,2 kHz se některé 45µs hrany dle fáze pohybují přes hranici kategorií. 192 kHz není požadováno.

T4/T5: kontrolované skutečné zkrácení rozlišených hran, rozšíření hran, digitální a modelované analogové omezení úrovně, šum, lokální dropout, ztráta rozlišení, opačné změny Rise/Fall, inverze, rozdílný gain, změny poměru bitů a ztráta referenčních skupin. Reference se automaticky nemění. Pozitivní kontrola 140 → 95 µs při 96 kHz dává přibližně +47 % také přes skutečný Avalonia recorder/worker; samotný všudypřítomný HOLD se nepovažuje za splnění testu.

T7: původní Capture byte-exact, hash, fronty, discontinuity a finalizace testy; navíc packet partition, zastarání, pause/reset, omezení historie a osmi skupin. WPF syntetický smoke pokrývá 114 kontrol včetně malého okna, měřítka 150 %, archivu a záznamového reportu; hardware se neotevírá. Avalonia má nový integrační test skutečné cesty referencí pro L/R, pozitivní kontrolu, sbalenou diagnostiku a vypnutí měření.

## Limity a přesný stav tvrzení

- **T6 NOT VERIFIED:** nebyly dodány opakované záznamy stejné pasáže se známým mechanickým nastavením a návratem hlavy. Funkce je relativní kvalita fyzických hran / head alignment assistance, ne změřený azimut.
- **Přesný externí případ Sony +34 % NOT REPRODUCED:** externí validační skript a přesné parametry/okna nejsou v repo. Proveden je samostatně definovaný párový LP3k test více úseků stejného skutečného Sony souboru; jeho výsledek se nesmí vydávat za reprodukci neznámého externího postupu.
- Před-ADC clipping není vždy rozlišitelný od jiné analogové odezvy. Kontrola rozsahu odhalí testovaný pokles úrovně; neprokazuje obecnou detekci každého analogového limiteru. Samotná stabilita/počet kandidátů není důkaz správného směru mechanické korekce.
- Mnoho reálných/rychlých úseků zůstává HOLD. Chybějící důkaz se nenahrazuje starým agregátním procentem ani spektrálním skóre. Žádná plošná deklarace funkčnosti všech ADC/kazet.
- Dočasný pooled buffer hran má horní mez 32768 struktur; kanály se zpracovávají postupně. V UI/reference/historii zůstávají jen malé souhrny, nikoli všechny hrany. Callback, archiv a detektory MZ/IC/TC se neměnily. Měření výkonu je v `worker-budget.json`; jde o konkrétní měření na vývojovém PC, nikoli hardware latency SLA.

## Opravy nalezené během ověřování

První cílený běh měl jednu chybu očekávání konfliktního scénáře: změnil i midpoint intervaly mimo zamknutou rodinu, proto správně vracel ContentChanged. Test byl změněn na fyzicky opačné změny hran, které zachovají přiřazení, a ověřuje Conflict. Nebyl zmírněn požadavek na HOLD.

První plná sada odhalila referenční rovnost nově přidaných kolekcí v record snapshotu. Doplněna strukturální hodnotová rovnost skupin/oken; původní packet-partition assertion zůstal zachován. Avalonia integrační test odhalil souběh při vypnutí měření: pozdní worker mohl oživit staré procento. Setter nyní okamžitě ruší referenci a stav a UpdateEdges ignoruje výsledek při vypnuté analýze. Sampling allowance platí i pro drobná nenulová procenta; negativní testy odmítají jakékoli kladné procento nad numerickou toleranci 1e-6, nikoli jen výrazný pohyb ručičky.

## Konečný výsledek

- Plná Capture sada po změně: **256/256 PASS**, bez skipů (`final/capture-final.trx`).
- Avalonia recorder: **11/11 PASS na Windows i Linuxu**, včetně nové end-to-end reference; TRX v `TestResults/Azimuth-2026-11-10/desktop-*-final.trx`.
- WPF: **114/114 PASS**, kompletní log `final/wpf-smoke.log`, bez hardware vstupu.
- 135 katalogových kombinací a 24 FLAC kanálových úseků: **0 falešných kladných oken** při definované degradaci; test odmítá kladná procenta > 1e-6. Dalších 18 kombinací se skutečnými programovými bajty rovněž PASS. To platí pro tento konkrétní testovací korpus, nikoli pro všechny možné analogové vstupy.
- Před nulováním bylo ReadyToZero v 32/135 katalogových kombinací a 15/24 FLAC kanálových úseků (AudioStar 11/12, Sony 4/12). Ostatní umožňují nanejvýš provisional zero; nelze je započítat jako kvalifikované. Konzervativní HOLD je záměrný a otevřeně reportovaný.

Výkon debug/WSL na tomto PC (8192 stereo, 96 kHz): původní měření medián 6.674 ms, nové 7.258 ms; přírůstek 0.584 ms. Celá testovací cesta včetně balení PCM, historie, FFT, stereo diagnostiky a modelu medián 11.535 ms, maximum 13.508 ms. Alokace celé cesty přes 24 oken 19,016,384 B jsou kumulativní GC provoz, nikoli retained RAM; nelze je srovnávat s limitem pracovního bufferu. Cíl přírůstku pod 10 ms v tomto měření splněn, nikoli zaručen všem PC.


Distribuce byly znovu sestaveny pomocí `python tools/build_desktop.py --rid win-x64 --rid linux-x64`. Aktuální spustitelné balíky jsou pouze `bin/win-x64/MZTools.Desktop.exe` a `bin/linux-x64/MZTools.Desktop`. Oba prošly skutečným `--validate-runtime`: .NET 10.0.11, libsndfile 1.2.2, captureStarted=false. V těchto dvou balících nejsou testy, zdroje, PDB, TRX ani FLAC fixture; inventář `final/distribution.json`. Linux balík má stávající minimum glibc 2.38. Staré jiné cesty v bin nejsou tímto úkolem přepisované ani považované za aktuální build.
