# F4 – Tape/MZF/M12/MZT a základní QuickDisk

**Rozsah schválen uživatelem 29. 10. 2026 odpovědí „souhlasím“.**
Skutečné provedení a výsledky jsou v [reportu F4](F4_TAPE_QUICKDISK_2026-10-29.md).
Následující text zachovává původní schválený plán; není výsledkem testování.
Uživatel schválil skutečný vzhled F3 a přípravu tohoto rozsahu. Otevřené ověření
macOS, ostatních nativních DPI a screen readeru zůstává evidované v
[reportu F3](F3_AVALONIA_SHELL_2026-10-29.md); schválení vzhledu není PASS těchto testů.

## Výsledek

Ve společném Avalonia shellu půjde skutečně otevřít, upravit, uložit a exportovat
kazetový dokument nebo podporovaný SHARP QuickDisk. Zmizí F3 placeholder pro
tyto dva moduly. Jednotlivé otevřené dokumenty budou mít vlastní záznamy, cestu,
modified stav, výběr, filter a viewport. Diskové a audio analyzéry ani recorder
se tím neoznačí za implementované.

Použijí se stávající Core modely a kodeky; nevznikne Avalonia parser, druhý writer,
nový pulse catalog ani nová implementace ZX0/ZX7. WPF zůstává referenční aplikací.

## Rozsah funkcí

| Oblast | Konkrétní chování F4 |
| --- | --- |
| Otevření a nový dokument | MZF, M12, MZT, MZQ, QDF a rozpoznané QD varianty přes existující Core; nový Tape/SHARP QuickDisk dokument; chybný soubor nezmění otevřené karty |
| Záznamy | společná tabulka, filter, Ctrl/Command a Shift výběr, detail, přidání/import, potvrzené odstranění, změna pořadí tlačítky i tažením |
| Metadata | jméno v SHARP kódování, typ, load/execute adresy, profil/rychlost a původ metadat; validace před Apply; zachování nesouvisejících raw header bajtů a trailing dat |
| Komprese | Keep/uncompressed/ZX0/ZX7/Auto podle stávající cílové politiky; původní směr a partial/embedded omezení; zdrojový stav přes společný CompressionDescription |
| Dokumenty | Open/Save/Save As/Close navázané na skutečné adaptéry; modified, read-only, kolize, neuložené změny a nezávislé karty |
| Save As | pouze bindable MZF/M12/MZT/MZQ/QDF a podporované SHARP QD cíle; explicitní rozlišení Sharp legacy / HxC / FlashFloppy při stejné příponě `.qd` |
| Import/transfer | preview záznamů a konfliktů před Apply, import ze souborů a přenos mezi Tape/QD dokumenty přes existující WorkspaceDragCodec; nepodporovaný přenos se odmítne bez změny dat |
| Export | vybrané/všechny záznamy do podporovaných Tape/QD cílů, samostatné MZF/M12 soubory, WAV/LEP/L16 a Native COM přes stávající Core exportéry; cesta aktivního dokumentu se nemění |
| Audio jako zdroj záznamů | explicitní import podporovaných WAV/LEP/L16 přes stávající SharpTapeImporter s progress/cancel; nesupluje Audio Analyzer a nespouští zařízení |
| QuickDisk | logický adresář/editor, kapacita/omezení podle Core, základní mapa oblastí, společný výběr tabulka–mapa–inspector, read-only hex detail záznamu |
| QuickDisk fyzické kontejnery | Core rozpoznání a host evidence, zachování původního physical profile; logické záznamy jen tam, kde je Core skutečně poskytuje; neznámý/non-SHARP obraz zůstává read-only bez zápisu SHARP writerem |

Save As nevybírá QD variantu jen podle přípony ani podle pořadí nativního picker
filtru. Formát se zvolí výslovně před pickerem a ověří proti výsledné cestě.
MZF/M12 mají omezení počtu záznamů podle existujícího writeru; limity prázdného
dokumentu, velikostí a QuickDisk adresáře se převezmou z Core, nikoli z nové UI konstanty.

Ztrátová konverze, vypuštění trailing dat nebo metadat vyžaduje konkrétní preview
a souhlas. Import i změna komprese pracují s klonovaným návrhem; Cancel neprovede
část změn. Undo/Redo se nezpřístupní bez skutečné implementace historie.

## Ukládání média a metadat

Ověřené názvy v `SidecarService.GetSidecarPath`:

| Hlavní soubor | Sidecar |
| --- | --- |
| `.mzf` | `.mfi` |
| `.m12` | `.m2i` |
| `.mzt` | `.mti` |

F3 `AtomicDocumentStore` zapisuje jeden soubor. F4 proto musí doplnit plán celé
sady výstupů: hlavní soubor i případný sidecar, všechny kolize, očekávané hashe
a post-commit změny modelu. Nestačí uložit médium a teprve potom zkusit sidecar.

Stávající `TapeDocumentWriter` již obsahuje transakční zápis se zálohami. Použije
se jeho společná implementace, případně se cíleně vyčlení a doplní pro potřeby
shellu; mechanismus se nebude kopírovat do druhého writeru. Stávající serializace
MZF/MZT a MFI/M2I/MTI, jejich obsah i pravidla původu metadat zůstávají stejné.

Před zápisem se potvrdí kolize všech výstupů včetně již existujícího sidecaru.
Staging a validace všech artefaktů předcházejí nahrazení cílů. Obnovitelné selhání
vrací předchozí soubory; neúspěšný rollback zachová potřebné zálohy a oznámí
konkrétní stav. Cesta, formát, metadata binding a clean stav se změní až po úspěchu
celé operace. Save As kopie nesmí změnit původní médium ani jeho sidecar.

Několik souborů nelze označovat za jednu univerzálně atomickou filesystem operaci.
F4 otestuje řízená selhání a rollback; bez samostatného důkazu nebude tvrdit
odolnost proti výpadku napájení nebo libovolnému souběžnému externímu zapisovači.
Ochrany symlinků, cizích otevřených dokumentů, read-only a case-sensitive cest
z F3 se nesmějí obejít při přidání sidecaru.

## Napojení a dotčené soubory

- `src/MZTools.Desktop/Documents`: skutečný loader a adaptér nad `TapeDocument`,
  explicitní cílový formát a sada artefaktů, doplnění koordinátoru/dialogů pro nový
  dokument, Import/Export a Save As options. Testovací adaptéry nebudou v produkci.
- `Views`, `ViewModels`, `Controls`, případně `Renderers`: Tape/QuickDisk kompozice,
  společný editor záznamu a kompresních voleb, read-only hex detail a základní mapa.
  Mapa čerpá z `QuickDiskLayout`/`QuickDiskVisualSnapshot`; byte a bitcell souřadnice
  zůstanou rozlišené. Pan/zoom/Fit použije pravidla F2 včetně faktoru 1.5.
- `src/MZTools.Core/MZTools.Core.csproj`: úzce vymezené friend assemblies pro
  Desktop a jeho testy; žádné plošné zveřejnění interních modelů.
- Core: podle potřeby vyčlenění čisté validace/capability/Save policy dosud
  umístěné v `MainWindow.xaml.cs` a sdílené transakce. WPF bude volat tutéž politiku;
  původní filtry a význam příkazů ověří regrese. Binární algoritmy se nepřepisují.
- `tests/MZTools.Desktop.Tests`: integrační testy reálných adaptérů a headless
  workflow, rozšíření portable Core testů o relevantní stávající sady bez WPF vazby.
- `tests/MZTools.Desktop.Smoke`: nativní příkazy, pickery, otevření a uložení
  vlastních testovacích kopií. Nepoužijí se skutečné uživatelské originály.
- README, dokumentace skutečného stavu/parity a F4 výsledky/screenshoty.

Základ tvoří `Mzf.cs` (TapeDocument/Record, MZTFileReader, SidecarService,
TapeDocumentWriter), `QuickDisk.cs` (detekce a QD kodeky), `QuickDiskLayout.cs`,
`QuickDiskVisualSnapshot.cs`, `TapeFormat.cs`, `Compression/*`, `ListReorder.cs`,
`TapeFileNameEncoding.cs` a `Media/WorkspaceDragCodec.cs`.

**Nové NuGet balíčky, nativní knihovny ani systémové instalace nejsou navržené.**
F4 použije existující lock soubory. Zjištěná potřeba nové závislosti se předloží
s přesnou verzí, licencí a dopadem před jejím přidáním.

## Co zůstává v dalších fázích

DSK/HFE editor, IPL diskové/exportní workflow, filesystems, repair a boot/system
UI patří do F5. Jejich dosavadní Core funkce zůstávají dostupné původnímu WPF.
Offline waveform/histogram/spectrum a Manual Analyzer jsou F6; recorder a OS
audio/FLAC integrace F7–F8. FLAC se v F4 nebude zpřístupňovat přes tichý nový backend.

Základní QD mapa není úplný port všech dosavadních fyzických surface/cell grafů,
floating detailů a pokročilých forenzních akcí. Každá taková položka se uvede
v parity reportu jako Not yet implemented s další návazností na společné grafové
komponenty. Před odstraněním WPF musí být doplněna nebo výslovně schválena jako
výjimka. Read-only evidence neznámých hostů se nesmí vydávat za jejich editor.
Úplný writable hex editor ani nová historie Undo nejsou součástí tohoto rozsahu.

## Akceptační brána po implementaci

1. Skutečné workflow New/Open → import → výběr → metadata/komprese → pořadí →
   Save As → reload → další editace → Save. Cesta a formát se po úspěchu navážou
   na kopii; originál a jeho metadata zůstanou beze změny.
2. Pro každý bindable formát: čistý roundtrip, úprava podporovaného pole,
   metadata/trailing data, prázdný dokument podle schopností a vadný vstup.
   Porovnání bajtů se stávajícím Core writerem a existujícími goldeny;
   žádná aktualizace golden souborů jen kvůli tomu, aby test prošel.
3. Save/Save As/Export oddělení včetně explicitního QD cíle, odmítnutí nesouladu
   přípony, read-only/non-SHARP, kolizí s jinou otevřenou kartou a ochrany source.
4. MFI/M2I/MTI: nová kopie i existující sidecar, metadata origin, chybný soubor,
   Cancel, kolize a simulované selhání druhého výstupu/rollbacku. Neúspěch nesmí
   označit dokument za čistý ani bez důkazu odstranit poslední obnovitelnou zálohu.
5. Export výběru i více souborů; potvrzení všech kolizí před zápisem, Cancel,
   deterministický WAV/LEP/L16 a COM vůči stávajícím exportérům. Žádná nová změna
   pulse timing, loader bytes, source compression detekce nebo PCM pravidel.
6. Skutečný UI výběr Ctrl/Command/Shift, klávesnice, F2/Delete potvrzení, reorder,
   transfer mezi dokumenty, map hit-test a synchronizace inspectoru, zachování
   viewportu a focusu. Textové vstupy mají přednost před globálními edit příkazy.
7. Headless a souborová integrace Windows/Linux; nativní smoke a screenshoty
   skutečného Tape a QuickDisk na Windows a WSLg včetně malého viewportu a chyb.
   Render 100/150/200 % se odliší od skutečného monitorového DPI.
8. Úplné dostupné legacy/Core/Capture/Windows Capture regrese a čtyři WPF smoke.
   Lokálně přítomné ignorované zdroje a fixtures se zahrnou; skipy se vysvětlí.
   Relevantní již existující sady: TapeDocumentTests, MZTFileReaderTests,
   M12SidecarTests, SidecarServiceTests, QuickDiskTests/HostDetection/Map/Visual,
   CompressionWorkflowTests, MzfCompressionTests, SharpTapeExporterTests,
   ListReorderTests, WorkspaceDragTransferTests a SaveAsDestinationTests.
9. Sestavení a locked restore bez nevyřešených chyb; dependency audit a parity
   evidence. macOS a nedostupné nativní/HW testy zůstanou UNTESTED.

Tyto testy jsou **plánované**. Při přípravě návrhu se pouze četly existující zdroje;
testy F4 nebyly spuštěny a produkční kód se nemění.

## Pořadí a návrat

Nejprve sjednotit ukládací/capability politiku a otestovat sadu médium–sidecar,
potom připojit Tape dokument a editor, následně QuickDisk a mapu, nakonec
kompresi/import/export, regresi a skutečné screenshoty. Jde o postup uvnitř jedné
schvalované F4, nikoli o oprávnění zahájit F5.

Změny se evidují odděleně od dosavadního rozpracovaného F1–F3 a uživatelských změn.
Při návratu se odeberou pouze konkrétní F4 změny; zachová se použitelný WPF host.
Po výsledcích F4 se předloží skutečné obrazovky a zbývající parity položky.

**Schválený rozsah:** implementace F4, včetně cíleného sdílení
ukládací politiky a transakce sidecarů s Core, při zachování binárních formátů
a referenční WPF aplikace.
