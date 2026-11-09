# MZTools – zadání pro Codex: Deep Analyze – kanály, časový posun a skutečný vstup analýzy

**Datum:** 9. 11. 2026  
**Repozitář:** https://github.com/bales0/MZTools  
**Referenční HEAD z předávacího protokolu:** `9b471ddcbf0508d80bba64303de363243ac86601` (nejdříve znovu ověřit)  
**Priorita:** P1 – funkční chování a UI audio analýzy

## 0. Závazný postup G0 – audit → návrh → schválení → implementace

**V prvním kroku prováděj pouze read-only audit.** Bez předchozího výslovného schválení uživatele **neměň zdrojové kódy, testy, konfigurace, projektové soubory ani repozitář; nespouštěj buildy ani testy; nevytvářej commity.** Nejprve:

1. Ověř aktuální HEAD a relevantní existující implementaci ve WPF, Avalonia Desktop a Core.
2. Dolož datový tok od ovládacích prvků přes ViewModel/session až po výpočet a výsledky analýzy.
3. Sepiš přesné plánované změny po souborech, dopady na stávající funkce, návrh testů a rizika.
4. Vyžádej **výslovný souhlas** s implementací. Teprve potom implementuj přesně schválený rozsah; build/test až po odpovídajícím schválení.

Zachovej stabilní Core algoritmy a referenční WPF, pokud není nezbytná a výslovně schválená oprava. Rozlišuj „ověřeno čtením kódu“, „doloženo existujícím testem“ a „neověřeno v běhu“.

## 1. Cíl a požadované rozdělení UI

Uživatel požaduje, aby **Deep Analyze** obsahovalo volbu skutečného zdrojového signálu **L / R / Average (L+R) / Both (Overlay)** a nastavování **časového posunu pravého kanálu vůči levému (R relative to L)**. Spuštěná analýza musí **opravdu používat zvolenou konfiguraci**, nikoli měnit pouze vykreslení waveformu.

V **běžné (Normal) analýze** stačí zjednodušená vizualizace **Overlay / Mono**. Pokročilé volby L, R, Average a posun R vůči L patří do Deep Analyze a nesmí zbytečně zahlcovat Normal UI.

**Pozor na rozlišení významu „Overlay“:** je to současné zobrazení obou kanálů, **nikoli automaticky čtvrtý matematicky definovaný mono signál**. Codex musí při auditu doložit, jak dnes funguje režim Both/stereo, a pro Deep Analyze navrhnout explicitní a konzistentní chování výpočtu při volbě Both/Overlay (např. oddělená analýza obou fyzických kanálů se zřetelným výsledkem pro každý). Nesmí skrytě ztotožnit Overlay se součtem nebo průměrem.

## 2. Funkční požadavky – Deep Analyze

### 2.1 Výběr kanálu / režimu

- **Left (L):** waveform, pulzy, histogramy, detekce/dekódování, kandidáti a výsledky vycházejí z levého kanálu.
- **Right (R):** totéž pouze z pravého kanálu.
- **Average (L+R)/2:** vytvoř analytický mono signál z časově zarovnaných kanálů a z **tohoto skutečného signálu** proveď všechny příslušné kroky analýzy, nejen vizualizaci. Základní definice: `(L[n] + R_aligned[n]) / 2` s bezpečnou celočíselnou aritmetikou a správně definovanými krajními vzorky.
- **Both / Overlay:** oba kanály zobraz současně; analytické chování musí být jednoznačné (preferovaně nezávislá analýza L a R, výsledky s označením zdroje). **Nesmí jít o falešné „analyzování overlay“ bez definice vstupního signálu.** Konečné chování navrhni po G0 auditu ke schválení.
- U mono zdrojů vhodně znepřístupni neaplikovatelné režimy / nastav Mono a zachovej plnou funkčnost analýzy.

### 2.2 Posun R vůči L

- Ovládání **R offset relative to L** umísti přímo v **Deep Analyze** vedle nastavení kanálu/režimu.
- Zajisti přesně popsanou jednotku (vzorky a případně přepočet na ms podle sample rate), znaménkovou konvenci (+/− znamená co), rozsah, krok, výchozí hodnotu `0` a reset.
- Posun uplatňuj **před** smícháním Average a v odpovídajícím náhledu Overlay; při R-only režimu výslovně definuj, zda offset ovlivňuje analytické časování, nebo jen synchronizaci vůči L. Nesmí vznikat nepozorované posuny výsledků či indexů.
- Nepoužívej cyklické přetáčení konců signálu. Ošetři neplatné/okrajové vzorky a časové indexy deterministicky; neměň tichým doplněním dat obsah zdroje.
- Změna offsetu musí zneplatnit zastaralé výsledky/cache a následující spuštění analýzy musí použít aktuální nastavení.

### 2.3 Spuštěná analýza musí vycházet ze zvoleného vstupu

- Zkontroluj skutečné cesty **Analyze / Deep Analyze**, dekodéry, pulzní analýzu, histogramy, recovery a výsledkové tabulky. Kde se operace spouští z Deep Analyze, musí čerpat ze zvoleného efektivního vstupního PCM nebo z přesně definované sady kanálů.
- Vyluč nesoulad „graf ukazuje Average, ale dekodér stále běží nad L/R“. Výběr kanálu/offsetu je součástí identifikace analytického běhu a jeho výsledků.
- Změna režimu nebo offsetu nesmí ponechat na obrazovce staré výsledky vydávané za nové. Rozliš analytický vstup od nastavení pouhého zobrazení.
- Pokud existují samostatné funkce exportu nebo Recovery, které mají vlastní nezávislé nastavení kanálů, **nepřepisuj jejich chování automaticky**. Při G0 popiš jejich vztah k Deep Analyze a navrhni bezpečné, konzistentní předávání parametrů tam, kde má navazující operace používat aktuálně analyzovaný signál.
- V UI jednoznačně uveď aktivní režim, offset a případně parametry výsledku; i při chybě / zrušení nesmí vzniknout zavádějící stav.

## 3. Funkční požadavky – Normal Analyze

- Zjednoduš zobrazení/výběr waveformu na **Overlay / Mono**.
- **Overlay:** současné vykreslení obou fyzických kanálů (u stereo); nesmí automaticky měnit analytický zdroj na Average.
- **Mono:** jednoduché mono zobrazení; před implementací dolož, jaký signál „Mono“ v aktuální aplikaci znamená (např. vybraný/automaticky použitý kanál či průměr), a navrhni jednoznačnou definici. Bez schválení význam neměň.
- Posun R vůči L ani výběr L/R/Average nezobrazuj jako běžné pokročilé ovladače Normal UI; zpřístupni je v Deep Analyze.
- Neodstraň existující schopnost Normal analýzy pracovat se stereofonními i mono soubory a detekovat/dekódovat záznamy. **Zjednodušení UI nesmí znamenat nechtěné omezení algoritmů.**
- Vyjasni, zda a jak se nastavení Deep Analyze zachovává při přepnutí na Normal; předej návrh ke schválení. Normal nesmí omylem převzít skrytý nenulový offset při analýze, pokud jej uživatel nevidí.

## 4. Kritické okrajové případy a bezpečnost

- Stereo se shodnými kanály; velmi odlišné kanály; jeden tichý kanál; opačná fáze (Average téměř vyruší signál); různý offset; krajní vzorky; mono soubor; krátký a dlouhý WAV/FLAC.
- Zachovej synchronizaci waveformu a analytických značek / indexů při zoomu, výběru úseku a opakované analýze.
- Ověř vliv na nahrávání a import, pokud sdílejí stejné modely; bez schválení neměň jejich workflow.
- Nevytvářej zbytečné kopie velkých PCM bufferů či fragmentující krátkodobé alokace. U velkých záznamů zachovej rozumnou paměťovou a časovou náročnost.
- Zdrojová média zůstávají read-only. Zachovej Preview/Confirm/Cancel, SHA-256 ověření a ochranu původních souborů v navazujících operacích, kde se používají.

## 5. Co cíleně zkontrolovat v G0 auditu

Podle předávacího protokolu existují tyto orientační body (cesty a skutečný stav vždy ověř proti aktuálnímu HEAD):

- `src/MZTools.Desktop/Documents/AudioSession.cs` – současné ChannelMode a stav analytického dokumentu.
- `src/MZTools.Core/Media/AudioAlignedPcm.cs` – již existující alignment/mix mechanismy; původní definice `Mix`: `(L+R)/2`.
- `AudioSignalAnalysisService.cs` – skutečný vstup detekce/dekódování a práce s kanály.
- Avalonia View/ViewModel pro Normal, Deep Analyze, Recovery a export; vazby ovladačů a povely spuštění analýzy.
- Referenční WPF odpovídající obrazovky, způsob výběru kanálů a vlastní spuštění analýzy.
- Současné testy a možnost malých deterministických regresních scénářů.

Předávací protokol **neprokazuje** úplný datový tok všech režimů; to je předmětem auditu. Neodvozuj implementační závěry pouze z názvů ovládacích prvků.

## 6. Akceptační scénáře pro návrh následných testů (zatím NESPOUŠTĚT)

1. Stereo L obsahuje dekódovatelný program, R je ticho: v Deep Analyze `L` je program detekován, `R` ne; `Average` odpovídá skutečnému směsnému PCM.
2. Stereo R obsahuje program, L je ticho: symetricky k bodu 1.
3. L a R obsahují různý dekódovatelný obsah: `L` a `R` vracejí výsledky ze správného kanálu, `Both` jasně rozlišuje zdroj výsledků.
4. L/R nesou stejný signál s definovaným časovým posunem: nastavení správného `R offset relative to L` před `Average` zlepší zarovnání; nulový či chybný offset vede k odpovídajícím odlišnostem. Ověř znaménko i hranice.
5. L/R mají opačnou polaritu: průměr bez inverze prokazatelně vede k vyrušení; aplikace nesmí ukázat výsledky ze starého individuálního kanálu jako z Average.
6. Změň `L → R → Average`, pak offset, a opakovaně spusť analýzu: waveform, pulzy, histogramy, dekódované výsledky, indikátory a metadata odpovídají každému právě vybranému vstupu, bez starých cache.
7. Normal UI má pouze `Overlay/Mono`, Deep Analyze má pokročilé volby; přechod mezi režimy nemění skrytě výsledek běžné analýzy.
8. Mono zdroj: žádné chybné indexování druhého kanálu, dostupný a funkční analytický režim.
9. Storno/zrušení a nové spuštění: nedochází k záměně konfigurace a výsledků.
10. Paměťově velký WAV/FLAC: bez zbytečného násobení celé PCM paměti.

Preferuj nejprve malé syntetické deterministické testy. Nativní UI, dlouhé nahrávky a plný korpus až po odsouhlasení.

## 7. Požadovaný výstup G0 od Codexu

Před jakoukoli změnou pošli:

1. **Mapa aktuálního chování:** co v Normal/Deep nyní vybírá waveform a co skutečně vybírá analytický zdroj, včetně vstupu do dekodérů.
2. **Návrh UI:** přesné umístění režimů L/R/Average/Both a R offset v Deep Analyze a Overlay/Mono v Normal; výchozí hodnoty a přepínání.
3. **Návrh datového toku:** jednotný „effective analysis input“ a jeho předání přes všechny dotčené funkce, invalidace výsledků.
4. **Seznam konkrétních souborů/metod** k úpravě a popis změny pro každou.
5. **Otevřená rozhodnutí ke schválení:** hlavně význam `Both/Overlay` při analýze, definice `Mono`, znaménková konvence offsetu a vazba na Recovery/export.
6. **Rizika a plán regresních testů**, s rozlišením již existujících a teprve navrhovaných testů.
7. **Výslovnou žádost o schválení** navržené implementace. Do té doby nic neupravuj a nespouštěj.

## 8. Definition of Done (až po schválené implementaci a ověření)

- Deep Analyze nabízí L/R/Average/Both a offset R vůči L; aktivní volba **řídí reálné výpočty**, nikoli jen graf.
- Average používá časově správně zarovnané PCM s definovanou hraniční politikou.
- Normal používá zjednodušené Overlay/Mono UI bez nečekané změny svého analytického chování.
- Výsledky i jejich označení odpovídají aktuálnímu vstupnímu signálu, bez zastaralých cache.
- Nedošlo k regresi Recovery, exportu, importu, ochrany souborů nebo mono podpory.
- Dokumentované a po schválení provedené cílené testy; jasně uvedené neověřené platformy a scénáře.

**Toto zadání samo o sobě NENÍ souhlas s implementací, buildy ani testy.**
