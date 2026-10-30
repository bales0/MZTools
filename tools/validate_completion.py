"""Rebuild measured completion evidence; no platform/hardware pass is inferred."""
import hashlib
import html
from datetime import datetime, timezone
from html.parser import HTMLParser
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import sys
import tarfile
import xml.etree.ElementTree as ET
import zipfile
from urllib.parse import unquote, urlsplit

ROOT=Path(__file__).resolve().parents[1]
RESULTS=ROOT/'TestResults/Completion'
checks=[]
def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()
def check(name,ok):checks.append({'name':name,'pass':bool(ok)})
def load(path):return json.loads(path.read_text(encoding='utf-8-sig'))
def relative(path):return path.relative_to(ROOT).as_posix()

def main():
    suites={}
    names={'windows':('core-final','legacy-final','MZTools.Capture.Tests-final','MZTools.Capture.Windows.Tests-final','desktop-accepted'),'linux':('core-final','capture-final','desktop-accepted')}
    for os,files in names.items():
        for name in files:
            p=RESULTS/os/(name+'.trx');tree=ET.parse(p);counter=tree.find('.//{*}Counters');counts={k:int(v) for k,v in counter.attrib.items()};check(os+'/'+name+' no failures',counts['failed']==0 and counts['passed']>0)
            suites[os+'/'+name]={'source':relative(p),'sha256':sha(p),'passed':counts['passed'],'failed':counts['failed'],'skipped':counts['total']-counts['executed']}
    native={};images=[];gallery=ROOT/'docs/ui-design-system/avalonia-completion';gallery.mkdir(parents=True,exist_ok=True)
    for os in names:
        native[os]={}
        for p in sorted((RESULTS/os/'native-accepted').glob('*.json')):
            j=load(p);native[os][p.stem]={'source':relative(p),'sha256':sha(p),'checks':j['checks'],'hardwareTested':False}
            check(os+'/'+p.stem,all(c['pass'] for c in j['checks']))
        check(os+' five native suites present',len(native[os])==5)
        for p in sorted((RESULTS/os/'native-accepted').glob('*.png')):
            target=gallery/(os+'-'+p.name);shutil.copyfile(p,target);images.append({'file':target.name,'source':relative(p),'sha256':sha(p),'os':os});check('Actual render '+target.name,sha(target)==sha(p))
    (gallery/'images.json').write_text(json.dumps(images,indent=2)+'\n',encoding='utf-8')
    cards=''.join('<figure><a href="'+x['file']+'"><img loading="lazy" src="'+x['file']+'" alt="'+html.escape(x['os']+' '+x['file'])+'"></a><figcaption>'+html.escape(x['os']+' · '+x['file'])+'</figcaption></figure>' for x in images)
    (gallery/'index.html').write_text('<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>MZTools · finální skutečné UI</title><style>body{font:16px system-ui;background:#f4f6fa;color:#172a40;margin:24px}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(360px,1fr));gap:20px}figure{margin:0;background:white;padding:12px;border:1px solid #ced8e6;border-radius:8px}img{width:100%;height:auto}figcaption{overflow-wrap:anywhere}a{color:#1559ca}</style><h1>MZTools · skutečné Avalonia UI</h1><p>Windows x64 a Debian/WSLg x64. Nezměněné Skia rendery skutečných native oken; nejsou desktop screenshoty ani návrhové mockupy. Fyzický audio hardware nebyl spuštěn.</p><p><a href="../../MIGRATION_COMPLETION_2026-10-30.md">Výsledky a omezení</a> · <a href="images.json">Původ a SHA-256 obrázků</a></p><main>'+cards+'</main></html>',encoding='utf-8')

    spec=importlib.util.spec_from_file_location('baseline',ROOT/'tests/MZTools.Desktop.Smoke/validate_f6.py');baseline=importlib.util.module_from_spec(spec);spec.loader.exec_module(baseline)
    for rel in ('WavHeuristicAnalyzer.cs','SharpMzPulseDecoder.cs','Media/AudioPulseMeasurementEngine.cs','Media/AudioRegionRecovery.cs','Media/AudioRecoveryMeasurements.cs','Media/PulseReferenceCatalog.cs','Media/AudioSyntheticHeader.cs','Media/AudioSelectionWaveExporter.cs','Media/SegmentedPcmStreamReader.cs'):
        check('Original algorithm '+rel,(ROOT/'src/MZTools.Core'/rel).read_text(encoding='utf-8-sig')==baseline.original(rel))
    before=baseline.original('MZTools.Capture/LiveSignalAnalyzer.cs');after=(ROOT/'MZTools.Capture/LiveSignalAnalyzer.cs').read_text(encoding='utf-8-sig')
    for method in ('StartAsync','Process'):check('Capture callback '+method+' unchanged',baseline.body(before,method)==baseline.body(after,method))
    check('Capture pipeline and WASAPI retained',subprocess.run(['git','diff','--quiet','--','MZTools.Capture','MZTools.Capture.Windows',':!MZTools.Capture/LiveSignalAnalyzer.cs'],cwd=ROOT).returncode==0)
    check('Reference goldens unchanged',subprocess.run(['git','diff','--quiet','--','MZTool.Tests/ReferenceGolden','boot'],cwd=ROOT).returncode==0)
    f5=load(ROOT/'docs/F5_VALIDATION_2026-10-30.json');inventory=load(ROOT/'docs/ui-design-system/source-inventory.json')
    for row in inventory['sources']:
        p=row['source'].replace('\\','/');expected=f5['sourceHashes'][p] if p in ('MainWindow.xaml.cs','DskEditorWindow.xaml.cs') else row['sha256'];check('WPF retained '+p,sha(ROOT/p)==expected)
    for p in (ROOT/'src/MZTools.Desktop/Controls').glob('AudioChart*.cs'):
        code=p.read_text(encoding='utf-8-sig');check('Render has no IO/FFT '+p.name,not any(x in code for x in ('File.','FileStream','OpenReader(','SpectralAnalysis.','Task.Run','WavHeuristicAnalyzer')))
    notices=ROOT/'docs/third-party/avalonia-f9'
    for row in load(notices/'native-sources.json'):check('Native licence '+row['file'],sha(notices/row['file'])==row['sha256'])
    for row in load(notices/'packages.json'):
        check('Package terms '+row['package'],bool(row['texts']))
        for text in row['texts']:check('Original archived terms '+text['file'],sha(notices/text['file'])==text['sha256'])

    archives=[]
    package_spec=importlib.util.spec_from_file_location('packager',ROOT/'tools/package_desktop.py');packager=importlib.util.module_from_spec(package_spec);package_spec.loader.exec_module(packager)
    for rid in packager.RIDS:
        ext='.zip' if rid.startswith(('win-','osx-')) else '.tar.gz';p=RESULTS/'distribution'/('MZTools-'+rid+ext);meta=load(p.with_suffix(p.suffix+'.json'));check('Archive hash '+rid,sha(p)==meta['sha256'])
        if ext=='.zip':
            with zipfile.ZipFile(p) as z:
                hashes=json.loads(z.read('MZTools/SHA256SUMS.json'))
                check('Archive contents '+rid,all(hashlib.sha256(z.read('MZTools/'+n)).hexdigest()==h for n,h in hashes.items()))
                if rid.startswith('osx-'):check('Executable permission '+rid,(z.getinfo('MZTools/MZTools.app/Contents/MacOS/MZTools.Desktop').external_attr>>16)&0o111)
        else:
            with tarfile.open(p,'r:gz') as z:
                hashes=json.load(z.extractfile('MZTools/SHA256SUMS.json'))
                check('Archive contents '+rid,all(hashlib.sha256(z.extractfile('MZTools/'+n).read()).hexdigest()==h for n,h in hashes.items()))
                check('Executable permission '+rid,z.getmember('MZTools/MZTools.Desktop').mode & 0o111)
        product=Path(meta['staging'])/'MZTools';exe=product/('MZTools.app/Contents/MacOS/MZTools.Desktop' if rid.startswith('osx-') else 'MZTools.Desktop.exe' if rid.startswith('win-') else 'MZTools.Desktop');packager.verify_apphost(exe,rid);check('Apphost architecture '+rid,True)
        assets=load(Path(meta['staging'])/'build/obj/MZTools.Desktop/project.assets.json');libraries=assets['libraries'];iswin=rid.startswith('win-')
        check('Platform dependencies '+rid,any('MZTools.Capture.Windows' in x for x in libraries)==iswin and any('org.sndfile.runtime.win-' in x for x in libraries)==iswin)
        check('No WPF frontend dependency '+rid,not any(x.startswith('MZTools/') for x in libraries))
        check('Native notice packaged '+rid,(product/'third-party/NATIVE_LIBRARIES.md').exists())
        if iswin:
            native_library=product/'runtimes'/rid/'native/sndfile.dll'
            if not native_library.exists():native_library=product/'sndfile.dll'
            packager.verify_apphost(native_library,rid);check('libsndfile CPU '+rid,True)
        archives.append({k:v for k,v in meta.items() if k!='staging'})
    for os in names:
        j=load(RESULTS/os/'packaged-runtime.json');check('Packaged runtime '+os,j['captureStarted'] is False and 'libsndfile' in j['flac'])
    wpf={}
    for name,marker in {'wpf-disk-trace.log':'PASS: Save As destination','wpf-recorder-final.log':'PASS: 114 recorder UI checks','wpf-audio-final.log':'PASS: combined histogram','wpf-report-final.log':'PASS: file/property tables'}.items():
        p=RESULTS/'windows'/name;ok=marker in p.read_text(encoding='utf-8-sig');check(name+' completed',ok);wpf[name]={'source':relative(p),'sha256':sha(p),'result':'PASS' if ok else 'INCOMPLETE'}
    class Links(HTMLParser):
        def handle_starttag(self,tag,attrs):
            for key,value in attrs:
                if key in ('src','href'):
                    url=urlsplit(value)
                    if not url.scheme:check('Gallery link '+value,(gallery/unquote(url.path)).exists())
    Links().feed((gallery/'index.html').read_text(encoding='utf-8'))
    sources={}
    for folder in ('src','MZTools.Capture','MZTools.Capture.Windows','tools','.github','tests'):
        for p in (ROOT/folder).rglob('*'):
            if p.is_file() and p.suffix in ('.cs','.csproj','.axaml','.py','.yml','.c') and not any(x in p.parts for x in ('bin','obj','__pycache__')):sources[relative(p)]=sha(p)
    report={'generatedUtc':datetime.now(timezone.utc).isoformat(),'scope':'Local Windows x64 and Debian/WSLg x64; macOS/ARM/hardware/signing/remote CI UNTESTED','suites':suites,'passed':sum(x['passed'] for x in suites.values()),'failedTests':sum(x['failed'] for x in suites.values()),'skipped':sum(x['skipped'] for x in suites.values()),'native':native,'wpfSmoke':wpf,'benchmarks':{os:load(RESULTS/os/'audio/audio-performance.json') for os in names},'fixtureArchiveInventory':load(RESULTS/'fixture-archive-inventory.json'),'archives':archives,'sourceHashes':sources,'checks':checks,'failedChecks':[c for c in checks if not c['pass']]}
    (ROOT/'docs/COMPLETION_VALIDATION_2026-10-30.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f"Completion: {report['passed']} tests passed, {report['failedTests']} failed, {report['skipped']} skipped; {sum(c['pass'] for c in checks)} evidence checks passed; {len(report['failedChecks'])} failed.")
    for c in report['failedChecks']:print(c['name'])
    if report['failedChecks']:sys.exit(1)
if __name__=='__main__':main()
