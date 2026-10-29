# MZTools – sjednocené zadání pro Codex: komprese MZF/M12 → COM a DSK Repair v Open

**Datum:** 28. 10. 2026  
**Projekt:** https://github.com/bales0/MZTools  
**Výchozí ověřený commit:** `ff2ddbba00633a9113fa8957784e4ef3133518d3` (28. 10. 2026). Před zahájením znovu ověř aktuální HEAD, lokální necommitované změny i mezitím provedené úpravy.  
**Rozsah:** dva nezávislé pracovní okruhy v jednom zadání; neměnit nesouvisející kód.  
**Jazyk UI:** angličtina; souhlas a zprávy uživateli v češtině.

## 0. ZÁVAZNÉ SCHVALOVÁNÍ – PLATÍ PRO OBA OKRUHY

1. **První krok: pouze READ-ONLY audit obou oblastí.** Zkontroluj skutečný stav, identifikuj všechny vstupní cesty, testy, rizika a navrhni přesné změny. Neklaď rovnítko mezi původní plán a aktuální HEAD, pokud mezitím proběhly změny.
2. **PŘED KAŽDOU změnou repozitáře, zdrojů, XAML, testů, dokumentace, konfigurace, závislostí nebo souborů žádej výslovný souhlas uživatele.** Neprováděj bez schválení ani formátování, refaktoring, instalaci balíčků, automatické opravy, build/testy zapisující do pracovního stromu, commit, push, reset či clean.
3. Každou etapu schvaluj **samostatně**. Souhlas s úpravou COM **není** souhlasem s úpravou DSK, a naopak. Po dokončení schválené etapy dej diff/souhrn, přesné výsledky PASS/FAIL/SKIP, stav ochrany dat a **zastav se** před další etapou.
4. **Nevytvářej automaticky commit/PR ani nepřepisuj uživatelské soubory.** Poškozené `.dsk`, původní `.mzf/.m12` a dříve existující `.com` musí zůstat zachované. Opravy DSK až na výslovnou akci v UI, uložení standardně `Save As` do odlišné cesty.
5. Při nejasnosti v zadání nebo neshodě s aktuálním kódem předlož alternativy, rizika a výslovně požádej o rozhodnutí. Žádné tiché rozšíření rozsahu.

## 0.1 Stručný cíl

**Okruh COM:** V dialogu `MZF/M12 → COM` zobrazit pod názvem vstupu detekovanou kompresi a přejmenovat volby `Keep source`, `None`, `Auto`, aby byly jednoznačné. Využít existující `NativeProgramImage.Compression`; **bez změny kompresních/dekompresních algoritmů, loaderů a výsledných COM bajtů.**

**Okruh DSK:** Z hlavních `Tools` **odstranit** položku `Inspect / Repair DSK Container...` a samostatný picker. Do běžného `Open .dsk` integrovat nabídku bezpečné opravy **jen když je potřebná a proveditelná**. Ponechat ruční `Disk → Repair Container…` v otevřeném DSK editoru, odděleně od `Filesystem → Guided Repair…`. **Žádné automatické opravy ani přepis originálu.**

## 0.2 Doporučené pořadí a schvalovací brány

- **AUDIT (bez zásahů):** COM + DSK včetně všech Open/Save cest, soupis souborů, návrh textů a regresních testů. Předej audit, zastav se a vyžádej schválení první implementační etapy.
- **COM-B** (samostatné schválení): popisky, detekovaná komprese v dialogu, testy. Nezasahovat do DSK. Poté report a další souhlas.
- **DSK-B** (samostatné schválení): bezpečný integrační tok pro `Open .dsk` + neprůstřelná ochrana původního DSK při Save/Save As + testy. Poté report a další souhlas.
- **DSK-C** (samostatné schválení): odstranit hlavní Tools položku/handler, upravit editorový Disk Repair a dialogové statusy, regresní/UI testy. Poté report.
- **COM-C** (pouze volitelné, nový souhlas): zpřesnit technický report o skutečné reprezentaci zvoleného kandidáta Auto, jestliže audit doloží, že je to možné bez přepsání algoritmů.

**Poznámka k etapám v přiložených specifikacích níže:** v každém okruhu existují interní označení A/B/C. Používej vždy prefix **COM-** nebo **DSK-**, aby nedošlo ke zmatku, a nepokračuj po schválení jedné etapy do druhé.

---

## ČÁST I – MZF/M12 → COM: detekce komprese a názvy voleb

### 1. Problém a požadovaný výsledek

V dialogu **MZF/M12 → COM** se zobrazují volby:

- `Keep source (no added compression)`
- `None`
- `Auto (smallest safe)`
- `ZX0`
- `ZX7`

Pro uživatele nejsou `Keep source` a `None` bez informace o vstupní kompresi srozumitelně odlišitelné. Uživatel chce **(A) trvale vidět detekovanou vstupní kompresi** a **(B) přejmenovat/objasnit volby pro výstupní COM**. Nejde o implementaci nového kompresoru, změnu umístění loaderu ani o dekompresi nerozpoznaných formátů.

#### Zásadní existující sémantika – nesmí se omylem změnit

- `KeepSource` předává `options == null` do `NativeComConversionService.Build(...)`; sestaví COM z aktuálně načtené reprezentace včetně stávajícího loaderu/dat, **bez nové komprese a bez předchozího rozbalení**.
- `None` předává `new MzfCompressionOptions(MzfCompressionAlgorithm.None)`; rozpoznanou ZX0/ZX7 nejprve **dekomprimuje**, poté vygeneruje COM bez přidané komprese.
- `Auto` porovnává bezpečně sestavitelné varianty COM včetně původní datové reprezentace a kandidátů bez komprese / ZX0 / ZX7; volí nejmenší validní výsledek.
- `ZX0` a `ZX7` rozpoznanou vstupní kompresi nejprve rozbalí a výsledek znovu zkomprimují zvoleným algoritmem.
- U obyčejného nekomprimovaného vstupu mohou `Keep source` a `None` vytvořit stejný nebo ekvivalentní COM. Zásadní rozdíl nastává u rozpoznaně komprimovaného vstupu.
- **Nerozpoznaná ZX0/ZX7 ≠ zaručeně nekomprimovaný soubor.** MZF může obsahovat vlastní loader nebo jinou kompresi. Aplikace nesmí ve stavu bez detekce tvrdit „uncompressed“ jako ověřený fakt.

### 2. Zjištěná existující implementace (ověřit na novém HEAD)

| Soubor / komponenta | Co už obsahuje / proč je relevantní |
|---|---|
| `Media/NativeProgramImage.cs` | `NativeProgramImage.Compression` typu `MzfCompressionInfo?`; parser `MzfNativeImageParser.Parse` volá rozpoznání komprese a vytváří `Input compression` warning. **Preferovat tuto již získanou informaci.** |
| `Compression/MzfLoaderBuilder.cs` | `MzfLoaderBuilder.TryGetCompressionInfo(TapeRecord, out MzfCompressionInfo?)`, `DetectCompression`, `MzfCompressionInfo.DisplayName`; detekuje ZX0/ZX7 forward/backward, včetně embedded ZX7 loaderů. |
| `NativeComDialog.cs` | `sourceInfo` nyní vypisuje jméno a file type; instancuje sdílený `CompressionOptionsControl`, volá `compression.KeepSource ? null : selectedOptions`, mění zdroj přes `Open MZF / M12`. |
| `CompressionOptionsControl.xaml`, `.xaml.cs` | Společná nabídka pro více cílů (`NativeCom`, MZF/MZT/QuickDisk/IPL); `ConfigureTarget(NativeCom)` vkládá `KeepSource`; obecná `None/Auto/ZX0/ZX7` data pocházejí z XAML. |
| `Media/NativeComConversionService.cs` | Faktické větvení `KeepSource` / `None` / `Auto`, také `NativeComCompressionChoice.Choices`; oddělit pojmenování od interní sémantiky. |
| `MZTool.Tests/NativeComTests.cs`, `MZTool.Tests/CompressionWorkflowTests.cs` | Stávající funkční testy COM a komprese, vhodná regresní základna. |

Pozor: fyzická cesta k `MzfNativeImageParser` je `Media/NativeProgramImage.cs`, **nikoli** `Media/MzfNativeImageParser.cs`.

### 3. UI: zobrazit detekovanou kompresi VSTUPU

V dialogu **MZF/M12 → COM** přidej nápadný, ale střídmý textový řádek / blok přímo **pod názvem a typem souboru, nad ovládáním výstupní komprese**:

- `Source compression: ZX0 · Forward · Separate loader`
- `Source compression: ZX0 · Backward · Separate loader`
- `Source compression: ZX7 · Forward · Embedded loader`
- `Source compression: ZX7 · Backward · Embedded loader`
- `Source compression: Not detected (none or unsupported)`

Jde o **příklady textů**, konkrétní výpis přizpůsob skutečně dostupným údajům. Nahrávej údaj **z `NativeProgramImage.Compression`**, případně z centralizovaného formatteru nad `MzfCompressionInfo`. Neimplementuj paralelní scanner bajtů či rozdílnou detekci v UI.

- U rozpoznaného formátu ukaž **algoritmus, směr a typ loaderu**; pokud to ověřeně dává smysl, i `stored payload size` a `restored size` jako samostatné explicitně popsané velikosti. Bezpečně odlišuj packed payload od celé délky MZF nebo COM.
- U nerozpoznaného formátu **nepoužívej prosté `None`** ani nepřisuzuj jistotu, že vstup je nekomprimovaný. Doprovodný tooltip: `No supported ZX0/ZX7 loader signature was recognized. Other or custom compression may be present.`
- U prázdného, nevalidního či nepodporovaného vstupu zobrazuj `Source compression: —`/`Not available` místo zastaralé informace o předchozím souboru.
- Hodnota se aktualizuje při přímém otevření MZF/M12, při spuštění dialogu s již vybraným `TapeRecord` i při výměně souboru. Nesmí se měnit jen kliknutím na výstupní algoritmus.
- Stav detekce je nezávislý na volbě `Keep/None/Auto/ZX0/ZX7`, kompresi výstupu a výsledku kontroly kompatibility CP/M.
- Nevyžaduj dlouhou dekompresi pouze pro zobrazení stavu. Detekce musí být rychlá a nesmí zablokovat UI; pozdější asynchronní konverze musí nadále respektovat cancellation/versioning existujícího dialogu.

### 4. UI: jednoznačné názvy způsobu převodu

Jde o **Output compression / COM payload policy**, nikoli o `Source compression`. Odděl tyto dvě informace vizuálně i významově.

Doporučené texty pouze pro target `NativeCom`:

| Interní volba (zachovat) | Navržený text v UI | Stručné vysvětlení |
|---|---|---|
| `KeepSource` | při zjištěné ZX0/ZX7: **`Keep original compression`**; jinak **`Keep source encoding (no recompression)`** | Ponechá načtený payload/loader a nepřidá ani neodstraní kompresi. |
| `None` | **`Uncompressed (decode recognized ZX0/ZX7)`** | U rozpoznané ZX0/ZX7 rozbalí vstup, výsledný COM nezabalí. Jiná komprese se tím automaticky nerozpoznává. |
| `Auto` | **`Auto (smallest valid COM)`** | Vybere nejmenší bezpečně sestavitelnou variantu COM, včetně původní. |
| `Zx0` | `ZX0 (recompress)` nebo stávající `ZX0` | Rozbalí známou vstupní kompresi a následně vytvoří ZX0. |
| `Zx7` | `ZX7 (recompress)` nebo stávající `ZX7` | Obdobně ZX7. |

Přípustná je i **jedna stabilní volba** `Keep original encoding (no recompression)` bez dynamického přejmenování; pokud ji Codex preferuje kvůli konzistenci, **před implementací to vysvětlí a nechá schválit**. Požadavek uživatele je hlavně na srozumitelnost a zachování přesného významu; nevytvářet falešný dojem existující komprese.

- Výchozí volba zůstane **`KeepSource`**.
- Stávající enumy, tagy (`KeepSource`, `None`, `Auto`, `Zx0`, `Zx7`), formát sidecarů, předávání `null` a algoritmická logika se **nemění**.
- Sdílený `CompressionOptionsControl` používají i další operace. **Nesmí se bez schválení změnit jejich texty nebo chování**. Pro odlišné texty NativeCom zvaž úzké cílově specifické labely při `ConfigureTarget`, nikoli plošnou změnu výchozího XAML.
- Změna nápisu musí zachovat aktuální `SelectedItem/Tag`; nesmí resetovat uživatelskou volbu při záměně vstupu.
- Pokud bude využito dynamické přejmenování `KeepSource`, musí se aktualizovat i jeho tooltip/popisek a testovat, že nedochází k rekursivnímu přepočtu nebo opakované konverzi při změně pouze textu.
- **Direction / quick / embedded / expert** zůstanou ve stejném funkčním stavu podle vybraného algoritmu; neměnit možnosti konfigurace komprese.

#### Stručný příklad rozhraní

```
MZF/M12 → COM
Open MZF / M12...  FLAPPY ver 1.0A · file type 01 (OBJ)
Source compression: Not detected (none or unsupported)

Output compression:
  Keep source encoding (no recompression)    [default]
  Uncompressed (decode recognized ZX0/ZX7)
  Auto (smallest valid COM)
  ZX0
  ZX7
```

Při souboru s rozpoznaným ZX0:

```
Source compression: ZX0 · Forward · Separate loader
Output compression: Keep original compression    [default]
```

### 5. Výsledný COM: popsat, co se skutečně zvolilo

V už existujícím **Technical details / report** jednoznačně odliš:

- rozpoznanou **vstupní** kompresi;
- požadovanou **výstupní politiku** (`Keep`, `None`, `Auto`, `ZX0`, `ZX7`);
- **skutečně výslednou** reprezentaci / vybraného kandidáta u `Auto`, **pokud ji kód umí spolehlivě určit**. Nesmí se falešně uvádět `None`, když je zachován původní vlastní kompresní loader.

Pokud současný výsledek `Auto` neobsahuje jednoznačné metadata o vybraném algoritmu a jejich doplnění vyžaduje změnu veřejných datových kontraktů, Codex to nejdříve popíše jako **volitelnou navazující etapu** a vyžádá si souhlas. Nedomýšlet způsob komprese jen z velikosti výsledného COM.

### 6. Zakázané změny / kompatibilita

- **Neměnit** `MzfDecompressionService` dekódování ZX0/ZX7, `MzfCompressionService`, kompresní formáty, loadery, paměťové adresy, CP/M profil ani výstupní COM bytes jen kvůli labelům.
- Neměnit chování `NativeComConversionService.Build` pro `null`, `None`, `Auto`, `Zx0`, `Zx7` bez zvláštního výslovného schválení.
- Neprovádět automatický převod původního `Keep` na `None` podle detekce.
- Zachovat podporu `.mzf`, `.m12` a existující kompatibilní jednotlivé soubory, samostatný otevřený soubor i výběr z hlavního okna.
- Zajistit, aby se žádný vstupní MZF/M12 nezměnil a bez vědomí uživatele se nepřepsal ani dříve uložený COM.
- Nepřidávat zbytečnou závislost ani kopii detekční logiky. Přidat jen malé UI helpery / formátovací metody, budou-li opodstatněné.
- Neměnit jiné kompresní volby v MZF/MZT/QuickDisk/IPL UI, pokud taková změna není samostatně schválená.

### 7. Testy a akceptační kritéria

**Testovací matice** (unit testy + přiměřené WPF/UI smoke testy):

1. Běžný nekomprimovaný MZF typu `01 OBJ` (např. testovací FLAPPY): zobrazí `Not detected (none or unsupported)`, `Keep` i `None` nadále procházejí a jejich funkční výsledky se neliší oproti výchozí verzi.
2. ZX0 forward a backward: správná identifikace, výchozí `Keep` zachová původní výstupní chování; `None` provede očekávanou dekompresi.
3. ZX7 forward a backward, včetně **embedded loader**: správný text a zachování souvisejících ochranných omezení.
4. Vstup s vlastním/nerozpoznaným loaderem: stav `Not detected`, nikdy falešné `Uncompressed`; `Keep` zůstane dostupný a nesmí nic destruktivně změnit.
5. Vstupní M12: zobrazení komprese stejným způsobem jako MZF tam, kde je rozpoznatelná; zachovat původní parsování.
6. Předání již vybraného `TapeRecord` do dialogu, následné otevření jiného souboru, chybné otevření, opětovné otevření: text se **neplete mezi soubory** a nezůstává zastaralý.
7. Přepínání `Keep → None → Auto → ZX0/ZX7`, změna souboru, asynchronní přepočet: neporuší stav volby, eventy a výstup odpovídá opravdu vybranému režimu.
8. Nezměněné chování komprese při exportu přes ostatní targety sdíleného `CompressionOptionsControl`.
9. Regresně porovnat **SHA-256/bytes výsledného COM** pro známé fixní testovací vzorky s výchozím commitem; nové UI labely nesmí změnit binární výsledek. Jestli baseline neexistuje, vytvořit validované referenční výsledky až po schválení.
10. Provést relevantní existující testy `NativeComTests`, `CompressionWorkflowTests`, `M12` a přiměřené UI smoke testy; reportovat skutečný počet PASS/FAIL/SKIP. Nikdy neoznačovat neprovedené HW testy jako úspěšné.

**Definition of Done:** pod vstupem je stav rozpoznané komprese; nápisy `Keep` a `None` jsou pro uživatele jednoznačně rozlišitelné; `Auto` je srozumitelný; interní chování a binární COM jsou zachovány; testy prošly; změny jsou malé a přehledné.

### 8. Schvalované etapy / co nyní udělat

**Etapa A – READ-ONLY AUDIT (proveď nyní bez dalších změn):**
- Ověřit aktuální `HEAD`, pracovní změny (nesahat na ně), současný detektor a UI/konverzní větve.
- Navrhnout konkrétní texty v UI a umístění informace o kompresi (případně zda má mít `Keep` dynamický popisek).
- Vypsat soubory, které chceš upravit, důsledky a testy.
- **ZASTAVIT A POŽÁDAT UŽIVATELE O SOUHLAS.**

**Etapa B – detekce + UI názvy (až po výslovném souhlasu):**
- Použít `NativeProgramImage.Compression` v `NativeComDialog`, přidat informační řádek a cílově specifické texty `CompressionOptionsControl`.
- Doplnit malé UI/formatter testy a regresní testy; nic víc.
- Předložit diff, testy, výsledek a **zastavit pro další souhlas**.

**Etapa C – případně zpřesnění reportu a výsledku Auto (jen pokud audit ukáže skutečnou potřebu a po dalším souhlasu):**
- Pokud je možno spolehlivě doložit výstupní algoritmus, přidat jeho rozlišení do reportu bez změny komprese. Jinak uvést omezení a etapu neprovádět bez rozhodnutí uživatele.

### 9. Požadovaná první odpověď Codexu

Před editací odešli v češtině:

1. Jak se v aktuálním commitu detekce komprese dostává do `NativeProgramImage.Compression` a zda pokrývá MZF/M12.
2. Jak přesně fungují `KeepSource`, `None` a `Auto` v aktuálním kódu.
3. Doporučené finální názvy a náhled jedné krátké sekce dialogu pro rozpoznanou/nerozpoznanou kompresi.
4. Seznam souborů k úpravě, testovací plán, možné okrajové případy.
5. Výslovnou otázku: **„Souhlasíte s provedením etapy B podle tohoto plánu?“**

**Do souhlasu nic neměň.**

---

## ČÁST II – DSK Container Repair: přesun z Tools do Open .dsk a DSK editoru

### 1. CÍL A PROČ

Funkce **Guided DSK Container Repair / Inspect / Repair DSK Container** je nyní vystavena jako samostatná položka hlavních `Tools`, která znovu vybírá `.dsk` a nabízí dialog i tehdy, když se souborem není potřeba nic dělat (např. `NotRepairable: No deterministic container repair is needed or available.`). Přitom uživatelsky dává větší smysl **otevřít `.dsk` běžným `Open` a pouze při konkrétním problému nabídnout bezpečnou opravu**, případně dovolit ruční vyvolání opravy z editoru otevřeného obrazu.

Cílové chování:

- `Open → *.dsk` je jediné běžné místo pro výběr DSK; příjem přes drag-and-drop/ostatní již existující vstupní cesty musí mít konzistentní chování, pokud používají společný importer.
- **Platný DSK** se otevře obvyklým způsobem, bez dalšího modálního okna a bez automatických změn.
- **Poškozený/neobvyklý DSK**, u kterého existuje bezpečně doložitelná oprava hlavičky/struktury kontejneru, nabídne konkrétní diagnostiku, náhled a volbu opravu aplikovat do paměti, odmítnout nebo zrušit.
- **Neopravitelný/nejasný DSK** zobrazí srozumitelné odůvodnění, **ne** prázdný dialog s deaktivovaným řádkem a nepravdivou formulací „NotRepairable“, pokud je obraz jen zdravý.
- **Nadbytečná data za kontejnerem** nejsou automaticky závadou k odstranění: nepřipustit tiché zkrácení obrazu. U parseovatelného DSK je to nanejvýš neblokující diagnostika / volitelná ruční oprava s výrazným potvrzením.
- **Originální diskový obraz zůstane nezměněn.** Po aplikaci opravy pracovat s upraveným dokumentem v paměti a jako výchozí požadovat `Save As` do **jiné** cesty; nesmí se omylem přepsat původní zdroj tlačítkem `Save` nebo `Save As` se stejným názvem.
- Z hlavního `Tools` odstranit duplicitní `Inspect / Repair DSK Container...` a jeho samostatný file-picker; zachovat konzistentní ruční vstup v editoru **Disk → Repair Container…**, ideálně aktivní jen při konkrétních použitelných návrzích opravy.
- **Filesystem → Guided Repair…** ponechat samostatně. Řeší metadata FSMZ/MRS/P-CP/M, **nejde** o opravu DSK kontejneru.

Nedělej nový obecný recovery engine, neslibuj obnovu chybějících stop/sektorů a nepřepisuj existující dekodéry či filesystem.

### 2. ZJIŠTĚNÝ STAV KÓDU — OVĚŘIT V AKTUÁLNÍM HEAD

Při předchozí kontrole byly identifikovány tyto relevantní body:

| Soubor / metoda | Současná role | Co prověřit |
|---|---|---|
| `MainWindow.xaml` | `Tools` obsahuje `repairDskFileMenu` a `Open...` | Duplicitní hlavní položka Tools a její viditelnost |
| `MainWindow.xaml.cs` / `RepairDskFile_Click` | Vlastní `OpenFileDialog` → surové bytes → `DskRepairDialog` → `DskDocument.Open` | Přesun logiky do sdíleného toku Open, ne duplikace pickeru |
| `MainWindow.xaml.cs` / `button_Click_Open` a `AddFile` větev `.dsk` | Normální otevření `DskDocument.Open(filePath)`; u `BootOnly` existuje speciální načtení IPL jako MZF | **Zachovat dosavadní platné DSK a IPL workflow**; chybný DSK nesmí změnit význam Open pro zdravé obrazy |
| `Dsk/DskDocument.cs` / `Open`, `ReplaceContents`, `Save` | Přísné parsování, reprezentace upraveného dokumentu, ukládání | Jak označit repaired-in-memory a bezpečně vynutit jinou cestu Save As |
| `Dsk/DskImage.cs` / `Parse` | Rozpoznání Extended CPC DSK, geometrie a deklarované délky tracků | Výjimky a odlišení rozbitého/nespodporovaného vstupu |
| `Dsk/DskContainerRepairService.cs` / `Inspect(byte[])` | Plány `SafeDeterministic`, `UnsafeAmbiguous`, `NotRepairable` | Typy výsledků, skutečné actionable plány a diagnostika bez zavádějících hlášek |
| `DskRepairDialog.cs` | Otevře vždy dialog a tlačítka plánů, i když nemají `Preview` | Otevřít jen pro existující opravný návrh; zbytečné dialogy eliminovat |
| `DskEditorWindow.xaml` a `.xaml.cs` / `RepairContainer_Click` | Už nyní v `Disk` obsahují opravu; při absenci dokumentu opět picker | Omezit na aktivní DSK dokument; žádné druhé otevírání souboru |
| `Dsk/DskFilesystemRepairService.cs`, `RepairFilesystem_Click` | Opravy filesystemu, odděleně od kontejneru | Zachovat beze změny významu |
| `Dsk/DskExtendedLengthRepair.cs` | Samostatná kompatibilní oprava nulových deklarovaných délek | Neslučovat automaticky s Guided Container Repair bez analýzy a souhlasu |
| `MZTool.Tests/DskPhysicalRepairTests.cs` | Testy kontejnerové opravy a ochrany dat | Rozšířit o integrované otevření a bezpečné ukládání |

**Důležité:** Současný `DskContainerRepairService.Inspect` může nabídnout opravu geometrie/`tsize`, případně návrh zkrátit trailing bytes. Nedokáže automaticky rekonstruovat chybějící sektory, nejednoznačné C/H, FDC status či filesystem konflikty. Toto omezení zachovat.

### 3. NAVRHOVANÝ TOK OTEVŘENÍ — NEJDŘÍVE NAVRHNOUT, POTOM SCHVÁLIT

Pro `Open → .dsk` navrhni jedno sdílené rozhraní/koordinační metodu (např. **DskOpenCoordinator**, název podle stávající architektury), které **není závislé na WPF** v rozhodovací části. Následné dialogy mohou zůstat ve WPF adaptéru kvůli budoucí migraci na Avalonia.

#### A. Zdravý, podporovaný DSK

1. Načíst bytes jako dnes, provést běžné `DskDocument.Open`.
2. Pokud není potřeba žádná použitelná oprava kontejneru, pokračovat přímo dosavadním tokem.
3. **Žádný opravný dialog.** Zachovat byte-exact save/no-change chování, typ filesystemu a zvláštní Open cestu `BootOnly`/IPL.
4. U platného obrazu s pouhými trailing bytes nevyvolávat automaticky destruktivní opravu; lze nabídnout neblokující indikaci v editoru.

#### B. Opravitelné poškození DSK hlavičky

1. Pokud `DskDocument.Open` selže na strukturálním formátu **nebo** kontrola najde jednoznačný problém kontejneru, spustit `DskContainerRepairService.Inspect` nad **původními bytes**.
2. Zobrazit jen skutečně použitelné návrhy s `Preview != null`. Pojmenovat konkrétní chybu, které bajty/struktury se mění, co zůstává shodné a jaká rizika existují.
3. Odmítnutí/zavření dialogu nesmí měnit soubor ani aktivní dokument; pokud je původní obraz validní, uživatel ho stále může otevřít bez opravy.
4. Po **výslovném Apply** ověřit, že podkladové bytes jsou stále stejné jako při náhledu. Opravenou kopii znovu naparsovat a analyzovat; otevřít ji ve standardním DSK editoru jako **neuložený upravený dokument**.
5. Originál zůstává nezměněný; uložit opravu pouze přes **Save As s odlišnou cestou** (detaily v části 4).
6. Není-li dosažitelná oprava (neplatná signature, geometrie nejednoznačná, chybějící stopy), oznámit přesný diagnostický důvod bez vytvoření smyčky `Open → Repair → Open`.

#### C. Parsovatelný DSK s trailing bytes

1. Zachovat trailing bytes beze změny při normálním otevření i běžném uložení.
2. Volitelnou operaci `DSK_TRAILING_DATA` nabízet teprve v ručním **Disk → Repair Container…**, případně formou neblokujícího upozornění, **nikdy automatickým promptem při Open**.
3. Před destruktivním zkrácením vyžádat samostatné potvrzení, přesně zobrazit rozsah odstraněných dat a stále vynutit Save As do nové cesty.

#### D. Nejednoznačné / nepodporované obrazy

- Rozlišit: **Healthy / Repair available / Cannot safely repair / Unsupported or unparseable** (interní názvy lze přizpůsobit).
- Původní `NotRepairable: No deterministic container repair is needed or available` **nesmí být zobrazen jako disabled „oprava“ u zdravého obrazu**.
- Výjimky přístupu k souboru, oprávnění a diskového I/O nepřevádět na zprávu o poškození formátu.
- Nepřidávat heuristické či lossy opravy na základě pouhého výskytu `Track-Info` uvnitř dat.

### 4. OCHRANA DAT A UKLÁDÁNÍ

Toto je **kritická bezpečnostní podmínka**: dosavadní `DskDocument.Open(repair.Result, originalPath)` a následné `ReplaceContents` mohou vytvořit modified dokument se stále původní `FilePath`. Pouhé přemístění dialogu bez úpravy Save flow by mohlo umožnit **Save přepsat původní poškozený obraz**.

Požadované chování:

- U opravou vytvořeného dokumentu výslovně udržovat **provenienci originálního souboru** a flag/omezení „repair requires Save As“ (konkrétní API navrhni po auditu).
- `Save` na původní cestu zamítnout nebo vynutit `Save As` se skutečně odlišnou cestou. Zákaz musí platit i při ručním zadání původní cesty v dialogu `Save As`, nejen při výchozím doporučeném názvu.
- Je-li potřeba porovnání cest, řešit `Path.GetFullPath`, case-insensitivity ve Windows, symbolické odkazy/hardlinky tam, kde to lze rozumně ověřit; nespoléhat pouze na příponu či relativní cestu. Pokud nelze totožnost ověřit bezpečně, raději ukládání zamítnout.
- Doporučený název `*_repaired.dsk`; nový soubor nesmí přepsat existující cizí soubor bez standardního potvrzení a bezpečného postupu.
- Při `Cancel`, neúspěšném `Apply`, selhání parse, zavření dialogu či neúspěšném `Save As` zůstanou původní bytes beze změny.
- Nedělat automatické či skryté ukládání, nepřidávat ticho/zero-fill pro chybějící sektory, neupravovat C/H, ID nebo statusy bez výslovně doložené bezpečné operace.
- Pokud dočasně vznikají pracovní soubory, musí být bezpečně obslouženy a bez souhlasu nesmějí poškodit původní obraz.

**Před implementací této části Codex zvlášť vysvětlí, jaký je nynější Save/Save As tok a jak bezpečnost vynutí i při obejití UI.**

### 5. UI: KAM FUNKCE PATŘÍ

- **Hlavní `Tools`: odstranit** `Inspect / Repair DSK Container...`, protože je duplicitní a zbytečně vyžaduje zvláštní výběr souboru.
- **Hlavní `Open...`: zachovat** jako běžnou cestu pro `.dsk`; nepřidávat další ruční „Repair DSK File“ picker.
- **DSK editor → Disk:** ponechat **`Repair Container…`** jako pokročilou ruční operaci pro **aktuálně otevřený** DSK, ne pro HFE (HFE má vlastní fyzickou strukturu). Preferovat `IsEnabled` podle existence skutečně použitelného návrhu; nechat tooltip/popisek s vysvětlením nepřístupnosti.
- **DSK editor → Filesystem → Guided Repair…:** ponechat, pouze rozlišit popisky / tooltip od opravy kontejneru.
- **DskRepairDialog:** nevytvářet prázdné tlačítko `NotRepairable` bez akce; při absenci oprav nabídnout pouze status v místě, kde to dává smysl, nebo dialog vůbec neotevírat. Zachovat předběžný diff, explicitní Apply a samostatné potvrzení truncate.
- Status po Apply: např. `Container repair applied in memory. Original .dsk unchanged. Save As required.`
- Všechny nové texty v souladu s aktuální jazykovou konvencí UI (nyní převážně anglicky); nerozšiřovat současně lokalizační systém.

**Důsledně zachovat** normální workflow DSK/HFE, BootOnly IPL importu a otevření jiných souborů (`.mzf`, `.mzt`, `.mzq`, `.qdf`, `.wav` atd.). Úprava nemá změnit způsob zpracování zdravého `.dsk`.

### 6. TESTY A PŘIJÍMACÍ KRITÉRIA

Minimálně doplnit/regresně prověřit:

1. **Healthy DSK:** `Open` proběhne bez dialogu a bez změny jediného bajtu; round-trip validace původní image.
2. **Deterministicky opravitelné poškození** počtu stop (`0x30`) a tabulky `tsize` (`0x34...`): nabídka opravy, preview, potvrzené Apply otevře upravený DSK v editoru; bytes všech tracků odpovídají originálu.
3. **Cancel/No:** žádná změna původních bytes ani aktuálního dokumentu. Žádné neočekávané pracovní soubory.
4. **Trailing bytes:** zdravý parsovatelný obraz se otevře bez vynucené opravy; data za koncem jsou zachována. Ruční truncate vyžaduje druhé potvrzení.
5. **Ambiguous/missing track/wrong C/H, ID sektoru, FDC status:** žádný falešný safe plán, žádná automatická výroba náhradního obsahu.
6. **Invalid signature / non-DSK / zkrácená hlavička:** čitelná specifická diagnostika, bez cyklu dialogů a bez chybného přepsání.
7. **Save As security:** po Apply nelze `Save` do původní cesty ani `Save As` se stejnou cestou; nová odlišná cesta funguje; originální soubor má po operaci stejný SHA-256.
8. **Stale preview:** zdroj se mezi náhledem a Apply změnil → odmítnutí, nic neaplikovat.
9. **Existující BootOnly a IPL import:** pro platné obrazy nezměněná sémantika běžného `Open`.
10. **HFE a ostatní formáty:** žádná regrese, hlavní Tools položka skutečně odstraněna a editorová Disk položka používá aktuální dokument.
11. **Filesystem Guided Repair:** stále funguje nezávisle, oprava kontejneru se neplete s opravou souborového systému.
12. **UI smoke testy:** pokud jsou v projektu vhodné scénáře, ověřit `Open`, zachování práce s již upraveným dokumentem, viditelnost/aktivaci menu a návrat ze zavřeného dialogu.

Použij existující `MZTool.Tests/DskPhysicalRepairTests.cs` a příslušné DSK/UI smoke projekty, nevytvářej bezdůvodně duplicity; po schválení proveď build a odpovídající testy. Uveď explicitně **PASS/FAIL/SKIP** a které scénáře byly ověřeny jen synteticky, nikoli reálnými poškozenými obrazy.

### 7. DOPORUČENÉ ETAPY — KAŽDÁ VYŽADUJE NOVÝ SOUHLAS

#### Etapa A — READ-ONLY audit + návrh (zatím BEZ změn)

- Ověřit aktuální HEAD, všechny Open/Save/Save As cesty, zacházení s DSK a HFE, `BootOnly` speciální větev, current Tools/Disk UI a testy.
- Navrhnout konkrétní změny metod, nová rozhraní a seznam dotčených souborů.
- Vysvětlit, jak se rozliší zdravý/poškozený/nejednoznačný kontejner, včetně trailing bytes.
- Výslovně posoudit stávající riziko přepsání originálu po `ReplaceContents`.
- **Ukončit etapou s žádostí o schválení implementace.**

#### Etapa B — bezpečný integrátor Open + Save As ochrana

- Implementovat odsouhlasený tok v portable/core logice a napojení na stávající `Open`/drag-and-drop.
- Vynutit nepoškození originálu a oddělenou výstupní cestu.
- Doplnit testy rozhodování, Apply, cancel a Save As.
- **Zastavit a žádat o souhlas před UI cleanup etapou.**

#### Etapa C — UI cleanup a zjednodušení

- Odstranit duplicitní hlavní `Tools → Inspect / Repair DSK Container...` a nepoužitý handler.
- Upravit `Disk → Repair Container…`, dialogové stavy a statusy pro aktuální dokument.
- Zachovat `Filesystem → Guided Repair…`.
- Ověřit běžné workflow a regresní/UI testy.
- **Po dokončení report a žádost o další souhlas, pokud je cokoli dalšího potřeba.**

### 8. CO NEMĚNIT

- Žádné měnění sektorových dat, časování, bootstrapu či formátů DSK/HFE/QD bez přímého vztahu k cíli.
- Žádný blanket refactoring `MainWindow` nebo `DskEditorControl`; pouze změny nutné pro integraci a udržitelnost.
- Nesmí se změnit výsledný obraz po běžném otevření/uložení bez opravy.
- Žádné automatické přepisování originálního souboru nebo „oprav všechno“.
- Neřešit přechod WPF → Avalonia nyní; pouze pokud se zavádí nová rozhodovací logika, držet ji odděleně od UI, aby šla znovu použít.

### 9. POŽADOVANÁ PRVNÍ ODPOVĚĎ CODEXU

Ve své **první odpovědi** Codex dodá **bez úprav projektu**:

1. Číslo aktuálního HEAD a souhrn zjištěného workflow `Open .dsk` / `Disk Repair` / `Save` / `Save As`.
2. Přesný návrh rozhodovacího toku a návrh ochrany původního DSK.
3. Soubory určené ke změně, rozdělené na nutné/volitelné.
4. Identifikovaná rizika a plán regresních testů.
5. Jednoznačnou otázku **„Souhlasíte s implementací etapy B?“**.

**Dokud uživatel výslovně nepotvrdí, nesmí Codex nic měnit.**

---

## ZÁVĚREČNÁ POŽADOVANÁ PRVNÍ ODPOVĚĎ CODEXU (JEDNO ZADÁNÍ)

1. Uveď **aktuální commit HEAD, pracovní změny** a zda se již některé požadavky realizovaly; nic nepřepisuj.
2. **COM audit:** kde se rozpoznává ZX0/ZX7, jak se vykazuje do `NativeProgramImage.Compression`, dnešní sémantika `Keep / None / Auto` a přesné navržené popisky.
3. **DSK audit:** proč se otevírání poškozeného `.dsk` může zastavit na parseru, kde je redundantní menu v `Tools`, jak jsou řešeny `Disk → Repair Container`, `Filesystem → Guided Repair` a všechny Save/Save As cesty.
4. Dodej **konkrétní seznam dotčených souborů** odděleně pro COM/DSK, rizika, testovací plán, a samostatné implementační etapy. Vysvětli, jak bude DSK originál chráněn také proti obejití UI.
5. **ZASTAV SE a požádej o výslovný souhlas nejprve s COM-B nebo DSK-B podle rozhodnutí uživatele.** Nic před schválením needituj, netestuj zapisujícími nástroji, nevytvářej commity.

**Akceptace celého zadání:** oba cíle splněny bez regresí: COM zachovává binární chování, DSK Open opravuje jen explicitně a bezpečně, hlavní Tools nepřekáží duplicitní opravou, originál DSK zůstává beze změn, UI zobrazí pravdivou diagnostiku a testy jsou reportovány.
