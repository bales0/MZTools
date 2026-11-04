# Payload-only deep analysis — unknown length, 4. 11. 2026

V Avalonia okně Deep analysis nyní prázdné pole Expected payload skutečně znamená neznámou délku. Pro Payload only automaticky zapne Core framing discovery. Explicitní Unknown length má přednost před dříve zadanou délkou; obsah zakázaného pole se neparsuje ani neposílá dekodéru. V režimu Standard MZF record se Unknown length nepoužije.

Po úspěšné payload analýze se otevře Block candidates. Samotný payload nevytváří kompletní MZF program bez hlavičky, ale zůstává dostupný jako kandidát s explicitním stavem kontrolního součtu a ověření délky. Nenalezený kandidát se nevydává za platný program. Core dekodér a jeho kontrolní pravidla nejsou změněné.

Regresní test `PayloadRecoveryFindsActualBytesWithBlankUnknownOrKnownLength` dekóduje skutečný 32bajtový payload a ověřuje jeho bajty i checksum pro prázdnou délku, Unknown se starou nesprávnou délkou 1, Unknown s nečíselným starým vstupem a známou délkou 32. Kontroluje také přesný interval, zobrazení Block candidates, nepřítomnost vymyšlené hlavičky a zachování zdroje i výsledků rodiče.

Aktuální distribuce: `TestResults/PayloadUnknown-2026-11-04/distribution`. Starší SelectedAnalysis build tuto opravu neobsahuje. Výsledky Windows a Linux testů jsou v témže adresáři nad distribution.
