# Opravené Extended DSK obrazy — 2026-10-06

Kopie vznikly z místních souborů `MZTool.Tests/DSK`, nikoli z nedostupného disku `E:`. Původní soubory nebyly změněny; jejich SHA-256 byl před a po vytvoření kopií ověřen.

Oprava doplňuje pouze uloženou délku sektoru na offsetech +6/+7 jeho osmibajtového descriptoru. Všechna ostatní data, včetně IPL, systémových souborů, adresáře, pořadí sektorů a výplní, jsou bajtově shodná. U čtyř IPL obrazů bylo ověřeno 1280 sektorů a změněno 1280 bajtů; u CPM80 648 sektorů a 648 bajtů. Všechny délky nyní odpovídají skutečné délce payloadu.

Extended DSK obsahuje explicitní uloženou délku sektoru. MZTools dříve při čtení používal náhradní délku podle N, takže mohl obsah zobrazit i z vadného obrazu.

| Kopie | SHA-256 |
| --- | --- |
| HLIPA_FF_fixed.dsk | 311C868E4D0BA89D7C4AD3821649FA5A559DB9B9CD7F085E455267ACAA289F79 |
| compress_HLIPA_FF_fixed.dsk | 6EFDAD135E03586EC63030B73D55B091A241B9741370A71E71C34892C20DCF9D |
| multi_FF_fixed.dsk | AD6AF2F1C808BECBC17119C985BC298F5CF0B7DD18E9AB5E0840D452D156EE23 |
| multi2_FF_fixed.dsk | A810079DFD4A5553714E0D2161E91A2186AEC66F7F083F53F5B64A7F70C08201 |
| CPM80_FF_fixed.dsk | BA123760D087025F75C38FC6C873F32A9E776B2A10F798557184D5290F03F296 |

V Compare Disk otevři původní a opravenou kopii. Karta **Physical sectors** nyní automaticky vybere změnu **Stored descriptor length (+6/+7)** a ukáže hodnoty vlevo/vpravo. **Hex diff → Descriptor / raw directory bytes** ukazuje konkrétní změněné bajty. Analyzer původního souboru hlásí **DSK_EXTENDED_ZERO_LENGTH**; opravená kopie tuto diagnostiku nemá. Běžné otevření/uložení starého obrazu jeho descriptory automaticky neopravuje.

Statické kontroly nepotvrzují boot na hardwaru. Tyto kopie je ještě nutné vyzkoušet na skutečném MZ + SFD800, zejména CPM80 s přemístěnou alokací systémového souboru.
