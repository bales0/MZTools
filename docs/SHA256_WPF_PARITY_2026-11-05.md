# SHA-256 a WPF parita — 5. listopadu 2026

Výchozí HEAD: d3dc601292101e4dd82bcc2022fa981301c75837. Uživatel po auditu G0 výslovně schválil etapy A i B. WPF zdroje se nemění; nic nebylo commitováno, pushováno ani publikováno.

## Rozsah implementace

- Audio loader zachovává SHA-256 před i po analýze celé zdrojové sady. Koordinátor používá tento ověřený otisk, místo aby nad týmž WAV/FLAC dělal další dva úplné průchody. Výjimka platí pouze pro konkrétní MediaDocumentLoader a audio vstupy; jiné dokumenty mají původní kontroly.
- Hash má přerušitelnou streamovanou variantu s 64KiB zapůjčeným bufferem. Nevytváří kopii média v RAM. Audio otevření a staging/deeper analysis předávají CancellationToken.
- Interaktivní viewport, stereo preview a range Spectrum/Spectrogram/Trigger kontrolují snapshot velikosti, mtime a creation time všech zdrojů včetně segmentů. Toto je metadata kontrola, nikoli kryptografická integrita ani stabilní file ID.
- Globální cache SHA segmentů podle metadat byla odstraněna. Přísné otevření segmentovaného readeru vždy ověřuje skutečné bajty proti manifestu. Explicitní metadata-only preview stále validuje schema, pořadí, délky, formát a kontinuitu segmentů. Model bez snapshotu se při VerifySource vrací na přísnou cestu.
- Exportní SHA před stagingem, po něm i před publikací zůstávají. Zůstává kontrola staging WAV, otisk/kopie/ověření transakčního výstupu, kontrola cíle, zákaz přepsání otevřených zdrojů a rollback. AtomicDocumentStore se nemění.
- Nový disk: dynamické profily 40/80 stop, uzamčené pevné geometrie, nominální kapacita a označení nestandardní geometrie. Raw sektorová ID používají existující Core výpočet. Historické boot tracky mohou mít jiné uložené délky než nominální kapacita; jejich algoritmy se nemění.
- Selected audio uchovává kompatibilní diagnostické overlaye při PCM refreshi. Nálezy patří k dokončenému transformu a intervalu; při nesouladu se nepromítají do aktuálního PCM. Historické histogramy zůstávají označeny OUTDATED. Reference doplňují software/verzi, polaritu, odvození, omezení a časy. Používá se původní katalog a existující scatter/drift grafy.
- HEX Browser zachovává všechny bajty, rozlišuje MZF header/data/trailing a nabízí SHASCII (EU). QDF hlavička je výslovně odvozená společným frame encoderem; prefix/CRC mají původní barvy. Není prezentována jako původní fyzická nahrávka.
- DSK HEX je nejprve zamčený; přepis a vložení bajtů používají původní FixedHexInput. Native TextBox zůstává read-only, takže Delete, Cut, drop a IME nemohou měnit délku bufferu. ASCII/SHASCII jsou read-only interpretace. Citlivé oblasti mají potvrzení odemknutí; Apply používá stávající Core preview a potvrzení dopadu.
- F2 zahajuje přímé přejmenování v buňce, Enter připraví a potvrdí Core změnu, Esc nic nemění. Neplatný nebo odmítnutý návrh obnoví původní session metadata. Enter mimo editaci zachovává inspektor.
- Batch má dynamickou viditelnost parametrů podle operace a způsobu výběru vstupů. Stávající preflight/replace/backup/compression/report/Cancel služby se nepřepisují. Deaktivovaný import sektorů má vysvětlení dostupné i na zakázané položce. Vybrané dialogy se vejdou do pracovní plochy při aktuálním DPI.

## Bezpečnostní garance a přiznané meze

| Scénář | Metadata preview | Přísné otevření / export |
|---|---|---|
| Změněná délka nebo mtime | Odmítne změnu snapshotu. | Odmítne také neshodný SHA. |
| Stejná délka, obnovené mtime, in-place změna bajtů | Nedetekovatelná, pokud zůstane i creation time. Regresní test tuto mez výslovně zachovává. | Trvale změněné bajty odmítne SHA; strict segment reader odmítne manifestový hash. |
| Obvyklé nahrazení souboru | Creation time přidává kontrolu; není to přenosná záruka identity inode/file ID. | Neshodné bajty odmítne SHA. |
| Změna zdroje po stagingu | N/A | beforeCommit odmítne publikaci; původní cíl zůstane zachovaný. |
| Chybějící zdroj/segment, chybný nebo neúplný manifest | Chyba přístupu nebo strukturální validace; nevytváří se náhradní PCM. | Odmítnutí. Fyzické odpojení média není simulací souborové chyby plně ověřeno. |
| Cancel | Starší preview se ruší; nepublikuje se jeho zastaralý výsledek. | Přerušitelný hash a staging. Commit/rollback zachovává původní politiku dokončení transakce. |
| Dočasný zápis a obnovení původních bajtů mezi kontrolami | Není garantována detekce. | Samotné pre/post SHA nedokazuje neměnnost všech mezilehlých čtení. Windows FileShare.Read chrání otevřené WAV handly; stejná záruka nebyla prokázána pro Linux, FLAC ani síťový disk. |

Jednoprůchodový hash při dekódování nebyl zaveden: WAV reader může seekovat a dekódování FLAC neprodukuje původní komprimované bajty. Bez důkazu ekvivalentní integrity zůstávají dvě audio-loader kontroly i všechny tři exportní kontroly. U segmentovaných vstupů se může přísné otevření oproti dřívější metadata cache prodloužit; jde o opravu nesprávné důvěry v cache.

## Měření

Metodika a skutečná čísla jsou v doprovodném validačním JSON a benchmarkových souborech. Benchmark je opt-in AudioIntegrityBenchmarkTests.ThirtyMinuteMediaOperations. Generuje pouze vlastní dočasná data: 30 minut při 48 kHz, 16/24 bit, mono/stereo WAV a stereo FLAC. U 24bit fixture je dolních osm bitů nulových; nejde o model libovolné reálné 24bit nahrávky. Signál obsahuje periodickou složku a deterministický šum; FLAC má přibližně 250 MB, nejde jen o snadno komprimovatelné ticho.

Každé otevření následuje po vytvoření/zakódování a samostatném SHA: výsledky jsou warm cache. GetProcessIoCounters měří logické procesové čtení, nikoli fyzické čtení SSD. CPU je rozdíl TotalProcessorTime. RAM je procesový celoživotní peak working set, nikoli izolované maximum jedné operace. Pro každou operaci je jeden vzorek; rozdíly časů nelze vydávat za statisticky prokázané zrychlení.

Měří se otevření, samostatný SHA, 20 navigací, R ±1/±10/invert/mix/reset, histogram 10/60/1800 s a WAV staging 10/1800 s. Doplňkové finální měření zahrnuje překrývající se preview, deeper analysis s jedním detektorem a normální polaritou a kompletní transakční WAV export. Plný WAV export stejné generované 16bit stereo nahrávky musí být bajtově totožný se vstupem.

Diagnostický meziběh po prvních úpravách není finálním benchmarkem; během jeho běhu probíhala další validace. Do konečného srovnání patří výchozí baseline a samostatný finální běh nad posledním buildem.

## Omezení ověření

- Windows x64: skutečné výsledky buildů, testů a nativních workflow jsou zaznamenány ve validačním JSON.
- Linux: spuštění WSL v této relaci selhalo; bez vykonaného runtime není PASS.
- macOS a ARM: NETESTOVÁNO.
- Cold cache, pomalejší médium, síťový disk a fyzické odpojení: NETESTOVÁNO. Není doloženo fyzické I/O konkrétního typu disku.
- Core referenční QD theory byla přeskočena: chybí tři dokumentované plné QD obrazy v specification.
- Fyzický audio vstup a 10–30min nahrávání: NETESTOVÁNO. Nativní recorder scénář používá testovací backend.
- Úplná parita všech specializovaných dialogů, klávesových cest a všech DPI není prokázána. Přehled rozdílů je ve WPF_PARITY_MATRIX_2026-11-05.md. Výsledek není označen jako 100% WPF parity.

## Výsledky 30min měření na Windows x64

Časy jsou jednotlivá warm-cache pozorování, nikoli prokázané zrychlení. Logické čtení zahrnuje všechny průchody analýzy a drobná pomocná čtení.

| Vstup | Otevření před / po (s) | Logické čtení před / po (GB) | Pokles čtení | 20 preview po (ms celkem) |
|---|---:|---:|---:|---:|
| thirty-16-1.wav | 39.49 / 40.07 | 1.555 / 1.210 | 22.22 % | 13.12 |
| thirty-16-2.wav | 67.90 / 68.35 | 3.111 / 2.419 | 22.22 % | 17.50 |
| thirty-16-2.flac | 61.78 / 61.83 | 2.261 / 1.759 | 22.22 % | 15.91 |
| thirty-24-1.wav | 41.35 / 40.30 | 2.333 / 1.815 | 22.22 % | 9.28 |
| thirty-24-2.wav | 73.59 / 72.24 | 4.666 / 3.629 | 22.22 % | 13.41 |
| thirty-24-2.flac | 62.59 / 61.40 | 2.277 / 1.771 | 22.22 % | 18.26 |

Odstranění dvou duplicitních SHA průchodů snižuje logické I/O přibližně o 22 %. Výpočty analýzy dominují celkovému času; tato měření nedokládají plošné zrychlení otevření. Malá preview dohromady čtou přibližně 1–3 MB proti stomegabajtovým zdrojům. Exportní ověření se neodstraňovala.

Kompletní CPU, procesový peak RAM, histogramy, transformace, staging, deeper analysis a publikace jsou v [benchmarkovém JSON](SHA256_BENCHMARK_2026-11-05.json). Výchozí test runner skončil kódem 0; finální měření zapsalo všech šest sad po kontrolách zdrojů a PCM. Návratový kód finálního runneru nebylo možné samostatným watcherem získat, proto není domyšlen jako PASS. Samostatné desktopové regrese mají vlastní TRX a návratový kód.

## Závěrečná validace

- Desktop: 310 prošlo, 0 chyb; finální binárky, opt-in dlouhý benchmark je samostatný běh.
- Core: úplná sada 411 prošlo, 1 přeskočená referenční QD theory; po posledním předání tokenů znovu 83 dotčených audio/portability kontrol, všechny prošly.
- Windows native Avalonia: 96 kontrol bez hlášené chyby; picker, fyzický vstup a všechny škály DPI nejsou tímto výsledkem prokázány.
- Zachované WPF: původní smoke aplikace skutečně spuštěna, exit 0.
- Nové dialogy, HEX/QDF a F2 byly také vizuálně prohlédnuty z vykreslených PNG.
- git diff --check prošel. Původní WPF, Capture, pulzní klasifikátory, formáty a CP/M DPB se nepřepisovaly.

Přesné soubory, jejich SHA-256, protokoly, platformní omezení a rozdíl mezi úplnou a cílenou Core sadou jsou v [záznamu validace](SHA256_WPF_VALIDATION_2026-11-05.json). Žádný commit, push, PR ani distribuční ZIP nebyl vytvořen.
