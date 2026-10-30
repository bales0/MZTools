# F3 – Avalonia shell a společné komponenty

**Rozsah schválen uživatelem 29. 10. 2026 odpovědí „souhlasím“.**
Implementace a skutečné výsledky jsou v [reportu F3](F3_AVALONIA_SHELL_2026-10-29.md).
Následující text zachovává schválený plán; nenahrazuje výsledky jeho ověření.
F2 vzhled a pravidla C01–C09 jsou [schválené](ui-design-system/APPROVAL.md).
Tento dokument konkretizuje další fázi zadání; nepředstavuje souhlas s F4–F9.

## Výsledek fáze

Samostatně spustitelná aplikace `src/MZTools.Desktop`, `net10.0`, se společným
Avalonia UI pro Windows, Linux a macOS. Závisí na Core, nikoli na WPF hostiteli
nebo Windows Capture adaptéru. Capture zůstává samostatnou existující vrstvou;
F3 nevyžaduje jeho přímé napojení do UI.

Shell obsahuje navigaci, command bar, karty dokumentů, inspector, stavový řádek,
report viewer, progress/cancel, validaci, chyby a klávesové příkazy podle F2.
Sdílené styly převezmou schválené tokeny, rozměry a stavové barvy; jejich hodnoty
nevzniknou ručním druhým přepisem palety. Generátor vytvoří Avalonia resources
z `docs/ui-design-system/tokens.json` a ověří shodu hodnot.

Vzniknou společné komponenty pro seznam/tabulku, property form, potvrzovací dialog,
prázdný/chybový stav a report. Doménové mapy, waveform, spektrum a recorder
zůstávají prací F4–F7. Produkční navigace výslovně označí dosud nepřipojené moduly;
neobsahuje smyšlené výsledky měření ani aktivní demonstrační Record/Save.

## Dokumenty a soubory

Společný koordinátor bude řídit Open / Save / Save As / Close přes dokumentové
adaptéry a platformní file picker. Uchovává cestu, identitu, modified/read-only
stav a příkazy každého dokumentu odděleně. Storno pickeru, neúspěšné otevření
a odmítnutí uzavření zachovají existující dokument i jeho výběr.

Save As kontroluje kolize cest a explicitní souhlas s přepsáním. Dočasný soubor
vzniká u cíle, zápis se dokončí před náhradou a stav dokumentu se změní až po úspěchu.
Při chybě zůstane původní obsah a modified stav zachován. Ověří se i selhání před
náhradou souboru. Path identity respektuje pravidla OS; nepřevede Linux cesty
plošně na case-insensitive porovnání. Bezpečné ukládání nesmí obcházet existující
ochrany DSK repair; konkrétní opravné workflow přijde v odpovídající fázi.

F3 implementuje obecný životní cyklus a testuje jej se skutečnými dočasnými
soubory a řízeným testovacím dokumentovým adaptérem. Produkční doménové editory
a serializéry se připojí ve F4 a dalších fázích. Dokud adaptér konkrétního typu
není připojen, Open zobrazí srozumitelný důvod nedostupnosti; Save zůstane zakázán.
To je hranice F3, nikoli tvrzení o hotovém editoru médií. Testovací adaptér
nebude dostupný jako veřejný produkční formát.

## Ověřené závislosti a licence

Verze níže byly ověřeny v publikovaných NuGet metadatech 29. 10. 2026.
Restore a skutečný build proběhnou až po schválení. Přesný transitivní graf se
uzamkne v nových `packages.lock.json`; staré projekty se hromadně neaktualizují.

| Přímá závislost | Verze | Licence / účel |
| --- | --- | --- |
| Avalonia | 12.1.4 | MIT; UI a vestavěný TableView |
| Avalonia.Desktop | 12.1.4 | MIT; desktop backendy |
| Avalonia.Themes.Fluent | 12.1.4 | MIT; základ šablon přizpůsobený F2 |
| Avalonia.Headless.XUnit | 12.1.4 | MIT; pouze nový testovací projekt |
| xunit.v3 | 3.2.2 | Apache-2.0; pouze nové UI testy |
| Microsoft.NET.Test.Sdk | 17.14.1 | MIT; existující verze, nový testovací projekt |
| xunit.runner.visualstudio | 3.1.4 | Apache-2.0; existující verze, nový testovací projekt |

Použije se explicitní sestavení služeb a standardní `INotifyPropertyChanged` /
`ICommand`, bez nového DI/MVVM balíčku. xUnit v3 je vyžadován Avalonia 12 headless
integrací; současné xUnit v2 sady se nepřevádějí. Integrace runneru se ověří
prvním cíleným testem před rozšířením nové sady.

`TableView` je vestavěný read-only seznam s výběrem a sloupci. Editace proběhne
ve společném property form/inspectoru. Samostatný DataGrid ani placený
TreeDataGrid se nepřidává. Pokud doménové workflow později vyžádá jinou editaci,
bude součástí návrhu příslušné fáze, ne tichou změnou schválených pravidel.

| Hlavní transitivní runtime | Ověřená verze | Dopad a licence |
| --- | --- | --- |
| Avalonia.Win32 / X11 / Native / Skia / HarfBuzz | 12.1.4 | MIT; Windows, X11, Cocoa a renderer |
| SkiaSharp a NativeAssets Win32/macOS/Linux | 3.119.4 | MIT obálka; nativní Skia má BSD licenci a další notices |
| HarfBuzzSharp a NativeAssets Win32/macOS/Linux | 8.3.1.3 | MIT balíčky; zachovat licence přibalených nativních částí |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | Windows; ověřený soubor LICENSE obsahuje BSD podmínky se třemi klauzulemi |
| Avalonia.BuildServices | 11.3.2 | MIT; build-time |
| MicroCom.Runtime | 0.11.6 | MIT; runtime interop |
| Avalonia.Fonts.Inter | 12.1.4 | transitivně headless testy, MIT balíček; font Inter pod SIL OFL 1.1 |

Po restore se zaznamená úplný graf včetně pomocných FreeDesktop/AT-SPI a Remote
Protocol balíčků, licenčních souborů a notices nativních artefaktů. Některé balíčky
obsahují i WebAssembly assets; F3 tím nezavádí browser target. Součástí validace
bude NuGet audit známých zranitelností přímých i transitivních balíčků. Dnes se
netvrdí, že balíčky prošly bezpečnostním auditem nebo že všechny nativní kombinace
byly spuštěny. Nová problematická licence nebo významná zranitelnost znamená
předložení konkrétního řešení před začleněním dotčené závislosti.

Zdroje: [Avalonia 12.1.4](https://www.nuget.org/packages/Avalonia/12.1.4),
[Desktop](https://www.nuget.org/packages/Avalonia.Desktop/12.1.4),
[TableView](https://docs.avaloniaui.net/controls/data-display/structured-data/tableview),
[headless xUnit](https://www.nuget.org/packages/Avalonia.Headless.XUnit/12.1.4),
[xUnit 3.2.2](https://www.nuget.org/packages/xunit.v3/3.2.2),
[ANGLE](https://www.nuget.org/packages/Avalonia.Angle.Windows.Natives/2.1.27548.20260419),
[Inter font](https://github.com/rsms/inter/blob/master/LICENSE.txt),
[Skia licence](https://github.com/google/skia/blob/main/LICENSE).
Verze transitivních závislostí pocházejí z `.nuspec` příslušných přesných verzí
na `api.nuget.org/v3-flatcontainer`; po restore mají přednost skutečné lock soubory.

## Platformy a instalace

Windows: místní nativní běh a smoke. Linux: Debian 13 ve WSL, existující trvale
nainstalované .NET 10 SDK, headless testy a skutečný X11 běh přes WSLg.
Pro GUI lze v rámci schválené F3 doplnit chybějící `libfontconfig1`, `libice6`,
`libsm6` přes Debian apt; `libX11` je přítomen. Instalace těchto GUI knihoven
dosud neproběhla. Audio/FLAC backend ani další zařízení se tím nezavádí.

macOS používá Cocoa runtime Avalonia.Native, bez závislosti na WPF.
Bez dostupného macOS hostitele budou jeho skutečný GUI běh, dialogy a screenshoty
**UNTESTED**. Cross-publish nebo headless test na Windows nenahrazuje ověření
macOS. F3 pak zůstane technicky připravená s otevřenou částí multiplatformní brány;
nelze tvrdit, že celý původní gate prošel. WSLg běh navíc nenahrazuje samostatný
Linux desktop ani hardware audio testy.

[Podporované platformy a Linux závislosti](https://docs.avaloniaui.net/docs/supported-platforms).
Wayland, distribuce instalátorů, podpisy, CI matrix a kompletní x64/arm64
distribuce jsou předmětem pozdějších fází.

## Dotčené soubory

- Nové `src/MZTools.Desktop`: projekt, vstup aplikace, App/MainWindow AXAML,
  generované theme resources, komponenty, shell a dokumentový koordinátor.
- Nové `tests/MZTools.Desktop.Tests`: samostatné unit/headless testy a testovací
  dokumentové adaptéry; žádné připojování WPF do těchto testů.
- Nové lock soubory pouze těchto projektů, generátor/validátor Avalonia tokenů,
  smoke/review nástroje a F3 testovací report v dokumentaci.
- `MZTools.sln` přidá nové projekty. `src/MZTools.Core/MZTools.Core.csproj`
  může cíleně přidat friend assembly pro nový host a jeho testy bez zveřejňování
  všech interních typů. Root WPF project bude ověřen proti nechtěnému globování.
- README, architektura/testovací návod a third-party notices popíší skutečný stav.

F3 nepřevádí parsery, dekodéry ani Capture pipeline. Nové dokumentové rozhraní
patří do UI vrstvy; obecný Core se nemění kvůli vlastnostem widgetů.

## Akceptační kontroly po implementaci

1. Build nového hostu na Windows/Linux, nulová reference na WPF/Win32 Capture.
   Ověřit i skutečně vyhodnocené compile/resource položky root WPF projektu.
2. Headless: karty a aktivní dokument, command availability, focus/tab/shortcuts,
   validace, chybové a prázdné stavy, progress/cancel a report viewer.
3. Dokumenty: Open/Cancel/chyba, modified Close Save/Discard/Cancel, chybný Save,
   Save As kolize/storno, read-only zdroj, diakritika a Linux case sensitivity.
   Testy zapisují jen do vlastních dočasných adresářů; porovnávají bajty originálu.
4. Windows a Linux WSLg native smoke: start/close, skutečné file pickery pouze
   po příkazu uživatele, Cancel, clipboard, klávesnice, resize a přepnutí dokumentu.
5. Screenshoty skutečné Avalonia kompozice a komponent při 100/150/200 % DPI,
   s evidencí logických/fyzických rozměrů. Headless screenshoty použijí Skia
   rendering, ne výchozí fake drawing. Každý obraz bude označen podle původu;
   skutečné DPI/native položky bez provedení zůstanou UNTESTED.
6. Porovnání s F2 a ruční kontrola bez ořezu ovladačů; nový nativní vzhled
   bude předložen uživateli ke schválení. HTML prototyp není screenshot baseline
   pro pixelové srovnání odlišných rendererů.
7. Všechny stávající dostupné testovací sady: legacy WPF, Core Windows/Linux,
   Capture Windows/Linux, Windows Capture a čtyři WPF smoke. Ignore list není
   důvodem k vynechání lokálně přítomných zdrojů/fixtures. Každý skip dostane
   konkrétní vysvětlení; chybějící externí fixture se nezamění za úspěšný test.
8. Locked restore, úplný seznam licencí/notices a NuGet audit. Výsledky rozlišují
   Implemented / Tested / Hardware-tested / Not yet implemented / Blocked.

Tyto kontroly jsou **plánované**, nebyly při přípravě tohoto dokumentu spuštěny.
Aktuálně byla provedena kontrola publikovaných metadat a dostupnosti prostředí.

## Pořadí a návrat

Nejprve založit izolované projekty a ověřit restore/build/test runner, poté převést
tokeny a komponenty, následně dokumentový koordinátor a nativní integraci,
nakonec regrese, screenshoty a report. WPF zůstává dostupným referenčním hostitelem.
Návrat spočívá v odebrání pouze nových F3 projektů a jejich přesně evidovaných
změn; nesmí zahrnout rozpracované F1/F2 ani původní uživatelské změny.

Po předložení výsledků F3 se znovu vyžádá schválení před zahájením F4.

**Schválený rozsah:** implementace F3 včetně uvedených NuGet
závislostí, nativních runtime, izolovaných xUnit v3 testů a případného doplnění
tří GUI knihoven ve WSL Debianu. Schválení nezastupuje budoucí výsledky testů
ani schválení nativních screenshotů. Ty se předkládají v závěrečném reportu F3.
