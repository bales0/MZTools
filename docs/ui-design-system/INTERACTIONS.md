# Společné ovládání a význam příkazů — návrh F2

Primární modifikátor `Primary` znamená Ctrl na Windows/Linux a Command na macOS.
F3 ověří konkrétní Avalonia verzi a platformní hotkey konfiguraci; nespoléhat pouze
na text v menu nebo na předpoklad, že každé použití `Ctrl` mapuje stejně.
[Oficiální hotkey dokumentace](https://docs.avaloniaui.net/docs/input-interaction/keyboard-and-hotkeys)
a [macOS konvence](https://docs.avaloniaui.net/docs/platform-specific-guides/macos).

## Příkazy

| Příkaz / zkratka | Význam a podmínka | Po Cancel / chybě |
| --- | --- | --- |
| Open… / Primary+O | explicitní picker; nový dokument až po úspěšném načtení a analýze | aktivní dokument beze změny |
| Save / Primary+S | zapíše podporovaný bound dokument a ověří výstup; clean/read-only/neplatný preview jej blokuje | dirty, path a původní data zůstávají |
| Save As… / Primary+Shift+S | vytvoří kopii v podporovaném bindable formátu, po úspěchu přepne aktivní path | nepřepne path ani neodznačí modified |
| Export… | samostatný artefakt/konverze/výběr/report; nepřepne aktivní dokument | nezmění zdroj ani jeho stav |
| Import… | preview obsahu, pořadí a kolizí; Apply mění paměťový dokument | bez změny dokumentu |
| Apply | aplikuje validovaný preview do dokumentu; není synonymum Save | nemění zdroj při stale/invalid preview |
| Reset | vrátí konkrétní nastavení/viewport; scope musí být v názvu/tooltipu | nikdy neformátuje médium ani nemaže archiv |
| Close / Primary+W | zavře aktivní dokument s ochranou neuložených a pending hex změn | původní dokument zůstává |
| Rename / F2 | stejný pravidlový editor ve výběru podporujícího seznamu | původní jméno i výběr |
| Copy/Paste / Primary+C/V | jen podporované dokumenty a bezpečný serializovaný přenos | unsupported data se nevynucují |
| Select all / Primary+A | v aktivním seznamu; v textovém vstupu pouze jeho text | žádný zásah do dokumentu |
| Delete / Delete | jen podporovaný výběr; bez bezpečného undo preview/potvrzení podle dopadu | výběr i data zůstávají |
| Undo/Redo | dostupné jen s existující bezpečnou implementací nebo samostatným schválením | žádná nová historie předstíraná v UI |

Save As nenabízí export-only WAV/LEP/L16/COM nebo ztrátovou konverzi jako změnu
aktuálního dokumentu. U repair kopie nejprve vyžaduje odlišný cíl a zachovává ochranu
originálu včetně odkazů. Po prvním Save As může další Save zapisovat bezpečně bound kopii.
Názvy příkazů ani file picker indexy nesmějí obcházet tuto politiku.
Batch výslovně zapisuje výstupy při Execute; recorder zapisuje při Record. Nejsou to
skryté varianty dokumentového Save. Význam je uveden před spuštěním workflow.

## Výběry a focus

Klik vybere položku a synchronizuje její detail. Primary+klik přepíná členství,
Shift+klik vybere rozsah od poslední kotvy podle zobrazeného pořadí. Změna focusu
výběr neodbarví do nečitelna. Šipky mění aktivní položku a scrollují ji do view.
Escape zruší dočasný výběr pouze ve vlastníkovi grafu/seznamu; v editaci nejprve ruší
editaci, v modálu ruší dialog, nikdy bez potvrzení nezastaví či nezahodí recording.
Tab/Shift+Tab přechází v pořadí menu → command bar → dokumenty → navigace → workspace
→ inspector → stavové akce. F6 přesouvá focus mezi těmito oblastmi; není textový shortcut.
Neaktivní inspector smí zachovat poslední kontext s označením, že není aktivní selection.

Dvojklik/Enter otevírá detail položky: sektor do hex detailu, záznam do jeho detailu,
audio řádek lokalizuje jeho interval. Význam je „zobraz detail / lokalizuj“, nikoli Save
nebo Apply. Kontextové menu pracuje s pravým klikem zvoleným kontextem a stejnými příkazy.

## Grafy

| Gesto | Společné pravidlo | Doménová výjimka |
| --- | --- | --- |
| Wheel nad grafem | nahoru zoom in, dolů out; faktor 1.5; anchor pod kurzorem, clamp do zdroje | nepřenáší se na scroll tabulky/formuláře |
| +/- nad focused grafem | stejný faktor, anchor střed viewportu | v textovém vstupu jde o text |
| Home / Fit all | plný rozsah; aktivní Fit zřetelně označit | live Fit může sledovat přibývající historii |
| Fit selection | přesný aktuální výběr s definovaným okrajem | bez výběru disabled s důvodem |
| Prostý levý drag | pan; klik bez překročení 3 DIP vybere | ve waveform label pásu klik lokalizuje region |
| Shift+drag | interval ve waveform/time grafu | diskový kruh má diskrétní sektory: Shift+klik, ne vymyšlený časový interval |
| Escape | ruší místní výběr / měřicí overlay | zachovává audio session a dokument |
| Změna záložky | drží viewport, channel/gain a výběr | explicitní nový zdroj dostane nový viewport |

Časová základna jsou sample/frame indexy a polouzavřené intervaly. Amplituda FS,
čas v s/µs a fyzické bit cells nejsou zaměnitelné. Tabulka uvádí textovou alternativu
ke grafu, přesné intervaly a hodnoty. Tooltip je odsazený od kurzoru a nesmí překrýt
měřený bod; pin/detail umožní čtení klávesnicí. Hidden guide ovlivní kreslení, ne detector.
Manual zoom/pan vypne Fit/follow stav a zachová délku při clampu k nule. Opětovný Fit je
explicitní. Channel, display gain, guide visibility ani IC/NORMAL alias se shodnými
vodítky nesmějí resetovat wave viewport nebo zbytečně opakovat klasifikaci.

## Recorder

Konstrukce view, obnovení session UI ani enumerace nesmějí spustit zařízení.
Monitor a Record jsou oddělené explicitní akce; Monitor netvoří archiv. Stop/finalize
požádá o stop, odvodní archiv a vyčká výsledek; Cancel/discard je jiná, potvrzovaná akce.
V době start/stop probíhá jasně označený stav, duplicity příkazů jsou blokované.
Requested/actual formát, Shared/Exclusive, RAW a převodní fallback musí být odlišeny;
neznámé processing provenance zůstávají neznámé. Nepovolovat tichý formátový fallback.

Archive loss a preview loss jsou samostatné hodnoty. Dropped preview není ztráta
archivu. Archival loss/disconnect označí incomplete a zachová dostupný WAV/report.
Bez ohledu na UI zátěž se kreslení/FFT/zápis/logování nestěhují do capture callbacku.
Výchozí refresh 75 ms, nejvýš jeden in-flight; bounded snapshots/caches se uvolní při close.

Zavření vloženého aktivního recorderu nabízí Keep recording / Stop and finalize / Cancel.
Keep recording jej zachová v samostatném Avalonia okně se stejnými komponentami.
Nezávislý recorder přežije zavření browseru. Quit celé aplikace koordinuje všechny
sessions a neoznačí neúspěšnou finalizaci jako úspěch. Discard potvrzuje ztrátu konkrétního
aktuálního záznamu a nesmí odstranit dříve dokončené recordings.

Head Alignment ukazuje kvalifikaci/odmítnutí, interval a metodu. A/B vyžaduje shodný
průchod, zařízení, gain/EQ/input chain a srovnatelnou konfiguraci; chybějící potvrzení
srovnání zablokuje. Relativní změna hran neznamená absolutní mechanický úhel ani obecné
skóre kvality. Manual zero se zneplatní při relevantní změně podmínek.
