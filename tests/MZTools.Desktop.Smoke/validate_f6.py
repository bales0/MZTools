"""Validate F6 evidence, preserved algorithms and actual review artifacts."""
import hashlib
import json
from pathlib import Path
import re
import subprocess
from html.parser import HTMLParser
from urllib.parse import unquote,urlsplit

REPO=Path(__file__).resolve().parents[2]
checks=[]
def check(name,value):checks.append({'name':name,'pass':bool(value)})
def digest(p):return hashlib.sha256(p.read_bytes()).hexdigest()
class Links(HTMLParser):
    def __init__(self):super().__init__();self.refs=[]
    def handle_starttag(self,tag,attrs):self.refs.extend(v for k,v in attrs if k in ('href','src'))
def original(path):return subprocess.check_output(['git','show','HEAD:'+path],cwd=REPO).decode('utf-8-sig').replace('\r\n','\n')
def body(text,name):
    match=re.search(r'^\s*public .*?\b'+name+r'\(',text,re.M);start=text.index('{',match.start());depth=0
    for i in range(start,len(text)):
        depth+=(text[i]=='{')-(text[i]=='}')
        if depth==0:return text[start:i+1]
def main():
    report=json.loads((REPO/'docs/F6_VALIDATION_2026-10-30.json').read_text(encoding='utf-8'))
    check('Eight full suites passed',len(report['suites'])==8 and not report['failed'])
    for name,suite in {**report['suites'],**report['targetedFollowup']}.items():
        check(name+' passed',suite['failed']==0);check(name+' measured TRX intact',digest(REPO/suite['source'])==suite['sha256'])
    for path,sha in report['sourceHashes'].items():check('Validated source '+path,digest(REPO/path)==sha)
    check('Ten byte-identical audio exports',len(report['binaryParity']['windows'])==10 and report['binaryParity']['windows']==report['binaryParity']['linux'])
    for row in report['fftReference']:
        check('Exact FFT reference '+row['case'],json.loads((REPO/row['before']).read_text())==json.loads((REPO/row['after']).read_text()))
    f5=json.loads((REPO/'docs/F5_VALIDATION_2026-10-30.json').read_text(encoding='utf-8'))
    allowed={'src/MZTools.Desktop/'+p for p in ('Documents/DiskDialogs.cs','Documents/DocumentCoordinator.cs','Documents/DocumentSession.cs','Documents/TapeSession.cs','Platform/DesktopDialogs.cs','Platform/DesktopDiskDialogs.cs','ViewModels/ShellDiskCommands.cs','ViewModels/ShellViewModel.cs','Views/MainWindow.axaml','Views/MainWindow.axaml.cs')}|{'tests/MZTools.Desktop.Smoke/Program.cs'}
    for path,sha in f5['sourceHashes'].items():
        if path not in allowed:check('F5 retained '+path,digest(REPO/path)==sha)
    inventory=json.loads((REPO/'docs/ui-design-system/source-inventory.json').read_text(encoding='utf-8'))
    for row in inventory['sources']:
        path=row['source'].replace('\\','/');expected=f5['sourceHashes'][path] if path in ('MainWindow.xaml.cs','DskEditorWindow.xaml.cs') else row['sha256']
        check('WPF preserved from approved baseline '+path,digest(REPO/path)==expected)
    algorithms=('WavHeuristicAnalyzer.cs','SharpMzPulseDecoder.cs','Media/AudioPulseMeasurementEngine.cs','Media/AudioRegionRecovery.cs','Media/AudioRecoveryMeasurements.cs','Media/PulseReferenceCatalog.cs','Media/AudioSyntheticHeader.cs','Media/AudioSelectionWaveExporter.cs','Media/SegmentedPcmStreamReader.cs')
    for rel in algorithms:check('Original algorithm '+rel,(REPO/'src/MZTools.Core'/rel).read_text(encoding='utf-8-sig')==original(rel))
    before=original('MZTools.Capture/LiveSignalAnalyzer.cs');after=(REPO/'MZTools.Capture/LiveSignalAnalyzer.cs').read_text(encoding='utf-8-sig')
    for method in ('StartAsync','Process'):check('Capture '+method+' body unchanged',body(before,method)==body(after,method))
    check('Capture pipeline and Windows backend unchanged',subprocess.run(['git','diff','--quiet','--','MZTools.Capture','MZTools.Capture.Windows',':!MZTools.Capture/LiveSignalAnalyzer.cs'],cwd=REPO).returncode==0)
    check('Reference goldens unchanged',subprocess.run(['git','diff','--quiet','--','MZTool.Tests/ReferenceGolden','boot'],cwd=REPO).returncode==0)
    renderer=(REPO/'src/MZTools.Desktop/Controls/AudioChart.cs').read_text(encoding='utf-8-sig')
    check('Graph renderer has no source IO, decoder or FFT calls',not any(x in renderer for x in ('File.','FileStream','OpenReader(','SpectralAnalysis.','Task.Run','WavHeuristicAnalyzer')))
    assets=json.loads((REPO/'src/MZTools.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
    check('No Windows Capture/NAudio dependency in Desktop',not any('NAudio' in x or 'MZTools.Capture.Windows' in x for x in assets['libraries']))
    review=REPO/'docs/ui-design-system/avalonia-f6';links=Links();links.feed((review/'index.html').read_text(encoding='utf-8'))
    for ref in links.refs:
        url=urlsplit(ref)
        if not url.scheme:check('Review link '+ref,(review/unquote(url.path)).exists())
    for row in json.loads((review/'images.json').read_text()):check('Unmodified actual UI image '+row['file'],digest(review/row['file'])==row['sha256']==digest(REPO/row['source']))
    for os,runs in report['native'].items():
        for name,run in runs.items():check(os+' native '+name,all(c['pass'] for c in run['checks']))
    for name,run in report['wpfSmoke'].items():check('WPF smoke '+name,run['result']=='PASS' and digest(REPO/run['source'])==run['sha256'])
    for p in Path(__file__).parent.glob('*.py'):compile(p.read_text(encoding='utf-8-sig'),str(p),'exec')
    result={'scope':'F6 static/evidence checks; not OS or hardware certification','passed':sum(c['pass'] for c in checks),'failed':[c for c in checks if not c['pass']],'checks':checks}
    (REPO/'TestResults/F6/static-checks.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f'F6 static checks: {result["passed"]} PASS; {len(result["failed"])} failed.')
    for row in result['failed']:print(row['name'])
    if result['failed']:raise SystemExit(1)
if __name__=='__main__':main()
