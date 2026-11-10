# MZTools — FINÁLNÍ zadání pro Codex: relativní seřízení azimutu CMT 1:1–1:4

**Datum:** 10. 11. 2026  
**Repozitář:** https://github.com/bales0/MZTools  
**Stav zadání:** **G0 = READ-ONLY AUDIT A KONKRÉTNÍ IMPLEMENTAČNÍ NÁVRH; BEZ SOUHLASU NIC NEIMPLEMENTOVAT.**  
**Rozhodnutí uživatele:** 192kHz ADC není běžný; **nesmí být podmínkou fungování nového algoritmu**. Podpora vyšších frekvencí může zůstat volitelná.  
**Rozsah:** `Record Audio / Live Tape Analysis → Head alignment`, společná Capture vrstva, WPF a Avalonia UI. Bez zásahu do archivovaných PCM/WAV a bez nevyžádaných změn Visual/Deep Analyze.

## 0. Zásadní pravidla vykonání

1. **NEMĚŇ soubory repozitáře, testy, konfiguraci, build ani historii bez samostatného výslovného souhlasu uživatele.** Nyní pouze G0: přečti aktuální HEAD, potvrď skutečné chování souborů/metod, proveď analýzu návrhu a vrať plán G1. Nic necommituj, nepushuj; nedělej automatické opravy „po cestě“.
2. Přesně odlišuj **závěry z aktuálního zdrojového kódu**, **externí numerické experimenty** a **neověřenou schopnost přesně odhadnout mechanický azimut**. Historická měření a prahy nejsou normativní hardware certifikací.
3. Stávající detekce MZ/IC/TC, analogová signal path, recorder a jeho raw archiv musí fungovat beze změny. Ztrátové nebo zrychlené cesty mimo zadání neupravovat.
4. Po G0 výslovně požádej o schválení **G1 implementace**. Buildy a regresní testy až po samostatném schválení. Toto zadání **není** povolení provádět G1/G2.
5. Pokud se odstavce zadání liší od aktuálního HEAD, nejprve zdokumentuj skutečný stav a navrhni úpravu zadání, nikdy předpoklad potichu neprosazuj.

## 1. Finální cíl a definice měřené veličiny

Uživatel při běžném **živém přehrávání skutečných CMT programů** (SHARP MZ ROM/InterCopy/TurboCopy, různé rychlosti 1:1, 1:2, 1:3, 1:4) pomalu mechanicky nastavuje azimut čtecí hlavy. Data programu se průběžně mění: SHORT/LONG intervaly, jejich pořadí, směr hran a amplitudový kontext. **Ručička nesmí pokládat rozdíl bitové posloupnosti za zlepšení či nestabilitu hlavy.**

- Měříme **dobu fyzického přechodu v PCM 10–90 % nebo 90–10 %** (µs), nikoli absolutní mechanický úhel, nikoli V/µs a nikoli pouze amplitudu.
- Primární výstup jsou relativní změny vůči **ručně uloženému `Zero here`**. Kratší odpovídající hrana = ostřejší přechod ve sledované analogové cestě; **není to automatická záruka správného mechanického azimutu**.
- Důležitější než častý pohyb ručičky je **nulový počet přesvědčivých falešných zlepšení** v testech, kde je přenos prokazatelně pouze degradován, nebo kde se změní jen data programu.
- Výsledek *nejisté / nelze měřit* je plnohodnotný korektní stav. Nikdy nevyplňuj neznámé údaje interpolovaným procentem.
- L a R zůstávají dva **samostatné** ukazatele fyzických stop; časový posun L/R a Average nemají být skrytě aplikovány na jednotlivé azimutové ručičky.

## 2. Závěrečná experimentální evidence (vně repozitáře)

### 2.1 Materiály

Dvě reálné stereo FLAC nahrávky:

- `testAudioStarB03A.flac`: ~5 min 14 s, 44,1 kHz, 16-bit, stereo.
- `SonyHF90V2A.flac`: ~46 min 37 s, 44,1 kHz, 16-bit, stereo.

Syntetické referenční běhy používají skutečné **zapisovací intervaly InterCopy 10.2** z `src/MZTools.Core/Media/PulseReferenceCatalog.cs` (HIGH/LOW v µs):

| Profil | SHORT HIGH | SHORT LOW | LONG HIGH | LONG LOW |
|---|---:|---:|---:|---:|
| IC 1:1 | 234.573 | 263.894 | 469.145 | 494.802 |
| IC 1:2 | 113.621 | 139.278 | 234.573 | 260.229 |
| IC 1:3 | 87.965 | 124.617 | 175.930 | 223.577 |
| IC 1:4 | 76.969 | 117.286 | 157.604 | 179.595 |

**Pozor:** jde o IC, nikoli univerzální dělení ROM pulzů 1/n; ROM, MZ700 a TC mají vlastní časování. Tyto **writer** reference nepoužívej jako pevné očekávané doby *analogových náběhů* nebo jako absolutní normu kvality magnetofonu.

### 2.2 Nově reprodukovaný test

Samostatný soubor `mztools_final_azimuth_validation_20261110.py` (mimo zdrojový projekt):

- Zpracovává identická syntetická CMT data podle uvedených IC časů, numericky generovaná na 384 kHz (**jen interní výpočetní mřížka, NE požadavek na ADC**).
- Modeluje AC vazbu vysokou propustí 40 Hz, dolní propustí 2. řádu na **12, 6 nebo 3 kHz**, a korektně převzorkuje do 44,1 / 48 / 96 kHz; nikdy netvrdí, že převzorkování skutečného 44,1kHz souboru zvýší fyzické rozlišení.
- Přibližuje část `HeadAlignmentAnalyzer.Channel()`: společné P5/P95 v okně, 10–90 % s interpolací, medián, MAD 25 %, minimálně osm hran Rise/Fall a filtr `min(čas Rise/Fall) >= 2/fs`. **Neobsahuje** kompletní kvalifikaci produkčního modelu (backtracking, clipping, overshoot, časovou historii a kontext `Zero here`).
- Vyhodnocuje neprokládaná okna 8192 vzorků. Jde o test kvalifikace měření, **nikoli o měření fyzicky nastaveného azimutu**.

Vybrané výsledky:

| IC profil / Fs | Mezní pásmo modelu | Medián Rise | Okna splňující jen základní kritéria |
|---|---:|---:|---:|
| 1:4 / 44,1 kHz | 12 kHz | 39,8 µs | 0 % |
| 1:4 / 44,1 kHz | 6 kHz | 66,1 µs | 100 % |
| 1:4 / 96 kHz | 12 kHz | 34,6 µs | 100 % |
| 1:4 / 96 kHz | 3 kHz | 288,2 µs | 0 % (vysoký MAD) |

**Klíčový paradox:** špatný úzkopásmový analogový přenos může vytvořit delší hrany, které splní dvouvzorkový limit, zatímco ostrý a potenciálně lepší přenos ne. Samotný příznak „platné okno“ proto **nesmí znamenat kvalitnější azimut**. V režimu nízkého vzorkování by ručička jinak mohla ztratit kvalifikaci právě při zlepšení hlavy.

Test izolované RC hrany (201 fází vzorkovací mřížky, lineární interpolace) ukázal pro 45µs hranu 95. percentil absolutní relativní chyby přibližně **26,7 % při 44,1 kHz**, **21,1 % při 48 kHz**, **6,75 % při 96 kHz**. To je **modelová chyba vzorkování**, nikoliv záruka pro skutečný magnetofon.

Krátký opakovaný test původních 44,1kHz FLAC bez umělého převzorkování:

| Zdroj, úsek | Kanál | Medián Rise | Základně kvalifikovaná okna |
|---|---|---:|---:|
| AudioStar 24–36 s | L | 292,3 µs | 90,5 % |
| AudioStar 24–36 s | R | 285,2 µs | 96,8 % |
| Sony HF90 300–312 s | L | 106,9 µs | 96,8 % |
| Sony HF90 300–312 s | R | 105,3 µs | 100 % |

Závěr: **44,1/48 kHz neodmítat paušálně**, pokud konkrétní signál poskytuje rozlišitelné hrany; u reálně rychlého 1:4 však z uživatelského testování víme, že 44,1 kHz nestačí pro spolehlivou optimalizaci. **96 kHz je vítaný dostupný režim, nikoli bezpodmínečný standard; 192 kHz podporovat případně volitelně.** Použitelnost rozhoduje skutečný časový tvar, kvalita ADC a množství kvalifikovaných hran, nikoli číslo v nabídce.

### 2.3 Negativní výsledky předchozích simulací — nezamlčet

- Pouhá klasifikace `Rise/Fall × předchozí/následující interval` na reálném Sony HF90 při dodatečné LP 3 kHz **ukázala falešné zlepšení kolem +34 %**, ačkoliv došlo k zúžení pásma. Pravděpodobným mechanismem je změna detekovaných/přijatých hran a přeznačení skupin. **Takový experimentální algoritmus není vhodný bez dalších kontrol.**
- 20–80 % se v testovaných reálných záznamech neosvědčilo stabilněji než 10–90 %; při některých úsecích bylo výrazně horší. **10–90 % zůstává základ**. Porovnání nesmí měnit výběr hran bez zaznamenání rozdílu.
- Spektrální poměr HF/LF a datově normalizované FFT pomáhají při známém stejném obsahu, ale šum, clipping a při 1:4 i selhání detekce přechodů mohou vytvořit falešný pozitivní výsledek. **Spektrální ukazatel nesmí sám řídit živou ručičku a nemá být povinným potvrzením u jiného obsahu programu.**
- Podíl `Unstable` v předchozích experimentálních skriptech kolísal s použitým oknem a pravidly výběru hran. **Neslučuj čísla z různých skriptů jako jeden validovaný výkon produkční aplikace.**
- U simulovaného zhoršení se periodicky mohou některé jiné typy hran zkrátit — individuální čas 10–90 % není dokonalou monotónní veličinou šířky pásma.

## 3. Povinný read-only audit G0 — konkrétní implementace

Ověř skutečný HEAD, soubory a metody, uveď aktuální commit SHA a přesný tok dat:

1. [`MZTools.Capture/HeadAlignmentAnalyzer.cs`](https://github.com/bales0/MZTools/blob/main/MZTools.Capture/HeadAlignmentAnalyzer.cs): per-window P5/P95, threshold crossing + interpolace, Rise/Fall, `EdgeEvidence`, HF % a koherence. Posuď chyby od lokálního kolísání amplitudy a vytváření dvou populací hran.
2. [`MZTools.Capture/HeadEdgeAdjustmentModel.cs`](https://github.com/bales0/MZTools/blob/main/MZTools.Capture/HeadEdgeAdjustmentModel.cs): `ZeroHere`, mediány, MAD, 2-vzorkový limit, pravidla pro `Unstable` / `ContentChanged` / `ResolutionLimited`, temporal-history, overlapped/stale okna, damping. Posuď, které stavy se pletou se změnou přehrávaného programu.
3. `MZTools.Capture/HeadAlignmentModels.cs`, `LiveSignalAnalyzer.cs`, `LivePulseAnalyzer.cs` (existující analýza přechodů; sdílení bez duplicity a bez práce v ISR).
4. `src/MZTools.Desktop/ViewModels/RecorderModel.cs` a `src/MZTools.Desktop/Views/RecorderWindow.axaml` (Avalonia), `AudioRecorderWindow.cs` a `HeadEdgeAdjustmentControl.cs` (WPF).
5. `MZTools.Capture.Tests/HeadAlignmentTests.cs`, `HeadEdgeAdjustmentTests.cs`, existující Capture / UI regresní testy.
6. [`src/MZTools.Core/Media/PulseReferenceCatalog.cs`](https://github.com/bales0/MZTools/blob/main/src/MZTools.Core/Media/PulseReferenceCatalog.cs), zdroj původu pulzních profilů; **zde nepřidávej „normální analogovou strmost“ z jediné kazety**.
7. Dokumentace recorderu `docs/LIVE_TAPE_RECORDER_GUIDE.md`: dnes záznam pro offline analyzátory podporuje do 96 kHz; nativní Exclusive umí zachovat driver-accepted rate bez převzorkování; některé vyšší rate/formáty nemusí být zpětně offline kompatibilní. **Nečinit rozšíření 192 kHz podmínkou hlavního úkolu.**

## 4. Navržený implementovatelný algoritmus G1 (až po schválení)

### 4.1 Primární veličina: samostatné kvalifikované hrany

- Zachovat dobu přechodu **10–90 % / 90–10 %** a existující ošetření clippingu, hluku, překmitů, nespojitostí, neplatných framů a příliš krátkých oken.
- Z každého detekovaného přechodu vést omezené množství metadat: čas, **směr v aktuální PCM polaritě**, interpolovaná doba hrany, úroveň/rozsah, interval **před** přechodem a interval **za** přechodem. To jsou sousední midpoint-to-midpoint intervaly signálu po AC vazbě, **ne domnělé absolutní magnetické půlpulzy**.
- Podle kvality lokálního signálu a dostupného Fs určovat, zda lze konkrétní hranu měřit; uživatelský program může mít v jednom úseku různě rozlišené hrany. Zaveď nejméně **tři úrovně jistoty**: `<2` vzorkovací periody = `ResolutionLimited`, přibližně `2–4` periody = `LowConfidence`, dostatečně rozlišené = `Candidate`. Přesné hranice musí být odůvodněny testem na 44,1/48/88,2/96 kHz, nikoliv slepým zvednutím prahu. Neoznačuj „nejostřejší, ale nepopsanou“ hranu za zhoršení.
- Kontrola propadů a lokálních rozdílů amplitudy; nedělat bez důkazů z každé hrany uměle normalizovaný ideální schod. P5/P95 z celého okna není neměnná fyzikální reference; zaznamenej vliv výpadků a AC driftu.
- Tolerance musí fungovat u 1:1–1:4 bez nutnosti vědět, zda jde o ROM, IC, TC, CP/M nebo Spectrum. Pro testy známe reference, pro live průměrnou dobu přechodu **nezakóduj**.

### 4.2 Adaptivní skupiny: směr × interval před × interval za

- Z aktuální pásky naučit **robustní rodiny sousedních intervalů**, typicky SHORT/LONG, ale pouze jsou-li v datech skutečně rozlišitelné. Nevnucovat vždy 8 skupin; pokud 2-klastrová hypotéza nesedí, raději menší počet nebo hold.
- Zahrnout **směr hrany + oba sousední intervaly**. Nedefinovat, že sestup patří jednomu „pulsu“ a vzestup následujícímu; fyzikální hrana je hranicí sousedních intervalů a AC cesta je asymetrická.
- Rozlišit dvě věci: (a) délek intervalů odhadnutých z měřeného signálu, (b) skutečné identity hran. Obojí se může při změně analogové odezvy měnit. **Zamknout klasifikaci a přiřazení ke skupinám vůči `Zero here`; pokud se nepodaří stabilně přiřadit, HOLD.** Neprovádět změnu počtu skupin ani tiché nové nuly.
- Výrazně penalizovat měnící se počet přijatých hran / obtížně rozhodnutelné hranice mezi skupinami / ztrátu přechodů. Bez důkazu identity **nepřeskupovat při zlepšení/zhoršení** a nepřičítat úspěch nové skupině.
- Ověřit polaritní ekvivalenci `x → -x`: L/R nepřepínat, Rise/Fall se prohodí, ale souhrnná interpretace je ekvivalentní. Trvale zachovat oddělenou diagnostiku náběh/sestup.

### 4.3 Ruční `Zero here` a relativní trend

- `Zero here` ukládá **stálou**, případně po ustálení získanou referenci pro každou kvalifikovanou skupinu: medián doby 10–90, robustní rozptyl, počet, kontext, kvalitu a identitu capture. Možnost ruční nuly při nestabilní referenci jen s jasným varováním; žádná tichá automatická rekalibrace.
- V každém dalším okně porovnej jen **odpovídající kvalifikované skupiny** se stejnými referenčními skupinami. Kladné relativní změny například `100*(t0/t-1)` vyjadřují kratší hranu; agregaci z různých skupin udělej v robustní log-doméně nebo ekvivalentně, s **pevnými referenčními váhami**, nikoli váhami podle aktuálního poměru bitů.
- Podmínit aktualizaci minimálním počtem hran a několika **nezávislými** časovými okny, ne stovkami vzájemně překrytých téměř stejných vzorků. Samostatně sledovat Rise a Fall; **rozpor mezi skupinami, nepřiměřeně velký skok, podezření na ringing nebo clipping ⇒ HOLD**.
- Nezhoršit reakci změnou bitového obsahu. Ale nezlepšovat výsledky umělým ignorováním nekvalitních částí; vyřazené hrany i důvody vždy diagnosticky spočítat.
- Damping aplikovat až na **kvalifikovaný** trend; po nekvalifikovaném okně podržet poslední platnou ručičku a viditelně označit **stale / insufficient / resolution / conflict**. Nelze použít starou hodnotu, jako by šlo o nový aktuální důkaz.
- Při změně kanálu, přerušení capture, vstupního zařízení, skutečného Fs, formátu či analytických parametrů zneplatnit nebo znovu explicitně potvrdit referenci. Nikdy neměnit uložený WAV/PCM.

### 4.4 FFT a širokopásmový šum — pouze pomocné režimy

- Běžný CMT program **nemá stacionární spektrum**: poměr SHORT/LONG se mění dle dat. Samotná HF energie ani poměr HF/LF **nejsou korektní live azimutovou ručičkou**; při některých zde ověřených simulacích ukazovaly opačný směr, šum a clipping falešné zlepšení.
- Pro běžné `Live Adjust` lze FFT ukázat jako **diagnostiku** (např. zda je dostupný smysluplný HF obsah, jaká je úroveň šumu), nikoli jako univerzální gate pozitivního trendu. Nezavírat ručičku jen proto, že aktuální bitový úsek neobsahuje vysokofrekvenční složky.
- Pro **opakovaně přehrávanou stejnou pasáž** (`A/B Reference`) je možné navíc spektrální porovnání po korekci rychlosti, časovém zarovnání a kontrole obsahu. Datově normalizované spektrum dle rekonstruovaných přechodů je experimentální; při ztrátě/množení přechodů se musí označit `Not assessable`.
- **Šumový test** lze nabídnout jen jako samostatnou diagnostickou možnost při známé referenční *nahrávce šumu na pásce*. Šum generovaný až v PC nebo za hlavou neodhalí mechanický azimut. Pro běžná programová data nic takového nevyžadovat.

### 4.5 Vzorkovací frekvence: bez závislosti na 192 kHz

- Zachovat běžný recorder a nativní PCM vstup, bez automatického převzorkování archivu.
- 44,1/48 kHz: **měřit, pokud to skutečná délka hran umožňuje**, ale u rychlého 1:4 nepředstírat přesnou optimalizaci; pokud aktuální data kvalitní výsledek neumožňují, zobrazit `ResolutionLimited`/`LowConfidence` a doporučit 96 kHz. Nezvyšovat sampling „softwarovým upsample“ jako domnělý důkaz.
- 88,2/96 kHz: podporovat jako rozumnou dostupnou volbu; ani 96 kHz není automatická záruka pro všechny hrany.
- 192 kHz: volitelné rozšíření a samostatná kompatibilita offline formátů, **nikoli P0 nebo povinná závislost** nové ručičky. Nevyžadovat nákup 192kHz ADC.
- Bez tvrdého profilového přepínání při 1:1–1:4: primárně rozhodovat dle **počtu vzorků měřené hrany a její kvality**, rychlost/profil může sloužit jen pro informativní upozornění a testovací scénáře.

### 4.6 Výkon, UI, kompatibilita

- Capture/preview kritická cesta má být bounded, bez dlouhých synchronních dekódovacích průchodů, neomezených front, per-sample heap alokací a zatížení ISR. Historie i počty skupin musí mít horní mez. Otestovat CPU/RAM/latenci při dostupných sample rates a obou kanálech.
- **WPF i Avalonia** musí mít ekvivalentní chování ručiček, stavů a `Zero here`. Hlavní obrazovku nepřetížit diagnostikou; podrobnosti (counts, per-group times, MAD, confidence, důvody hold) do rozbalovací sekce.
- Archive writer RAW/WAV, jejich nativní bytes, hash, finalizace a cancel zůstávají nezměněny. Jde jen o **odvozenou live analytickou větev**. Výsledky z Deep Analyze a posuny kanálů se do Live nezavedou skrytě.

## 5. Povinný testovací plán pro G1 (po schválení)

**T0 — baseline a porovnání:** Přesně zaznamenat původní chování `HeadAlignmentAnalyzer` a `HeadEdgeAdjustmentModel` (ne náhradní zjednodušený simulátor) a porovnat s novým návrhem na stejných PCM oknech/hranách. Evidence o skutečném počtu přijatých/vyřazených přechodů; stavové přechody a ručičky ve stejném čase.

**T1 — real FLAC:** Oba poskytnuté stereo soubory (AudioStar, Sony HF90), několik různých datových, leader, synchronizačních a případně klidových úseků. Samostatné L/R, amplituda, šum, dropout, clipping, polarity. Nevyužívat jeden 12sekundový výřez jako validaci celé kazety; neposílat velké binární soubory do Git.

**T2 — skutečné reference:** SHARP MZ ROM 1:1, InterCopy 1:1–1:4, TC podle jeho **skutečných** referencí, MZ700 podle potřeby. Kromě pravidelných leader tónů testovat reálné a nezávislé náhodné programové bloky, opakované bloky, změnu poměru bitů, přerušení a restart loaderu.

**T3 — ADC bez 192kHz předpokladu:** 22,05 / 44,1 / 48 / 88,2 / 96 kHz; 192 kHz jen volitelně, pokud zařízení umí. Simulovat z dostatečně jemné analogové numerické reference a zahrnout různé časové fáze vzorkování. **Nepovažovat interpolovaný 44,1kHz FLAC za skutečný nový 96kHz záznam.** Ověřit paradox „ostřejší hrana kvůli vzorkování ztratí status usable“, který se už v experimentu projevil.

**T4 — analogové poruchy:** kontrolované rozšíření i zúžení pásma, AC high-pass drift, různé délky sousedních intervalů, asymetrické Rise/Fall, šum, ringing, lokální dropouts, kolísání úrovně, clipping před ADC i v ADC, pomalé změny rychlosti. Při degradaci nesmí systém **bez přezkumu přijmout falešné kladné zlepšení**; správné `HOLD` je povolené, opakované validní zlepšení má být rozpoznatelné tam, kde je signál dostatečně měřitelný.

**T5 — inverze a skupiny:** x → −x, změna poměru SHORT/LONG beze změny fyzické odezvy, změna pohybu prahů, ztráta části skupin, jiný segment po `Zero here`; reference a skupiny se nesmí tiše měnit. Změna dat nemá vyvolat mechanické tvrzení.

**T6 — skutečná mechanická validace (nutná pro tvrzení o azimutu):** několik opakovaných záznamů **stejné pasáže** při známém měněném azimutu jedné čtecí hlavy, včetně návratu na původní polohu. Bez tohoto testu smí být funkce označena pouze jako **relative tape signal edge quality / head alignment assistance**, ne „změřený azimut“.

**T7 — real-time + UI + regression:** žádné dropy archivu, stejné sample rate/channel count, bounded RAM a CPU, `Pause/Stop`, změna zdroje, reset reference, A/B, WPF/Avalonia, kompatibilita původních reportů a recorderu. Výstup pass/fail/skip s důvody; **nezamlčovat negativní testy**.

## 6. Akceptační kritéria (G1)

1. Žádné **sugestivní kladné procento** z pouhé změny poměru CMT datových symbolů. Pokud nejsou srovnatelné hrany, místo procenta je `Hold`/`Uncertain` s uvedením důvodu.
2. Žádné přeznačení referenčních skupin při změně analogové odezvy bez detekce a re-kvalifikace. Zvláště **nesmí projít** známý regresní příklad Sony HF90 s falešným +34 % při LP 3 kHz.
3. Tvrdé chyby (zdroj diskontinuita, digitální clipping, extrémní šum, nezřetelný přechod) nevedou k potvrzení zlepšení. Podezření na před-ADC clipping nemůže být vždy jednoznačně odhaleno; algoritmus musí otevřeně přiznávat tuto mez.
4. Polaritní ekvivalence, oddělené Rise/Fall a L/R, stabilní `Zero here`, kontrola měřitelnosti podle vzorků na **hraně**, ne pouze deklarace Fs.
5. 44,1/48 kHz a 96 kHz funkční při dostatečně rozlišeném signálu; při 1:4 na 44,1 kHz **nevydávat za jisté** výsledky pod fyzickým rozlišením. **192 kHz není podmínkou úspěchu.**
6. Reálné programové streamy 1:1–1:4: žádný automatický předpoklad rovnoměrného spektra, dostupnosti leaderu nebo opakování stejné pasáže.
7. Žádná regrese nahrávání, kompatibility archivu a WPF/Avalonia parita.
8. Rozlišovat **zlepšení statistické stability** od **důkazu správného směru ručičky**. Zlepšení „platných oken“ samo **není** kritérium úspěchu.

## 7. Povinný výstup Codexu teď — G0, read-only

Předlož:

1. HEAD SHA, skutečné soubory/metody a přesný datový tok PCM → kvalifikace → skupiny → reference → ručičky; potvrzení či opravy předpokladů dokumentu.
2. Konkrétní popis, proč současný algoritmus vyhlašuje `Unstable`, `ContentChanged`, `ResolutionLimited`, a co se stane při rychlé ostré hraně na 44,1 kHz.
3. Minimální návrh G1: datové struktury, kvalifikační pravidla, zamčení kategorií, výpočet agregované relativní změny, rekalibrace/hold, status messages; porovnej s jednodušším fallbackem „oddělené Rise/Fall bez kontextových skupin“.
4. Odděl „musí být v první implementaci“ od experimentální spektrální kontroly a volitelné podpory 192 kHz. Nepřekonstruuj UI; zachovej obě verze.
5. Reprodukovatelný plán T0–T7 s metrikami, přibližnou RAM/CPU náročností, riziky a přesnými dotčenými soubory.
6. Explicitní seznam otevřených rozhodnutí (např. kvalifikační interval 2–4 periody, počet/velikost skupin, přísnost a doba `Zero here`).
7. **Požádej uživatele o výslovný souhlas s G1. Neprováděj žádné změny ani buildy/testy do schválení.**

## 8. Přílohy (mimo repo, z předchozích a závěrečných simulací)

- `mztools_final_azimuth_validation_20261110.py` — zjednodušený deterministický test, nikoli produkční implementace.
- `MZTools_final_IC_sampling_stress_2026-11-10.csv` — IC 1:1–1:4 × 44,1/48/96 kHz × modelovaný cutoff.
- `MZTools_final_real_FLAC_edge_check_2026-11-10.csv` — kontrolní reálné FLAC úseky.
- `MZTools_final_sampling_phase_2026-11-10.csv` — chyba interpolace známé RC hrany.
- `MZTools_final_sampling_window_qualification.png`, `MZTools_final_sampling_phase_chart.png` — diagnostické grafy.
- Dřívější `MZTools_controlled_DSP_paired_summary.csv` a spektrální stres testy jsou **výzkumné podklady**; v G0 nepřebírej z jejich zjednodušených modelů produkční prahy bez nového ověření.

**Definitivní vývojové rozhodnutí:** začít spolehlivou *kvalifikací a relativním porovnáním hran* na běžném dostupném vzorkování, ne „povinným 192 kHz“ a ne spektrálním skóre z libovolného programu. Bez fyzického A/B záznamu známé změny azimutu nikdy netvrdit, že byl mechanický úhel optimalizován s ověřenou přesností.
