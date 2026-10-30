# F3 – implementace Avalonia shellu a validace

29. 10. 2026. [Rozsah F3](F3_AVALONIA_SHELL_PLAN_2026-10-29.md) byl uživatelem
výslovně schválen. Uživatel následně schválil i skutečný vzhled implementace;
multiplatformní brána zůstává otevřená pro nedostupné macOS a nativní DPI ověření.

## Co je implementováno

`src/MZTools.Desktop` je samostatný `net10.0` executable s Avalonia 12.1.4.
Odkazuje na přenositelné Core, nikoli na WPF hostitele, NAudio nebo Windows Capture
adaptér. Ke Core nebylo nutné přidávat friend assembly ani měnit jeho algoritmy.
Původní funkční WPF aplikace zůstává dostupná.

Shell má menu/command bar, navigaci sedmi oblastí, dokumentové karty s modified
stavem, společný inspector, filtrovanou TableView, property form s validací,
report viewer, clipboard, error banner, progress a cancel. Primární modifikátor
Open/Save/Save As/Close je Ctrl na Windows/Linux a Command na macOS; F6 přepíná
oblasti focusu. macOS konfigurace je implementovaná, ale nebyla spuštěna.

Tokeny barev, numerických hodnot a typovaných radius/padding resources generuje
`docs/ui-design-system/tools/build_avalonia_tokens.py` z původního F2 `tokens.json`.
Kontrola `--check` ověřuje shodu generovaného AXAML. Fluent šablony mají upravený
výběr, řádky, hlavičky, vstupy a taby. Nevznikl nový MVVM/DI balíček.

Malé okno používá scrollovaný workspace a vlastněné inspector okno. Příkazy a stav
zůstávají v pevných lištách. Tabulka má vlastní scroll pro řádky i sloupce.
Samostatný test ověřuje dosažitelnost posledního řádku při 640 × 480.

## Hranice funkčnosti

Produkční `PendingDocumentLoader` dosud nepřipojuje žádný doménový editor. Open
na vybraném existujícím souboru vysvětlí nedostupnost adaptéru, nezaloží falešný
dokument a nezmění zdroj. Save/Save As zůstávají bez adaptéru zakázané.
Seznam dostupnosti modulů a report obsahují skutečný stav implementace shellu,
nikoli ilustrativní bajty, výsledky analýzy nebo falešnou capture session.

Obecný dokumentový koordinátor je funkční. Souborové testy používají řízené
testovací dokumentové adaptéry a skutečné dočasné soubory; adaptéry nejsou
registrované v produkční aplikaci. Formáty a serializéry se napojí až ve F4–F6.
Recorder, audio zařízení, waveform/disk renderery a OS audio/FLAC backendy
zůstávají v dalších fázích. Core a Capture produkční zdroje se v F3 nemění.

## Bezpečnost dokumentů

- Otevření se zveřejní až po úspěšném načtení; kontrola hashů odmítne změnu zdroje
  během loadu. Stejná cesta aktivuje existující kartu.
- Save As ověřuje kolizi s otevřeným dokumentem a potvrzení nahrazení existujícího
  cíle. Read-only zdroj vyžaduje odlišnou kopii.
- Zápis probíhá do nového souboru vedle cíle, následuje flush na disk, validace
  dokumentovým adaptérem a kontrola hashů před náhradou. Teprve úspěšná náhrada
  přepne cestu a uložený stav. Změna revize během ukládání zůstane modified.
- Chyba zápisu/validace, storno a zjištěná externí změna zachovají originál i dirty
  stav. Dočasný soubor se uklidí. Zápis přes symlinky/junctions je odmítnut.
- Zavření řeší Save/Discard/Cancel. Quit nejprve posoudí všechny dokumenty, takže
  pozdější Cancel neodstraní dříve posouzené karty. Souběžné operace jsou blokované.

Tato infrastruktura nenahrazuje specifické ochrany DSK repair; jejich bezpečnost
musí připojený adaptér dále zachovat. Hash kontrola a rename nejsou univerzální
transakce proti neřízenému souběžnému zapisovači mezi poslední kontrolou a rename.
Crash/power-loss odolnost konkrétních filesystémů, síťové shares, Windows ACL
a velká média vyžadují další validaci při portu reálných formátů. V F3 neexistuje
produkční zapisující adaptér, který by tyto budoucí integrace obcházel.

## Skutečné výsledky

Počty označují provedené případy na daném OS, nikoli počet unikátních metod.
Machine-readable evidence včetně důvodů skipů je v
[F3_VALIDATION_2026-10-29.json](F3_VALIDATION_2026-10-29.json).
Úplné TRX a provozní logy zůstávají lokálně v ignorovaném `TestResults/F3`.

| Sada / prostředí | Passed | Failed | Skip |
| --- | ---: | ---: | ---: |
| Legacy WPF testy, Windows | 1202 | 0 | 3 |
| Core, Windows | 411 | 0 | 1 |
| Core, Debian 13 / WSL | 411 | 0 | 1 |
| Capture, Windows | 210 | 0 | 0 |
| Capture, Linux | 210 | 0 | 0 |
| Windows Capture | 52 | 0 | 0 |
| Desktop unit/headless, Windows | 48 | 0 | 0 |
| Desktop unit/headless, Linux | 48 | 0 | 0 |
| Native Avalonia smoke, Windows | 15 kontrol | 0 | 0 |
| Native Avalonia smoke, X11 / WSLg | 15 kontrol | 0 | 0 |
| Původní Report / DSK / Audio WPF smoke | PASS | 0 | 0 |
| Původní Recorder WPF smoke | 114 kontrol | 0 | 0 |
| Celá solution build, Windows | PASS | 0 chyb | 0 varování |
| Shoda Avalonia tokenů s F2 | PASS | | |
| F2 static inventory / source hash checks po F3 | 181 kontrol | 0 | |

Zůstaly stejné fixture skipy jako ve F1: jeden QD integrační test vyžaduje tři
úplné referenční obrazy ve `specification`; dva legacy CP/M testy vyžadují pět
referencí v sousedním `_SFD800`. QD skip se opakuje v portable Core sadách.
Ignore list nebyl použit jako důvod pro vynechání místně přítomných zdrojů nebo
médií. Finální výsledky nemají nevyřešený selhaný test.

Při přípravě se zjistilo globování pomocných `.xaml` podkladů z `TestResults`
do WPF Page položek; root projekt je nyní výslovně vylučuje, stejně jako XAML
pod `src`/`tests`. Vyhodnocené položky jsou archivované v lokálním reportu.
Pokus o opakovaný build během dlouhé regrese narazil na DLL zámky testhostů;
po skončení testů prošel finální build bez varování a chyb.

## Nativní a vizuální ověření

[Galerie skutečných Avalonia obrazovek](ui-design-system/avalonia-f3/index.html)
obsahuje Windows/Linux, report, chybu, malý viewport a 200% headless render.
Obrazy jsou kopie skutečných výstupů; [manifest](ui-design-system/avalonia-f3/images.json)
uvádí původní cestu a SHA-256. Skutečný vzhled uživatel schválil 29. 10. 2026.

Windows: backend `Avalonia.Win32.WindowImpl`, provider `Win32StorageProvider`,
skutečný render scale 1.5. Linux: `Avalonia.X11.X11Window`, scale 1.0,
`FallbackStorageProvider`. Na obou prošlo skutečné otevření/storno Open i Save As
pickeru, clipboard roundtrip, změna velikosti, focus a zavření. Linux fallback
picker není vydáván za GTK/portal dialog. Nativní smoke pracuje jen s vlastním
procesem a jeho dialogy; nevynucuje akce v cizích oknech.

Headless používá `UseSkia` a `UseHeadlessDrawing=false`. Sedm kombinací kontroluje
1440×960, 1366×768, 1920×1080, 920×640 a 640×480 při 100 %, plus 1366×768 při
150/200 % do odpovídající bitmapy. Nejde o potvrzení všech skutečných monitorových
DPI. Nativní obrazy jsou renderem vizuálního stromu běžícího okna, nikoli celé plochy.

**UNTESTED:** macOS/Cocoa, samostatný Linux desktop mimo WSLg, skutečné monitory
na všech 100/150/200 %, screen reader, fyzické audio zařízení a ostatní CPU
architektury. macOS case/path normalizace je implementovaná, ale není nativně
ověřená. Úplná původní multiplatformní brána F3 proto není označená jako splněná.

## Závislosti a prostředí

Dva nové lock soubory izolují Avalonia 12.1.4 a xUnit v3 3.2.2. Dosavadní testy
zůstaly na xUnit v2. Samostatný `tests/MZTools.Desktop.Smoke` je review executable
s jediným ProjectReference, bez dalších přímých balíčků. Nepřidává produkční režim.

[Přehled 50 obnovených balíčků](third-party/avalonia-f3/README.md) archivuje licence,
source commity, content hashes a 19 jedinečných původních textů/notices.
MIT označení obálek nenahrazuje licence přibalených Skia/HarfBuzz/ANGLE součástí.
Licence i hlavní notices se kopírují do desktop build/publish výstupu.
NuGet audit přímých i transitivních balíčků obou nových projektů nehlásil známou
zranitelnost; to není záruka absence všech zranitelností.

Ve WSL byly podle schválení nainstalovány `libfontconfig1`, `libice6`, `libsm6`
a jejich apt závislosti (fontconfig-config, DejaVu fonty, FreeType, libpng a
x11-common). Používá se již trvale nainstalované .NET 10 SDK. Nebyl instalován
audio backend. macOS instalátor, podpisy a CI matrix patří do F9.

## Reprodukce

```powershell
dotnet run --project src/MZTools.Desktop/MZTools.Desktop.csproj
py -3 docs/ui-design-system/tools/build_avalonia_tokens.py --check
dotnet test tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj -p:RestoreLockedMode=true
dotnet test MZTools.sln --logger "trx;LogFilePrefix=f3-regression" --results-directory TestResults/F3/windows/regression
dotnet run --project tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj -- TestResults/F3/windows/native
```

Linux (spuštěno z PowerShell; přímý exec, žádné vložené rozbalování Windows PATH):

```powershell
wsl -d Debian --exec env DOTNET_ROOT=/home/ales/.dotnet /home/ales/.dotnet/dotnet test /mnt/c/Dokumenty/GitHub/MZTools/tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj --artifacts-path /home/ales/.cache/mztools/f3-linux-artifacts --logger "trx;LogFileName=desktop-final-linux.trx" --results-directory /mnt/c/Dokumenty/GitHub/MZTools/TestResults/F3/linux -p:RestoreLockedMode=true
wsl -d Debian --exec env DOTNET_ROOT=/home/ales/.dotnet /home/ales/.dotnet/dotnet run --project /mnt/c/Dokumenty/GitHub/MZTools/tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj --artifacts-path /home/ales/.cache/mztools/f3-linux-artifacts -- /mnt/c/Dokumenty/GitHub/MZTools/TestResults/F3/linux/native
```

Pro uložení headless screenshotů nastavit `MZTOOLS_F3_OUTPUT` na vlastní výstupní
adresář. Nativní smoke sám otevře a zruší své pickery; clipboard test krátce
změní text clipboardu a pak obnoví předchozí text. Regrese a následný build
spouštět postupně kvůli Windows DLL zámkům. WPF smoke projekty mají vlastní
outputy a nesmějí sdílet jeden společný adresář.

`collect_notices.py` obnovuje licenční evidenci z existujících assets a přesných
source commitů; neinstaluje balíčky. `summarize_results.py` exportuje skutečné
TRX výsledky a kopíruje review obrazy. Nezaměňuje plánované testy za provedené.

## Otevřené položky

Uživatel následně dne 29. 10. 2026 odpovědí „souhlasím“ schválil skutečný vzhled
F3 a přípravu konkrétního rozsahu F4. Toto schválení nenahrazuje chybějící macOS
a nativní DPI/accessibility ověření na dostupných hostitelích.
Připojení Tape/QuickDisk editorů a binárních workflow popisuje
[návrh F4](F4_TAPE_QUICKDISK_PLAN_2026-10-29.md). Uživatel jej následně samostatně
schválil; skutečný stav zachycuje [report F4](F4_TAPE_QUICKDISK_2026-10-29.md).
