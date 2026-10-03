# Zadání pro Codex: MZTools – format-aware konverze, Install Boot/System a dokončení DSK nástrojů

Datum: 2026-10-03  
Projekt: `https://github.com/bales0/MZTools`  
Cílová větev: aktuální `main`

Výchozí stav při přípravě zadání:
- DSK editor již má Files + Disk Map,
- existuje DSK Analyzer s bloky/issues a exportem reportu,
- existuje QuickDisk Disk Map,
- DSK backend rozlišuje `SingleIpl`, `MultiIpl`, `Fsmz`, `Cpm`, `Mrs`, `BootOnly`, `Raw`,
- CP/M má konkrétní DPB/layout presety,
- současný projekt stále uvádí jako nedodělané zejména CP/M attribute editing, MRS address editing a writable in-place hex editor,
- předchozí obecný koncept `Make Bootable` byl opuštěn, protože umožňoval nesmyslné kombinace formátu média a boot/system layoutu.

Toto zadání NESMÍ znovu zavést univerzální „libovolný disk -> libovolný boot systém“.

## 1. Hlavní cíl

Doplnit MZTools o několik navazujících funkcí:

1. **Format-aware Convert Format...**
2. **Format-aware Install Boot/System...**
3. **DSK Compare / Diff**
4. **Filesystem Structure Inspector**
5. **CP/M attribute + user area editing**
6. **MRS LOAD/EXEC metadata editing**
7. **Bezpečný writable hex editor pro DSK**
8. **Analyzer repair suggestions / explanation**, ale bez automatického repair/defrag

CLI do tohoto tasku NEPATŘÍ.

Všechny nové operace musí vycházet ze skutečně detekovaného:
- filesystemu,
- geometrie,
- DPB,
- fyzického track/sector layoutu,
- kapacity,
- alokačního modelu.

## 2. Zásadní pravidlo pro boot/system funkce

`Install Boot/System...` smí nabídnout pouze systémové varianty, které jsou skutečně kompatibilní s právě otevřeným image.

Například `Single IPL` / `Multi IPL` NESMÍ nikdy nabídnout CP/M 2.x/4.x.

Stejně tak `FSMZ / MZ-BASIC` nesmí dostat CP/M system tracks jen proto, že fyzická kapacita vypadá podobně.

Předchozí chyba vznikala přesně tímto:
- byl vytvořen jeden typ média,
- následně se na něj pokusil instalovat boot/system area určený pro jiný formát.

Toto se nesmí opakovat.

## 3. Nová centrální capability vrstva

Vytvoř UI-independent vrstvu, například:

```csharp
DskCapabilityService
DskConversionProfile
DskBootSystemProfile
DskCompatibilityResult
```

API například:

```csharp
IReadOnlyList<DskConversionProfile> GetAvailableConversions(DskDocument document);
IReadOnlyList<DskBootSystemProfile> GetAvailableBootSystems(DskDocument document);
DskCompatibilityResult CanConvert(DskDocument document, DskConversionProfile profile);
DskCompatibilityResult CanInstallBootSystem(DskDocument document, DskBootSystemProfile profile);
```

UI nesmí obsahovat hardcoded rozhodování typu `if (filesystem == Cpm)` tam, kde to lze dát do capability vrstvy.

## 4. Compatibility key

Při rozhodování neber pouze `DskFileSystemType`.

Kompatibilita musí používat minimálně:

```text
filesystem type
track count
side count
sector count / track
sector sizes
sector IDs
interleave / physical sector map
boot-track geometry
CP/M DPB
CP/M OFF
CP/M block size
CP/M DSM
directory allocation
physical track map
physical sector map
```

Podle potřeby přidej další hodnoty.

## 5. Convert Format... – základní filozofie

Rozliš:

### A. Skutečná konverze příbuzného formátu
Například:

```text
FSMZ 63 entries
-> IPLDISK / Extended FSMZ 127 entries
```

pokud ji lze udělat bezpečně se zachováním souborů.

### B. Přenos souborů do nového média
Například:

```text
MRS -> CP/M
CP/M -> FSMZ
```

To není in-place filesystem conversion. V UI to musí být jasně označeno jako vytvoření nového image a kopie kompatibilních souborů.

### C. Nesmyslné kombinace
Například:

```text
CP/M -> Single IPL
MRS -> Multi IPL
Single IPL -> CP/M filesystem conversion
```

Tyto kombinace vůbec nenabízet jako běžný `Convert Format`.

## 6. Convert Format... nesmí defaultně měnit otevřený image in-place

Pokud se mění fyzická geometrie, filesystem, directory structure, block size nebo DPB, vytvoř **nový DSK document/image**.

Doporučený workflow:

```text
Disk -> Convert Format...

Source:
  LEC CP/M DD 720 KiB

Target:
  P-CP/M80 320 KiB

Mode:
  Create new image and copy compatible files

Files:
  24 compatible
  2 skipped / incompatible

[Convert and Save As...]
```

Původní document musí zůstat beze změny.

## 7. Povinná první bezpečná konverze

Implementuj minimálně:

```text
MZ-BASIC / FSMZ 63 entries
<-> IPLDISK / Extended FSMZ 127 entries
```

ale pouze pokud:
- geometrie je kompatibilní,
- všechny soubory se vejdou,
- metadata jsou reprezentovatelná,
- directory expansion/shrink nezničí data.

Při převodu 127 -> 63:
- pokud je použito více než 63 položek, konverzi odmítni;
- pokud data zasahují do oblasti, která se má stát standardní directory/data layout oblastí, bezpečně přeuspořádej jen pokud je implementována transakční relokace;
- jinak odmítni s konkrétním důvodem.

Neprováděj tichou ztrátu souborů.

## 8. Obecný file-transfer converter

Pro nesouvisející filesystemy implementuj samostatný backend, například:

```csharp
DskFileTransferService
```

Princip:

```text
source filesystem entries
-> neutral transferable file model
-> target filesystem insert
```

Neutral model může obsahovat:

```csharp
Name
Extension
Data
FileType
LoadAddress
ExecuteAddress
User
ReadOnly
System
Archived
OriginalMetadata
```

Target rozhodne, které atributy umí zachovat.

## 9. Před konverzí zobrazit ztráty metadat

Například CP/M -> FSMZ:

```text
Will preserve:
  filename
  data

Will lose:
  CP/M user area
  RO attribute
  SYS attribute
  ARC attribute
```

Nevymýšlej převod metadat, která cílový filesystem nepodporuje. Uživatel musí dostat přehled před potvrzením.

## 10. Capacity preflight

Před samotnou konverzí proveď úplný dry-run:

```text
target directory capacity
target data capacity
file name compatibility
duplicate target names
block allocation
metadata representability
```

Výsledek například:

```text
24 files can be copied
2 files cannot be copied:
  LONGFILENAME.BIN -> target name not representable
  GAME.BIN -> insufficient contiguous FSMZ space
```

Default preferuj strict/safe chování: pokud nelze přenést vše, konverzi neprovádět bez explicitního potvrzení jiné politiky.

## 11. Install Boot/System... – pouze format-aware

Do DSK editoru přidej:

```text
Disk -> Install Boot/System...
```

Položka je skrytá nebo disabled, pokud aktuální formát nemá podporované systémové profily. Nikdy nenabízí nesouvisející systém.

## 12. Boot/system profily

Profil musí explicitně deklarovat kompatibilitu, například:

```csharp
DskBootSystemProfile
{
    Id
    DisplayName
    CompatibleFileSystem
    CompatibleDpbId
    CompatibleGeometry
    RequiredBootTrackLayout
    RequiredSystemTracks
    SourceKind
}
```

Profily nesmí být definovány jen textovým názvem.

## 13. CP/M profily musí být layout-specific

Rozliš minimálně:

```text
P-CP/M80 original 320 KiB
P-CP/M80 SDS/400
LEC CP/M DD 720 KiB
LEC CP/M HD 1.44 MiB
```

Pokud existují konkrétní verze, například `CP/M 2.3`, `4.1`, `4.2`, musí existovat konkrétní vazba verze + layout, například:

```text
CP/M 4.2 for LEC DD
CP/M 4.2 for LEC HD
```

pokud jsou skutečně dostupné a ověřené.

Nesmí existovat obecný profil `CP/M 4.2`, který lze aplikovat na libovolný CP/M disk.

## 14. System image source

Použij pouze:
- skutečná systémová data, která už existují v projektu,
- ověřený referenční DSK,
- nebo uživatelem vybraný source DSK.

Nevytvářej falešné CP/M boot/system bytes.

Pokud pro kombinaci není dostupný system image, označ ji `Not available` a nenabízej aktivní instalaci.

## 15. Reuse současné CP/M validace

Současný kód již obsahuje:
- `ImportBootSystemArea(...)`
- `ValidateMatchingCpmLayout(...)`
- `GetSystemPhysicalTracks(...)`
- kontrolu track geometrie a sector IDs.

Tuto logiku refaktoruj do capability/install služby. Neduplikuj ji v dialogu.

## 16. Install Boot/System nesmí měnit filesystem

Instalace:
- smí měnit boot/system area definovanou profilem,
- NESMÍ změnit DPB,
- NESMÍ změnit geometry,
- NESMÍ přeformátovat data tracks,
- NESMÍ změnit directory/data allocation.

Pokud source system vyžaduje jiný layout, vrať `Not compatible` a operaci neprováděj.

## 17. IPL single/multi

Pro `SingleIpl` a `MultiIpl` `Install Boot/System...` obecně nenabízej.

Tyto image už jsou specializované bootovací layouty.

UI může zobrazit:

```text
This image already uses a dedicated IPL loader layout.
Boot/System installation is not applicable.
```

Rozhodně nenabízet CP/M.

## 18. FSMZ / IPLDISK / MRS boot možnosti

Nenabízej žádnou boot variantu jen proto, že image fyzicky obsahuje podobnou geometrii.

Boot profil přidej pouze tehdy, pokud existuje:
- známý formát,
- ověřený loader/system image,
- přesně definovaná boot/system oblast,
- bezpečný způsob instalace bez poškození filesystemu.

Jinak je správný výsledek `No compatible boot/system installer available`.

## 19. DSK Compare / Diff

Přidej:

```text
Disk -> Compare with...
```

Uživatel vybere druhý `.dsk`.

Porovnání musí mít minimálně 3 úrovně:

```text
Container
Physical sectors
Filesystem
```

## 20. Compare – container level

Porovnej:
- track count,
- side count,
- creator,
- track block sizes,
- missing tracks,
- sector descriptor metadata,
- trailing data.

Zobraz jasně `same`, `changed`, `only in left`, `only in right`.

## 21. Compare – physical sector level

Porovnej podle bezpečné identity:

```text
physical track index
physical sector index
C/H/R/N
```

Zobraz:
- metadata changed,
- data changed,
- byte count changed,
- ST1/ST2 changed.

Při kliknutí zobraz byte diff.

Nepředpokládej, že stejné `R` znamená automaticky stejnou fyzickou pozici, pokud se změnilo pořadí descriptorů.

## 22. Compare – filesystem level

Pokud oba image mají kompatibilně rozpoznaný filesystem, porovnej:
- přidané soubory,
- odstraněné soubory,
- změněný obsah,
- změněné metadata,
- změněnou alokaci.

CP/M:
- user area,
- RO/SYS/ARC,
- extents,
- allocation blocks.

FSMZ:
- DINFO,
- directory slot,
- start block,
- size,
- lock,
- allocation bitmap.

MRS:
- file ID,
- FAT ownership,
- block count,
- LOAD/EXEC.

## 23. Compare selection synchronization

Kliknutí na změněný soubor/sektor:
- zvýrazní odpovídající oblast v mapě,
- nabídne `Hex diff`.

První verze Compare je čistě read-only. Žádné merge/apply patch.

## 24. Filesystem Structure Inspector

Přidej inspector dostupný z Disk Map nebo Files.

Doporučené záložky:

```text
Filesystem
Directory
Allocation
Raw structure
```

## 25. FSMZ Structure Inspector

Zobraz:
- DINFO raw + decoded fields,
- volume number,
- file-area start,
- used blocks,
- last block,
- bitmap,
- standard/extended directory,
- directory slot -> file,
- block -> owner.

Kliknutí na block zvýrazní Disk Map.

## 26. CP/M Structure Inspector

Zobraz DPB:

```text
SPT
BSH
BLM
EXM
DSM
DRM
AL0
AL1
CKS
OFF
block size
directory blocks
physical track map
physical sector map
```

Dále:
- raw directory entries,
- decoded extent number,
- RC,
- allocation pointers,
- file grouping,
- free/used block view.

Toto je diagnostika, ne DPB editor.

## 27. MRS Structure Inspector

Zobraz:
- FAT layout,
- reserved blocks,
- directory area,
- data start,
- file IDs,
- FAT block owner,
- raw directory entry.

## 28. CP/M attribute editor

Současný `DskFileEntry` již nese:

```text
User
ReadOnly
System
Archived
```

a UI je zobrazuje.

Doplň bezpečné editování:

```text
User area
RO
SYS
ARC
```

Preferuj dialog `File properties...` místo nekontrolovaného přímého binding zápisu.

## 29. CP/M atributy – pravidla

Při změně:
- změň všechny extenty daného logického souboru konzistentně,
- zachovej filename/extension,
- zachovej data blocks,
- nezměň RC,
- nezměň extent numbering,
- nevytvářej duplicate `(user,name,ext)` kolizi.

Změna User:
- pokud cílová user area již obsahuje stejné jméno, odmítni.

Rozšiř `CpmFileSystem` o explicitní metodu `UpdateAttributes(...)` nebo obecné `UpdateMetadata`.

## 30. MRS LOAD/EXEC editor

Současný UI LOAD/EXEC zobrazuje.

Doplň `File properties...` s LOAD/EXEC, ale pouze pokud MRS formát skutečně tato metadata ukládá.

Před implementací ověř přesný offset a význam v současném parseru/specifikaci.

Pokud se ukáže, že některá hodnota není v MRS nativně uložená:
- nevymýšlej ji,
- ponech ji read-only,
- zdokumentuj omezení.

## 31. Writable DSK hex editor

Současný `HexBrowser` je read-only.

Doplň bezpečný edit mode pro:
- vybraný raw physical sector,
- případně vybraný filesystem block.

Nezačínej plným image-wide hex editorem.

Workflow:

```text
Open selected sector
Unlock editing
Modify bytes
Show changed byte count
Apply
```

Před Apply zobraz změněné offsety a původní/nové hodnoty.

## 32. Hex edit – omezení podle role

Pokud sektor patří k `Directory`, `FAT`, `AllocationMap`, `System` nebo `Boot`, zobraz výrazné varování.

Nezakazuj expert edit absolutně, ale vyžaduj explicitní potvrzení.

Před Apply drž original sector buffer. `Cancel/Revert` nesmí měnit document.

## 33. Po hex editaci znovu sestavit stav documentu

Po Apply:
- označ document modified,
- obnov filesystem pohled,
- obnov analyzer snapshot,
- pokud byl změněn signature/directory/allocation/boot sektor, znovu ověř filesystem konzistenci.

Pokud filesystem již nelze bezpečně otevřít, zachovej image a přepni do bezpečného raw/BootOnly režimu dle stávající detekce.

## 34. Analyzer repair suggestions

Analyzer dnes správně nic neopravuje. Toto zachovat.

Přidej k issue volitelné:

```text
Suggested action
Repairability
```

Například:

```text
FSMZ_BITMAP_MISMATCH
Suggested action:
Rebuild allocation bitmap from directory entries.
Automatic repair: not implemented.
```

```text
CPM_CROSSLINKED_BLOCK
Suggested action:
Inspect both files; automatic repair cannot safely determine ownership.
```

```text
MRS_ORPHAN_FAT_BLOCK
Suggested action:
Block is referenced by FAT but no directory entry. Verify before freeing.
```

Návrh nesmí tvrdit, že oprava je bezpečná, pokud není jednoznačná.

## 35. Repair v tomto tasku NEIMPLEMENTOVAT

Stále nedělej:
- automatic repair,
- defrag,
- rebuild extents,
- free orphan blocks,
- cross-link resolver.

Pouze diagnostika + vysvětlení možné opravy.

## 36. Export reportu

DSK Analyzer už má `Copy report` a `Save report...`.

Rozšiř report o:
- capability summary,
- případně conversion preflight report,
- suggested actions.

Nepřidávej CLI.

## 37. UI návrh

Do DSK editoru nedávej příliš mnoho trvalých tlačítek.

Preferuj menu:

```text
Disk
  Convert Format...
  Install Boot/System...
  Compare with...
  File/Filesystem Properties...
```

V Files toolbaru:
- Properties...
- Hex view

V Disk Map:
- existing Hex view block
- Structure Inspector
- existing report controls

## 38. Capability UI

Pokud operace nedává smysl:
- ideálně ji disable,
- tooltip vysvětlí proč.

Například:

```text
Install Boot/System...
Disabled

Reason:
Dedicated MZTools multi-game IPL images already contain their own IPL loader.
```

## 39. Transakční operace

Convert i Install Boot/System musí pracovat přes pracovní kopii.

Nikdy:
- částečně nezapiš image,
- neměň otevřený document před dokončením validace.

Při chybě musí zůstat původní document byte-identical.

## 40. Testy – capability matrix

Přidej testy minimálně pro:

```text
SingleIpl
MultiIpl
FSMZ
IPLDISK
P-CP/M80
SDS/400
LEC DD
LEC HD
MRS
BootOnly
Raw
```

Ověř:
- které conversion profily jsou dostupné,
- které boot/system profily jsou dostupné,
- nesmyslné kombinace nejsou nikdy nabídnuty.

Explicitní regresní test:

```text
SingleIpl -> CP/M boot/system = unavailable
MultiIpl -> CP/M boot/system = unavailable
```

## 41. Testy – Install Boot/System

Pro každý skutečně dostupný profil:

1. vytvoř/načti kompatibilní image,
2. přidej několik souborů,
3. ulož jejich obsah + metadata,
4. proveď install,
5. reopen,
6. ověř:
   - filesystem type stejný,
   - DPB stejný,
   - geometry stejná,
   - directory/data beze změny,
   - soubory byte-identical,
   - změnila se pouze povolená boot/system oblast.

Nekompatibilní source:
- operace odmítnuta,
- document beze změny.

## 42. Testy – conversion

FSMZ <-> IPLDISK:
- empty,
- několik souborů,
- near-full,
- >63 entries při 127 -> 63,
- kolize / nedostatečná kapacita.

File-transfer conversion:
- metadata loss report,
- duplicate target names,
- insufficient capacity,
- unsupported filename,
- strict mode cancels before write.

## 43. Testy – CP/M attributes

Ověř:
- RO,
- SYS,
- ARC,
- user area,
- více extentů jednoho souboru,
- kolize po změně user area,
- data zůstávají byte-identical.

## 44. Testy – MRS metadata

Pokud LOAD/EXEC jsou skutečně podporována formátem:
- změna hodnot,
- reopen,
- hodnoty zachované,
- data a FAT beze změny.

Pokud ne:
- přidej test, že UI/backend editaci nenabízí.

## 45. Testy – writable hex

Ověř:
- edit data sector,
- apply,
- serialize/reopen,
- změna přesně na požadovaném offsetu,
- ostatní sektory byte-identical,
- cancel = žádná změna,
- invalid length = odmítnuto,
- metadata sector edit spustí refresh/analyzer.

## 46. Testy – Compare

Ověř:
- identical image -> no diff,
- one changed byte,
- changed sector descriptor,
- reordered sector descriptors,
- added/deleted file,
- changed CP/M attribute,
- changed FSMZ allocation,
- changed MRS FAT ownership.

## 47. Dokumentace

Aktualizuj README.

Přidej sekce:

```text
Format conversion
Install Boot/System
DSK Compare
Filesystem Structure Inspector
File properties
Writable sector hex editing
Analyzer suggestions
```

Výslovně vysvětli:

```text
Install Boot/System is format-aware.
It never converts an IPL disk into CP/M or vice versa.
Only systems compatible with the detected filesystem, DPB and physical layout are offered.
```

## 48. Co nedělat

- žádné CLI,
- žádný univerzální Make Bootable,
- žádné CP/M na SingleIpl/MultiIpl,
- žádné falešné system images,
- žádná in-place konverze geometrie bez nové target image,
- žádné tiché zahazování souborů,
- žádné automatické metadata guessing,
- žádný automatic repair/defrag,
- žádný flux/MFM editor,
- žádný full-disk unrestricted hex editor jako první krok.

## 49. Doporučené pořadí implementace

### Phase 1
Capability matrix + format-aware `Install Boot/System`

### Phase 2
`Convert Format...` + FSMZ/IPLDISK + file transfer service

### Phase 3
CP/M attributes + MRS metadata editor

### Phase 4
DSK Compare

### Phase 5
Filesystem Structure Inspector

### Phase 6
Writable sector/block hex editor

### Phase 7
Analyzer suggested actions

Každá fáze musí mít vlastní testy a nesmí rozbít předchozí.

## 50. Acceptance criteria

Úloha je hotová, pokud:

- capability service centrálně rozhoduje, co lze s daným DSK dělat;
- Single/Multi IPL nikdy nenabízí CP/M system install;
- CP/M system install je vázán na konkrétní DPB/layout;
- Install Boot/System nemění filesystem ani data;
- Convert Format rozlišuje skutečný filesystem conversion a file-copy-to-new-image;
- FSMZ 63 <-> IPLDISK 127 je bezpečně podporováno;
- před konverzí existuje capacity/metadata-loss preflight;
- DSK Compare umí container/sector/filesystem diff;
- existuje filesystem structure inspector;
- CP/M RO/SYS/ARC/User lze bezpečně editovat;
- MRS LOAD/EXEC lze editovat jen pokud to formát skutečně podporuje;
- existuje bezpečný writable sector/block hex workflow;
- analyzer nabízí vysvětlení/suggested action, ale nic automaticky neopravuje;
- všechny destruktivní operace jsou transakční;
- README odpovídá skutečnému chování;
- CLI nebylo přidáno.

## 51. Závěrečný report Codexu

Po dokončení uveď:

1. aktuální capability matrix;
2. všechny dostupné Convert Format kombinace;
3. všechny dostupné Install Boot/System profily;
4. přesnou vazbu CP/M systémů na DPB/layout;
5. které kombinace jsou explicitně zakázány;
6. jak funguje conversion preflight;
7. jaká metadata se při jednotlivých převodech zachovají/ztratí;
8. implementované CP/M a MRS property editace;
9. jak funguje Compare;
10. jak funguje writable hex edit;
11. jaké analyzer suggestions byly přidány;
12. seznam testů;
13. známá omezení;
14. potvrzení, že nebylo přidáno CLI;
15. potvrzení, že nebyl obnoven univerzální Make Bootable.
