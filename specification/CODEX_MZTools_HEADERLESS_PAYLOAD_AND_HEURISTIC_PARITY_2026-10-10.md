# MZTools — Codex zadání: Headerless Payload Recovery + oprava rozporu Manual vs Heuristic

**Datum:** 2026-10-10  
**Projekt:** https://github.com/bales0/MZTools  
**Typ práce:** analýza příčiny, oprava dekódování, rozšíření ruční analýzy, regresní testy  
**Výchozí revize při předchozí kontrole:** `0ef87e798139c8a7dcc09cbca4bd0ad46ff2c65b` (`batch process added`). Před prací ověř aktuální HEAD a skutečné implementované chování.

## 1. Cíl a uživatelský problém

Jsou zde **dvě související, ale odlišné úlohy**:

1. **Ruční analýza musí umět detekovat, dekódovat a exportovat samostatný Sharp MZ datový blok (payload) bez předcházející platné MZF hlavičky.** Nesmí pro start vyžadovat `HeaderValid`, název souboru, LOAD/EXEC ani deklarovanou délku ze standardní hlavičky. Umožni zadání známé délky, bezpečné automatické hledání délky podle konce bloku a checksumu a případně ruční určení konce.
2. **Zjisti a oprav příčinu, proč heuristická analýza původní nahrávky nenajde program Exploding Fist, zatímco ruční analýza jej podle uživatelského testu najde.** Cílem je společné dekódovací jádro a konzistentní automatický discovery/assembly, nikoliv zavedení dalšího odlišného dekodéru.

**Zásadní:** Neprohlašuj předem, že root cause je jediný 1-sample glitch nebo že selhává Schmitt. To je hypotéza založená na rozdílu zero-cross vs hystereze; je třeba přesně zaznamenat, v které etapě *reálně používaná* heuristická cesta kandidát ztratila. Zvlášť ověř, zda uživatel použil režim **Use heuristic analysis / recovery** (historicky UI `IsChecked="False"`), a srovnávej stejný soubor, nastavení, interval i verzi programu.

## 2. Reálné regresní vstupy

V lokální pracovní sadě použij, pokud jsou dostupné:

- `AudioStarB03A_selection.wav`: 44 100 Hz, mono, 24bit PCM, přibližně **710,593 s**. Obsahuje tři zaznamenané programy. Předchozí nezávislá rekonstrukce identifikovala:
  - **Exploding Fist**: 37 744 B, LOAD=`10F0h`, EXEC=`10F0h`;
  - **MANIC MINER 800**: 36 864 B, LOAD=`2000h`, EXEC=`2000h`;
  - **S-BASIC**: 27 552 B, LOAD=`1200h`, EXEC=`7D79h`.
- `explo_payload.wav`: 44 100 Hz, stereo, 16bit PCM, přibližně **274,302 s**, úmyslně bez MZF headeru. Vstup pro headerless manual recovery. Předpokládaná délka payloadu z původního programu **37 744 B** je užitečné vodítko, nikoli důkaz o obsahu tohoto nového WAV.
- `recovered_1_Exploding_Fist.mzf`: předchozí referenční extrakce pro binární porovnání, jen pokud k dispozici a skutečně ověřená proti originálnímu WAV. Nevycházej pouze z popisné statistiky; porovnávej bajty a checksumy.

**Důležité:** Přiložené soubory nemusí být přímo součástí repozitáře. Není-li možné je získat v prostředí Codex, nesmí to vést k vymyšleným výsledkům. Připrav reprodukční harness, instrukce pro vložení testovacího souboru do lokálního adresáře ignorovaného Gitem a syntetické fixture; ve finálním reportu výslovně napiš, co bylo ověřeno na reálném WAV a co pouze synteticky. Velká audio data bez výslovného požadavku necommituj.

## 3. Audit existujících cest před opravou

Dohledat a zakreslit skutečné volání:

```text
UI import options / Manual Analyze
 -> PCM reader (WAV / FLAC)
 -> preprocessing
 -> zero-cross / Schmitt
 -> pulse candidates
 -> leader + mark / header discovery
 -> RawBlockScanner / expected length registration
 -> candidate list
 -> AssembleRecords / polarity + interval matching
 -> optional SelectiveRecovery / fallback
 -> TapeRecord / export
```

Prostudovat zejména:

```text
WavImportOptionsWindow.xaml[.cs]
WavHeuristicAnalyzer.cs
SharpMzPulseDecoder.cs
WavPcmStreamReader.cs
FlacPcmStreamReader.cs
ruční audio analyzer / jeho aktuální entrypoint
statistics/results/report vrstvy
```

Při minulé kontrole v `WavHeuristicAnalyzer.cs` platilo:

- `SharpTapeCandidateScanner.AcceptHeader()` volá `AddExpectedBlockLength(...)` podle `header[18..20]`;
- `RawBlockScanner` vzniká prostřednictvím `EnsureBlockScanner(byteCount)` až pro známé délky;
- `AssembleRecords()` při `headers.Count == 0` hlásí „No Sharp MZ signal found“, i když jsou přítomné samostatné datové pulzy;
- preprocessor/detekce má zero-cross i Schmitt; selective recovery používá také alternativní `schmittScale`;
- `SharpMzPulseDecoder.ClassifyPulse` při neklasifikovatelném pulzu resetuje stav.

Tyto poznatky **ověř v aktuálním HEAD**, nespoléhej na jejich trvalou platnost. Dále ověř časový model `StartSample/EndSample`: historicky `RawBlockScanner` může nastavit `startSample` až na první datový bajt, zatímco `AssembleRecords()` omezuje kandidáty intervalem mezi headery; nesmí se potichu ztrácet validní payload kvůli chybnému pairing pravidlu.

## 4. Povinná forenzní diagnostika: proč heuristika nezachytí Exploding Fist?

Nejprve bez změny dekódovacího algoritmu reprodukuj obě cesty na **totožném původním WAV**:

```text
A: Standard import
B: Heuristic import (zero-cross + Schmitt; obě polarity)
C: Manual Analyze se stejnými defaulty
D: Manual Analyze se skutečnými parametry, které vedou k úspěchu
```

Pro každou cestu zaznamenej:

- input hash, verzi programu/commitu, sample rate, channels, režim, channel, polarity, Schmitt thresholds/filtering;
- čas/polohu každého nalezeného leaderu a marku;
- rozpoznané headery a jejich checksums;
- kde/odkud se získává délka payloadu;
- vytvoření a reset každého `RawBlockScanner` a očekávanou délku;
- průběh prvních chyb klasifikace, včetně půlperiod v samples/µs, polarity a původního PCM okolí;
- zda vůbec vzniká `PayloadCandidate` a zda `ChecksumValid`, `Data.Length`, `StartSample/EndSample` odpovídají;
- zda `AssembleRecords` kandidáta zahodí kvůli intervalu, polaritě, délce, duplicitě, scoringu nebo checksumu;
- zda výsledky mění standard fallback / selective recovery a proč.

**Povinný checkpoint:** Pokud ruční cesta získá checksum-valid payload, heuristická cesta musí vysvětlit, v které konkrétní fázi se ztrácí. Přilož minimální log s `stage/reason/code/time/sample` a test porovnání kandidátů. Neuzavírej problém pouhým vylepšením hlášky „no signal“.

Prověř následující hypotézy odděleně:

1. `Heuristic` režim není zapnutý nebo UI spouští jiné backendové volání než ruční analýza.
2. Automatický scanner nedostane správný `expectedLength`, ačkoliv ruční ano.
3. Schmitt parametr/preprocessing/edge filtering se mezi cestami liší.
4. Jeden falešný edge vede k `ResetDecoder` v dlouhém payloadu.
5. Leader/mark je nalezen, ale blok se zahodí kvůli resetu, neplatnému terminátoru nebo checksumu.
6. Kandidát existuje, ale je nesprávně spárován s headerem (čas/polarita/channel/identita kopie).
7. `SelectiveRecovery` neprojde relevantní interval nebo je omezen jen na jiný typ selhání.
8. U velkého payloadu scanner nepřiměřeně mění kandidáty, limity/stav nebo resetuje při náhodných podobných mark vzorech.

Root cause dolož a oprav **minimální bezpečnou změnou**. Pokud více příčin, odděl je v reportu.

## 5. Ruční analýza bez hlavičky: „Headerless Payload“ režim

Do existující ruční analýzy přidej další režim zdroje/bloku:

```text
Decode scope:
 ( ) Standard MZF record (header + payload)
 ( ) Payload only (no MZF header)
```

V `Payload only` režimu se automatická detekce headeru nesmí vyžadovat ani používat jako gate. Musí být možné:

- ručně ohraničit vzorky/time range (počátek a konec); kliknout na leader nebo sync v grafu;
- automaticky najít **data leader -> sync/mark -> data** v zadaném intervalu;
- vybrat Left / Right, polarity Normal / Inverted / Auto, ZeroCross / Schmitt / Auto;
- upravit Schmitt threshold nebo hysterezi pouze v re-decode vybraného intervalu;
- zadat `Expected payload length` explicitně (`37 744` B pro test), anebo zvolit bezpečný automatic/unknown-size mode;
- ukázat počet načtených bajtů, počet chyb/nekonzistencí, uložený Sharp checksum, vypočtený checksum, `Valid/Invalid/Unknown`;
- zobrazit první chybu s příslušným sample indexem a časem, důvod resetu a okolní pulzy;
- zobrazit kandidáty jednotlivých kopií, nejsou-li stejné, včetně jejich checksums;
- exportovat `raw .bin`; sestavit `.mzf` pouze po explicitním doplnění hlavičky/metadata.

### 5.1 Known-length mode (priorita P0)

Uživatel zadá délku `N`. Zavolej sdílené blokové dekódování `BeginRawBlock(N)`/odpovídající aktuální API a hledej validní leader/mark, `N` bajtů a **2 bajty checksumu**, nezávisle na `HeaderValid`.

V případě úspěchu:

```text
Payload-only candidate: N bytes
Sharp popcount checksum: recorded == computed
Status: VALID
```

Zachovej původní data beze změny. Nebude-li checksum validní, nezaměňuj data za validní, ale můžeš umožnit export s varováním a jasným označením.

### 5.2 Unknown-length mode (priorita P1)

Navrhni rozumnou samostatnou streaming variantu. Neskenuj naivně všechny délky `1..65535` se samostatnými paralelními dekodéry nebo `O(N²)` kandidáty.

Bez headeru **nelze automaticky prohlásit každou sekvenci bajtů za jednoznačný Sharp payload**, protože checksum je pouhý popcount16, nikoliv CRC. Pokud lze podle struktury terminátoru, marku, mezery a kopie omezit možné hranice bloku, ověřuj pouze tyto kandidáty; vyžaduj explicitní evidenci a úroveň jistoty. Pokud zůstane více validních možností, ukaž je a nepředstírej jistotu.

Nabídni „Decode until next validated framing boundary / selected end“, ale uveď režim `unverified length`, je-li to nezbytné.

### 5.3 Syntetická MZF hlavička

Payload bez hlavičky **nemá zjistitelné jméno, LOAD ani EXEC pouze z pulzů**. Uživatel je musí zadat nebo vědomě použít metadata z referenčního MZF.

Dialog:

```text
Filename (Sharp charset encoding)
Type
Length = recovered bytes
LOAD
EXEC
Optional description
```

Jasně označ `Reconstructed/synthetic header`; nikdy nepředstírej původní archivní hlavičku. Výstup `.mzf` má standardní 128B header + dekódované data; jeho vlastní binární export `.bin` je preferovaná bezeztrátová cesta, pokud metadata neznáme.

## 6. Bezpečnost pulzních korekcí

Je vhodné zavést společnou volitelnou diagnostickou `edge deglitch` vrstvu, ale **nikoliv neomezené ignorování chybných pulzů**.

- Při 44,1 kHz je 1 sample ~22,676 µs; nominální half-wave jsou řádově stovky µs. Falešný 1-sample edge lze ověřovat jako kandidáta na glitch, nikoli automaticky mazat.
- Použij bounded-lookahead merging a ověř, že výsledné sousední intervaly odpovídají očekávanému short/long profilu, polaritě a časové kontinuitě.
- Korekce zaznamenej (`sample`, původní/složené intervaly, confidence), uživateli ukaž `correction applied`.
- Výstup musí projít checksumem. Korekce nikdy nesmí z neověřeného bloku vytvořit fiktivně „VALID“.
- Zero-cross i Schmitt musí zůstat dostupné; v AUTO upřednostni checksum-valid výsledek, nikoli jeden detektor dogmaticky. Nesnižuj kvalitu standardních, čistých WAV.
- Zvaž checkpoint/resynchronizaci v delším bloku pouze pokud lze bezpečně zrekonstruovat polohu bit/byte bez ztráty dat; jinak ukaž partial a diagnostiku. Nevnucuj „best effort“ jako validní soubor.

## 7. Sjednocení ruční a heuristické cesty

**Nechtěné chování:** ruční analýza najde checksum-valid payload, automatická heuristika jej ztratí, protože nepoužije stejná data, scanner nebo pravidla.

Požadovaný model:

```text
Audio PCM input
   -> shared preprocessing/pulse extraction
   -> shared candidate discovery (header OR raw payload)
   -> shared block decoder and checksum validator
   -> candidate collection + explicit provenance
             |                        |
          Heuristic                  Manual UI
          matching/assembly          interval/options/export
```

Ruční režim může mít více nastavení a silnější cílené vyhledávání, ale rozdíl musí být transparentní. Heuristika s výchozím AUTO se má pokusit o oba validní detektory a správně spárovat kandidáty. **Změna výchozího UI checkboxu sama o sobě není dostatečná oprava**, pokud heuristika ztrácí candidate už nalezeného ručně.

Do reportu přidej `candidate found but rejected: reason`, například:

```text
NO_VALID_HEADER
NO_LENGTH_HINT
NO_LEADER
BAD_MARK
PULSE_GLITCH_RESET
BLOCK_TRUNCATED
CHECKSUM_MISMATCH
POLARITY_MISMATCH
CHANNEL_MISMATCH
OUTSIDE_HEADER_INTERVAL
DUPLICATE_SUPPRESSED
CANDIDATE_RANKING
```

Výsledky označ `Verified checksum`, `Partial`, `No verified payload`, podle skutečné evidence.

## 8. Diagnostické logy a UI

V `Manual Analyze` přidej pohled/tab **Candidates / Recovery** se sloupci:

```text
Time range | Channel | Polarity | Detector | Leader | Mark | Length
Recorded checksum | Calculated checksum | Status | Failure reason
```

Detail kandidáta:

```text
signal waveform around selected transition
pulse durations (samples + µs)
threshold/hysteresis
pulse classification
actual reset/reject cause
start/end byte index
```

Zvýrazni časový výsek chyb i při úspěchu jinou metodou; ukaž `Manual found / Heuristic missed` a konkrétní důvod. Logy musí být limitované a vypnutelné, bez výpisu milionů pulzů do UI/konzole; diagnostika po vybraných intervalech.

## 9. Výkonnost a stabilita

- Streaming PCM reader, bez načítání desetiminutového 24bit WAV do několika velkých kopií.
- Dlouhý payload 37 744 B dekóduj bez závislosti na libovolných UI-timeoutech.
- Bounded memory pro kandidáty, progress/cancellation, UI thread neblokovat.
- Re-decode vybraného intervalu nezničí úspěšné dřívější kandidáty ani nezmění otevřený dokument bez potvrzení.
- Nenaruš stávající Intercopy/TurboCopy/MZ700/Normal profily a duplicate/consensus workflow.
- Zachovej WAV i FLAC přes současné reader API.

## 10. Povinné testy

### Test A: Headerless known-length

Vytvoř validní syntetický payload-only WAV (leader + mark + N bytes + Sharp checksum, **bez MZF headeru**). Ruční analýza s `N` musí vrátit checksum-valid raw bytes. Export BIN musí být bitově totožný.

### Test B: Headerless unknown length

Ověř nejméně jeden jednoznačně ohraničený případ a jeden nejednoznačný případ; nejednoznačný se nesmí vydávat za verifikovaný.

### Test C: Stereo/polarity

Left, Right, inverted, nepravidelný gain, drobný DC offset. Pro všechny platné fyzikální signály ověř checksum a výběr kanálu.

### Test D: Glitch/regrese

Synteticky vlož falešnou 1-sample změnu znaménka během validního payloadu:

- raw zero-cross může zachytit glitch,
- Schmitt nebo bezpečný deglitch musí obnovit správný výsledek,
- reálnou deformaci, kterou nelze bezpečně opravit, dekodér nesmí tiše ignorovat.

### Test E: Exploding Fist — hlavní akceptační test

Na **stejném `AudioStarB03A_selection.wav`** spusť Heuristic Auto a Manual Auto:

- oba mají najít `Exploding Fist` se **37 744 B**;
- checksum payloadu musí být validní;
- obnovené bajty porovnej s referenční `recovered_1_Exploding_Fist.mzf`, pokud je dostupný;
- další programy `MANIC MINER 800` a `S-BASIC` nesmí zmizet nebo změnit payload;
- přesný důvod původního neúspěchu je uveden v reportu a je pokryt unit/integration testem.

### Test F: explo_payload.wav — hlavní headerless test

Na `explo_payload.wav` ručně dekóduj bez MZF headeru:

- hledání leader/sync v obou kanálech, obou polaritách a Auto/Schmitt;
- známá délka `37 744 B`;
- zkontroluj uložený/vypočtený Sharp checksum;
- je-li validní, porovnej payload bajt po bajtu s původní referencí;
- není-li validní, vrať konkrétní failure event + sample offset, nikoli nepodložené „success“.

### Test G: Ostatní současné režimy

Původní standardní WAV/FLAC a validní MZF/MZT import musí zůstat bez regresí; testy na trim/truncation, marker, header checksum mismatch, payload mismatch, duplicate copy a selective recovery.

## 11. Postup implementace pro Codex

1. Synchronizuj repo, uveď HEAD, spusť původní testy.
2. Projdi manual a heuristic execution paths; vytvoř krátký flow diagram.
3. Reprodukuj mismatch `Exploding Fist` a uveď přesný stage + reason.
4. Implementuj diagnostics bez změny algoritmu a znovu ověř mismatch.
5. Oprav root cause v příslušné vrstvě s malým diffem; ideálně sdíleným decoderem.
6. Zaveď `Payload only / known length` do ruční analýzy a export RAW.
7. Přidej bezpečný `unknown length / bounded candidates` a syntetickou MZF hlavičku.
8. Přidej diagnostický candidate view a lokální waveform/pulse pohled, reuse existující/rozpracované Phase F UI.
9. Přidej regresní testy, dokumentaci, README a aktualizované screenshots/usage pouze pokud odpovídají skutečně implementovanému stavu.
10. Spusť testy, porovnej zachráněná data, napiš přesně, které testy běžely na reálných WAV a které ne.

Po každé funkční části: build + test. Neprováděj nevyžádané masivní přepisování celého projektu.

## 12. Akceptační kritéria

- [ ] Manual Analyze dekóduje payload bez `HeaderValid` se zadanou délkou.
- [ ] Pokud není hlavička, chybějící LOAD/EXEC/jméno se nevymýšlí.
- [ ] Existuje export RAW `.bin` a vědomě syntetizovaného `.mzf`.
- [ ] Je dostupné vyhledání leader/mark, polarity, channel, detector, hranic a explicitní lokalizace neúspěchu.
- [ ] `explo_payload.wav` je vyhodnocen v headerless režimu a jsou uvedeny skutečné výsledky checksumu, ne pouze nalezení pulzů.
- [ ] Je prokázána konkrétní příčina rozporu Manual vs Heuristic u Exploding Fist.
- [ ] Heuristic Auto obnoví všechny tři programy z původního WAV, pokud byl testovací soubor dodán a je možné jej spustit.
- [ ] `Exploding Fist` má délku 37 744 B, správný checksum, referenčně shodný payload (je-li reference k dispozici).
- [ ] Žádné tiché maskování skutečných vad při „glitch repair“; neplatná data nejsou označena `VALID`.
- [ ] Nezhorší se ostatní formáty/profily ani standardní workflow.
- [ ] Existují automatické unit/regresní testy a přehledný forenzní report.

## 13. Požadovaný výstup od Codexu

Vytvoř `docs/AUDIO_HEADERLESS_HEURISTIC_ROOT_CAUSE.md` s těmito kapitolami:

```text
Baseline commit
Test input identities (hashes)
Manual vs Heuristic flow comparison
Observed failing stage
Root cause and evidence (sample offset / timestamp / event)
Code changes and reasons
Headerless UX / checksum semantics
Regression test results
Limitations and unverified hypotheses
HW / user test checklist
```

**Závazná zásada:** Není přijatelné pouze deklarovat, že „bez hlavičky neznáme délku“, nebo pouze přepnout default na Schmitt. Cílem je **funkční ruční obnovování samostatných datových bloků a vysvětlená, opravená odchylka heuristického vyhledávání od úspěšné ruční cesty**.
