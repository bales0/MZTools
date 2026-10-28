# Záznam kazety a živá analýza

Otevřete **Tools → Record Audio / Live Tape Analysis…**. Okno umožňuje sledovat vstup,
uložit WAV a po zastavení jej předat stávající analýze. Podpora skutečného vstupu je
zatím pro Windows; přenosné výpočty jsou oddělené v knihovně `MZTools.Capture`.

Tento návod popisuje implementaci etap B–G. Automatické a syntetické kontroly recorderu
prošly. **Nahrávání z fyzického zařízení a seřizování skutečné hlavy dosud nebyly
ověřeny na hardwaru.** Po samostatně schváleném sladění dvou starších testů prošlo
1 335 testů bez selhání; tři kontroly se přeskočily kvůli chybějícím referenčním obrazům.
Podrobnosti, příkazy a omezení volitelných kontrol jsou v [implementačním auditu](LIVE_TAPE_RECORDER_IMPLEMENTATION.md#approved-follow-up--legacy-test-alignment-2026-10-28).

## První vyzkoušení bez zvukové karty

1. V **Input / recording settings** vyberte **Synthetic demo — no hardware**.
2. Vyberte zařízení **Synthetic audio input** a například **48 000 Hz / 24 bit / 2 ch**.
3. Klikněte na **Monitor only**. Uvidíte syntetický tón; žádný WAV se nevytváří.
4. Pro vyzkoušení zápisu zvolte nový název s příponou `.wav` a klikněte na **Record**.
5. Klikněte na **Stop / finalize** a počkejte na dokončení kontroly souboru.
6. V kartě **Recordings** vyberte uložený segment a otevřete jej v další analýze.

Všechny obrázky níže pocházejí ze skutečně spuštěného automatického WPF testu se
syntetickým vstupem 48 kHz / PCM24 / stereo. Jsou to snímky klientské plochy okna,
bez titulkového pruhu; neprokazují funkci žádného fyzického vstupu. Cesty a časy na
snímcích jsou dočasná testovací data.

![Volba syntetického vstupu a výstupního WAV](images/live-recorder/inputs.png)

## Výběr skutečného vstupu a formátu

Ve **Source** vyberte **Windows input (WASAPI shared)** a následně zařízení ve
**Device**. **Refresh inputs** obnoví nabídku. Otevření okna ani vyhledání zařízení
nespouští záznam; vstup se spustí až tlačítkem Monitor nebo Record. Podporován je
mono nebo stereo vstup.

Výchozí nabídka používá nativní mix formát vstupu. V rozbalovacím panelu **Actual
input / processing** po spuštění zkontrolujte skutečně dodaný formát a informace o
zpracování. Počet dodaných platných bitů není záruka fyzického rozlišení převodníku.

| Volba | Význam |
| --- | --- |
| **Request RAW** | Požádá Windows o RAW vstup. Nepotvrzuje transparentnost hardwaru ani veškerého driverového zpracování. Když jej zařízení nepodporuje, chybu uvidíte; zkoušku bez RAW musíte zvolit výslovně. |
| **Allow Windows format conversion** | Pokročilá výslovná volba pro formátový převod ve Windows enginu. Pro výchozí zachování dodaného mix formátu ji nechte vypnutou. |
| **Allow float32 / PCM32 → PCM24** | Povolí nutný převod takového vstupu do PCM24 pro stávající reader/analyzátory. Při převodu může dojít ke kvantizaci či saturaci; výsledek a počty jsou v reportu. |
| **WAV destination** | Nová cesta pro výstup. Existující WAV ani jeho sidecar se nepřepisuje; zvolte jiný název. |
| **Tape note / Side** | Volitelné poznámky do JSON reportu. |

WAV zachovává dodaný sample rate a kanály. PCM8/16/24 se zapisuje beze změny dodaných
bajtů. Float32/PCM32 vyžaduje výslovné povolení PCM24; aplikace při tom neprovádí
automatický resampling. Záznam pro existující analyzátory podporuje 22 050, 44 100,
48 000, 88 200 a 96 000 Hz. Nepodporovaný výstupní sample rate se odmítne.

Exclusive režim se nenabízí: použitý transport NAudio 3.3.0 pro něj není spolehlivý.
Aktuální implementace používá shared a volitelně RAW; důvody jsou v auditu. Nespouští
odposlech přes reproduktory ani nemění hlasitost zařízení.

## Monitor, záznam a bezpečné zastavení

**Monitor only** zpracovává živý náhled bez výstupního souboru. **Record** vytváří
archivní WAV. Přechod Monitor → Record nejprve ukončí monitor a otevře nový vstupní
běh; nejde o bezmezerový záznam s předchozí historií.

Při **Stop / finalize** vyčkejte na uzavření vstupu, dopsání přijatých bufferů,
opravu RIFF hlavičky, kontrolu uložených dat a dokončení sidecaru. Zavření aktivního
okna nabídne bezpečné zastavení; odmítnutí ponechá capture spuštěný.

Před limitem RIFF se výstup automaticky rozdělí na číslované segmenty. JSON report
obsahuje skutečný formát, převod, časové údaje, počty rámců, diagnostiku, cesty segmentů
a SHA-256. Dočasné nebo částečné soubory při chybě zůstávají zachované. Přípona či
existence souboru sama nepotvrzuje úplnost záznamu.

V horním stavu rozlišujte **preview dropped**, **missing** a **unknown loss**. Výpadek
náhledu může zanechat archivní data v pořádku, ale přeruší kontinuitu živých měření.
Výpadek vstupních dat nebo přetečení archivní fronty je důvod ukončení s neúplnou
integritou; aplikace nedoplňuje chybějící audio tichem.

## Live signal: waveform, úrovně a FFT

![Živý waveform, digitální úrovně a spektrum syntetického tónu](images/live-recorder/monitor.png)

- **View** přepíná Left/Mono, Right nebo Overlay L/R. **Gain** a **Auto scale** ovlivňují
  pouze kresbu. Archivní záznam se tím nenormalizuje.
- Kolečko mění časový zoom, tažení posouvá pohled v omezené historii. **Follow live**
  sleduje nejnovější data; **Fit history** zobrazí dostupnou historii. Změna kanálu
  či gainu časový zoom neruší. Historie náhledu není celá dosavadní nahrávka.
- **Trigger**, jeho úroveň v FS a Rising/Falling stabilizují zobrazení kolem nalezené
  hrany. Bez vhodné hrany běží pohled volně. Nejde o dekódování kazetového bloku.
- Peak a RMS jsou digitální úrovně v FS/dBFS; DC je průměr v FS. RMS zahrnuje DC.
  Nejde o vstupní napětí line-in ani o kalibrované analogové měřidlo.
- **Clip ≥ % FS** a **Apply / reset clip hold** mění diagnostický práh a vynulují hold.
  Hold počítá analyzované vzorky od resetu; při ztrátě náhledu nepokrývá celý archiv.
- FFT používá Hannovo okno a zvolenou délku. Zobrazuje špičkovou amplitudu dBFS,
  nikoliv PSD nebo automatický odhad šumu. Údaj dominantní frekvence sám neurčuje
  rušení ani správné nastavení hlavy.

U waveformu, úrovní a FFT jsou uvedené jejich intervaly vzorků. Nemusí mít stejnou
délku: úrovně běžně používají posledních 250 ms, FFT posledních N vzorků. Náhled se
obnovuje nejvýše přibližně 13× za sekundu podle výkonu; jeho kadence neurčuje sample
rate ukládaného WAV.

## Head alignment: relativní pomoc při seřízení hlavy

![Ukazatele a podmíněná fáze známého stereo tónu](images/live-recorder/head-indicators.png)

Karta **Head alignment** ukazuje jednotlivé ukazatele, ne mechanický úhel. Volby
**Band low / split / high** určují porovnávaná frekvenční pásma; **Window N** měřený
interval. V mono lze sledovat podíl vysokofrekvenční energie, AC RMS, normalizovanou
strmost a doby přechodů. Závisí také na obsahu pásku, úrovni, EQ a elektronice.

U stereo signálu se zpoždění zobrazí jen při dostatečné korelaci, koherenci, jedinečném
vrcholu a stabilitě mezi částmi okna. Kladné zpoždění znamená, že Right zaostává za
Left. Pro **Known stereo tone** zadejte frekvenci skutečně použitého testovacího tónu;
fáze se zobrazí až po jeho kvalifikaci. Periodický tón má fázi modulo jednu periodu,
proto se z něj neuvádí jednoznačné absolutní zpoždění.

**Not assessable** znamená, že podmínky měření nejsou splněny: například ticho,
clipping, rozdílný obsah kanálů, nestabilita, chybná frekvence nebo krátké okno. Ani
kvalifikovaná fáze neprokazuje správný mechanický azimut vůči původnímu záznamu.

### Porovnání A/B

![Porovnání dvou výslovně uložených měření stejné referenční pasáže](images/live-recorder/head-comparison.png)

1. Použijte opakovatelnou referenční pasáž. Nastavte stejné zařízení, gain, EQ a vstupní
   zpracování; zvolte stejná pásma, frekvenci a délku okna.
2. V **Compare A/B** vyplňte **Reference passage ID** a potvrďte opakování stejné pasáže
   s nezměněným řetězcem. Potvrzení není automatická kontrola identity obsahu.
3. Při přehrání referenčního úseku použijte **Save A**. Po změně nastavení hlavy znovu
   přehrajte stejný úsek a uložte **Save B**.
4. Porovnejte jednotlivé hodnoty a jejich rozdíl B−A. Změna formátu, nastavení či
   identifikátoru pasáže porovnání zneplatní. Nevzniká souhrnná známka kvality.

A/B uchovává dvě měření pouze v paměti do nahrazení, **Clear A/B** nebo zavření okna.
Nekopíruje audio a neupravuje WAV. Vyšší HF/strmost či kratší přechody mohou ukazovat
na zachování pásma jen pro skutečně srovnatelnou pasáž.

## Live pulses: měření a porovnání referencí

![Histogram samostatného adaptivního detektoru s uživatelskou referencí](images/live-recorder/live-pulses-small.png)

V kartě **Live pulses** zaškrtněte **Enable live pulse measurements**. Zero crossing,
Hysteresis a Adaptive peak jsou nezávislé detektory; jejich zaškrtávátka určují běžící
měření. **Measured detector** a **Channel** vybírají jeden pohled. Po přepnutí jiné
karty již povolené měření pokračuje, dokud jej výslovně nevypnete.

V **Thresholds / polarity / time window** nastavíte škálu dynamického prahu,
adaptivní pásmo kolem nuly, polaritu, délku historie a parametry porovnání. Referenční
časová škála a tolerance mění porovnání délek, nikoliv waveform nebo sample rate.
**Signal preprocessing / interpretation** vysvětluje filtry a měřicí metodu.

**Pulse references / user values** používá společný katalog ROM/kopírek, jeho původ a
kontextové délky. Lze zadat čtyři vlastní délky v µs; ty jsou označené jako uživatelské,
platí v tomto okně a nejsou historicky ověřenými hodnotami.

Histogram odděluje modré SHORT High, červené SHORT Low, zelené LONG High, oranžové
LONG Low a šedé neklasifikované pulzy. Barevné značky u spodní osy jsou reference.
**Fit all** zobrazí celé dostupné rozmezí, **Fit references** zvolené reference. Kolečko
zoomuje u kurzoru, tažení posouvá. Tlačítka zoomu drží dominantní viditelný shluk;
statistiky nadále obsahují i pulzy mimo aktuální výřez.

Karty **Statistics**, **Timing windows**, **Detector streams** a **Recent measured
pulses** zobrazují podrobnosti. Sledujte počet změřených, referenčních a neklasifikovaných
délek, resetů kontinuity a omezení historie. Nezapočítávají se dohromady různé detektory
jako další fyzické pulzy. Výpadek náhledu či změna nastavení zahájí nový měřicí úsek.

**Reference match není potvrzený dekódovaný bit, blok ani checksum.** Živá karta
neprovádí plnou recovery. Detektory mohou na stejném signálu dávat různé délky;
adaptivní měření čeká na opačné vrcholy, má omezenou historii a vynechá nedokončené
laloky. Uložený WAV analyzujte dosavadním offline postupem.

## Otevření uloženého záznamu

![Finalizovaný segment v kartě Recordings](images/live-recorder/recordings.png)

V **Recordings** vyberte segment s opravenou hlavičkou, nenulovými daty a kompatibilním
formátem. Zkontrolujte integritu, ověření PCM, případné chyby a stav sidecaru.

| Akce | Výsledek |
| --- | --- |
| **Visual analysis…** | Otevře existující vizuální analýzu WAV. |
| **Heuristic import…** | Otevře samostatné hlavní okno a dosavadní heuristický import. |
| **Manual recovery…** | Provede stávající analýzu a otevře ruční recovery nad záznamem. |
| **Show in folder** | Vybere soubor v Průzkumníku. |

Otevření neúplného, ale čitelného segmentu nepotvrzuje úplnost dat. Pokud soubor někdo
mezitím přesunul nebo smazal, aplikace zobrazí chybu. Seznam uchovává posledních 16
reportů v daném okně; nejde o trvalou knihovnu nahrávek.

## Když něco nefunguje

| Projev | Další krok |
| --- | --- |
| V nabídce není vstup | Zkontrolujte mono/stereo vstup ve Windows, zařízení připojte a použijte Refresh inputs. Pro ověření UI použijte demo. |
| RAW otevření selže | Zkontrolujte hlášení; pokud chcete zkusit zpracovaný vstup, výslovně vypněte Request RAW a zopakujte spuštění. |
| Record odmítá float32/PCM32 | Po posouzení převodu zapněte Allow float32 / PCM32 → PCM24, nebo zvolte podporovaný dodaný integer formát. |
| Cíl existuje nebo nelze zapisovat | Zvolte nový název a přístupnou složku. Existující záznam se tiše nenahradí. |
| Waveform vypadá zesíleně/oříznutě | Zkontrolujte display Gain/Auto scale a skutečné měřené úrovně; kresba a archivní PCM mají oddělené cesty. |
| Head alignment hlásí Not assessable | Ověřte energii/clipping, skutečný referenční tón, délku okna a vhodnost obsahu obou kanálů. |
| Histogram je prázdný | Ověřte Enable, povolený vybraný detektor/kanál, aktuální chybu nastavení a Fit all. Viditelné a celkové počty jsou oddělené. |
| Stop trvá déle | Finalizace také ověřuje uložené soubory a hashe. Nezavírejte proces násilně; sledujte stav a chyby. |
| Chyba zařízení/disku | Zkontrolujte Diagnostics a ponechaná částečná data. Neoznačujte záznam za kompletní jen podle existence WAV. |

## Postup ověření na skutečném hardwaru

Následující kroky jsou **plán akceptace, nikoliv hotový výsledek**. Použijte nový
výstupní adresář, vhodný line-in/USB vstup a skutečné zařízení spouštějte výslovně.
Ke každému běhu poznamenejte datum, verzi aplikace, Windows/driver, ID zařízení,
zvolený i dodaný formát, RAW stav, zdroj signálu, výsledek, report a případnou odchylku.

| Kontrola | Požadovaný důkaz |
| --- | --- |
| Enumerace a spuštění | Dostupný/nedostupný/obsazený vstup; podporovaný/nepodporovaný RAW a formát. Žádná tichá změna na jiné zařízení. |
| Krátký mono/stereo záznam | Očekávaný počet kanálů, skutečný rate/bit depth, rámce a report; opětovné načtení přes existující reader. |
| Opakované Start/Stop | Několik desítek cyklů, Stop během Start a potvrzené/odmítnuté zavření; žádný zbylý aktivní vstup. |
| Souvislých 10–30 minut | Cesty segmentů, délka podle rámců, integrita, SHA-256, spotřeba paměti a zvlášť archivní/náhledové výpadky. |
| Zatížení UI | Zoom, změny karet, resize a skutečné DPI při záznamu; žádný skrytý vliv kresby na archivní časovou osu. |
| Odpojení a chyba zápisu | Srozumitelný stav chyby, neúplná integrita a zachovaná získaná data. Diskové chyby zkoušejte v řízeném prostředí; nesmí se ničit jiné soubory. |
| Skutečná MZ kazeta | Změřené úrovně a pulzy, otevření uloženého WAV ve Visual/Heuristic/Manual a zaznamenaný výsledek recovery. Neúspěšné dekódování nekvalitní pásky samo nedokazuje chybu recordingu. |
| Srovnatelná pasáž A/B | Stejný řetězec a obsah; doložené změny jednotlivých indikátorů. Žádný odhad absolutního mechanického úhlu. |

Softwarové testy dokládají odolnost proti nepravidelnému doručování bufferů. Neměří
fyzický jitter A/D převodníku, kalibraci hodin ani všechny vlastnosti driveru. Linux,
macOS, skutečné per-monitor DPI a fyzická akceptace zůstávají neověřené.
