# QuickDisk profily a rychlosti v .mqi

Výslovný následný požadavek uživatele: ukládat rychlosti QuickDisk podobně jako metadata MZT; výsledná zvolená přípona je **.mqi** (nahrazuje předběžné .mzi). Aktuální HEAD při ověření: `3a6176208bc5be0ce64c00331e69aa71b07e5a8d`.

## Pojmenování a obsah

`disk.qd` → `disk.mqi`. Stejná přípona se používá pro `disk.qdf` a `disk.mzq`. Jde o doprovodný textový soubor, hlavní formát obrazu se nemění. MQI používá stejnou kanonickou ASCII serializaci a parser profilů jako MTI:

```ini
RECORD=1
TYPE=NORMAL
SPEED=1:3

RECORD=2
TYPE=IC
SPEED=1:2
```

Čísla RECORD jsou pořadí od 1. Při uložení po přidání, odstranění či přeuspořádání se sidecar kompletně přegeneruje pro aktuální pořadí. Přenáší se loader i rychlost. Původ metadat se značí `LoadedFromMqi`; stávající enum hodnoty nebyly přečíslovány. Bez sidecaru zůstává původní implicitní chování. Načítání validních sekcí/ignorování neplatného profilu odpovídá MTI, batch integrity používá svou existující přísnější kontrolu sekcí.

## Integrace a ochrany

- Save/Save As standardně vytváří MQI pro QD Sharp, QD HxC, QD FlashFloppy, QDF a MZQ. Obraz a sidecar se ukládají stejnou ověřenou atomickou transakcí jako MZT/MTI. Samotná změna rychlosti nemění hlavičky ani payloady programů v obrazu.
- Open automaticky načte MQI přes centrální `TapeDocumentIO.Read`. Zdrojová metadata se hashují před/po otevření a evidují v souborové sadě. Externí změna MQI zablokuje save; existující metadata se při vypnutém sidecaru nesmí tiše ponechat zastaralá.
- Import přebírá profily a SHA-256 závislost MQI. Export pro QuickDisk zpřístupňuje stejnou volbu sidecaru jako Tape formáty. Přípona MQI se nevybírá jako samostatný hlavní mediální dokument.
- Batch konverze i extrakce QuickDisk zapisují MQI; náhled i execute evidují sidecar vstupu. Změna MQI po preview zablokuje výstup. Profily doložené MQI jsou viditelné v katalogu. Profil s MQI již nevyvolává potvrzení ztráty rychlosti; varování před skutečnou ztrátou dlouhého MZF popisu nebo nepodporovaným embedded loaderem zůstávají.
- Volba nevytvořit metadata je nadále explicitní; při nenulových profilech pak zůstává existující upozornění na ztrátu metadat. Ochrany kolizí, Cancel, read-only hostů a celých zdrojových sad se zachovávají.

Core změny jsou omezené na metadata I/O: `Mzf.cs` (MQI přípona, původ, sdílení MTI parseru/serializace), `TapeDocumentIO.cs` (načtení) a `BatchTapeService.cs` (závislosti/výstupy). Dekodéry, kompresní algoritmy, fyzické QuickDisk writery a referenční WPF UI se nemění. Desktop změny: `TapeSession` file-set save/load, `MediaOperations` import závislosti, společné Save As/Export volby.

## Ověření

`QuickDiskSidecarTests` pokrývá všech pět formátů, Save As a Save, opětovné otevření přes Core i Desktop, shodné byte schéma MQI/MTI, změnu pořadí, origin MQI, zachování původního média, externí změnu metadat, kolizi a neúspěšný sidecar zápis bez poloviční dvojice, import/export a batch včetně změny po preview. Současná UI regrese hromadné editace byla rozšířena na skutečný QDF/MQI round-trip.

Důkazy jsou v `TestResults/QuickDiskSpeed-2026-11-09`: `*-fast.log`, `windows-mqi.trx`, `core-sidecars`, cílené UI výsledky a publish/runtime protokoly. FAST runner zaznamenává přesné SDK/OS/HEAD/příkazy a nulové kopírování korpusu do `TestResults/PickerPolicy/<host>/fast/summary.json`.

Finální výsledky: Windows i WSL Debian/Linux **218 FAST PASS, 0 FAIL / 0 SKIP**; samostatně **41 cílených UI PASS** na každé platformě. Linux dále **19 původních Core sidecar PASS**, **18 cílených Core batch PASS** a **17 Core raw/patch FAST PASS**. Překlady bez chyb a upozornění. Kopie finálních FAST TRX a souhrnů jsou také ve složce této etapy jako `<host>-fast.trx` a `<host>-summary.json`.

Standardní self-contained distribuce se aktualizuje do `bin/win-x64` a `bin/linux-x64`; staging a metadata zůstávají v `obj/desktop`. Publikovaný Linux launcher se kontroluje přes `--validate-runtime`. Plný korpus a nativní interaktivní UI nebyly součástí tohoto ověření.

Publikování obou finálních buildů dokončeno. Windows runtime prošel kontrolou builderu; publikovaný Linux launcher prošel ve WSL kontrolou `--validate-runtime` (.NET 10.0.11, libsndfile 1.2.2, captureStarted=false).
