"""Verify F5 evidence, codec boundaries, frozen design inventory and review links."""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import subprocess
from urllib.parse import unquote,urlsplit

REPO=Path(__file__).resolve().parents[2]
checks=[]
def check(name,value):checks.append({'name':name,'pass':bool(value)})
def digest(path):return hashlib.sha256(path.read_bytes()).hexdigest()
class Links(HTMLParser):
    def __init__(self):super().__init__();self.refs=[]
    def handle_starttag(self,tag,attrs):self.refs.extend(v for k,v in attrs if k in ('href','src'))
def main():
    report=json.loads((REPO/'docs/F5_VALIDATION_2026-10-30.json').read_text(encoding='utf-8'))
    for name,suite in {**report['suites'],**report['targetedFollowup']}.items():
        check(name+' has no unresolved failures',suite['failed']==0)
        check(name+' original TRX fingerprint',digest(REPO/suite['source'])==suite['sha256'])
    for path,sha in report['sourceHashes'].items():check('Validated source '+path,digest(REPO/path)==sha)
    check('Binary outputs match across operating systems',len(report['binaryParity']['windows'])>=28 and report['binaryParity']['windows']==report['binaryParity']['linux'])
    inventory=json.loads((REPO/'docs/ui-design-system/source-inventory.json').read_text(encoding='utf-8'))
    f4=json.loads((REPO/'docs/F4_VALIDATION_2026-10-29.json').read_text(encoding='utf-8'))
    for source in inventory['sources']:
        path=source['source'].replace('\\','/');current=REPO/path
        if path=='MainWindow.xaml.cs':check('F4 WPF policy delegation unchanged in F5',digest(current)==f4['sourceHashes'][path])
        elif path=='DskEditorWindow.xaml.cs':
            text=current.read_text(encoding='utf-8-sig');a=text.index('        internal static string GetImportName(');b=text.index('        private bool EnsureWritable()',a)
            policy=(REPO/'src/MZTools.Core/Dsk/DskImportPolicy.cs').read_text(encoding='utf-8-sig');policy=policy[policy.index('        internal static string GetImportName('):policy.rfind('}')]
            restored=text[:a]+policy+text[b:]
            variants=[hashlib.sha256((bom+restored.replace('\n',ending)).encode('utf-8')).hexdigest() for bom in ('','\ufeff') for ending in ('\n','\r\n')]
            check('WPF editor differs only by exact shared import policy extraction',source['sha256'] in variants)
        else:check('Unchanged WPF UI '+path,digest(current)==source['sha256'])
    codecs=('Dsk/DskImage.cs','Media/HfeImage.cs','Dsk/CpmFileSystem.cs','Dsk/FsmzFileSystem.cs','Dsk/MrsFileSystem.cs','Dsk/SingleGameIplFileSystem.cs','Dsk/MultiGameIplFileSystem.cs','Mz800IplDskWriter.cs','Mz800MultiGameIplDskWriter.cs','Mz800IplDskReader.cs')
    for rel in codecs:
        old=subprocess.check_output(['git','show','HEAD:'+rel],cwd=REPO).decode('utf-8-sig').replace('\r\n','\n')
        check('Original codec unchanged '+rel,(REPO/'src/MZTools.Core'/rel).read_text(encoding='utf-8-sig')==old)
    old=subprocess.check_output(['git','show','HEAD:Dsk/DskDocument.cs'],cwd=REPO).decode('utf-8-sig').replace('\r\n','\n');new=(REPO/'src/MZTools.Core/Dsk/DskDocument.cs').read_text(encoding='utf-8-sig');marker='    internal static class DskDocumentFactory'
    check('Disk factories and remaining filesystem algorithms unchanged',old.split(marker)[1]==new.split(marker)[1])
    for paths in [('MZTools.Capture','MZTools.Capture.Windows'),('boot','MZTool.Tests/ReferenceGolden')]:check('Unchanged production/goldens '+str(paths),subprocess.run(['git','diff','--quiet','--',*paths],cwd=REPO).returncode==0)
    items=json.loads((REPO/'TestResults/F5/wpf-evaluated-items.json').read_text(encoding='utf-8-sig'))['Items']
    check('WPF Compile/Page excludes new frontend and test artifacts',not any(i['Identity'].replace('\\','/').lower().startswith(('src/','tests/','testresults/')) for values in items.values() for i in values))
    assets=json.loads((REPO/'src/MZTools.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
    check('Desktop has no WPF or Windows Capture/NAudio dependency',not any('NAudio' in name or 'MZTools.Capture.Windows' in name or name.startswith('MZTools/') for name in assets['libraries']))
    for name in ('desktop-audit','tests-audit'):
        audit=json.loads((REPO/'TestResults/F5'/(name+'.json')).read_text(encoding='utf-8-sig'))
        check(name+' no reported vulnerabilities',not any(f.get('topLevelPackages') or f.get('transitivePackages') for p in audit['projects'] for f in p.get('frameworks',[])))
    review=REPO/'docs/ui-design-system/avalonia-f5'
    for html in review.glob('*.html'):
        parser=Links();parser.feed(html.read_text(encoding='utf-8'))
        for ref in parser.refs:
            url=urlsplit(ref)
            if not url.scheme:check('Review link '+ref,(html.parent/unquote(url.path)).exists())
    for row in json.loads((review/'images.json').read_text()):
        check('Actual unmodified image '+row['file'],digest(review/row['file'])==row['sha256']==digest(REPO/row['source']))
    for name,entry in report['wpfSmoke'].items():check('Original WPF smoke '+name,entry['result']=='PASS' and digest(REPO/entry['source'])==entry['sha256'])
    for os,runs in report['native'].items():
        for name,run in runs.items():check(os+' native '+name,all(c['pass'] for c in run['checks']))
    for p in Path(__file__).parent.glob('*.py'):compile(p.read_text(encoding='utf-8-sig'),str(p),'exec')
    check('Validation scripts parse',True)
    result={'passed':sum(c['pass'] for c in checks),'failed':[c for c in checks if not c['pass']],'checks':checks}
    (REPO/'TestResults/F5/static-checks.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f'F5 static checks: {result["passed"]} passed; {len(result["failed"])} failed.')
    for row in result['failed']:print(row['name'])
    if result['failed']:raise SystemExit(1)
if __name__=='__main__':main()
