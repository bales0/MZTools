# QuickDisk – rychlost a Properties bez potvrzování výběru

Úprava na výslovný požadavek uživatele z 9. 11. 2026. Nahrazuje předchozí návrh položky `Selected record properties...` v Tools.

- Tools už neobsahuje vlastnosti záznamu. Dvojklik na položku otevírá okno s titulkem a nadpisem **Properties**. Zkratky Enter/F2 zpřístupňují stejné okno. Dvojklik do editoru rychlosti vlastnosti neotevírá.
- Název a loader jsou v hlavní tabulce pouze ke čtení. Jediným inline editorem zůstává **Speed**; změna se aplikuje ihned na všechny označené položky s použitím původního Core skládání profilu. Každé položce se zachová její vlastní loader.
- Změna výběru v tabulce i na mapě neotevírá dialog `Unapplied metadata`. Události z nabídky rychlosti se nezaměňují za změnu výběru řádků.
- Otevírání nabídky rychlosti myší zachovává původní vícenásobný výběr. Předchozí tabulkové chování ho zúžilo na jediný řádek; nově se cíl hromadné editace podrží po dobu tohoto kliknutí. Kliknutí na editor rychlosti také nezačne přetahování pořadí.
- Metadata se při obnově připraví před oznámením nových řádků. Nevzniká přechodný falešný draft porovnáním nového řádku se starými poli; obnova buněk nespouští nové uživatelské změny rychlosti.
- Properties je samostatný modální editor s původními Apply/Cancel akcemi. Zavření bez Apply zahodí pouze neaplikovaná pole, bez další otázky; nevytváří blokující draft v hlavním okně. Aplikované změny zůstanou v dokumentu. Rušivé označení neaplikovaných metadat se v editoru nezobrazuje.

Původní soubory se inline editací nepřepisují. Následný výslovný požadavek uživatele doplnil ukládání QuickDisk profilů do **.mqi**: viz [QUICKDISK_MQI_SIDECAR_2026-11-09.md](QUICKDISK_MQI_SIDECAR_2026-11-09.md). Rušivé potvrzení změny výběru zůstává odstraněné; obraz a jeho metadata se ukládají společně.

Změny: `MainWindow.Wpf.cs`, `RecordWorkspace.axaml/.axaml.cs/.Wpf.cs`, `RecordInspector.axaml`, `TapeSession.cs`; úpravy odpovídajících UI regresí a nové `QuickDiskSpeedEditingTests` ve FAST runneru. V této UI části zůstala Core a referenční WPF beze změny; následná MQI etapa rozšiřuje pouze Core metadata I/O podle nového požadavku.

Regrese ověřují opakované změny rychlosti pro skupiny [0,1] a [1,2], různé loadery, reálné kliknutí na šipku nabídky, zachování výběru, bajtů hlaviček a původního QDF, nepřítomnost potvrzení i při zamítnutých preview dialozích, skutečný dvojklik, zavření neaplikovaných polí, další editaci rychlosti, QDF/MQI round-trip a přítomnost MQI ve file-set save plánu. Cílená sada navíc zahrnuje `ShellPresentationTests`, `WpfParityTests` a `MediaUiTests`.

Důkazy: `TestResults/QuickDiskSpeed-2026-11-09/*-targeted.trx`, `*-fast.log` a `linux-targeted.log`; přesné FAST příkazy a prostředí v `TestResults/PickerPolicy/<host>/fast/summary.json`. Nejde o celý mediální korpus ani nativní interaktivní UI: UI testy jsou Avalonia Headless.

Finální stav po MQI rozšíření: 218 FAST PASS a 41 cílených UI PASS na Windows i Linuxu. Přesné metadata testy a další Core výsledky uvádí navazující MQI protokol.
