# Aktuální build aplikace

Hotová aplikace má vždy stejnou cestu v kořenovém `bin`:

| Platforma | Spuštění |
|---|---|
| Windows x64 | `bin/win-x64/MZTools.Desktop.exe` |
| Linux x64 | `bin/linux-x64/MZTools.Desktop` |

Na jiný počítač zkopírujte **celý odpovídající adresář**, nikoli jen EXE. Obsahuje aplikaci, .NET runtime, nativní knihovny a povinné licence. Neobsahuje zdrojové soubory, testy, snímky, audity, PDB ani mezivýstupy. Na cílovém počítači není potřeba SDK, samostatně instalovaný .NET ani Python. Windows balík obsahuje také app-local Visual C++ runtime potřebný pro zvukový kodek; do systému se nic neinstaluje.

Linuxový spouštěč `MZTools.Desktop` nastaví cestu k přibaleným audio/FLAC knihovnám a předá programu všechny argumenty. Nespouštějte jeho interní `MZTools.Desktop.bin` přímo. Linux musí mít odpovídající architekturu, běžné desktopové/grafické knihovny, ovladače a dostatečně novou glibc; přesný minimální požadavek aktuálních přibalených knihoven je v `LINUX-RUNTIME.txt`. Není to univerzální balík pro všechny historické linuxové distribuce.

## Sestavení

Ve VS Code je výchozí **Build MZTools (Release)** a spuštění **Run MZTools** přesměrováno na Avalonia aplikaci. Úloha **Build MZTools (Windows + Linux x64)** připraví obě varianty.

Z kořene repozitáře:

```text
python tools/build_desktop.py
python tools/build_desktop.py --rid win-x64 --rid linux-x64
```

Na Linuxu použijte `python3`. Staví se Release; volbou `--configuration Debug` se stejný aktuální výstup nahradí Debug verzí. Přímý `dotnet build src/MZTools.Desktop/MZTools.Desktop.csproj -c Release` rovněž automaticky aktualizuje `bin/<RID>`. Opakované sestavení beze změn ověří kontrolní součty a znovu nepřekládá celý runtime balík. `--force` vynutí nový publish.

Build stroj potřebuje .NET 10 SDK a Python 3. Linuxové audio knihovny se přibalují z odpovídajícího linuxového build stroje (`ldconfig`, `ldd`, `readelf`, balíky libsndfile/PortAudio a jejich původní licence). Při přípravě Linux x64 na Windows se použije WSL Debian. Pro jinou linuxovou architekturu sestavte balík na odpovídajícím linuxovém stroji. Jiné RIDs zůstávají dostupné přes `--rid`; Windows/Linux x64 jsou místně ověřované varianty.

## Oddělení výstupů

- `bin/<RID>`: poslední kompletní spustitelná aplikace, bez datových podadresářů podle data/configurace/TFM.
- `obj/artifacts/<host>`: běžné sestavení projektů a testů; jména projektů a konfigurace jsou oddělené.
- `obj/desktop/<RID>`: publish staging, kontrolní součty/SDK/configurace v `latest.json` a ochrana souběžného sestavování.
- `TestResults`: pouze výsledky a důkazy testů; zde se běžný aktuální build nehledá.

Do `bin/<RID>` neukládejte vlastní média ani pracovní soubory. Úspěšné sestavení nahradí celý předchozí spravovaný výstup, takže nezůstávají staré knihovny. Při chybě sestavení/náhrady se zachová předchozí funkční verze. Neznámé soubory se automaticky nemažou. Před náhradou je vhodné běžící MZTools zavřít. Metadata `obj/desktop` ponechte vedle spravovaných výstupů.

Při automatizovaných testech lze opakovaný publish z referencovaného projektu vynechat parametrem `-p:MZToolsPublishLatest=false`. Testovací a WPF referenční výstupy se do distribučního adresáře nekopírují.

## Volitelný archiv

```text
python tools/package_desktop.py --rid win-x64
python tools/package_desktop.py --rid win-x64 --output obj/packages
```

Bez `--output` se pouze aktualizuje stejný `bin/<RID>`. S `--output` vznikne navíc runtime-only ZIP/tar.gz s kontrolními součty; již existující vydaný archiv se nepřepisuje. Staging ani sestavovací adresáře nejsou uvnitř balíku. Licence v `third-party` jsou součást distribuce, nikoli testovací dokumentace.

## Ověření změny (2026-11-09)

- Běžný `dotnet build` (Release): úspěch, 0 upozornění a chyb; automaticky aktualizoval `bin/win-x64`.
- Opakovaný build přes Windows i WSL používá stejné `bin/<RID>` a ověřuje kontrolní součty.
- `tests/test_build_desktop.py`: 6/6; včetně rollbacku a ochrany vlastních souborů.
- Desktop testy: Windows 362 úspěšných + 1 vynechaný benchmark, Linux 370 úspěšných + 1 vynechaný benchmark. Windows ověřeno přímo xUnit runnerem, protože VSTest v této relaci končil timeoutem spojení před zahájením testů.
- Oba produkty byly zkopírovány mimo repozitář do dočasné cesty s mezerami a spuštěny s `--validate-runtime` bez SDK cest. Linux ověřil načtení přibaleného libsndfile; Windows obsahuje potřebný `vcruntime140.dll`.
- Aktuální Linux nativní knihovny vyžadují glibc alespoň 2.38.
- Windows ZIP ověřen bez testů, auditů, sestavovacích adresářů a PDB.

Výsledky jsou v `TestResults/BuildOutput-2026-11-09` a `obj/desktop/verification`. Tato kontrola přenositelnosti ověřuje runtime a FLAC kodek; fyzická audio zařízení ani všechny obrazovky aplikace znovu netestuje.
