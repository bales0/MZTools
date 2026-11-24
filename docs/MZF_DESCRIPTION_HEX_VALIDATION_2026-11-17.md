# MZF Description a celosouborová HEX editace — 17. 11. 2026

G0 audit výchozího HEAD `12c1e8415167ec94210fb28f67b9c1576d2ff034` proběhl bez změn, buildů a testů. Uživatel následně schválil textovou editaci Description a doplnil požadavek na HEX editaci **celého souboru**. Implementace zachovává původní uživatelské odstranění zadání azimutu; historie repozitáře nebyla měněna.

## Chování

- Avalonia Properties nyní obsahuje Description, počet zakódovaných bajtů, inline výstrahu a vstup do HEX editoru. V původním WPF je kompaktní `Properties…` v kontextovém menu záznamu, se stejnou politikou. Zobrazovací sloupec je nadále read-only.
- Textová editace podporuje existující SHARP mapování. Maximálně 104 výsledných jednobajtových znaků, bez automatického Trim ani tichých náhrad. Příliš dlouhý/nepodporovaný text je odmítnut, ne zkrácen. Explicitně změněný text nahradí 104B oblast 0x18–0x7F a zbytek doplní 00. Bez skutečné změny textu zůstávají všechny původní bajty přesné, i když jejich textová projekce byla ztrátová.
- HEX viewer nabízí `Edit entire MZF…`; také Properties otevírá tento editor s kurzorem na 0x18. Lze přepsat **všechny bajty hlavičky, payloadu a trailing dat**. Není omezen pouze na Description. Jde o pevně dlouhý overwrite editor, nikoli vkládání/mazání bajtů. Hlavičkou lze explicitně změnit rozdělení payload/trailing, pokud deklarovaný payload nepřesáhne celý buffer.
- Zarovnané sloupce HEX/ASCII/MZASCII, souborové offsety, RAM adresy payloadu a společná pozice kurzoru. Zápis probíhá HEX sloupcem, znakové sloupce jsou čtecí projekce libovolných bajtů. Undo historie má nejvýše 32 bufferů; Revert obnoví původní buffer. Apply předá kopii do dokumentu, Cancel nic neaplikuje. Teprve Save/Save As zapisuje disk.
- Editace Description i celého HEX bufferu je v této etapě povolena pro samostatný editovatelný MZF. MZT/M12/QD/QDF/MZQ nebyly tímto úkolem otevřeny nové editační cestě; existující readonly a konverzní pravidla zůstávají. Existující diskové HEX editory zůstávají samostatné.

## Autorita bajtů, draft a serializace

`RawHeader` zůstává autoritou pro Description. Nové `MzfDescriptionEditing.Replace` a `MzfRecordEditing.ParseEdited` sestaví novou kopii recordu z přesných bajtů přes skutečný MZF parser; tím jsou RawHeader[24..128] a Header.MzfHeaderDescription synchronní. Re-serializace celého upraveného souboru musí být byte-exact. Payload, trailing, profil a ostatní metadata se samovolně nemění.

TapeSession zahrnuje Description do HasDraft/LoadFields/ResetMetadata/ApplyMetadata. Název, typ, LOAD a EXEC se nově přepisují pouze při jejich skutečné editaci; samotná změna Description proto nesmí např. změnit NUL padding původního názvu na CR padding. No-op Apply neoznačí dokument jako změněný. HEX transakce odmítne neaplikovaný Properties draft nebo nahrazený source record; nemůže přepsat novější metadata. Filtr zachová stávající draft.

Původní source fingerprint a profil se přenášejí přes DeepClone, HEX rekonstrukci a kompresní/dekompresní rekonstrukci. Algoritmy komprese ani jejich signatury se neměnily. Při nezměněném profilu lze původní MFI zachovat včetně komentářů a formátu; explicitní změna profilu používá běžnou serializaci metadat. Save nastavení pro vytvoření sidecaru zůstává explicitní. WPF writer i Avalonia save plan kontrolují zdroj a sidecar před commitem, včetně Save As. Zápis dál používá AtomicFileTransaction. Načtení neprovádí žádné zápisy.

## Detekce a omezení varování

Klasifikátor je read-only. Silným důkazem je existující detekce embedded ZX7 (forward/backward) s konkrétním rozsahem v Description. Běžný ZX0 loader v payloadu není důkazem kódu v Description. Samotný neASCII SHARP znak není označen jako kód: bere se v úvahu podporované SHARP mapování a padding. Ostatní bajty mají Unknown, více netextových bajtů slabší SuspectedBinaryOrCode; varování říká pouze možnost binárních dat/kódu, nikoli dokázaný Z80 program.

Riziko z původního obsahu zůstává při Apply, klonování a přepnutí výběru až do úspěšného Save/reload. Text výstrahy udržovaného rizika výslovně odkazuje na původní Description. Nová rozpoznaná signatura má přednost před slabším původním rizikem. Žádný popup ani checkbox, varování neblokuje platnou editaci. Nedetekovaný loader neznamená prokázanou bezpečnost. Pro přesnou opravu jednotlivého bajtu v neznámém obsahu slouží HEX.

## Ověření

Nová `MzfDescriptionEditingTests` používá jen malé syntetické MZF a skutečně vytvořené ZX0/ZX7 varianty. Nezávislá očekávání kontrolují: 0/1/104 B, nulové doplnění, nepodporované znaky a 105B overflow, textové mezery a SHARP malé znaky, náhodnou binární Description bez editace, selektivní metadata, shodný header/payload/trailing, sidecar s CRLF/komentáři, Apply/Cancel, filtr, readonly, skutečný embedded ZX7, sticky warning, Save/reopen, vnější změnu MZF nebo MFI, zastaralý HEX buffer, přepis hlavičky/payloadu/trailing i skutečné psaní do vybraného bajtu se společným kurzorem. Osm kompresních/dekompresních variant ověřuje round-trip a zachování původní ochrany zdroje.

Příkazy po sestavení Desktop.Tests s `-p:MZToolsPublishLatest=false -p:MZToolsFastTests=true`:

```text
dotnet <artifacts>/MZTools.Desktop.Tests.dll -noLogo -class MZTools.Desktop.Tests.MzfDescriptionEditingTests -class MZTools.Desktop.Tests.MediaDocumentTests -class MZTools.Desktop.Tests.HexPresentationTests -trx <results>
```

Stejná sada se spouští Windows a WSL Debian/.NET 10. WPF UI smoke obsahuje devět nových kontrol Properties/HEX navíc k recorder sadě. TRX/logy a obrázky zůstávají mimo distribuci v `TestResults/MzfDescription` nebo pracovních `obj`.

### Oddělené zjištění z širší sady

Při zahrnutí celé `BatchWindowAndHexCursorTests` selhává nezměněný test `FolderScanRejectsStaleGenerationAndKeepsUnsupportedInputsVisible`: očekává 4 řádky včetně `.txt`, ale aktuální a výchozí HEAD `BatchInputSelection.Scan` explicitně filtruje přes `BatchMediaPolicy.Supports`, takže vrátí 3 podporované soubory. Batch produkční kód ani tento test nebyly změněny. Průběh širší sady (93 PASS/1 FAIL před doplněním finálních testů) zůstává v `desktop-windows.trx`; neprezentujeme jej jako plně úspěšný. Konečná cílená sada vybírá výše uvedené tři relevantní třídy. Toto není nová regrese Description a není zde skrytě měněna politika batch filtrů.


## Konečný výsledek

- Windows Desktop cílená sada: **86/86 PASS**, žádné skipy (`desktop-windows-final.trx`).
- Linux Desktop stejná cílená sada: **86/86 PASS**, žádné skipy (`desktop-linux-final.trx`).
- WPF UI smoke: **123/123 PASS**, včetně devíti kontrol této změny (`wpf-smoke-final.log`). Při vizuální kontrole se odhalil i zastaralý cursor status po Undo/Revert; opraveno a doplněno samostatné ověření vybraného bajtu.
- Součástí je přímý důkaz, že oprava jediného písmene v běžném textu s koncovými mezerami změnila přesně jediný bajt (offset 26); celý zbytek souboru zůstal shodný.
- Samostatné Windows/Linux balíky aktualizovány do `bin/win-x64` a `bin/linux-x64`. Skutečné spuštění obou přes `--validate-runtime`: .NET 10.0.11, libsndfile 1.2.2, captureStarted=false.
- Není to prohlášení o úspěchu celé historické testovací sady: výše uvedený nezměněný batch test má zvlášť zaznamenané selhání. Testy samotné nové funkce a zvolené regrese prošly.


## Navazující sjednocení vieweru a editoru

Samostatná HEX editační okna byla následně odstraněna podle požadavku uživatele. Nyní se přímo v Hex Browser přepíná View/Edit a zůstávají stejné barevné role a řádky. Properties je také v kontextovém menu. Aktuální stav a výsledky 89/89 Windows + 89/89 Linux + 124 WPF kontrol: [UNIFIED_HEX_VIEW_EDIT_2026-11-17.md](UNIFIED_HEX_VIEW_EDIT_2026-11-17.md).
