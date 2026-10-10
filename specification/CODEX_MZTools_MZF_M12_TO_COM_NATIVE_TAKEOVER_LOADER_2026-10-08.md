# Zadání pro Codex — MZTools: MZF/M12 → .COM přes one-way native takeover loader

**Datum:** 2026-10-08  
**Repozitář:** `bales0/MZTools`

---

# 0. Cíl

Doplň do MZTools funkci pro převod:

```text
MZF / M12
-> CP/M .COM
```

ale ne jako klasický CP/M program.

Výsledný `.COM` má fungovat jako:

```text
CP/M loader/bootstrap
-> definitivně opustí CP/M
-> nastaví MZ-800 do native/monitor-like stavu
-> zavede původní MZF/M12 payload na jeho LOAD adresu
-> skočí na EXEC
-> návrat do CP/M se neočekává
-> RESET je normální způsob ukončení hry
```

Toto je hlavní architektura.

---

# 1. DŮLEŽITÁ ZMĚNA OPROTI PŘEDCHOZÍMU NÁVRHU

Neřeš návrat do CP/M.

Po takeover point:

```text
CP/M preservation = irrelevant
```

Je povoleno přepsat:

```text
CCP
BDOS
BIOS
CP/M zero page
CP/M stack
další CP/M pracovní oblasti
```

pokud to původní native MZ program potřebuje.

Podmínka:

- bootstrap nesmí zničit sám sebe dřív, než dokončí přechod,
- musí zůstat dostupný zdroj payloadu, dokud není program kompletně zaveden.

---

# 2. ŽÁDNÁ KOMPRESE V PRVNÍ VERZI

První implementace:

```text
NO compression
NO ZX0
NO ZX7
NO packed payload
```

Výstup:

```text
[stage0 bootstrap]
[stage1 bootstrap]
[raw original payload]
```

Důvod:

- jednodušší debug,
- jednodušší HW validace,
- menší počet proměnných,
- snazší porovnání s historickým MZX/MZRUN,
- přesnější diagnostika overlap problémů.

Komprese může být budoucí volitelná optimalizace až po ověření nekomprimované verze na reálném HW.

---

# 3. Hlavní model

Použij společný interní model:

```text
NativeProgramImage
```

například:

```text
SourceType
Name
Segments[]
EntryPoint
MachineMode
RequiredMemoryMap
RequiredInitialState
Dependencies
Warnings
```

Segment:

```text
Address
Length
Data
```

MZF:

```text
1 segment
Address = LOAD
Data = MZF body
EntryPoint = EXEC
```

M12:

- použij existující parser, pokud aktuální HEAD již M12 podporuje,
- jinak analyzuj skutečný formát,
- převáděj jen pokud lze jednoznačně získat:
  - segmenty,
  - LOAD,
  - EXEC / entrypoint,
  - pořadí,
  - dependency informace.

Nedělej:

```text
extension == .m12 => assume MZF
```

---

# 4. LOW/HIGH bootstrap detector

Implementuj stejný princip jako u loader selection v projektu SD2CMT2:

```text
analyze target address ranges
-> select loader placement that does not conflict
```

Neřeš jen samotné `LOAD`.

Pro každý segment vytvoř interval:

```text
targetStart
targetEnd
```

Bootstrap candidate má vlastní interval:

```text
loaderStart
loaderEnd
```

Candidate je validní jen pokud:

```text
no overlap with any target segment
```

---

# 5. Loader candidates

První verze minimálně:

```text
LOW loader
HIGH loader
```

Volitelně může být backend připraven na:

```text
TOP loader
CUSTOM scratch
```

ale není nutné je implementovat, pokud LOW/HIGH pokryjí bezpečně první verzi.

Nevkládej konkrétní adresy LOW/HIGH natvrdo bez ověření aktuální memory mapy cílového CP/M a MZ-800.

Loader placement má pocházet z explicitního target profile.

---

# 6. Target CP/M profiles

Zaveď minimálně:

```text
P-CP/M80
CP/M 4.1
CP/M 4.2
Custom / conservative
```

Profil má definovat:

```text
COM load base
safe bootstrap candidate ranges
available temporary ranges
protected areas while CP/M is still active
native takeover requirements
known BIOS/BDOS locations if relevant before takeover
```

Důležité:

CP/M areas jsou chráněné jen do takeover point.

Po takeover point už chráněné být nemusí.

---

# 7. Stage 0

CP/M načte `.COM` na:

```text
0x0100
```

Stage 0 má být co nejmenší.

Jeho úloha:

1. získat embedded metadata,
2. ověřit zvolený loader plan,
3. vybrat LOW/HIGH loader,
4. přesunout Stage 1 do bezpečné oblasti,
5. předat mu:
   - source payload address,
   - payload size,
   - target segments,
   - entrypoint,
   - target machine profile,
6. jump na Stage 1.

Stage 0 nesmí provádět složité operace.

---

# 8. Stage 1

Stage 1 už běží mimo oblast cílové hry.

Jeho úloha:

```text
1. případně DI
2. připravit native MZ-800 memory map
3. nastavit potřebný HW state
4. překopírovat payload/segmenty na jejich native adresy
5. nastavit případný stack / registry state
6. jump EntryPoint
```

Po bodu:

```text
native takeover started
```

se CP/M nesmí používat.

Žádné další BDOS call.

---

# 9. Overlap-safe copy

Payload je uložen uvnitř COM bufferu.

Je nutné analyzovat překryv:

```text
source range
destination range
```

Použij:

```text
LDIR
```

pokud forward copy je bezpečná.

Použij:

```text
LDDR
```

pokud backward copy je bezpečná.

Pokud jeden směr nestačí nebo je více segmentů:

- zvol pořadí segmentů,
- případně přesun Stage 1 jinam,
- nebo vstup označ jako Unsupported.

Nevytvářej náhodné memcpy pořadí.

---

# 10. Loader detector nesmí sledovat jen LOAD

Pro MZF typicky:

```text
gameStart = LOAD
gameEnd   = LOAD + SIZE - 1
```

Pro M12:

```text
all segment ranges
```

Loader candidate je validní pouze pokud nezasahuje do žádného cílového segmentu.

Příklad:

```text
if LOW does not overlap any target:
    use LOW
else if HIGH does not overlap any target:
    use HIGH
else:
    Unsupported
```

Volitelně:

```text
else if TOP safe:
    use TOP
```

---

# 11. Native machine state

Nejdůležitější část není kopírování dat, ale správný přechod:

```text
CP/M state
-> native MZ program state
```

Proto reverse-engineer historické utility:

```text
MZX.COM
MZRUN.COM
RUNMZFN.COM
MZXCONV.COM
MZXBACK.COM
```

Zajímají nás zejména kroky těsně před spuštěním MZF.

Zjisti:

```text
memory banking
monitor ROM mapping
RAM mapping
VRAM mapping
MZ-700/MZ-800 display mode
interrupt mode
DI/EI
SP
8255/PPI
keyboard
PIT/timers
video controller
sound-related state
monitor work areas
register initialization
jump to EXEC
```

Cílem není kopírovat historický program byte-for-byte.

Cílem je zjistit:

```text
jaký machine state MZX/MZRUN vytvoří před spuštěním native programu
```

---

# 12. Historický research report

Před implementací HW transition části vytvoř:

```text
docs/MZF_CPM_NATIVE_TAKEOVER_RESEARCH.md
```

Pro každou utilitu:

```text
filename
size
SHA-256
source/archive
command syntax
MZF header handling
memory copy logic
memory map changes
I/O ports
interrupt handling
stack handling
ROM mapping
final jump logic
return-to-CP/M behavior if any
```

Minimálně:

```text
MZX.COM
MZRUN.COM
RUNMZFN.COM
MZXCONV.COM
MZXBACK.COM
```

Pokud utility nelze redistribuovat, neukládej binárky do repo.

Ulož pouze:

```text
hash
source URL/path
analysis
```

---

# 13. MZF parser

Standardní MZF:

```text
0x00       type
0x01..0x11 filename
0x12..13   size LE
0x14..15   LOAD LE
0x16..17   EXEC LE
0x18..7F   reserved/comment
0x80..     body
```

Použij přesně deklarovanou body size.

Rozliš:

```text
payload
trailing padding
trailing garbage
multipart / extra records
```

Trailing data nesmí být automaticky přidáno do COM payloadu.

---

# 14. M12

M12 převáděj přes stejný:

```text
NativeProgramImage
```

backend.

Pokud M12 obsahuje více segmentů:

```text
Segment A
Segment B
...
EntryPoint
```

loader detector musí zohlednit všechny.

Pokud formát M12 neumožňuje jednoznačně určit native load model:

```text
Unsupported
```

Nepoužívej heuristický conversion bez diagnostiky.

---

# 15. MZF/M12 analyzer v MZTools

Přidej např.:

```text
Tools ▼
  MZF/M12 -> COM...
```

nebo podle aktuálního UI auditu.

Zobraz:

```text
Source:
  MZF / M12

Name
Type
Payload size

Segments:
  start
  end
  size

EntryPoint

Target CP/M:
  P-CP/M80 / 4.1 / 4.2 / ...

Selected bootstrap:
  LOW
  HIGH
  Unsupported

Source payload range inside COM
Destination ranges
Overlap strategy:
  LDIR
  LDDR
  multi-segment ordered copy

Native takeover:
  Yes

Return to CP/M:
  No

Exit:
  RESET
```

---

# 16. Warnings

Příklad reportu:

```text
FLAPPY.MZF

LOAD: 1200h
END:  A8FFh
EXEC: 1200h

Bootstrap:
  HIGH

Native takeover:
  YES

CP/M will be destroyed:
  YES

Return to CP/M:
  NO

Exit:
  RESET

Result:
  Convertible
```

To není chyba.

Naopak chyby:

```text
No safe loader placement
Payload cannot be copied without destroying source
Unknown M12 layout
Unknown required bank state
Multipart dependency unresolved
```

---

# 17. CP/M memory conflicts po takeover nejsou automaticky chyba

Starší návrh nesmí odmítat program jen proto, že:

```text
payload overwrites BDOS
payload overwrites BIOS
payload overwrites CCP
payload overlaps CP/M work area
```

Pokud k přepsání dojde až po:

```text
Stage 1 active
no more CP/M calls
```

je to validní.

Analyzer má místo chyby zobrazit:

```text
CP/M overwritten after native takeover: YES
```

---

# 18. Kdy je program Unsupported

Odmítni pokud:

```text
no safe LOW/HIGH bootstrap placement
source payload is destroyed before copy completes
target memory layout cannot be made available
unknown required memory banking
unknown M12 segment model
unresolved multipart loader dependency
requires tape data after startup
requires another file/block that is not embedded
entrypoint cannot be resolved
```

Absence návratu do CP/M není důvod k odmítnutí.

---

# 19. Testy

## Loader placement

```text
LOW safe -> LOW selected
HIGH safe -> HIGH selected
LOW conflict / HIGH safe -> HIGH
HIGH conflict / LOW safe -> LOW
both conflict -> Unsupported
multi-segment overlap detection
```

## Copy direction

```text
non-overlap
forward overlap -> LDIR
backward overlap -> LDDR
unsafe impossible overlap -> reject
```

## Stage relocation

```text
stage0 relocates stage1 correctly
stage1 survives payload copy
stage1 not inside target segment
```

## MZF

```text
valid single-file MZF
invalid header
truncated body
trailing data ignored/preserved as metadata only
LOAD/EXEC parse
```

## M12

```text
known valid M12
segment extraction
entrypoint
invalid/unsupported M12
```

## Native takeover

Pokud emulator umožňuje instrumentation:

```text
run generated COM
stop at target EXEC
compare machine state
with historical MZX/MZRUN runner
```

---

# 20. HW test workflow

Výsledný report musí obsahovat:

```text
input SHA-256
output SHA-256
source type
segment list
entrypoint
target CP/M profile
selected loader
copy direction
native transition profile
```

HW test:

```text
generate GAME.COM
-> insert to P-CP/M80 / CP/M disk
-> boot actual MZ-800
-> run GAME
-> game takes over machine
-> RESET to exit
```

Toto je očekávané chování.

---

# 21. První testovací hry

Pro první HW ověření vyber jednoduché:

```text
single-file
machine code
one segment
LOAD == EXEC preferred
no multipart
no secondary tape load
```

Nezačínej komplikovaným multipart title.

Po ověření jednoduchého případu pokračuj Flappy nebo jiným reálným programem.

---

# 22. ŽÁDNÁ KOMPRESE

Znovu explicitně:

```text
Do NOT add compression in first implementation.
```

Pokud se `.COM` nevejde do dostupného CP/M load area:

```text
Program too large for selected CP/M profile
```

je správný výsledek první verze.

Teprve reálná data z HW testů mohou později ospravedlnit:

```text
compression
external .DAT
streaming loader
```

---

# 23. Architektura

Doporučené vrstvy:

```text
MzfNativeImageParser
M12NativeImageParser
NativeProgramImage
NativeLoaderPlacementAnalyzer
CpmTargetProfile
NativeTakeoverProfile
MzNativeComBuilder
MzNativeComReport
```

Názvy nejsou povinné.

Business logiku nedávej do UI code-behind.

---

# 24. UI

Novou funkci nepřidávej jako další samostatné top-level tlačítko, pokud je toolbar už přeplněný.

Preferuj současný dropdown pattern:

```text
Tools ▼
```

např.:

```text
Tools ▼
    MZF/M12 -> COM...
```

nebo tematickou skupinu podle aktuálního stavu UI.

---

# 25. Acceptance criteria

Hotovo pokud:

1. MZF se parsuje na `NativeProgramImage`.
2. M12 se parsuje na stejný model, pokud je jeho layout známý.
3. Loader placement analyzer volí LOW/HIGH podle všech cílových intervalů.
4. Stage 0 přesune Stage 1 do bezpečné oblasti.
5. Stage 1 již nepotřebuje CP/M.
6. Po takeover lze přepsat CP/M oblasti.
7. Payload copy používá správně LDIR/LDDR podle overlapu.
8. Jump jde na původní EXEC.
9. Návrat do CP/M není implementační požadavek.
10. RESET je podporovaný/zdokumentovaný exit.
11. Není použita komprese.
12. Neexistuje tiché best-effort conversion.
13. Unsafe layout = Unsupported.
14. Historical MZX/MZRUN je analyzován kvůli native machine-state transition.
15. `MZXCONV.COM`/`MZXBACK.COM` jsou analyzovány, pokud jsou binárky dostupné.
16. Výstupní report má SHA-256.
17. Existují unit tests pro placement, overlap a parser.
18. Alespoň jeden generated COM je ověřen v emulatoru.
19. Poté je připraven pro reálný HW test.
20. Stávající MZTools workflow nemá regresi.

---

# 26. Priority

```text
1. správný native MZ state
2. bezpečný loader placement
3. správné překopírování payloadu
4. přesný EXEC
5. diagnostika
6. HW ověření
7. až potom rozšiřování kompatibility
```

Hlavní princip:

> CP/M slouží pouze jako prostředek k načtení `.COM`. Jakmile běží bezpečně relokovaný Stage 1, CP/M může být definitivně opuštěn a native MZ program převezme celý stroj.
