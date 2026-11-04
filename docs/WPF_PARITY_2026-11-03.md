# MZTools · návrat k WPF vzhledu a ovládání · 3. 11. 2026

Aktuálním zadáním je WPF reference, nikoliv původní vizuální návrh F2. Původní
WPF aplikace a Core/Capture algoritmy zůstávají beze změn. Úpravy jsou v Avalonia
frontend adaptéru; existující rozpracované parity opravy byly zachovány a doplněny.

## Provedené změny

- Hlavní okno: výchozí 1320×600, minimum 900×480, WPF toolbar a jeho názvy,
  stavy příkazů, neutrální barvy, Segoe UI s platformními fallbacky, velikost
  písma 12, kompaktní tlačítka, tabulky, checkboxy, expandery a stavový řádek.
  Vývojový dashboard/navigace nejsou běžnou pracovní obrazovkou.
- Tape/QuickDisk Files: stejné sloupce Type / File name / Size / Size(Hex) /
  Trailing / Start / Exec / Compression / Description / Loader / Speed.
  Název se edituje v řádku; Loader/Speed používají původní Core kompozici
  a hromadný výběr. Neznámé header/payload/trailing bajty se nemění.
- Hex Browser je samostatné okno a zahrnuje celý záznam i trailing data,
  nikoliv pouze první 4096 B. Files/Disk Map zůstávají hlavní navigací.
- Disk používá menu Disk / Filesystem / Boot / System / Tools. Všechny dosavadní
  diskové nástroje zůstávají dostupné. Copy from another disk nově poskytuje
  přímý výběr zdroje/souborů, kolizí, Core preview a explicitní Apply.
- Hex, fyzický track, comparison, structure a report jsou pomocná okna.
  DSK/HFE Visual Inspector má Disk Surface se stranami vedle sebe, Track /
  Sector, Issues, Statistics, Tracks, MFM detail, sdílený výběr a export reportů.
  QuickDisk má samostatný read-only inspector se stejným Core snapshotem.
- Audio a Batch mají samostatná okna. Waveform zůstává při procházení dlouhých
  analýz viditelný; dlouhé detailní panely mají vlastní scrollování.
- Recorder přebírá WPF kompozici: Input / recording settings, WAV destination
  a Browse, Monitor / Record / Stop / Cancel, Actual input / processing,
  Live signal / Recordings / Diagnostics / Head alignment / Live pulses.
  Levels jsou nad dělitelným waveform/FFT; clipping threshold a reset hold
  používají původní Capture API. Live graphy zachovávají bounded snapshoty.
  Fit history používá celou dvousekundovou historii; audio zoom přebírá
  původní WPF kroky a drží kurzorový anchor i souřadnice source frame.
- Explicitní probe přijme vlastní rate/container/valid bits/channels, pouze
  Open/Dispose session ověří formát; Start se nevolá a archiv nevzniká.
  Zařízení se nadále volí výslovně. Default source je platformní backend,
  synthetic demo zůstává samostatnou volbou. Konstrukce okna nic neotevírá.
- Předchozí opravy fyzického track zoomu/scrollbarů, QuickDisk forensics,
  stereo alignment bez resetu viewportu a Native COM byly integrovány.

## Ověření

Přesná finální čísla, TRX/SHA-256, native checks a zdrojové hashe jsou v
[strojovém reportu](WPF_PARITY_VALIDATION_2026-11-03.json).
Skutečné WPF referenční rendery vznikly spuštěním nezměněné původní aplikace,
včetně syntetického recorderu bez přístupu k hardware. Nové Avalonia rendery
pocházejí ze skutečných Windows/WSLg oken a Skia headless testů:
[porovnávací galerie](ui-design-system/wpf-parity-2026-11-03/index.html).

Testy pokrývají inline rename/profile multi-selection, byte identity, Save stav,
úplný Hex Browser, všechny diskové nástroje v menu, velikosti viewportu,
samostatné inspektory, source změny/cancel/collision preflight, native probe
bez startu, destination source ochranu, dokončení WAV a recorder lifecycle.
Původní Core/Capture/WPF zdroje jsou kontrolovány proti HEAD; jejich rozsáhlé
historické výsledky nejsou vydávány za nové běhy tohoto UI-only zásahu.

## Buildy a rozsah skutečného ověření

Nové self-contained archivy jsou v
`TestResults/WpfParity-2026-11-03/distribution`; starší říjnové archivy zůstávají
zachované. Windows/Linux x64 mají skutečné lokální testy a spuštění rozbalených
balíčků. Ostatní RID jsou cross-buildy, nikoliv důkaz spuštění na těchto strojích.
Linux vyžaduje `libportaudio2 libsndfile1`; .NET je v archivu.

Vnější rámy oken, systémové pickery, dostupnost fontů a rasterizace textu se
řídí OS. Galerie není automatický důkaz pixelové identity všech dialogů.
Fyzické vstupy, macOS/ARM, screen reader a všechny skutečné monitorové DPI
nebyly v této lokální validaci pokryty. Balíčky nejsou podepsané/notarizované.
Stabilní WPF zůstává dostupné; neproběhl commit ani publikace.
