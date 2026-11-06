# Batch / HEX — audit a rozsah opravy, 6. 11. 2026

Baseline: větev `main`, HEAD `bb39bc4175a75f3ec71eb6de3446f243434dd4fa`, shodný s kontrolovaným zadáním. Před změnami pracovního kódu obsahoval strom pouze uživatelské odstranění předchozí specifikace a nové zadání Batch; obojí zachováno. Batch A1–A7 a regresní testy byly samostatně schváleny odpovědí uživatele „Ano, schvaluji Batch A1–A7 a testy“. První pokus o Batch změnu před tímto souhlasem automatická kontrola odmítla a nebyl proveden. H1 je výslovný požadavek uživatele na opravu MZF kontextu. Žádný commit, push, PR ani publikovaný release není součástí práce; lokální binární vývojové buildy slouží k ověření. Část B nemá schválení implementace.

| ID | Priorita | Klasifikace baseline | WPF → Avalonia → Core | Důkaz / dopad | Návrh | Regrese |
|---|---|---|---|---|---|---|
| A1 | P1 | Ověřená chyba | BatchProcessDialog.ExtractFormat → InitializeBatch.extract → BatchTapeService.ExtractRecord | `Mzf` / `Bin` neodpovídají case-sensitive `MZF` / `BIN` | Kanonické hodnoty v jediném seznamu formuláře; Core validaci neměnit | Skutečný dialog → BatchOptions → Preview/Execute → bajty MZF/BIN + trailing |
| A2 | P1 | Ověřený funkční rozdíl | Devět WPF extrakčních voleb → čtyři Avalonia volby → Targets/ExtractRecord | MZT/WAV/FLAC/LEP/L16 nejsou dosažitelné, ačkoli Core podporu má | Zpřístupnit devět WPF voleb, zachovat Core odmítnutí neuchovatelných metadat | GUI extrakce všech voleb, zpětné čtení, očekávané odmítnutí trailing |
| A3 | P1 | Ověřený funkční rozdíl | BatchMediaPolicy dynamický filtr → statický `extensions` a neomezený picker → Supports/Extensions | Chybí m12/mz0/mz7; operace/cíl se do pickeru nepromítají | Použít Core policy; doporučení aktualizovat, explicitní uživatelský filtr zachovat; unsupported v preflight uvést | Choose files a scan, rekurze, custom filtr, nepodporované kombinace |
| A4 | P1 | Ověřená chyba a funkční rozdíl | WPF AudioSampleRate/UseHeuristic → BatchProcessForm.Update → TapeFormat.ValidateWavSampleRate / Prepare | FLAC/ExtractAll nemá rate; heuristic chybí u podporovaných operací; 48000/88200/96000 Core odmítá | Pouze prokázané 22050/44100; viditelnost i enabled podle Core podpory; skryté hodnoty nepředávat | Přepínání Operation/Target/Extract; reálné WAV/FLAC PCM rate |
| A5 | P1 | Ověřená chyba | WPF samostatné acceptLoss → `Execute(..., true, ...)` → RequiresLossConfirmation | Obecný Execute automaticky přijímá ztrátu | Samostatné potvrzení konkrétních loss položek; skutečné odpovědi předat Core; replace potvrdit odděleně | No/Cancel bez zápisu; mixed batch; lossless bez dalšího dotazu |
| A6 | P1 | Ověřený funkční rozdíl | WPF BatchProgress/Stop → globální Cancel bez BatchProgress → Preview/Execute progress | Není jméno zpracovávaného vstupu a počet; při zrušení může UI zůstat bez obnovení řádků | Jediný DocumentCoordinator, generation guard; Refresh ve finally; zrušený preview zachovat jako neproveditelný | Cancel během práce, dokončené výstupy a žádné částečné soubory |
| A7 | P1 | Částečně již opraveno | WPF Invalidate → existující Attach → BatchOptions | Folder/replace/auxiliary už přepínají; chybí extract handler a výše uvedené závislosti | Doplnit pouze potvrzené chybějící závislosti a validaci | Reálné ovládací prvky, Enter/Esc a Cancel |
| H1 | P1 | Ověřená chyba prezentace | WPF-derived QDF → bezpodmínečné DisplayRows → EncodeHeaderFrame | MZF/M12/MZT viewer ukazuje nesouvisející QDF blok | QDF pouze pro explicitní QDF kontext; MZF 128 B + payload + trailing | Skutečné otevření MZF a QDF z editoru, úplnost bajtů, žádná QDF legenda v MZF |

Baseline desktop Batch: 5 passed / 0 failed / 0 skipped. První paralelní Core pokus kolidoval s běžným desktop build adresářem; opakován v izolovaném artifacts adresáři. Takový build konflikt není chyba produktu.

## Další dialogy: evidence a hranice auditu

| B / C scénář | WPF reference | Avalonia / Core | Klasifikace a očekávaný test |
|---|---|---|---|
| Profily nového disku / HFE / raw ID | DskNewDialog, DskEditorControl.FormatDisk | DiskFormatForm, DiskFormatService, GetRawSectorIds | Profilová geometrie již opravena; zachovat a provést existující GUI/regresní matice. Úplné WPF klávesové srovnání všech profilů NETESTOVÁNO. |
| CP/M layout, DPB | CpmLayoutDialog, profile formuláře | ShellDiskDetails, CpmLayoutService, UserProfileService | Generický formulář je ověřený UI rozdíl. Chyba výstupních bajtů tím není prokázána; každou variantu DPB/import/Cancel je nutné samostatně reprodukovat. |
| Boot / system profiles | boot/profile dialogy | ShellDiskDetails, CpmSystemBuilder, BootstrapService | Core transakční cesta existuje; úplná dynamika a fokus NETESTOVÁNO. Bez prokázané vady žádný nový boot algoritmus. |
| Filesystem repair / Defragment | DskRepairDialog a editor handlery | ShellDiskCommands, DskFilesystemRepairService, DskDefragmentService | Existující preflight/explicitní souhlas; současné UI důkazy neprokazují všechny filesystem profily. Riziko vyžadující jednotlivou reprodukci. |
| Normalize / Compare / DSK-HFE conversion | DskCompareWindow, DskConversionDialog | ShellDiskCommands, ContainerNormalizeService, MediaConversionService | Existující Core workflow; obecný formulář je UI rozdíl. Oprava bezpečnosti Batch loss je A5; samostatné diskové formuláře se plošně nepřepisují. |
| Raw import/export / patch / bootstrap | DskRawRangeDialog, DskPatchPreviewWindow | DskRawRangeService, DskPatchService, BootstrapService | Ochrana draftu/read-only a preview již implementovány. Všechny focus/Cancel kombinace NETESTOVÁNO. |
| File properties / IPL / compression | DskFilePropertiesDialog, MultiGame IPL dialogy | DiskInspector, ShellDiskDetails, MzfCompressionService | Existující F2/Enter/Esc a Core regrese zachovat. Úplná WPF shoda každého formuláře NETESTOVÁNO, ne PASS. |
| MZF/M12 → COM / Import Native COM | NativeCom dialogy | ShellNativeComTools, NativeComFileService | Kontextové příkazy existují; platnost loaderu se nemění. Všechny UI vstupy a fokus NETESTOVÁNO. |
| HEX / SHASCII / fixed-size edit | HexBrowser, DskHexEditorControl | HexViewWindow, FixedHexInput | Zarovnání, scroll a zamknutí již opraveny; v tomto kroku měnit pouze chybný formátový kontext H1. |
| macOS / ARM / hardware / multi-monitor / screen reader / síť | Platformní ověření | Dostupné prostředí Windows + WSL Linux x64 | NETESTOVÁNO. Žádná deklarace all-platform parity. |

Pořadí malých kroků: H1; A1–A4 formulář a kanonické parametry; A5 potvrzení; A6/A7 progress, Cancel a detaily; GUI/end-to-end regrese; dostupné Core/desktop/nativní testy. Neprokázané B neshody jsou auditní backlog pro samostatné cílené ověření, nikoli záminka k redesignu nebo změně Core formátů.

## Provedená schválená oprava

| ID | Stav | Aktuální důkaz |
|---|---|---|
| A1–A2 | Implementováno, testováno | Jediný seznam Native/BIN/MZF/M12/MZT/WAV/FLAC/LEP/L16. Osm voleb projde skutečným formulářem až do Execute; všech devět end-to-end testů znovu čte výstup a porovnává payload, u MZF/M12 všechny bajty. |
| A3 | Implementováno, testováno | Formulář i file picker čerpají přípony z BatchMediaPolicy. GUI aktualizuje doporučení při změně cíle, uchovává vlastní omezení. Folder scan skutečně načte m12/mz0/mz7. Core testy pokrývají rekurzi, kolize a nepodporované kombinace. |
| A4 | Implementováno, testováno | WAV/FLAC při konverzi i extrakci zpřístupní 22050/44100; skutečné výstupy mají zvolených 22050 Hz. Heuristic používá Core podporu; neaktivní rate/auxiliary/extract/replace se při sestavení BatchOptions ignorují. |
| A5 | Implementováno, testováno | Execute, Accept batch data loss a Replace original batch inputs jsou oddělené odpovědi. Smíšená lossy/lossless dávka při odmítnutí nic nezapíše, po souhlasu dokončí obě položky. Replace odmítnutí zachová originál; přijetí zachová původní bajty v .bak. Bezztrátová extrakce nemá loss dotaz. |
| A6 | Implementováno, testováno | Batch používá existující coordinator a jeho Cancel, generation guard a progress stage/count/file; tabulka přidává Integrity. Deterministický Cancel během verifikace druhé položky zachová první kompletní pár, druhý nepublikuje, obnoví řádky na Completed/Cancelled a zablokuje další Execute. |
| A7 | Implementováno, testováno | Změny Operation/Input/Target/Extract/Replace aktualizují viditelnost i enabled příslušných řádků; Esc na skutečně fokusovaném ComboBoxu zavře formulář bez změny dokumentu. |
| H1 | Implementováno, testováno | Skutečné otevření MZF/M12/MZT/QDF z pracovního editoru ověřuje QDF blok a legendu pouze u QDF. Zkontrolovány rendery MZF a QDF; MZF má přesně 128 bajtů hlavičky a kompletní data/trailing. |
| B / C | Auditní backlog | Žádný plošný přepis. Matice výše rozlišuje staticky zjištěné rozdíly od dosud neověřených scénářů; není úplným runtime srovnáním všech specializovaných dialogů. |

Změny produkce: `BatchProcessForm.cs` (jednotná pole a dynamika), `ShellBatchCommands.cs` (parametry/policy/potvrzení/progress/finally), `DiskDialogs.cs` a `DesktopDiskDialogs.cs` (filtrovaný Batch picker), `DocumentCoordinator.cs` (adaptér progress), `BatchWorkspace.axaml` (Stop/progress/Integrity), `HexViewWindow.cs` a `RecordWorkspace.Wpf.cs` (formátový kontext vieweru). Soubor `RecordWorkspace.Wpf.cs` je Avalonia view pod `src/MZTools.Desktop`, není to původní WPF produkce.

Testy: nové `BatchParityTests.cs`; rozšířené `DiskDocumentTests.cs`, `HexPresentationTests.cs`, `Sha256AndParityTests.cs` a nativní `MediaScenario.cs`. Portable Core projekt nově zahrnuje původní `BatchProcessTests.cs` a `PersonalCpmSystemTests.cs`, používá skutečný platformní FLAC adaptér nastavený testovacím `AudioCodecSetup.cs`. V původním testu CP/M je pouze nahrazena neplatná relativní cesta za existující `TestMediaPaths.Resolve`, aby izolovaný build našel již přítomný referenční disk. Core produkční algoritmy ani původní WPF produkční soubory se nemění. Readme, tento audit, HEX dokumentace a přibalení auditu v packageru popisují aktuální stav.

## Validace upraveného pracovního stromu

SDK Windows i WSL Debian: .NET `10.0.303`, runtime `.NET 10.0.11`. Datum 6. 11. 2026, základ HEAD `bb39bc4175a75f3ec71eb6de3446f243434dd4fa` + uvedené necommitované změny. Výsledky jsou v `TestResults/BatchParity-2026-11-06`; nejde o výsledky staršího commitu.

| Běh | Passed | Failed | Skipped |
|---|---:|---:|---:|
| Windows cílené Batch GUI/Shell | 28 | 0 | 0 |
| Windows Desktop plná sada | 343 | 0 | 1 |
| Linux Desktop plná sada | 351 | 0 | 1 |
| Windows rozšířený smíšený loss scénář | 1 | 0 | 0 |
| Linux rozšířený smíšený loss scénář | 1 | 0 | 0 |
| Windows Core plna prenositelna sada | 487 | 0 | 1 |
| Linux Core plná přenositelná sada | 487 | 0 | 1 |

Desktop skip je pouze explicitně volitelný `ThirtyMinuteMediaOperations`. Skutečné dlouhé nahrávání se zde neprovádí. Ignorování souboru v Gitu testy nevypíná: média se hledají i v původním lokálním corpus a kopírují do izolovaného výstupu. Externí trojice `DSKA0001_Roland.QD`, `DSKA0002_MO5_CQ90-028_formatted.QD`, `DSKA0003_Akai_formatted.QD` není v tomto workspace přítomná ani při vyhledání bez ignore filtrů; Core ji správně eviduje jako nepřítomnou referenci.

Windows i Linux nativní smoke proběhl s exit=0; každá platforma má 103 úspěšných kontrol: hlavní scénář 15, media 27, disk 24, audio 21 a recorder 16. Rendery pocházejí ze skutečných nativních oken, nikoli z desktopových screenshotů. Neprokazují fyzický audio hardware ani úplnou WPF paritu.

Zaznamenané neúspěšné pracovní pokusy: kolize sdíleného artifacts adresáře, nekompatibilní glob původního WPF test projektu, chybějící konfigurace FLAC v nově zapojeném testhostu, jednorázový timeout připojení testhost a test odesílající KeyRelease do již zavřeného okna. Nejsou vydávány za PASS; výše jsou výsledky opravených izolovaných běhů. Starší duplicitní Core běh byl nahrazen finálním buildem.

## Lokalni binarni buildy

Aktualni self-contained Windows/Linux x64 buildy jsou v `TestResults/BatchParity-2026-11-06/distribution`. Kazdy archiv ma SHA-256 sidecar a vnitrni `SHA256SUMS.json`; runtime diagnostika rozbalenych aplikaci je v `{windows,linux}/packaged-runtime.json`. Jde o nepodepsane vyvojove buildy, nikoli publikovany release. Balik upravenych zdrojovych souboru se nevytvari.
