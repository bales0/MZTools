# Multiplatformní migrace · dokončení implementace 30. 10. 2026

Avalonia implementuje Tape/QuickDisk, DSK/HFE, offline Audio, samostatný
Recorder/Head Alignment a Batch ve společném F2 design systému. Pokyn
„dokonči to už celé“ schválil zbývající implementaci bez dalších etapových
souhlasů. Neproběhl commit ani publikace.

## Dokončená implementace

- Recorder používá původní AudioRecorderViewModel, bounded Capture pipeline
  a kopírované snapshoty. Výběr zařízení/start jsou explicitní. Obsahuje monitor,
  record, stop/cancel/discard, historii, integritu a oddělené archive/preview loss.
- Waveform/spectrum sdílí renderer s offline Audio. Live pulses používají původní
  katalog i uživatelskou referenci. Head alignment má kvalifikované metriky,
  stereo indikátor, per-channel manuální nulu, damping a A/B s potvrzením stejné
  pasáže. Neodhaduje mechanický úhel hlavy.
- Windows používá WASAPI Shared/Exclusive; Unix nový PortAudio v19 adaptér.
  Callback pouze předává PCM původní pipeline. Změna rate není tichý fallback.
  Neznámé zpracování hostem je označeno jako neznámé.
- Posouzené F8 varianty: přímý ALSA/CoreAudio znamená dva nové backendy;
  SDL menší kontrolu nad capture provenance. PortAudio poskytuje společné C ABI
  a explicitní PCM pod MIT licencí. Latence host API není garantovaným výsledkem.
- FLAC adaptér používá libsndfile, původní sdílený STREAMINFO parser, přesné
  integer PCM, seek a Unicode. Zápis používá vlastní temporary a create-only
  dokončení. WAV čte původní managed parser.
- Batch poskytuje Core preflight/execute/report, výslovná potvrzení a ochranu
  změněných/otevřených zdrojů. Hashování audio zdrojů je streamované.
- OS integrace: native pickery, clipboard, Open z argumentů, reveal, nezávislá
  okna, macOS souborová identita a atomická nastavení bez výběru/startu zařízení.
- CI má šest runner/RID kombinací. tools/package_desktop.py připravuje
  self-contained ZIP/TAR.GZ, macOS .app, licence a SHA-256 manifest.

## Skutečně spuštěné úplné testy

| Sada | Windows x64 | Debian x64 |
| --- | ---: | ---: |
| Core | 411 PASS, 1 SKIP | 411 PASS, 1 SKIP |
| Původní WPF regrese | 1 202 PASS, 3 SKIP | Windows-only |
| Capture | 224 PASS | 224 PASS |
| WASAPI adaptéry | 52 PASS | Windows-only |
| Avalonia unit + Skia headless | 242 PASS | 250 PASS |

Celkem **3 016 PASS, 0 FAIL, 5 SKIP** v osmi úplných sadách. Linux navíc
obsahuje osm testů skutečné PortAudio P/Invoke hranice proti nezávislé C knihovně:
callback flags, null/empty packet, spontánní konec, chyby Open/Start/rate a
quiescent Stop/Dispose. Nejde o fyzické zařízení. FLAC integer roundtrip
8/16/24 bit mono/stereo, seek, Unicode a cancellation jsou součástí obou sad.

Na Windows i Debian/WSLg prošlo po **76 kontrolách skutečných native oken**:
shell, Tape/QuickDisk, DSK/HFE, offline Audio a Recorder. Native pickery byly
lokálně zapnuté; CI je explicitně označuje UNTESTED, pokud je vypne.
[Finální galerie](ui-design-system/avalonia-completion/index.html) obsahuje
nezměněné Skia rendery skutečných oken, nikoliv mockupy.

Prošly také všechny čtyři původní WPF smoke programy: Audio, Reports,
DSK/QuickDisk visual a Recorder (114 kontrol). Diskový smoke nyní používá
vlastní adresář pro každý běh; úplný průběh včetně HFE/IPL a Save As je zachycen
ve finálním logu. Oprava se týká izolace testovacích fixtures.

Pětiminutový WAV: 14 400 000 stereo frame, 57 600 044 B. Poslední souběžný běh:
Windows analýza 21 106 ms, Linux 29 568 ms; overview pan 0–1 ms, FFT cancel
včetně prvních oken 55/41 ms. Source handles byly uvolněny. Paměťové údaje
zahrnují test host s renderovacími cache; nejde o garantovaný limit produktu.
Přesné výsledky a zdrojové hashe: [strojový report](COMPLETION_VALIDATION_2026-10-30.json),
TRX/native JSON v TestResults/Completion.

Pět SKIP vyžaduje chybějící konkrétní QD/CPM reference, nikoliv ignorované zdroje.
Prohledáno i 39 ZIP archivů: nalezen pouze jeden z osmi požadovaných souborů.
Experimentální obrazy nebyly vydávány za chybějící reference.

## Distribuce a hranice akceptace

Vývojové archivy: TestResults/Completion/distribution pro win-x64, win-arm64,
linux-x64, linux-arm64, osx-x64 a osx-arm64. Překlad RID není důkaz spuštění.
Balicí kontrola ověřuje obsahové hashe a apphost architekturu; skutečné spuštění
je dostupné pouze pro Windows/Linux x64. Unix vyžaduje systémové knihovny:
Debian libportaudio2 libsndfile1; macOS brew install portaudio libsndfile.

NuGet audit neohlásil známé zranitelnosti spravovaných závislostí. Native audit
má samostatná omezení, zdroje a původní LGPL/MIT/BSD texty:
[native knihovny](third-party/avalonia-f9/NATIVE_LIBRARIES.md).
Tím se netvrdí, že native knihovny nemají bezpečnostní chyby.

**Technicky připravené, HW neověřené.** macOS/ARM, skutečné audio vstupy,
10–30min fyzický záznam, všechny reálné DPI kombinace, OS drag/drop a screen
reader nebyly lokálně ověřeny. Vzdálené CI nebylo spuštěno. Balíčky nejsou
podepsané ani notarizované; nejsou označené za certifikovaný release.

WPF zůstává funkční a regresně ověřované. Zadání vyžaduje prokázanou úplnou
paritu před odstraněním; neověřené platformy a hardware tuto podmínku nesplňují.
[Release checklist](RELEASE_CHECKLIST.md) odděluje implementaci od zbývajícího
ověření na skutečných cílových systémech.
