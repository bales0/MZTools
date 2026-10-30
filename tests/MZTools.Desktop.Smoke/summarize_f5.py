"""Archive measured F5 evidence; only authoritative final runs count in totals."""
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
from summarize_results import trx,NS

REPO=Path(__file__).resolve().parents[2]
RESULTS=REPO/'TestResults/F5'
REVIEW=REPO/'docs/ui-design-system/avalonia-f5'

def main():
    suites={}
    for path in sorted((RESULTS/'windows/regression').glob('*.trx')):
        root=ET.parse(path).getroot();unit=root.find('.//t:UnitTest',NS)
        if unit is None:continue
        name=unit.attrib['storage'].replace('\\','/').rsplit('/',1)[-1]
        if name!='mztools.desktop.tests.dll':suites['Windows/'+name]=trx(path)
    for name,rel in {'Windows/Desktop':'windows/desktop-final.trx','Linux/Desktop':'linux/desktop-linux.trx','Linux/Core':'linux/core-linux.trx','Linux/Capture':'linux/capture-linux.trx'}.items():suites[name]=trx(RESULTS/rel)
    targeted={'Linux/final Core IO':trx(RESULTS/'linux/core-io-final-linux.trx')}
    binary={os:{p.stem:json.loads(p.read_text()) for p in (RESULTS/os/'binary-checks').glob('*.json')} for os in ('windows','linux')}
    assert len(binary['windows'])>=28 and binary['windows']==binary['linux'],'Binary parity mismatch'
    native={os:{name:json.loads((RESULTS/os/'native'/(name+'.json')).read_text()) for name in ('native-smoke','native-media','native-disk')} for os in ('windows','linux')}
    failed=sum(s['failed'] for s in [*suites.values(),*targeted.values()])+sum(not c['pass'] for runs in native.values() for run in runs.values() for c in run['checks'])
    assert len(suites)==8 and not failed,'Unresolved final failures'
    smoke={}
    for name in ('report','dsk','audio','recorder'):
        p=RESULTS/'windows'/(name+'-smoke.log');s=p.read_text(encoding='utf-8-sig');assert 'PASS' in s and 'FAIL' not in s
        smoke[name]={'source':p.relative_to(REPO).as_posix(),'sha256':hashlib.sha256(p.read_bytes()).hexdigest(),'result':'PASS'}
    sources=[p for parent in ('src/MZTools.Desktop','tests/MZTools.Desktop.Tests','tests/MZTools.Desktop.Smoke') for p in (REPO/parent).rglob('*') if p.is_file() and not any(x in ('bin','obj','__pycache__') for x in p.parts) and p.suffix in ('.cs','.axaml','.csproj','.py','.json')]
    sources += [REPO/p for p in ('MainWindow.xaml.cs','DskEditorWindow.xaml.cs','src/MZTools.Core/AtomicFileTransaction.cs','src/MZTools.Core/Platform/CorePlatformServices.cs','src/MZTools.Core/Dsk/DskDocument.cs','src/MZTools.Core/Dsk/DskImportPolicy.cs')]
    report={'phase':'F5','date':'2026-10-30','suites':suites,'targetedFollowup':targeted,'passedCaseExecutions':sum(s['passed'] for s in suites.values()),'fixtureSkips':sum(len(s['skipped']) for s in suites.values()),'failed':failed,'binaryParity':binary,'native':native,'wpfSmoke':smoke,'unverified':['macOS','ARM64','external OS drag/drop pointer bridge','all real monitor DPI combinations','screen reader','physical disk/audio hardware'],'sourceHashes':{p.relative_to(REPO).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(set(sources))}}
    (REPO/'docs/F5_VALIDATION_2026-10-30.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    images={
        'windows-files.png':'windows/native/native-disk-files.png','windows-map.png':'windows/native/native-disk-map.png','windows-hfe.png':'windows/native/native-hfe-physical.png','windows-repair.png':'windows/native/native-disk-repair.png',
        'linux-files.png':'linux/native/native-disk-files.png','linux-map.png':'linux/native/native-disk-map.png','linux-hfe.png':'linux/native/native-hfe-physical.png',
        'hex.png':'windows/screenshots/disk-hex.png','surface.png':'windows/screenshots/disk-surface.png','compact.png':'windows/screenshots/disk-640x480-1.00.png','scaled-125.png':'windows/screenshots/disk-1366x768-1.25.png','scaled-200.png':'windows/screenshots/disk-1366x768-2.00.png'}
    REVIEW.mkdir(parents=True,exist_ok=True);manifest=[]
    for name,rel in images.items():
        source=RESULTS/rel;shutil.copyfile(source,REVIEW/name);manifest.append({'file':name,'source':source.relative_to(REPO).as_posix(),'sha256':hashlib.sha256(source.read_bytes()).hexdigest()})
    (REVIEW/'images.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(f'{len(suites)} complete suites; {report["passedCaseExecutions"]} passed; {report["fixtureSkips"]} fixture skips; {len(binary["windows"])} identical binary outputs; {failed} unresolved failures.')

if __name__=='__main__':main()
