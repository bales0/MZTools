# Analyze selected audio — samostatný vybraný interval, 4. 11. 2026

Tato revize mění skutečný uživatelský příkaz Analyze selected audio podle původního WPF AudioRegionRecoveryWindow.

- Tlačítko otevře rovnou okno **Selected audio — deeper analysis / recovery preview**. Obecný formulář ani výpočet mu nepředcházejí. Dekodér se spustí až tlačítkem **Run deeper analysis** uvnitř okna.
- Při otevření se zachytí přesné framy vybraného intervalu `[start,end)`. Nové okno má vlastní session, nastavení, životní cyklus a výsledky. Neimportuje kompletní programy z předchozí analýzy celého záznamu. Pozdější výběr nebo zoom v hlavním okně jeho vstup nemění.
- From / To jsou přímo v okně, v sekundách a s dostatečnou přesností pro obnovení původních framů. Změnu rozsahu musí zadat uživatel. Fit, zoom out a posouvání preview respektují aktuální rozsah. Shift+drag v preview aktualizuje From / To a následný běh dostane právě tento interval.
- Nastavení detektorů, klasifikace, polarity, kanálů, gatingu, referencí, očekávané délky a bounded diagnostics jsou přímo v okně. Posun R je dostupný ve vzorcích i milisekundách, s kroky a inverzí. PCM preview probíhá nezávisle na dekodéru.
- Záložky podle WPF: Visual analysis, Programs, Block candidates, Scan passes, Messages, Problems, Candidates / Recovery, Pass comparison. Visual analysis obsahuje Waveform a Histogram. Histogram vybírá jeden skutečně spotřebovaný stream dokončeného průchodu a nesčítá opakované testy. Jeho kanál a polarita jsou oddělené od waveformu. Změněná nastavení označí staré histogramové výsledky jako OUTDATED.
- Problems a markery ve waveformu používají problémy preview a skutečné scan findings. Po otevření se celý původní záznam nepředstavuje jako znovu naměřený/dekódovaný výsledek vybraného úseku.
- Manuální recovery z recorderu otevírá stejné okno. Uložení vybraného programu jako MZF používá přímo MZF výstup.

Regresní testy v `SelectedAnalysisWindowTests` ověřují skutečné kliknutí, prázdné výsledky před spuštěním, změnu výběru v rodiči po otevření, přesný StartSample / EndSample výsledku, zachování rodičovských programů a zdrojových bajtů, omezený viewport, vlastní stereo nastavení i Shift+drag uvnitř preview. Nativní GUI test opakuje otevření a spuštění vybraných framů na Windows a Linuxu.

Výsledky jsou v `TestResults/SelectedAnalysis-2026-11-04`; nová distribuce je v jeho podadresáři `distribution`. Starší buildy ve WpfParity-* a UiFixes-* tuto revizi neobsahují.

[Porovnání skutečného WPF a Avalonia renderu](ui-design-system/selected-analysis-2026-11-04/index.html) zachovává původní PNG. WPF obrázek má skutečné systémové DPI, Avalonia testovací render 96 DPI; jde o stejné stereo PCM a framy `[111,999)`, s odlišným názvem/containerem fixture. Toto není tvrzení pixelové identity všech dialogů ani všech platforem.
