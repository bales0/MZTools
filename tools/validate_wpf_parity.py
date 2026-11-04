"""Validate measured WPF-reference UI evidence and actual distribution contents."""
from datetime import datetime,timezone
import hashlib
import html
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT=Path(__file__).resolve().parents[1]
RESULTS=ROOT/'TestResults/WpfParity-2026-11-03'
checks=[]
def read(p):return json.loads(p.read_text(encoding='utf-8-sig'))
def sha(p):return hashlib.sha256(p.read_bytes()).hexdigest()
def rel(p):return p.relative_to(ROOT).as_posix()
def check(name,ok):checks.append({'name':name,'pass':bool(ok)})
def main():
    suites={};native={};images=[]
    gallery=ROOT/'docs/ui-design-system/wpf-parity-2026-11-03';gallery.mkdir(parents=True,exist_ok=True)
    for os in ('windows','linux'):
        trx=RESULTS/os/'desktop-release.trx';counts=ET.parse(trx).find('.//{*}Counters').attrib
        suites[os]={'source':rel(trx),'sha256':sha(trx),'passed':int(counts['passed']),'failed':int(counts['failed']),'skipped':int(counts['total'])-int(counts['executed'])}
        check('Full '+os+' desktop suite',int(counts['failed'])==0 and int(counts['passed'])>=255)
        native[os]={}
        for p in sorted((RESULTS/os/'native-release').glob('*.json')):
            data=read(p);native[os][p.stem]={'source':rel(p),'sha256':sha(p),'checks':data['checks']}
            check(os+' '+p.stem,all(c['pass'] for c in data['checks']))
        check(os+' five native scenarios',len(native[os])==5)
        for p in sorted((RESULTS/os/'native-release').glob('*.png')):
            target=gallery/(os+'-'+p.name);shutil.copyfile(p,target);images.append({'file':target.name,'source':rel(p),'sha256':sha(p),'kind':os+' native Avalonia'});check('Unmodified render '+target.name,sha(p)==sha(target))
        runtime=read(RESULTS/os/'packaged-runtime.json');check(os+' extracted apphost and native FLAC runtime',runtime['captureStarted'] is False and 'libsndfile' in runtime['flac'])
    reference=RESULTS/'wpf-reference/wpf-reference.json';check('Actual unchanged WPF frontend reference',read(reference)['exit']==0)
    for p in sorted((RESULTS/'wpf-reference').glob('*.png')):
        target=gallery/p.name;shutil.copyfile(p,target);images.append({'file':target.name,'source':rel(p),'sha256':sha(p),'kind':'Original WPF'});check('WPF reference intact '+p.name,sha(p)==sha(target))
    for p in sorted((RESULTS/'windows/ui').glob('wpf-*.png')):
        target=gallery/('detail-'+p.name);shutil.copyfile(p,target);images.append({'file':target.name,'source':rel(p),'sha256':sha(p),'kind':'Avalonia Skia headless detail'});check('Detail render intact '+p.name,sha(p)==sha(target))
    for label,paths in {
        'Core/Capture pipeline':['src/MZTools.Core','MZTools.Capture','MZTools.Capture.Windows'],
        'WPF production reference':['MainWindow.xaml','MainWindow.xaml.cs','DskEditorWindow.xaml','DskEditorWindow.xaml.cs','AudioRecorderWindow.cs','AudioSignalAnalyzerWindow.cs','DskVisualInspectorWindow.cs','QuickDiskVisualInspectorWindow.cs'],
        'Golden outputs':['MZTool.Tests/ReferenceGolden','boot']
    }.items():check(label+' unchanged',subprocess.run(['git','diff','--quiet','HEAD','--',*paths],cwd=ROOT).returncode==0)
    spec=importlib.util.spec_from_file_location('packager',ROOT/'tools/package_desktop.py');packager=importlib.util.module_from_spec(spec);spec.loader.exec_module(packager)
    archives=[]
    for rid in packager.RIDS:
        ext='.zip' if rid.startswith(('win-','osx-')) else '.tar.gz';archive=RESULTS/'distribution'/('MZTools-'+rid+ext);meta=read(archive.with_suffix(archive.suffix+'.json'))
        check(rid+' archive checksum',sha(archive)==meta['sha256'])
        if ext=='.zip':
            with zipfile.ZipFile(archive) as file:
                manifest=json.loads(file.read('MZTools/SHA256SUMS.json'));check(rid+' every archived file checksum',all(hashlib.sha256(file.read('MZTools/'+name)).hexdigest()==digest for name,digest in manifest.items()))
                if rid.startswith('osx-'):check(rid+' executable permission',(file.getinfo('MZTools/MZTools.app/Contents/MacOS/MZTools.Desktop').external_attr>>16)&0o111)
        else:
            with tarfile.open(archive,'r:gz') as file:
                manifest=json.load(file.extractfile('MZTools/SHA256SUMS.json'));check(rid+' every archived file checksum',all(hashlib.sha256(file.extractfile('MZTools/'+name).read()).hexdigest()==digest for name,digest in manifest.items()));check(rid+' executable permission',file.getmember('MZTools/MZTools.Desktop').mode&0o111)
        product=Path(meta['staging'])/'MZTools';exe=product/('MZTools.app/Contents/MacOS/MZTools.Desktop' if rid.startswith('osx-') else 'MZTools.Desktop.exe' if rid.startswith('win-') else 'MZTools.Desktop');packager.verify_apphost(exe,rid);check(rid+' actual CPU apphost',True)
        assets=read(Path(meta['staging'])/'build/obj/MZTools.Desktop/project.assets.json')['libraries'];win=rid.startswith('win-')
        check(rid+' conditional native dependencies',any('MZTools.Capture.Windows' in p for p in assets)==win and any('org.sndfile.runtime.win-' in p for p in assets)==win)
        check(rid+' native original licence texts',(product/'third-party/NATIVE_LIBRARIES.md').exists() and (product/'third-party/LIBSNDFILE_COPYING.txt').exists())
        if win:
            library=product/'runtimes'/rid/'native/sndfile.dll'
            if not library.exists():library=product/'sndfile.dll'
            packager.verify_apphost(library,rid);check(rid+' native codec CPU',True)
        archives.append({k:v for k,v in meta.items() if k!='staging'})
    (RESULTS/'distribution/SHA256SUMS.txt').write_text(''.join(x['sha256']+'  '+Path(x['archive']).name+'\n' for x in archives),encoding='utf-8')
    sources={}
    for name in ('.gitignore','README.md','docs/WPF_PARITY_2026-11-03.md'):
        sources[name]=sha(ROOT/name)
    for root in ('src/MZTools.Desktop','tests/MZTools.Desktop.Tests','tests/MZTools.Desktop.Smoke','MZTool.Tests/WpfParityUiSmoke','tools'):
        for p in (ROOT/root).rglob('*'):
            if p.is_file() and p.suffix in ('.cs','.csproj','.axaml','.py') and not any(x in p.parts for x in ('bin','obj','__pycache__')):sources[rel(p)]=sha(p)
    result={'generatedUtc':datetime.now(timezone.utc).isoformat(),'baseline':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),'scope':'WPF-reference frontend change. Actual Windows/Linux x64; other RID execution, physical audio and signing UNTESTED. Gallery is not a claim of pixel identity of every dialog.','suites':suites,'native':native,'wpfReference':{'source':rel(reference),'sha256':sha(reference)},'archives':archives,'sourceHashes':sources,'checks':checks,'failed':[c for c in checks if not c['pass']]}
    (ROOT/'docs/WPF_PARITY_VALIDATION_2026-11-03.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    (gallery/'images.json').write_text(json.dumps(images,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    cards=''.join('<figure><a href="'+x['file']+'"><img loading="lazy" src="'+x['file']+'" alt="'+html.escape(x['file'])+'"></a><figcaption>'+html.escape(x['kind']+' · '+x['file'])+'</figcaption></figure>' for x in images)
    (gallery/'index.html').write_text('<!doctype html><html lang="cs"><meta charset="utf-8"><meta name="viewport" content="width=device-width"><title>MZTools · WPF reference</title><style>body{font:14px Segoe UI,Arial,sans-serif;margin:20px;color:#111;background:#fff}main{display:grid;grid-template-columns:repeat(auto-fit,minmax(400px,1fr));gap:16px}figure{margin:0;padding:8px;border:1px solid #ccc}img{width:100%;height:auto}figcaption{overflow-wrap:anywhere}</style><h1>MZTools · WPF a multiplatformní rozhraní</h1><p>Nezměněné skutečné WPF reference, native Windows/WSLg rendery a Skia headless detaily. Různé DPI, data a stav vstupu nejsou pixelový golden test. Fyzický audio hardware nebyl spuštěn.</p><p><a href="../../WPF_PARITY_2026-11-03.md">Změny a rozsah ověření</a> · <a href="images.json">Původ a SHA-256</a></p><main>'+cards+'</main></html>',encoding='utf-8')
    print('Desktop tests:',sum(s['passed'] for s in suites.values()),'PASS;',sum(s['failed'] for s in suites.values()),'FAIL')
    print('Native checks:',sum(len(v['checks']) for runs in native.values() for v in runs.values()))
    print('Evidence checks:',sum(c['pass'] for c in checks),'PASS;',len(result['failed']),'FAIL')
    for c in result['failed']:print(c['name'])
    if result['failed']:raise SystemExit(1)
if __name__=='__main__':main()
