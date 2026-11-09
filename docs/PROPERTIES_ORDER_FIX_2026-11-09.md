# Properties nesmí přesouvat záznam

Hlášení uživatele: po dvojkliku na položku QD a zavření Properties se záznam přesunul na konec seznamu. Pořadí smí měnit pouze výslovné řazení/přesun.

V `RecordWorkspace` se uchovával `PointerPressedEventArgs` pro zahájení drag-and-drop, ale `DragMove` neověřoval aktuální stav levého tlačítka. Otevření modálního Properties mohlo odklonit uvolnění tlačítka do dialogu, takže uložený stisk zůstal v tabulce. Následující pouhý pohyb myši pak mohl zahájit DnD; drop mimo řádek používá konec seznamu jako místo vložení. Tento mechanismus odpovídá hlášenému chování; nativní OS drag relace nebyla v této etapě interaktivně reprodukována.

Oprava je pouze v životním cyklu gesta v `Views/RecordWorkspace.axaml.cs`:

- Otevření i zavření Properties zruší připravené přetahování.
- Druhý a další klik se nezaregistruje jako začátek drag gesta.
- Přetahování vyžaduje aktuálně držené levé tlačítko a zavřené Properties. Pohyb bez tlačítka starý stisk zruší.
- Uvolnění tlačítka se zachytává i při již obsloužené události; ztráta capture také zruší drag stav.

Logika modelového pořadí, ručního Move/Reorder, MQI ani Core formáty se v této opravě nemění.

Regrese `PropertiesRoundTripCannotReorderQdWhenMouseReleaseGoesToTheDialog` má dva scénáře: Cancel neaplikovaných polí a Apply přejmenování. Vytváří skutečný QD Sharp se třemi položkami, otevře Properties dvojklikem bez druhého release v owneru, release pošle dialogu, zavře ho a přesune myš bez tlačítka ke konci tabulky. Kontroluje jména ve stejném pořadí, payloady, primární výběr, přesný počet revizí, nulový draft, nezměněný zdroj a nepřítomnost chyby/operace. Následný explicitní Move ověřuje, že ruční přesun funguje dál.

Důkazy: `TestResults/PropertiesOrder-2026-11-09/*-targeted.trx`, `*-fast.log` a `linux-targeted.log`. UI regrese běží přes Avalonia Headless; nejedná se o celý mediální korpus ani nativní interaktivní UI.

Finální ověření: Windows i WSL Debian/Linux **220 FAST PASS, 0 FAIL / 0 SKIP**; samostatně **28 cílených UI PASS** na každé platformě. Překlady bez chyb a upozornění. Kopie FAST TRX a prostředí jsou v této složce jako `<host>-fast.trx` a `<host>-summary.json`.

Standardní self-contained buildy: `bin/win-x64` a `bin/linux-x64`; publish/runtime důkazy v `TestResults/PropertiesOrder-2026-11-09/publish.log` a `linux-runtime.json`.
