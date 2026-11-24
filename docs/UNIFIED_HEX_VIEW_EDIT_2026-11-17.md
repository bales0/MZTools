# Společný HEX viewer/editor a Properties — 17. 11. 2026

Navazuje na schválenou MZF Description / celosouborovou HEX editaci. Uživatel požadoval označení stejných částí souboru v editoru jako ve vieweru, odstranění dalšího téměř stejného okna a Properties v kontextovém menu záznamů.

## Výsledné ovládání

- MZF se otevře v **Hex Browser**. Tlačítko **Edit** odemkne tentýž dump v témže okně. Stejná tabulka/řádky zůstanou na místě; neotevírá se navazující editor.
- Hlavička je barevně rozlišená na Type, Name, Name terminator, Payload length, LOAD, EXEC a Description. Data a Trailing mají samostatné označení. Legenda je společná pro View/Edit; aktuální pole je i ve stavovém řádku.
- Zachovány jsou souborové offsety, RAM adresy payloadu, HEX/ASCII/MZASCII, výběr odpovídajícího bajtu ve všech třech sloupcích a inline upozornění na kód v Description. Jde o strukturu MZF, nikoli nový disassembler Z80.
- Zadávání a vkládání HEX přepisuje bajty pevně dlouhé kopie celého MZF. Undo/Revert/Cancel nic nezapisují do souboru. Apply aktualizuje dokument a **vrací stejný Hex Browser do View**, bez zavření. Další Edit/Apply v témže okně funguje proti aktuální verzi záznamu. Teprve Save zapisuje disk.
- Při změně délky nebo LOAD v draftu se aktualizují hranice Data/Trailing a RAM adresy. Neplatná deklarovaná velikost stále zabrání Apply; nepoškodí dokument. Cancel obnoví aktuální aplikovanou verzi.
- V Avalonia kontextovém menu záznamů je **Properties...**, napojené na stejný RecordInspector jako dvojklik. Původní WPF položka používá stejný popisek.
- Ostatní host formáty, readonly a QDF derived prefix/CRC náhled zůstávají zachované. Sjednocení neotevírá nové zápisové cesty pro QD/QDF/MZT/M12.

## Implementace

Core `MzfHexPresentation` sdílí role, popisky a barvy polí; `MzfHexBuffer` sdílí fixed-size draft, ověřené zadávání/vkládání HEX a historii nejvýše 32 kopií. Autorita původních bajtů a skutečný MZF parser/validator se nemění.

Avalonia `HexViewWindow.Editing.cs` připojuje editaci přímo ke stávajícím řádkům a byte-selection rendereru `HexViewWindow`. Pracovní pole bajtů drží stabilní identitu, takže se při běžném přepisu pouze překreslí realizované buňky; nedochází k novému oknu ani přestavbě celé tabulky. Při změně geometrie se aktualizují řádky v téže tabulce. Původní `MzfHexEditorWindow` byl odstraněn.

WPF `HexBrowser` hostuje barevný `MzfHexViewControl` s virtualizovanými řádky. View/Edit používá tentýž renderer, výběr a data. Samostatné `MzfHexEditingWindow` bylo odstraněno. Apply aktualizuje i zachycenou referenci záznamu, aby druhý Apply v jednom okně nepřepsal zastaralý record.

## Ověření

Konečná cílená sada zahrnuje `UnifiedHexTests`, `MzfDescriptionEditingTests`, `HexPresentationTests` a `MediaDocumentTests`.

- **89/89 PASS Windows**, bez skipů, build bez upozornění/chyb.
- **89/89 PASS Linux**, bez skipů, build bez upozornění/chyb.
- **124/124 PASS WPF smoke**, včetně návratu Apply do View v témže okně. Hardware audio vstup není součástí tohoto UI ověření.
- Nové integrační testy ověřují identitu téže tabulky a počet oken při přepínání, sedm odlišných barev polí hlavičky, Cancel bez mutace modelu, dva Apply za sebou, aktualizaci RAM adres, odmítnutí neplatné délky/paste, hranice Description/Data/Trailing a skutečný kontextový příkaz Properties.
- Původní MZF ochrany, byte-exact metadata/payload/trailing, source/sidecar ochrany, readonly a kompresní regrese zůstávají v cílené sadě.
- Screenshoty skutečného View/Edit jsou v `TestResults/UnifiedHex/images`. WPF snímek a kompletní log jsou pod pracovním `obj` a `TestResults/UnifiedHex/wpf-final.log`.

Samostatný dříve zdokumentovaný nesoulad očekávání nepodporovaného `.txt` v batch folder testu nebyl tímto úkolem upravován; není součástí uvedených 89 cílených testů.


## Distribuce

Linux balík aktualizován v `bin/linux-x64`; skutečný `--validate-runtime` ověřil .NET 10.0.11 a libsndfile 1.2.2 bez zahájení capture. Windows publish proběhl do stagingu, ale atomické nahrazení `bin/win-x64` blokuje běžící MZTools (`WinError 32`); původní balík je zachován. Uživateli byla předložena žádost o zavření aplikace po uložení práce. Do té doby Windows distribuce obsahuje předchozí verzi.


Po potvrzení zavření aplikace byl Windows balík úspěšně nahrazen v `bin/win-x64`. Následný `--validate-runtime` prošel: .NET 10.0.11, libsndfile 1.2.2, captureStarted=false. Obě aktuální distribuce nyní obsahují sjednocený viewer/editor i kontextové Properties.
