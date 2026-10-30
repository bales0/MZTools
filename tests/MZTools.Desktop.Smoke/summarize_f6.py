"""Archive final measured F6 evidence; keep targeted repeats out of full-suite totals."""
import hashlib
import json
from pathlib import Path
import shutil
from summarize_results import trx

REPO=Path(__file__).resolve().parents[2]
RESULTS=REPO/'TestResults/F6'
REVIEW=REPO/'docs/ui-design-system/avalonia-f6'
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def main():
    suites={name:trx(RESULTS/rel) for name,rel in {
        'Windows/Legacy':'windows/legacy-final.trx','Windows/Core':'windows/core-final.trx',
        'Windows/Capture':'windows/capture-final.trx','Windows/Capture.Windows':'windows/capture-windows-final.trx',
        'Windows/Desktop':'windows/desktop-final.trx','Linux/Core':'linux/core-final.trx',
        'Linux/Capture':'linux/capture-final.trx','Linux/Desktop':'linux/desktop-final.trx'}.items()}
    follow={os+'/final audio UI':trx(RESULTS/os/'audio-ui-final.trx') for os in ('windows','linux')}
    binary={os:{p.name:digest(p) for p in sorted((RESULTS/os/'ui/binary').glob('*'))} for os in ('windows','linux')}
    assert len(binary['windows'])==10 and binary['windows']==binary['linux'],'Audio export parity mismatch'
    native={os:{name:json.loads((RESULTS/os/'native'/(name+'.json')).read_text(encoding='utf-8')) for name in ('native-smoke','native-media','native-disk','native-audio')} for os in ('windows','linux')}
    assert all(c['pass'] for runs in native.values() for run in runs.values() for c in run['checks'])
    assert not any(s['failed'] for s in [*suites.values(),*follow.values()])
    fft=[]
    for p in sorted((RESULTS/'fft-before').glob('*.json')):
        final=RESULTS/'fft-final'/p.name
        assert json.loads(p.read_text())==json.loads(final.read_text()),'FFT reference changed: '+p.name
        fft.append({'case':p.name,'before':p.relative_to(REPO).as_posix(),'after':final.relative_to(REPO).as_posix(),'beforeSha256':digest(p),'afterSha256':digest(final),'equalValues':True})
    assert len(fft)==12
    smoke={}
    for name,filename in {'audio':'wpf-audio-smoke.log','disk':'wpf-disk-smoke.log','report':'wpf-report-smoke.log','recorder':'recorder-smoke.log'}.items():
        p=RESULTS/'windows'/filename;s=p.read_text(encoding='utf-8-sig');assert 'PASS' in s and 'FAIL' not in s
        smoke[name]={'source':p.relative_to(REPO).as_posix(),'sha256':digest(p),'result':'PASS'}
    sources=[p for parent in ('src/MZTools.Desktop','tests/MZTools.Desktop.Tests','tests/MZTools.Desktop.Smoke','MZTools.Capture') for p in (REPO/parent).rglob('*') if p.is_file() and not any(x in ('bin','obj','__pycache__') for x in p.parts) and p.suffix in ('.cs','.axaml','.csproj','.py','.json')]
    sources += [REPO/p for p in ('MainWindow.xaml.cs','DskEditorWindow.xaml.cs','src/MZTools.Core/Media/AudioSignalAnalysis.cs','src/MZTools.Core/Media/AudioSignalAnalysisService.cs','src/MZTools.Core/Media/AudioRangeInspection.cs','src/MZTools.Core/Media/AudioSpectralAnalysis.cs','MZTools.Capture.Tests/SpectralReferenceTests.cs','MZTools.Capture.Tests/SpectralFrequencyTests.cs')]
    report={'phase':'F6','date':'2026-10-30','suites':suites,'targetedFollowup':follow,'passedCaseExecutions':sum(s['passed'] for s in suites.values()),'fixtureSkips':sum(len(s['skipped']) for s in suites.values()),'failed':0,'binaryParity':binary,'fftReference':fft,'native':native,'wpfSmoke':smoke,
        'unverified':['macOS','ARM64','independent ordinary Linux desktop (WSLg used)','external OS drag/drop','screen reader','all physical monitor DPI combinations','physical audio/disk hardware','FLAC backend (F8)'],
        'sourceHashes':{p.relative_to(REPO).as_posix():digest(p) for p in sorted(set(sources))}}
    (REPO/'docs/F6_VALIDATION_2026-10-30.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    rows=[
        ('windows-records.png','windows/native/native-audio-records.png','Windows · záznamy a read-only audio','Skia render skutečného nativního okna; vlastní dekódovatelná WAV fixture.'),
        ('histogram.png','windows/ui/audio-histogram-detail.png','Sdílený histogram v detailu','Skutečné Core bins a reference; stejná komponenta jako vložený graf.'),
        ('scatter.png','windows/ui/audio-scatter-detail.png','High / Low scatter','Skutečné naměřené páry; rozsah grafu lze přiblížit a posouvat.'),
        ('drift.png','windows/ui/audio-drift-detail.png','Drift a krátkodobá variabilita','Relativní timing error a lokální stddev; globální rychlost se vykazuje zvlášť.'),
        ('spectrum.png','windows/ui/audio-spectrum-detail.png','Offline amplitudové FFT spektrum','Periodic Hann, Hz a dBFS; není PSD.'),
        ('spectrogram.png','windows/ui/audio-spectrogram-detail.png','Offline spektrogram skutečného tónu','Celý sdílený graf; max agregace přes deklarovaná FFT okna a frekvenční bins.'),
        ('windows-recovery.png','windows/native/native-audio-recovery.png','Windows · manual recovery','Skutečný Core výsledek a přenositelné workflow.'),
        ('candidate.png','windows/ui/audio-candidate.png','Kandidáti a jejich platnost','Checksum a neúplnost zůstávají explicitní; synthetic header je uživatelská metadata.'),
        ('linux-records.png','linux/native/native-audio-records.png','Linux/WSLg · stejný audio workspace','Skia render skutečného X11 okna; WSLg není samostatná běžná desktop instalace.'),
        ('linux-spectrogram.png','linux/ui/audio-spectrogram-detail.png','Linux · stejný spektrogram','Skutečný headless Skia render; pixelový test ověřuje vykreslení barevných dat.'),
        ('compact.png','windows/ui/audio-layout-640x480-1,0.png','Malý viewport 640 × 480','Posuvný obsah a inspector dostupný v samostatném sdíleném detailu.'),
        ('inspector.png','windows/ui/audio-inspector-compact-640.png','Kompaktní inspector','Stejná komponenta pro vložený i plovoucí detail.'),
        ('scaled-150.png','windows/ui/audio-layout-1366x768-1,5.png','Render scale 150 %','Simulovaný headless render scale; nepředstavuje certifikaci všech monitorových DPI.'),
        ('scaled-200.png','windows/ui/audio-layout-920x640-2,0.png','Render scale 200 %','Simulovaný headless render scale; data a frame souřadnice se nemění.')]
    REVIEW.mkdir(parents=True,exist_ok=True);images=[];figures=[]
    for name,rel,title,description in rows:
        p=RESULTS/rel;shutil.copyfile(p,REVIEW/name);images.append({'file':name,'source':p.relative_to(REPO).as_posix(),'sha256':digest(p)})
        figures.append(f'<figure><figcaption><strong>{title}</strong><small>{description}</small></figcaption><a href="{name}"><img src="{name}" alt="{title}" loading="lazy"></a></figure>')
    (REVIEW/'images.json').write_text(json.dumps(images,indent=2)+'\n',encoding='utf-8')
    style='body{margin:0;background:var(--canvas);color:var(--text);font:16px system-ui,sans-serif}main{max-width:1440px;margin:auto;padding:28px}p{line-height:1.6}a{color:var(--accent)}figure{margin:24px 0;padding:20px;background:var(--surface);border:1px solid var(--border);border-radius:8px}figcaption{margin-bottom:14px}small{display:block;margin-top:8px}img{display:block;width:100%;height:auto}nav a{margin-right:18px}'
    html='<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>MZTools F6 · skutečný Audio Analyzer</title><link rel="stylesheet" href="../prototype/tokens.css"><style>'+style+'</style><main><h1>F6 · skutečný Audio Analyzer</h1><p>Implementace schváleného rozsahu F6, 30. 10. 2026. Skutečný vzhled je předložen k posouzení. Nezměněné rendery skutečných Avalonia kontrol; žádné obrazové makety. Nativní obraz znamená Skia render okna, nikoliv snímek celé pracovní plochy.</p><nav><a href="../../F6_AUDIO_ANALYZER_2026-10-30.md">Report a omezení</a><a href="../../F6_VALIDATION_2026-10-30.json">Naměřené výsledky</a><a href="../avalonia-f5/index.html">Schválená F5</a></nav><p><strong>'+str(report['passedCaseExecutions'])+' PASS, '+str(report['fixtureSkips'])+' fixture skipů, 10 shodných binárních audio exportů Windows/Linux.</strong> Zdrojové audio je read-only. F7 a FLAC/backend F8 nejsou součástí tohoto výsledku.</p>'+''.join(figures)+'</main></html>'
    (REVIEW/'index.html').write_text(html+'\n',encoding='utf-8')
    print(f"F6: {len(suites)} suites, {report['passedCaseExecutions']} PASS, {report['fixtureSkips']} fixture skips; 10 identical exports; 12 exact FFT references; {len(images)} review images.")
if __name__=='__main__':main()
