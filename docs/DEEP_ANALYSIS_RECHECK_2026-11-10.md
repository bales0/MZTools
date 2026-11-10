# Deep Analyze – opakované zadání a dokončení invalidace

Aktuální HEAD při čtení zadání: `46d3fc470e4a39631a72bd1640eedc1bff072120`. Znovu předané zadání z 9. 11. 2026 bylo porovnáno s aktuálním kódem a protokolem `DEEP_ANALYSIS_INPUT_2026-11-09.md`. Původní G0 návrh, význam Both, znaménko offsetu a cílené testy byly již v této konverzaci schváleny. Nezahajovalo se nové potvrzování téhož rozsahu.

## Stav hlavních bodů

- Deep L/R/Average/Both používá `DeepAnalysisInput` → `AudioPcmTransform` → skutečný Core vstup. Both má dva samostatné decode běhy a označené výsledky; Average skutečně průměruje zarovnané PCM.
- Offset znamená `R[n-offset]`, kladný posun zpožďuje R, maximálně ±2 s. Žádné přetáčení ani doplňování nul; společný platný interval zůstává v původní referenční ose.
- Normal podle pozdějšího výslovného pokynu uživatele nemá volbu Overlay/Mono: stereo automaticky zobrazuje overlay, mono jeden průběh. Pokročilé ovladače jsou pouze v nezávislém Deep.
- Zachovány source SHA-256/read-only ochrany, oddělené nastavení WAV exportu, mono, snapshoty/generace asynchronní práce a označování staré recovery jako OUTDATED.

Tyto body jsou doloženy čtením současných Desktop/Core cest a již existujícími `DeepAnalysisInputTests`, `AudioDocumentTests`, `SelectedAnalysisWindowTests` a `ReportedUiRegressionTests`. Dlouhý corpus ani nativní UI se tímto nevydává za ověřené.

## Dvě dokončené mezery

1. Změna rozsahu v `SetPreviewBounds` dříve nemazala samostatné pulzní měření ani neoznamovala změnu popisu vstupu. Nyní pouze skutečně změněné bounds zvýší generaci a provedou společnou invalidaci. Staré měření, histogram, spektrum a porovnání se odstraní. Opětovné nastavení stejného rozsahu platné měření nemaže.
2. Porovnání průchodů zůstávalo po změně vstupu dostupné. `InputChanged` nyní čistí porovnání a navazující tabulky. `ComparePassesAsync` odmítá OUTDATED recovery, hlídá generaci i při dokončení a po změně vstupu starý výsledek nepublikuje. Export porovnání vyžaduje aktuální vstup. Nový běh opět dovolí vytvořit a exportovat nové porovnání.

Dotčené soubory: `Documents/DeepAnalysisInput.cs`, `AudioSession.SelectedPreview.cs`, `AudioSession.Recovery.cs`, `AudioOperations.cs`; nové regrese v `DeepAnalysisInputTests.cs`. Core algoritmy a referenční WPF nejsou v této etapě měněny.

Nové testy měří skutečné PCM a FFT před změnou rozsahu, sledují oznámení aktuálního vstupu, kontrolují nemazání při shodném rozsahu a třemi scénáři (rozsah/kanál/offset) ověřují vytvoření porovnání, jeho invalidaci, zamítnutí exportu a obnovu po novém decode běhu. Existující sada pokrývá tichý kanál, různé programy L/R, opačnou fázi, skutečný Average s posunem, okraje a mono.

Testovací důkazy této etapy: `TestResults/DeepInput-2026-11-10/*-targeted.trx`, `*-fast.log`, souhrny FAST prostředí a publish/runtime záznamy. Nativní interaktivní UI, dlouhé WAV/FLAC a plný korpus zůstávají mimo ověření.

Finální ověření: Windows i WSL Debian/Linux **224 FAST PASS, 0 FAIL / 0 SKIP**; samostatně **67 cílených audio/UI PASS** na každé platformě. Překlady bez chyb a upozornění. FAST TRX a souhrny prostředí jsou zkopírovány také do složky této etapy jako `<host>-fast.trx` a `<host>-summary.json`.

Distribuce se aktualizuje standardním builderem do `bin/win-x64` a `bin/linux-x64`; publish a runtime záznamy jsou ve stejné složce důkazů.
