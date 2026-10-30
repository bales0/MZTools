"""Validate F4 artifacts and boundaries without changing the frozen F2 inventory."""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import subprocess
from urllib.parse import unquote, urlsplit

REPO=Path(__file__).resolve().parents[2]
checks=[]
def check(name,passed):
    checks.append({'name':name,'pass':bool(passed)})
class Links(HTMLParser):
    def __init__(self):super().__init__();self.links=[]
    def handle_starttag(self,tag,attrs):self.links.extend(v for k,v in attrs if k in ('src','href'))

def main():
    report=json.loads((REPO/'docs/F4_VALIDATION_2026-10-29.json').read_text(encoding='utf-8'))
    for name,suite in {**report['suites'],**report['targetedFollowup']}.items():
        check(name+': no unresolved failures',suite['failed']==0)
        check(name+': original TRX fingerprint',hashlib.sha256((REPO/suite['source']).read_bytes()).hexdigest()==suite['sha256'])
    for name,digest in report['sourceHashes'].items():check('Validated source '+name,hashlib.sha256((REPO/name).read_bytes()).hexdigest()==digest)
    inventory=json.loads((REPO/'docs/ui-design-system/source-inventory.json').read_text(encoding='utf-8'))
    for source in inventory['sources']:
        current=hashlib.sha256((REPO/source['source']).read_bytes()).hexdigest()
        if source['source'].replace('\\','/')=='MainWindow.xaml.cs':
            check('Authorized F4 WPF policy delegation is recorded',report['sourceHashes'].get('MainWindow.xaml.cs')==current)
        else:check('Unchanged WPF UI '+source['source'],current==source['sha256'])
    items=json.loads((REPO/'TestResults/F4/wpf-evaluated-items.json').read_text(encoding='utf-8-sig'))['Items']
    check('WPF compile/Page isolation',not any(i['Identity'].replace('\\','/').lower().startswith(('src/','tests/','testresults/')) for v in items.values() for i in v))
    assets=json.loads((REPO/'src/MZTools.Desktop/obj/project.assets.json').read_text(encoding='utf-8-sig'))
    check('No WPF host or Windows Capture/NAudio dependency',not any('NAudio' in k or 'MZTools.Capture.Windows' in k or k.startswith('MZTools/') for k in assets['libraries']))
    check('Capture production sources unchanged',subprocess.run(['git','diff','--quiet','--','MZTools.Capture','MZTools.Capture.Windows'],cwd=REPO).returncode==0)
    check('Original binary goldens and boot images unchanged',subprocess.run(['git','diff','--quiet','--','boot','MZTool.Tests/ReferenceGolden'],cwd=REPO).returncode==0)
    original=subprocess.check_output(['git','show','HEAD:QuickDisk.cs'],cwd=REPO).decode('utf-8-sig').replace('\r\n','\n')
    current=(REPO/'src/MZTools.Core/QuickDisk.cs').read_text(encoding='utf-8-sig')
    for method in ('WriteMZQHeaderToFile','WriteMZQFileHeaderToFile','WriteMZQFileBodyToFile'):current=current.replace(method+'(Stream fileStream',method+'(FileStream fileStream')
    check('QD codec unchanged except three stream parameter widenings',current==original)
    old_mzf=subprocess.check_output(['git','show','HEAD:Mzf.cs'],cwd=REPO).decode('utf-8-sig').replace('\r\n','\n')
    new_mzf=(REPO/'src/MZTools.Core/Mzf.cs').read_text(encoding='utf-8-sig')
    marker='        private static void WriteAtomically('
    check('MZF parser/serializer/profile bytes unchanged',old_mzf.split(marker)[0]==new_mzf.split(marker)[0])
    for parent in ('docs/ui-design-system/avalonia-f4',):
        root=REPO/parent
        for html in root.glob('*.html'):
            parser=Links();parser.feed(html.read_text(encoding='utf-8'))
            for ref in parser.links:
                url=urlsplit(ref)
                if not url.scheme:check('Review link '+ref,(html.parent/unquote(url.path)).exists())
        for item in json.loads((root/'images.json').read_text()):check('Actual image '+item['file'],hashlib.sha256((root/item['file']).read_bytes()).hexdigest()==item['sha256'])
    for name in ('desktop-audit','tests-audit'):
        audit=json.loads((REPO/'TestResults/F4'/(name+'.json')).read_text(encoding='utf-8-sig'))
        check(name+': no reported package vulnerabilities',not any(f.get('topLevelPackages') or f.get('transitivePackages') for p in audit['projects'] for f in p.get('frameworks',[])))
    for p in Path(__file__).parent.glob('*.py'):compile(p.read_text(encoding='utf-8-sig'),str(p),'exec')
    check('Validation tool syntax',True)
    result={'passed':sum(c['pass'] for c in checks),'failed':[c for c in checks if not c['pass']],'checks':checks}
    (REPO/'TestResults/F4/static-checks.json').write_text(json.dumps(result,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f'F4 static checks: {result["passed"]} passed; {len(result["failed"])} failed.')
    for failed in result['failed']:print(failed['name'])
    if result['failed']:raise SystemExit(1)

if __name__=='__main__':main()
