# Zarovnání a barvy hex zobrazení — 5. 11. 2026

Hex editor má tři souběžné sloupce **HEX / ASCII / SHASCII (EU)**. Každý řádek představuje stejných 16 bajtů. Sloupce mají stejný explicitní monospace font, velikost 12, výšku řádku 18, padding, rámeček a horní odsazení. Celá trojice je v jednom horizontálně i vertikálně posouvaném panelu. Interní scrollování a zalamování jednotlivých TextBoxů jsou vypnuté. ASCII a SHASCII se aktualizují při overwrite editaci, resetu, změně sektoru i CP/M bloku; zamknutí a transakční Apply zůstávají zachované.

Hex viewer používá jediný pohled bez záložek. Obsahuje MZF hlavičku, odvozenou QDF hlavičku a kompletní payload i trailing data. Hex má pevná místa pro 16 bajtů se skupinami po čtyřech, oba znakové sloupce 16 pozic, včetně doplnění posledního neúplného řádku. Sloupce se při malé šířce posouvají horizontálně společně.

Barevné hodnoty odpovídají původnímu WPF HexBrowser: QDF prefix bajty 0–3 mají ARGB `55FF5555`, vypočtené CRC bajty 68–69 `554285F4`; trailing data mají foreground `#999999`. Zvýraznění je po jednotlivých bajtech uvnitř řádku, také v ASCII a SHASCII, s legendou a vysvětlením. Odvozená QDF reprezentace používá sdílený encoder a není další zdrojový soubor. V této úpravě se nemění Core dekodéry, diskové formáty ani zdrojové bajty.

`HexPresentationTests` ověřují částečné řádky délky 1/15/16/17/33, skutečné Run barvy na hranicích polí, platné QDF CRC, úplnost zdroje, nepřítomnost záložek a stejné Y souřadnice textových presenterů editoru před a po scrollování. Součástí testu je skutečná overwrite editace přes klávesnici a aktualizace obou znakových sloupců. Nativní scénáře otevírají viewer skutečnou akcí Hex view a editor přes výběr sektoru.

Výsledky Windows/Linux testů, nativní rendery a WPF reference jsou v `TestResults/HexPresentation-2026-11-05`. Běžné plné sady obsahují samostatně spouštěný `ThirtyMinuteMediaOperations` benchmark; jeho případné přeskočení je v protokolu explicitně evidováno a nesouvisí s ignore listem. Pro tuto UI úpravu se benchmark nespouští.

[Snímky hex vieweru a editoru](ui-design-system/hex-presentation-2026-11-05/index.html) zachovávají originální PNG a kontrolní součty. Aktuální Windows/Linux x64 archivy jsou v `TestResults/HexPresentation-2026-11-05/distribution`; starší buildy tuto revizi neobsahují.
