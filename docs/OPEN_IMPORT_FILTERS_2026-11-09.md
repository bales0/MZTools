# Kontextové filtry a cílené testování (2026-11-09)

> Historický záznam: aktuální programové Open/Add, omezené Linux globy a upřesnění IPL viz [audit Phase 5](PHASE5_FUNCTIONAL_AUDIT_2026-11-09.md). Níže uvedené starší výsledky nejsou novým runtime ověřením.

Schválený rozsah A + B; výchozí HEAD `181d217664fb343f5ac97fb50f5be22a257a5aa7`. Předchozí změny buildů i uživatelské změny dokumentů byly zachovány. Původní produkční WPF ani Core parsery, dekodéry a exportní algoritmy se nemění.

## Změna A

`Documents/FilePickerPurpose.cs` deklaruje účel; `Platform/FilePickerPolicy.cs` sestavuje `FilePickerOpenOptions` před otevřením provideru. Titulek neurčuje filtr. Bindable tape/QD přípony čerpá z `FormatChoice.All`, přidává čitelné aliasy MZ0/MZ7; Batch přípony přebírá beze změny z `BatchMediaPolicy.Extensions`. Audio Open a pulzní import mají záměrně rozdílnou podporu. Linux patterns obsahují varianty velikosti písmen krátké přípony, včetně celého `.wav.json`, bez enumerace souborů.

První filtr je vždy kompatibilní. Obecný fallback existuje pouze pro explicitně binární operace a jmenuje se „Any binary file (including nonstandard extensions)“. Začíná až druhou položkou. V ostatních dialozích All files není. Dodané cesty se kontrolují také po návratu provideru, obsah dál validuje dosavadní loader/Core.

| Operace / volající | WPF filtr / výchozí volba | Nová Avalonia volba / zdůvodnění |
|---|---|---|
| Open, `DesktopDialogs.OpenAsync` | All supported, obsahuje také LEP/L16 a poslední All files | All supported media: `.mzt,.mzf,.m12,.mz0,.mz7,.mzq,.qdf,.qd,.dsk,.hfe,.wav,.flac,.wav.json`; samostatné QD/disk/tape/audio skupiny. LEP/L16 loader Open nepodporuje, zůstávají v importu. |
| Analyze Audio, `AudioOnly` | WAV/FLAC/`.wav.json` | Zachováno, žádné obecné JSON. |
| Import Tape/QD, `ShellMediaCommands` | Širší `GetOpenFilter` | Tape/QD + WAV/LEP/L16. FLAC a manifest touto cestou podporovány nejsou. Read-only non-SHARP/unknown QD cíl picker nespustí, takový zdroj se odmítne před Apply. |
| New/Add IPL, `ShellDiskCommands`, `ShellDiskDetails` | MZF/MZ0/MZ7 + All files | Tape/QD kontejnery skutečně čitelné `TapeDocumentIO`, včetně M12 a vícezáznamového MZT. Programy dál kontroluje IPL builder; non-SHARP/unknown QD odmítnut. |
| DSK/HFE Import, `ShellDiskCommands`, `ShellNativeComTools` | MZF/binary, první maska obsahuje i `*.*` | MZF/binary první; explicitní binární fallback druhý. Pro CP/M navíc M12/COM. MZF má dosavadní strukturovaný import, ostatní přípony dosavadní raw obsah. |
| Samostatný COM import, `MainWindow.Tools` | WPF NativeCom má MZF/M12/MZ0/MZ7 | Pouze MZF/M12, protože tento Avalonia frontend ostatní explicitně odmítá. Nepřidáváme nové formáty změnou filtru. |
| COM konverze, `MainWindow.Tools` | MZF/M12/MZ0/MZ7 | Všechny čtyři skutečně přijímané varianty. |
| Starší COM import, `ShellDiskDetails.ImportDiskComAsync` | Nativní tape program | Oddělený účel s tape/QD; dosavadní kontrola právě jednoho záznamu a kompatibility COM zůstává. |
| Copy, `ShellDiskWpfCopy` | DSK | DSK/HFE podle `DskDocument.Open`; HFE vyžaduje platnou projekci, přenos podporovaný filesystém. |
| Compare, `ShellDiskCommands` | Editor DSK; fyzický viewer DSK/HFE | DSK/HFE podle `CompareMedia`. |
| Patch baseline | DSK | Pouze DSK. |
| Apply patch | `.mzpatch.json;*.json` | Shoda WPF. JSON deserializace není sama validací; neplatná struktura je odmítnuta při Preview. |
| Raw sector/range | BIN, někde All files | BIN první, explicitní binární fallback druhý. Kontrola přesné délky zůstává. |
| Bootstrap header/track | BIN | BIN první, explicitní binární fallback druhý; dosavadní délka/struktura. |
| Install boot/system, `ShellDiskDetails` | DSK | DSK/HFE podle čtecí služby; kontrola kompatibility zůstává. |
| Batch files, `BatchProcessWindow`, `ShellBatchCommands` | `BatchMediaPolicy.PickerFilter` | Stejná policy podle aktuální Operation/Target, odstraněno All files. Vlastní folder restriction zachována. |
| Batch boot source/profile | DSK/JSON | Zachováno pod centrální politikou. |

Import dál pracuje přes Preview a explicitní potvrzení; Cancel ani chybný vícenásobný import nic neaplikuje. Přímé otevření/drag&drop stále prochází loaderem. Filtr nepotvrzuje validitu ani kompatibilitu obsahu.

Změněné produkční soubory: tři `Platform/Desktop*Dialogs.cs`, nová politika a enum, `Documents/DiskDialogs.cs`, importní guard v `MediaOperations.cs` a IPL guard v `DiskOperations.cs`; příslušná volání v `ShellMediaCommands`, `ShellDiskCommands`, `ShellDiskDetails`, `ShellDiskWpfCopy`, `ShellNativeComTools`, `MainWindow.Tools` a `BatchProcessWindow`. Test adaptéry jsou aktualizované v Desktop.Tests i Desktop.Smoke.

## Změna B a příkazy

Výchozí rychlá kontrola z kořene, na Linuxu `python3`:

```text
python tools/test_picker_policy.py
python tools/test_picker_policy.py --core
```

Runner vždy nejdřív sestaví změněné zdroje. Pak použije přímý xUnit v3 runner s explicitními třídami `FilePickerPolicyTests`, `OpenImportPickerTests`, `MediaDocumentTests`, `DiskDocumentTests`, `BatchParityTests`. FAST vyjímá pouze metodu `HfeMetadataEditKeepsOriginalAndCorePhysicalSerialization` (tři fyzické serializace, které filtry nemění); výjimka je vidět i ve výsledném JSON. `--core` doplní pouze syntetické `DskPatchTests` a `BootstrapRawRangeTests` přes podporovaný VSTest filtr. Případná chyba testu běh ukončí, nemění se na Skip.

`MZToolsFastTests=true` v Core.Tests podmíněně vyřadí skupinu kopírovaných médií, nikoli kompilaci testů. Výchozí prázdná/false hodnota zachovává dosavadní korpus. FAST používá samostatné `obj/test-runs/<host>/fast`, FULL jiné `.../full`, takže si nepřebírají staré fixture. `TestMediaPaths` zůstává beze změny: **přepínač kopírování sám nebrání testu číst původní korpus z repozitáře**. Proto ho používejte s uvedeným explicitním FAST výběrem. Nepoužívejte jej jako náhradu FULL běhu.

```text
python tools/test_picker_policy.py --mode full --core
```

FULL spustí celé Desktop + Core a obnoví kopírování fixture. Nespouští se automaticky. Další Capture/WPF/hardware/release regrese zůstávají dostupné původními projektovými příkazy; příklad Windows:

```text
dotnet test MZTools.Capture.Tests/MZTools.Capture.Tests.csproj
dotnet test MZTools.Capture.Windows.Tests/MZTools.Capture.Windows.Tests.csproj
dotnet test MZTool.Tests/MZTools.Tests.csproj
```

Půlhodinový benchmark zůstává opt-in přes `MZTOOLS_SHA256_BENCH=baseline` nebo `after`; FAST jeho třídu nevybírá. Změna filtrů nevyžaduje automatické balení všech platforem, snímkování všech oken ani hledání médií v uživatelských složkách.

Runner ukládá `timings.json`, TRX a `summary.json` do `TestResults/PickerPolicy/<host>/<mode>`. Souhrn uvádí počet a bajty zkopírovaného korpusu. Testovací výstupy ani self-contained publish při FAST nevznikají v distribučním `bin`.

## Profil a ověření

Naměřeno na tomto stroji; nejde o garantované časy:

| Kontrola | Před | Po / FAST |
|---|---:|---:|
| Pět širokých globů DSK/QD/MZF/ROM/LEP | 109 souborů, 36 656 900 B | 0 souborů, 0 B |
| Všechny kopírované fixture v izolovaném Core výstupu, včetně WAV/golden | 112 souborů, 58 521 045 B | 0 souborů, 0 B |
| Samostatná MSBuild Copy úloha nad tímto známým výstupem | 160 ms | 1 ms, prázdný seznam |
| Čistý Core build do nového adresáře | 3,89 s | 4,58 s |
| Tři nesouvisející HFE serializace v širším Windows výběru | 66,32 s | FAST je nevybírá; FULL je zachovává |

Samotné kopírování zde není hlavní časová brzda. Jednorázová měření čistého buildu nedokládají zrychlení kompilace; rozdíl je v omezení testů na dotčené operace a v odstranění zbytečného I/O. Copy-only měření je samostatná úloha, není zaměňováno za celý MSBuild čas.

Důkazy: `TestResults/PickerPolicy-2026-11-09` (výchozí buildy, copy profil, nativní pickery) a `TestResults/PickerPolicy` (FAST). Finální výsledky jsou v TRX/summary; neúspěšné diagnostické pokusy jsou zachované samostatně.

- Windows FAST: **123/123 PASS**, 0 skipped; build 4,391 s, runner testů 4,966 s (xUnit vykázal samotné testy 4,611 s), přímo xUnit. Windows VSTest Core v této relaci nepřipojil testhost do 90 s; jde o ABORTED, nikoli PASS.
- Linux FAST + Core: **123/123 Desktop PASS + 17/17 Core PASS**, 0 skipped; buildy 9,691 + 4,515 s, runner testů 4,241 + 2,442 s; samotné Core testy 885 ms. Ověřeno ve WSL Debianu; případný restart WSL při souběžném sestavování je zaznamenán v diagnostice, runner sestavuje sériově s jedním workerem.
- Windows skutečný Win32 picker: Open/Cancel, výběr uppercase MZF a DSK, import MZF do Preview, Import/Raw/Compare Cancel, zachování source bytes.
- Linux/WSLg: dostupný `FallbackStorageProvider`, skutečná okna Open/Import/Raw/Compare a Cancel. Automatizovaný výběr souboru ve fallback dialogu se nepodařilo dokončit; GTK/KDE OS picker není dostupný. Výběr přes tyto OS providery a jejich zobrazení kategorií jsou **NETESTOVÁNO**. Loader/import a předávané options jsou otestované headless na Linuxu.
- macOS: **NETESTOVÁNO**. Nativní screenshoty proti WPF nebyly pořízené; Computer Use host byl nedostupný. Shoda filtrů je doložená čtením konkrétních WPF volání, nikoli tvrzením úplné vizuální parity.
- FULL/corpus/30min/hardware v této opravě znovu nespouštěno; zůstává explicitně dostupné.

Nové testy pokrývají přesné skupiny, skutečné dialog options před výběrem, uppercase/mixed-case suffix, manifest bez obecného JSON, ruční nepodporovanou cestu, raw fallback, filesystem účel včetně CP/M, Cancel, atomický neúspěšný multi-import, zachování zdroje/sidecaru, malý WAV/FLAC/manifest, symlink, M12 + vícezáznamové MZT pro IPL, skutečnou čitelnost COM aliasů a neplatný patch při Preview. Stávající cílené regrese doplňují Batch folder-filter a Preview/Confirm loss/replace/Execute.

Samostatný omezený nativní smoke (ne celá galerie):

```text
dotnet build tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj -p:MZToolsPublishLatest=false
# DLL je v obj/artifacts/windows/bin/... nebo obj/artifacts/linux/bin/...
dotnet obj/artifacts/windows/bin/MZTools.Desktop.Smoke/debug/MZTools.Desktop.Smoke.dll --pickers TestResults/picker-native
```
