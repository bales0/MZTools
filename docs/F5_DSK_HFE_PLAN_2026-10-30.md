# F5 – DSK/HFE, filesystems, disk map, hex a opravné workflow

**Rozsah schválen uživatelem 30. 10. 2026 odpovědí „souhlasím“.**
Skutečné provedení a výsledky: [report F5](F5_DSK_HFE_2026-10-30.md).
Následující text zachovává schválený plán; není sám výsledkem testování.
Uživatel 30. 10. 2026 schválil skutečný vzhled a workflow F4 a přípravu tohoto
konkrétního plánu. Následným samostatným souhlasem schválil implementaci níže uvedeného rozsahu F5.
Výsledky předchozí etapy: [F4](F4_TAPE_QUICKDISK_2026-10-29.md),
[galerie](ui-design-system/avalonia-f4/index.html).

## Výchozí stav a výsledek

Read-only kontrola: větev `main`, HEAD `fb0c49cd69e487d218ac9e5d279d2b3111413303`.
Pracovní strom obsahuje rozpracovanou migraci F1–F4 i předchozí uživatelské změny;
není čistý a není určen k resetu, checkoutu ani hromadnému vracení souborů.
Core již obsahuje DSK/HFE kodeky, souborové systémy, analýzy i opravné služby.
F5 je připojí do stejného Avalonia shellu a design systému jako Tape/QuickDisk.
WPF zůstane funkční regresní referencí. Nevznikne druhý diskový parser či writer.

Cílem je skutečný diskový workspace: New/Open → soubory/mapa/analýza/hex →
preview změn → Apply → Save As → reload → další editace a Save.
F5 není jen prohlížeč a základní mapa nebude vydávána za plnou diskovou paritu.

## Mapa funkcí a existujících služeb

| Dosavadní WPF schopnost | F5 Avalonia workflow a zdroj pravidel |
| --- | --- |
| Open/New DSK a HFE | Loader a samostatná disková session; `DskDocument`, `DskOpenCoordinator`, `DiskFormatService`, `DskNewOptions`, `HfeImage`, `HfeDocumentSession`; Extended CPC DSK a podporované HFE v1/v2/v3, HD/DD a profily podle Core |
| Adresář CP/M, FSMZ, MRS, single/multi IPL | Jedna společná tabulka souborů a inspector; `CpmFileSystem`, `FsmzFileSystem`, `MrsFileSystem`, `SingleGameIplFileSystem`, `MultiGameIplFileSystem`; rozpoznání, volné místo, atributy, warning/read-only stav |
| Import/export, delete a properties | Výběr/všechny, preview konfliktů, rename a podporované atributy; `DskFileTransferService`, `DskFilePropertyService`, `DskExportSupport`; žádné přidělení zápisu neznámému filesystemu |
| Přenos mezi disky a Tape/QD | Společné clipboard/drop příkazy a `WorkspaceDragCodec`, `CrossDiskTransferService`; kontrola filesystemu, DPB, kapacity a konverzních ztrát; source se nemění |
| Disk Map | `DskLayoutModel`, `DskVisualModel`, `DskSectorSelection`, `DskGeneratedProjection`; logické/fyzické pořadí, výběr soubor–sektor–mapa–inspector, CHRN/ST1/ST2, chybové oblasti, zoom/pan/fit a přesné jednotky |
| Analýza, detail struktury a report | `DskAnalyzer`, `DskIssueHelp`, `DskStructureService`, `DskVisualReport`; filtr závažnosti, textová alternativa grafiky a přechod z nálezu na skutečný sektor/rozsah |
| Writable hex, patch a raw rozsah | Sdílená komponenta hex s draftem, Apply/Cancel, adresou a validací; `DskHexEditService`, `DskPatchService`, `DskRawRangeService`, `FixedHexInput`; sektor/blok a podporované raw rozsahy, ochrana nesprávné délky |
| Fyzické vlastnosti a HFE detail | `DskPhysicalPropertyService`, `HfeSemanticInspection`, existující HFE model; zachování track/sector metadat, fyzických dat a omezení zápisu, rozlišení zachycené stopy od generované projekce |
| Container repair při Open i ručně | Stávající nabídka z `DskOpenCoordinator`, `DskContainerRepairService`, `DskExtendedLengthRepair`; zdravý obraz bez opravného dialogu, oprava pouze preview kopie a povinný Save As |
| Filesystem repair a defragmentace | Samostatné příkazy, `DskFilesystemRepairService`, `DskDefragmentService`; nález, riziko a změněné rozsahy před Apply; nejsou zaměněny s container repair |
| Compare a konverze | `DskCompareService`, `DskCapabilityService`, `MediaConversionService`; čitelný diff s navigací; převod je explicitní Export/Convert, nepřepisuje nepozorovaně fyzický dokument |
| Format, CP/M layout, bootstrap a boot/system install | `DiskFormatService`, `CpmLayoutService`, `BootstrapService`, `CpmSystemBuilder`, `CpmSystemProfiles`, `BundledBootSystems`, `PersonalCpmSystemInstaller`, `UserProfileService`; potvrzený preview, kompatibilita cíle a zachování licence/původu již přibalených dat |
| Single/multi IPL tvorba a editace | Stávající `Mz800IplDskReader/Writer`, `Mz800MultiGameIplDskWriter` a filesystemy; přidání programů, názvy, pořadí, komprese a přepočet kapacity; Apply jen hotového návrhu, zastaralý asynchronní návrh nesmí změnit novější dokument |

Referenční akce byly dohledány v `DskEditorWindow.xaml.cs` včetně format,
normalizace, physical properties, compare, bootstrap, cross-disk copy,
CP/M layout, boot profiles, Native COM a IPL compression/reorder. Převody COM
použijí stejné Core služby a volby z F4. Dosavadní diskové dialogy se mapují na
sdílené formuláře/detailové komponenty; nebude se otevírat WPF okno z Avalonia.
Batch obrazovka zůstává pozdější samostatnou parity položkou, ne skrytým PASS F5.

## Dokument, jednotné UI a změny

Každá karta drží vlastní model, cestu, container variantu, modified/read-only stav,
filtr, výběr, map viewport a rozepsaný hex/properties draft. Shellové Open, Save,
Save As, Export a Close zachovají význam z F4. Přepnutí karty nesmaže draft.
Před operací, která by draft zahodila, se nabídne Apply/Discard/Cancel.
Undo/Redo se nezpřístupní bez skutečné historie; nový undo engine se nenavrhuje.

Workspace použije schválenou DSK/HFE kompozici F2, společnou tabulku, inspector,
report viewer a graph toolbar. Obsahové záložky seskupí Files, Map, Analysis,
Hex a Physical/Structure; specializované detaily budou sdílet komponentu s
vloženým pohledem. Na malém viewportu bude obsah dostupný posunem a inspektor
společným detailovým oknem. Textové editory mají přednost před globálními
zkratkami; Enter/F2, Delete, Ctrl/Command, Shift, F6 a kontextová nabídka použijí
stejnou politiku jako F4. Modelové souřadnice, raw bytes, bitcells a logické bloky
se nezamění. Render nebude načítat soubory ani znovu analyzovat disk při kreslení.
Výběr a výřezy budou testované i po změně pořadí, filtru a aktivní karty.

Destruktivní akce pracují nad klonovaným návrhem a revision kontrolou. Preview
ukáže konkrétní dopad a omezení. Cancel nepřidá část změn. Core capability určí
povolené operace a důvod odmítnutí; UI nepřidá vlastní odlišnou detekci filesystemu.
Neznámá, neúplná, nejednoznačná nebo write-protected média zachovají odpovídající
omezení a budou mít textově vysvětlený stav.

## Bezpečný zápis a originál po opravě

`DskDocument.Save` již ověřuje serializované bajty a teprve po úspěchu mění cestu
a clean stav. F5 jej propojí se shellovými kontrolami kolizí, source hashů,
cizích otevřených dokumentů a formátu. Pokud bude potřeba oddělit přípravu,
ověření a commit, půjde o úzký společný Core kontrakt používaný oběma hosty;
serializace DSK/HFE a stávající výsledky operací se nezmění. Použije se společný
bezpečný zápis z F4, nikoli nezávislá kopie writeru. HFE se neuloží jako DSK
pouhou změnou přípony; převod má vlastní explicitní volby a loss preview.

Důležité zjištění auditu: `CorePlatformServices.CaptureRepairSource` pro
file-backed obraz vyžaduje `IDskRepairProtectionFactory`. WPF má
`DskRepairSaveProtection` s Windows file identity a read leases; Avalonia tuto
službu zatím nekonfiguruje. Nesmí se nahradit pouhým porovnáním textových cest.

Součástí navržené F5 je hostový adaptér ověřující identitu zdroje a cíle přes
systémová API Windows/Linux, propojený s existujícím Core rozhraním. Použije
systémové funkce OS bez nového NuGet balíčku či instalované knihovny. Ověří
hardlinky, symlinky/aliasy, nahrazení i změnu zdroje a drží ochranu při commitu.
Na Linuxu nelze vydávat běžný file share flag za zákaz cizího zápisu; navržený
commit opravené kopie proto musí být bez přepsání existujícího cíle a znovu
ověřit zdroj. Pokud nelze ochranu doložit, zápis se odmítne s konkrétním důvodem.
Pro opravené kopie se ve společném Avalonia workflow použije nový neexistující
cíl; existující soubor se nepřepíše ani po obecném overwrite potvrzení. To je
vědomé zpřísnění Save As u repair kopie, které tento plán předkládá ke schválení.
Běžný Save As zdravého dokumentu nadále umožňuje potvrzenou kolizi.

Mac adaptér nebude označen za ověřený bez dostupného macOS. Bez ověřitelné
identity zůstane příslušný opravný zápis blokovaný; ostatní platformní výsledky
tím nezískají status macOS PASS. Konfigurace repair služby nesmí současně
zpřístupnit neimplementovaný FLAC backend. Žádná nová audio instalace ani capture.

Container repair zachová originál a stav RequiresRepairSaveAs přes další úpravy.
Truncate trailing dat potřebuje vlastní explicitní potvrzení podle existujícího
Core pravidla. Nejednoznačné CHRN, chybějící stopy či FDC status se nevyřeší
vymyšlením obsahu. Filesystem repair bude samostatná vědomá operace.

## Konkrétní místa změn

- `src/MZTools.Desktop/Documents`: diskový loader/session, příprava a commit
  save/export, zdrojové snapshoty, invalidace starých preview a ochrana draftů.
- `ViewModels`, `Views`, `Controls`, případně `Renderers`: diskový workspace,
  map/hex a jednotné forms pro diskové operace; napojení modulové navigace.
- `src/MZTools.Desktop/Platform`: pickery, repair source protection a kompozice
  platformních služeb; clipboard/drop využije F4 kontrakty.
- `src/MZTools.Core/Dsk`, `Media/Hfe*`, `Platform/CorePlatformServices.cs`:
  pouze cílené oddělení hostového IO/commit kontraktu a čistých politik,
  pokud je to pro bezpečné napojení potřeba. Žádný nový decoder, filesystem,
  formát, změna boot/ROM dat ani plošné zveřejnění interních API.
- Kořenové WPF soubory jen pokud musí volat tutéž vyčleněnou politiku;
  každá taková odchylka bude konkrétně evidována vůči F2 inventory.
- `tests/MZTools.Desktop.Tests`, `tests/MZTools.Desktop.Smoke`, relevantní Core
  integrační testy; README, parity report, F5 validation JSON a skutečná galerie.

Nové NuGet verze, native runtime distribuce, CI, release, instalace, změny jiných
repozitářů, přístup na fyzické diskové zařízení, commit/push ani odstranění WPF
nejsou navrženy. Fyzický obraz znamená soubor HFE a model stopy; ne zápis na disk.
Již používané závislosti a lockfiles zůstávají výchozí. Zásadní nový požadavek
na architekturu nebo závislost se předloží před jeho provedením.

## Akceptační testy – dosud plánované

1. New/Open a uložení DSK, HFE v1/v2/v3; fixture roundtrip i edit-save-reload,
   přesné bajty deterministických operací vůči původnímu Core a nezměněným
   goldenům. HFE zachování fyzické informace a loss policy se prokáže podle
   stávajícího modelu; univerzální fyzická bezztrátovost se nebude předpokládat.
2. CP/M varianty včetně custom DPB, FSMZ, MRS a single/multi IPL: list, import,
   export, delete, metadata/flags, pořadí a kapacita; nepodporovaná kombinace
   se odmítne bez mutace. Cross-disk i Tape/QD transfer zachová source.
3. Mapové hit-testy, logické/fyzické pořadí, CHRN/ST1/ST2 a souborové rozsahy,
   Ctrl/Command/Shift a synchronizace mapy, tabulky, hex a findings; zachování
   výběru/zoom/focusu a stav více nezávislých karet.
4. Hex a patch: délky, offsety a limity, Apply/Cancel, draft při switch/close,
   read-only/write-protected a neprůhledné fyzické stopy. Raw export má vlastní
   cíl, kolize a žádný účinek na binding dokumentu.
5. Open repair: zdravý disk bez promptu; safe deterministic nabídka; odmítnutí,
   Apply kopie, opakovaný preview, změněný source, ruční truncate a ambiguous
   obraz. Original bytes zůstanou stejné po každém scénáři.
6. Repair Save As: odmítnutí originálu, jeho hardlinku/symlinku i aliasu adresáře,
   cizí otevřené karty, read-only cíle a nahrazení zdroje. Cíl vzniklý mezi
   preview a commitem se nepřepíše. Chyba/cancel ponechá modified a původní
   binding; žádný neúspěšný zápis se neoznačí clean.
7. Container/filesystem repair, defragment, format/interleave, compare,
   conversion, bootstrap, boot install/profiles a CP/M layout: skutečný preview,
   Cancel a potvrzený Apply, výsledky shodné s Core. IPL rebuild testuje také
   zastaralé výsledky, změnu pořadí a compression options z F4.
8. Úplné dostupné Windows legacy/Core/Capture/Windows Capture a Linux Core/
   Capture/Desktop sady; čtyři původní WPF smoke i nový nativní diskový smoke.
   Lokálně přítomné zdroje a fixtures se zahrnou bez ohledu na gitignore.
   Chybějící referenční data budou konkrétní fixture skip, nikoli zamlčený PASS.
9. Relevantní existující sady: DskImage/FileSystem/Capability/FileProperty,
   DskHexEdit/Patch/SectorSelection/VisualAnalysis/MfmDetail/Structure/IssueHelp,
   DskPhysicalRepair/ExtendedLength/Defragment/Compare/Conversion/Analyzer,
   HfeTests/HfeEditingTests, CustomCpmLayout, CpmSystemBuilder,
   BundledBootSystem, PersonalCpmSystem, BootstrapRawRange a všechny IPL reader/
   writer sady. Testy se přesunou do portable sady jen se zachováním významu.
10. Headless i nativní Windows/WSLg diskový workflow, skutečné obrázky Files,
    Map, Hex, HFE physical a repair preview; viewport 1366×768, 1920×1080 a malý
    displej, render 100/125/150/200 %. Simulovaný render se odliší od monitor DPI.
    Nedostupné macOS, externí OS DnD, screen reader a HW ověření zůstanou UNTESTED.
11. Build bez chyb, locked restore, dependency audit, ověření tokenů, projektové
    izolace, nezměněných goldens, Capture a binárních algoritmů. Závěrečný report
    rozliší Implemented/Tested/Not yet implemented/Blocked a přiloží hashe.

Při přípravě tohoto návrhu byly pouze čteny zdroje a zapsán plán/záznam schválení
F4. Testy F5, build ani nativní aplikace nebyly spuštěny. Výsledky F4 nejsou
výsledky F5. Externí computer-use pipe byla ve F4 nedostupná; nebude se bez nového
důkazu tvrdit úspěch pointer bridge.

## Pořadí, návrat a další brána

Uvnitř schválené F5: nejprve disková session a safety kontrakt včetně repair
identity, potom Files/Map/Analysis/Hex a HFE detail, následně mutační operace,
filesystem/boot/IPL workflow, nakonec úplná regrese, parity matrix a skutečná
UI galerie. Průběžné kroky nejsou automatickým schválením dalších fází.
Případné neportované akce se nesmějí schovat pod označení hotové F5; musí být
konkrétně předloženy jako omezení k rozhodnutí uživatele.

Návrat znamená odebrat jen doložené změny F5 se zachováním F1–F4, uživatelské
práce a fungujícího WPF. Žádný hromadný reset/clean ani odstranění souborů.
Po implementaci se předloží změněné soubory, skutečné obrazovky, testy a rizika;
teprve další souhlas umožní přípravu rozsahu F6. F6 audio analyzér, F7 recorder,
F8 audio backendy/FLAC a F9 distribuce/odstranění WPF zůstávají mimo tuto etapu.

**Otázka ke schválení:** Souhlasíte s implementací této F5 včetně bezpečného
zápisu opravené kopie pouze do nového cíle, při zachování formátů a WPF reference?
