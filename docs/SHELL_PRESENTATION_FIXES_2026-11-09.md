# Hlavní okno – čekání na picker, Tools a záložky dokumentů

Oprava podle hlášení uživatele z 9. 11. 2026. Původní WPF a Core se nemění.

Následný požadavek odstranil `Selected record properties...` z Tools a změnil editor na Properties po dvojkliku. Aktuální chování popisuje [QUICKDISK_SPEED_EDITING_2026-11-09.md](QUICKDISK_SPEED_EDITING_2026-11-09.md); následující body jsou záznamem předchozí etapy.

- `Working…` bylo obecným stavem operace, která zahrnovala i čekání na nativní picker. Nový stav `IsAwaitingInput` odděluje čekání od `IsWorking`. Při výběru se zobrazí `Choose a file…` nebo `Choose a destination…`, bez pracovního progressu a tlačítka Cancel operation. Zámek operace `IsBusy` zůstává aktivní, aby nebylo možné souběžně měnit dokumenty. Scope se uvolní i při Cancel/chybě; běžná práce poté opět používá pracovní indikátor. Pokryty jsou společné Desktop input/output pickery a koordinátor Open/Save As.
- `Record properties` znamenalo vlastnosti programového záznamu v Tape/QuickDisk, nikoli vlastnosti zvukového záznamu. Položka se jmenuje `Selected record properties...`, je aktivní pouze pro vybraný programový záznam a otevírá přímo jeho inspektor.
- Tools neobsahuje New. Tlačítko `New ▾` na příkazové liště nabízí Tape, QD a Disk, i když už je jiný dokument otevřený. Diskový toolbar poskytuje tutéž nabídku.
- Záložky dokumentů nyní leží vždy nad příkazovými lištami. Původně byl společný toolbar Tape/QuickDisk nad záložkami, kdežto diskový toolbar pod nimi; přepnutí měnilo pořadí. Vybraný název používal modrý text na modrém pozadí. Nově má bílý text na modrém pozadí, ostatní názvy černý text. Zachována vazba na název dokumentu, indikace neuložených změn, tooltip s cestou, přepínání a vodorovný posuv; dlouhé názvy se zkracují elipsou.

Změněné části: DocumentCoordinator; společné DesktopDialogs, DesktopDiskDialogs a DesktopMediaDialogs; MainWindow XAML/Wpf rozložení; DiskWorkspace toolbar; cílené styly DocumentTabs v WpfStyles. Nové ShellPresentationTests hlídají operation lock během pickeru, skrytý pracovní indikátor, návrat do funkčního stavu po Cancel, čitelnost názvů a stejnou souřadnici záložek při opakovaném přepínání dvou DSK a QuickDisk v šířkách 980/1500, umístění New a otevření skutečných vlastností záznamu.

Ověření: Windows a WSL Debian/Linux – 207 FAST testů na každé platformě, 0 selhání / 0 přeskočení. Dále cílená sada ShellPresentationTests, ShellTests, DocumentLifecycleTests a WpfParityTests – 62 PASS na Windows i Linuxu; výsledky jsou v `TestResults/ShellPresentation-2026-11-09/*-targeted.trx`. Konzolové FAST záznamy jsou ve stejné složce, přesné příkazy/SDK/časování v `TestResults/PickerPolicy/<host>/fast/summary.json`. Plný mediální korpus ani nativní interaktivní UI zde nebyly spuštěny; UI regrese běží přes Avalonia Headless.

Spustitelné self-contained sestavení aktualizuje standardní `python tools/build_desktop.py --rid win-x64 --rid linux-x64`: pouze runtime soubory v `bin/win-x64` a `bin/linux-x64`, staging a build metadata v `obj/desktop`. Výsledek publikování je v `TestResults/ShellPresentation-2026-11-09/publish.log`.

Publikování obou platforem dokončeno. Windows runtime validuje přímo builder; linuxový publikovaný launcher byl navíc spuštěn ve WSL s `--validate-runtime`, viz `linux-runtime.json`.
