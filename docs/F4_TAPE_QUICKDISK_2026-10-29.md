# F4 – skutečné Tape a základní QuickDisk editory

29. 10. 2026. [Rozsah F4](F4_TAPE_QUICKDISK_PLAN_2026-10-29.md) uživatel výslovně
schválil. Implementace je připravená k posouzení v dostupném prostředí.
[Galerii skutečných obrazovek](ui-design-system/avalonia-f4/index.html) uživatel
schválil 30. 10. 2026. Následně samostatně schválil rozsah F5; její stav eviduje
[report F5](F5_DSK_HFE_2026-10-30.md). Nejde o dokončení celé migrace.

## Implemented

Avalonia shell nyní skutečně otevírá MZF/M12/MZT, MZQ/QDF a rozpoznané QD varianty.
Nové Tape a QuickDisk dokumenty mají samostatnou cestu, modified stav, rozpracovaná
metadata, výběr, filter, aktivní view a mapový viewport. Karty nejsou modelová data.

Společný editor nabízí víceřádkový výběr, import s preview, metadata s Apply/Cancel,
Move up/down, pořadí tažením, potvrzené odstranění, copy/paste a drop přes původní
`WorkspaceDragCodec`, kompresní preview, export a read-only hex detail. Enter/F2
a dvojklik zpřístupní inspector; kontextové menu používá stejné příkazy jako lišta.
Textové vstupy mají přednost před edit příkazy seznamu. F6 přepíná oblasti focusu.

Základní mapa kreslí existující `QuickDiskLayout`/`QuickDiskVisualSnapshot`;
nevznikl druhý dekodér. Výběr mapy, záznamu a inspectoru sdílí kontext. Zoom má
faktor 1.5, anchor pod kurzorem, pan a Fit all/selection. Logické bajty, fyzické
bitcells a ilustrační pořadí Tape záznamů nejsou zaměňované za čas nebo flux.
Core host/integrity/hash evidence je dostupná v mapě a média reportu.

Non-SHARP/unknown fyzické QD zůstává read-only: bez editace a bez Save/Save As
SHARP writerem. Lze prohlížet existující Core mapu a exportovat zvolený decoded/raw
region se zachováním jeho významu. Prázdný fyzický obraz právě vytvořený SHARP
writerem lze dále upravovat v téže session; při novém otevření se host znovu
posuzuje podle skutečných bajtů, bez vymyšlené SHARP identifikace.

### Formáty a ukládání

| Operace | Podpora F4 |
| --- | --- |
| Save As / bind | MZF, M12, MZT, MZQ, QDF, QD Sharp legacy, QD HxC, QD FlashFloppy |
| Metadata sidecar | MZF → MFI, M12 → M2I, MZT → MTI |
| Import | podporované Tape/SHARP QD záznamy a explicitně WAV/LEP/L16 přes stávající importer |
| Export | výběr/všechny záznamy do bindable formátů, oddělené soubory, WAV/LEP/L16 a Native COM |
| Export regionu | existující Core decoded block nebo raw gap/range; nepřevádí unknown host na SHARP |

QD cíl se volí výslovně před pickerem; stejná přípona `.qd` nevybírá variantu
podle pořadí filtrů. MZF/M12 vyžadují jediný záznam. Více záznamů lze uložit přes
Save As do MZT/QD nebo Export separate files. Packed vstupy `.mz0/.mz7` lze otevřít
a konvertovat, ale jejich přípony se nepoužijí pro běžný bound Save.

WAV/LEP/L16 a COM jsou exportní artefakty, nikoli Save As binding. Export nepřepne
aktivní cestu a nesmí přepsat žádný otevřený zdroj ani jeho metadata. Výstupy
signálu zůstávají ve staging souborech; commit je kopíruje 64 KiB bufferem, nikoli
načtením celého vygenerovaného WAV do RAM. Staging se po úspěchu, stornu i chybě uklidí.

Keep/None/ZX0/ZX7/Auto používají původní Core služby, včetně směru, quick, embedded
a partial omezení. None dekóduje rozpoznaný loader; neznámá komprese se nevydává za
nekomprimovaný obsah. Preview pracuje s klony a kontroluje revizi před Apply.
Nulová změna komprese neznečistí dokument. Zdrojový stav uvádí i embedded loader.

Rozpracovaná metadata nejsou totéž co aplikovaná změna. Filter je nezahodí;
změna výběru vyžaduje rozhodnutí. Save a další mutace vyžadují Apply/Cancel;
Close rozlišuje Save/Discard/Cancel a validaci. Nepodporované SHARP jméno nebo
neplatná hex hodnota nemění raw header ani payload. Ostatní header bajty zůstávají
zachované. WPF ani Avalonia tím nezískávají novou neexistující Undo historii.

## Architektura a ochrana dat

`TapeDocumentIO` spojuje existující Core loadery a serializéry. MZQ writer přijímá
`Stream` místo `FileStream`; samotné tělo jeho parserů/kodeků zůstává stejné.
`TapeSavePolicy` sdílí původní čistá validační pravidla s WPF delegacemi.
`TapeDocumentWriter` používá společnou `AtomicFileTransaction`; jeho MZF/MZT
serializace, profily a sidecar obsah se nepřepisují.

Desktop adaptér připraví celý plán média a metadat. Potvrdí všechny existující
cíle, zkontroluje kolize s otevřenými dokumenty a očekávané hashe. Všechny výstupy
se nejprve vytvoří/ověří vedle cíle, včetně flush. Teprve potom následuje commit
s možností rollbacku. Selhání druhého artefaktu neoznačí dokument za clean ani
nepřepne jeho cestu. Při neúspěšné obnově se poslední obnovitelné zálohy neodstraní
a chyba uvádí jejich umístění. Zápis přes symlink/junction se odmítá.

Existující sidecar nelze při nové kopii ponechat vedle nových dat jako neaktualizované
metadata: volba bez sidecaru takový cíl odmítne. Vypuštění trailing dat, profilů,
části MZF description při QuickDisk konverzi nebo regenerace fyzické QD struktury
vyžaduje konkrétní loss preview. Raw fyzická kopie se nepředstírá.

Více souborů netvoří univerzálně atomickou filesystem transakci. Power-loss,
síťové shares, nepřetržitý cizí zapisovač a všechny ACL/alias kombinace nejsou tímto
testováním certifikované. Specifické DSK repair ochrany patří do F5 a tento adaptér
je neobchází. Přibyly jen přesně vymezené friend assemblies Desktop a jeho testů.

Core kompresory, pulse catalog, exportní loadery a Capture produkční pipeline
se nemění. Žádná nová NuGet závislost, native runtime ani systémová instalace
nebyla v F4 přidána. Dva lock soubory z F3 zůstávají platné. Audit přímých
i transitivních balíčků nehlásil známou zranitelnost.

## Tested

[Strojová evidence](F4_VALIDATION_2026-10-29.json) obsahuje hashe původních TRX,
aktuálních zdrojů, binárních výstupů a nativní frame metadata. Lokální plné logy
jsou v ignorovaném `TestResults/F4`; ignorování souboru není důvod k vynechání testu.

| Sada | Passed | Failed | Fixture skip |
| --- | ---: | ---: | ---: |
| Úplná legacy WPF sada, Windows, konečný běh | 1202 | 0 | 3 |
| Úplné Core testy, Windows | 411 | 0 | 1 |
| Úplné Core testy, Linux | 411 | 0 | 1 |
| Capture, Windows | 210 | 0 | 0 |
| Capture, Linux | 210 | 0 | 0 |
| Windows Capture | 52 | 0 | 0 |
| Desktop unit/headless, Windows | 110 | 0 | 0 |
| Desktop unit/headless, Linux | 110 | 0 | 0 |
| Cílené poslední legacy Tape/sidecar/Save As IO testy | 75 | 0 | 0 |
| Cílené poslední Linux Core sidecar/signal IO testy | 83 | 0 | 0 |
| Nativní shell/pickery, každý OS | 15 kontrol | 0 | 0 |
| Nativní skutečné media workflow, každý OS | 14 kontrol | 0 | 0 |
| Původní Report / DSK / Audio WPF smoke | PASS | 0 | 0 |
| Původní Recorder WPF smoke | 114 kontrol | 0 | 0 |
| Celá solution build, Windows | PASS | 0 chyb | 0 varování |

Osm úplných sad dohromady znamená **2716 úspěšných případů a 5 fixture skipů**;
opakované cílené subsety se do tohoto součtu podruhé nezapočítávají. Skipy jsou
stejné chybějící úplné QD a CP/M reference jako ve F1/F3. Místně přítomná média
a původně ignorované zdroje se testují.

Všech **20 archivovaných binárních baseline/edited/export výstupů** má na Windows
a Linuxu totožnou délku a SHA-256: osm bindable formátů před/po editaci, WAV, LEP,
L16 a COM. Výstupy se také porovnávají se stávajícími Core writery. Původní binární
goldeny nebyly aktualizovány kvůli novému UI. Sidecary mají otestované profily
a správný binding; uvedených 20 hashů netvrdí shodu všech textových line endings.

Nové testy pokrývají také neúspěšný druhý výstup, rollback včetně zachování zálohy
při chybě obnovy, externí změnu sidecaru, odmítnutou kolizi, chybné metadata,
neaplikované drafty, skutečný signal export/import, COM Core shodu, source
preservation, read-only unknown host, mapový pointer hit-test/Ctrl výběr,
focus při psaní, droppy/reorder a přenos mezi dokumenty.

### Opravené problémy testovacího běhu

První úplný legacy běh měl jedinou chybu lifecycle: STA test vytvořil globální
WPF `Application`, kterou zavření posledního testovacího okna mohlo ukončit před
načtením XAML dalšího testu. Test nastavuje `OnExplicitShutdown`; produkční App
se kvůli tomu nemění. Opakovaná celá legacy sada prošla 1202/0/3.
Izolované pokusy o testhost pod `TestResults` navíc skončily timeoutem připojení
s nulou provedených testů; nejsou započtené jako PASS. Běžný výstupní adresář
prošel. Nezůstává nevyřešený selhaný test.

Původní F2 source inventory zůstává zmrazená. F4 vědomě mění jediný evidovaný
WPF UI zdroj `MainWindow.xaml.cs` delegací ukládací policy; ostatní původní UI
zdroje mají kontrolovanou shodu hashů. Nejde o vynechání testů kvůli ignore listu.

## Nativní a vizuální evidence

Windows a Debian 13/X11 přes WSLg: skutečné aplikace, nikoli jen headless.
Root smoke ruší skutečné platformní Open/Save As pickery. Media smoke používá
řízené testovací cesty a skutečně provede import, metadata, Save As s MTI, reload,
kodekový transfer do QuickDisk, QDF Save As a streamovaný LEP export. Původní
importované bajty zůstávají stejné. Řízené cesty se nevydávají za interaktivní picker test.

Nativní obrázky jsou Skia renderem vizuálního stromu běžícího okna. Rozměry,
skutečný render scale a pixel size jsou u každého frame v JSON. Headless matice
obsahuje 100/150/200% render, 1440×960, 1366×768, 920×640 a 640×480 podle modulu.
Není to potvrzení všech monitorových DPI nebo screenshot celé plochy.

## UNTESTED / Not yet implemented

- macOS/Cocoa a arm64; samostatný Linux desktop mimo WSLg.
- Všechny skutečné monitorové DPI, screen reader a fyzické audio zařízení.
- Skutečný OS drag/drop pointer bridge. Pokus přes dovednost computer-use skončil
  dvakrát chybou `Computer Use native pipe is unavailable … os error 2`.
  Nebylo přes něj ovládáno jiné okno. Mapové raw pointer události a skutečné
  routované drop handlery, lokální reorder i cross-document copy prošly headless.
  To nenahrazuje externí nativní OLE/DnD ověření.
- Pokročilé QuickDisk surface/cell grafy, všechny dosavadní floating forenzní
  detaily a plná fyzická parita. Před odstraněním WPF musí být doplněny nebo
  výslovně schváleny jako výjimka; základní mapa není jejich náhradou.
- DSK/HFE/filesystems/repair/IPL workflow (F5), plný Audio Analyzer (F6),
  Recorder a OS audio/FLAC integrace (F7–F8), zbývající batch UI a distribuce (F9).

## Použití a reprodukce

Spuštění: `dotnet run --project src/MZTools.Desktop/MZTools.Desktop.csproj`.
New Tape nebo New QuickDisk vytvoří prázdný dokument. Import nabídne soubory,
preview a teprve Continue přidá záznamy. Vyberte záznam, upravte metadata a
stiskněte Apply metadata; Cancel edits vrací pouze rozpracovaná pole.
Adresy a typ se zadávají hexadecimálně, například `4000` a `01`.

Save As nejprve vybírá konkrétní bindable formát a sidecar/trailing volby,
potom cestu. Po úspěchu další Save patří této kopii. Export má vlastní cíl,
výběr/všechny/separate a podle formátu tape machine nebo COM target/mode/
compression. Loss preview a všechny overwrite kolize předcházejí zápisu.
Unknown/non-SHARP host nezpřístupní SHARP save příkazy.

```powershell
py -3 docs/ui-design-system/tools/build_avalonia_tokens.py --check
dotnet test tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj -p:RestoreLockedMode=true
dotnet test MZTools.sln --logger "trx;LogFilePrefix=f4-regression" --results-directory TestResults/F4/windows/regression
dotnet run --project tests/MZTools.Desktop.Smoke/MZTools.Desktop.Smoke.csproj -- TestResults/F4/windows/native
```

Linux používá již schválené SDK a GUI knihovny z F3; např. z PowerShell:

```powershell
wsl -d Debian --exec env DOTNET_ROOT=/home/ales/.dotnet /home/ales/.dotnet/dotnet test /mnt/c/Dokumenty/GitHub/MZTools/tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj --artifacts-path /home/ales/.cache/mztools/f4-linux-desktop-artifacts --logger "trx;LogFileName=desktop-final-linux.trx" --results-directory /mnt/c/Dokumenty/GitHub/MZTools/TestResults/F4/linux -p:RestoreLockedMode=true
```

`MZTOOLS_F4_OUTPUT` nastaví adresář headless obrázků; do sourozeneckého
`binary-checks` testy uloží binární hashe. `summarize_f4.py` exportuje skutečné
výsledky/obrázky a porovná oba OS; `validate_f4.py` ověřuje artefakty a hranice
bez přepisování F2 baseline. Reprodukce galerie vyžaduje provedené příslušné běhy,
nikoli jen samotný build. Smoke krátce testuje clipboard pouze pokud neobsahuje
rich/non-text formáty; jinak jej zachová a roundtrip označí UNTESTED pro daný běh.

F4 nemění původní uživatelské změny ani neodstraňuje WPF. Další krok je schválit
skutečný vzhled a workflow F4, potom předložit konkrétní rozsah F5 k samostatnému
schválení. Otevřené multiplatformní položky tím nemění status na PASS.


## Schválení výsledku

Uživatel 30. 10. 2026 odpovědí „souhlasím“ schválil skutečný vzhled a workflow F4
a přípravu [konkrétního rozsahu F5](F5_DSK_HFE_PLAN_2026-10-30.md).
Schválení nemění evidovaná UNTESTED omezení a nezahajuje implementaci F5.
