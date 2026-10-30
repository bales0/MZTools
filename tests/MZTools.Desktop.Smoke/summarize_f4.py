"""Archive the measured F4 result set, binary parity and unmodified UI images."""
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET
from summarize_results import trx, NS

REPO=Path(__file__).resolve().parents[2]
RESULTS=REPO/'TestResults/F4'
REVIEW=REPO/'docs/ui-design-system/avalonia-f4'

def main():
    suites={}
    initial_legacy=None
    for path in sorted((RESULTS/'windows/regression').glob('*.trx')):
        root=ET.parse(path).getroot();unit=root.find('.//t:UnitTest',NS)
        if unit is None:continue
        name=unit.attrib['storage'].replace('\\','/').rsplit('/',1)[-1]
        if name=='mztools.tests.dll':initial_legacy=trx(path)
        if name not in ('mztools.tests.dll','mztools.desktop.tests.dll'):suites['Windows/'+name]=trx(path)
    for name,path in {
        'Windows/Legacy':'windows/legacy-final.trx','Windows/Desktop':'windows/desktop-final.trx',
        'Linux/Desktop':'linux/desktop-final-linux.trx','Linux/Core':'linux/core-linux.trx',
        'Linux/Capture':'linux/capture-linux.trx'
    }.items():suites[name]=trx(RESULTS/path)
    targeted={'Windows/Legacy IO':trx(RESULTS/'windows/legacy-io-final.trx'),
              'Linux/Core IO':trx(RESULTS/'linux/core-io-final-linux.trx')}
    binary={}
    for os in ('windows','linux'):
        binary[os]={p.stem:json.loads(p.read_text()) for p in (RESULTS/os/'binary-checks').glob('*.json')}
    assert len(binary['windows'])==20 and binary['windows']==binary['linux'],'Binary parity mismatch'
    native={os:{name:json.loads((RESULTS/os/'native'/(name+'.json')).read_text())
                for name in ('native-smoke','native-media')} for os in ('windows','linux')}
    failed=sum(s['failed'] for s in suites.values())+sum(s['failed'] for s in targeted.values())
    failed+=sum(not c['pass'] for os in native.values() for run in os.values() for c in run['checks'])
    assert len(suites)==8 and not failed
    source_files=[p for parent in ('src/MZTools.Desktop','tests/MZTools.Desktop.Tests','tests/MZTools.Desktop.Smoke')
                  for p in (REPO/parent).rglob('*') if p.is_file() and not any(x in ('bin','obj','__pycache__') for x in p.parts)
                  and p.suffix in ('.cs','.axaml','.csproj','.py','.json')]
    source_files += [REPO/p for p in ('MainWindow.xaml.cs','MZTool.Tests/DskAnalyzerTests.cs','src/MZTools.Core/AtomicFileTransaction.cs',
        'src/MZTools.Core/TapeSavePolicy.cs','src/MZTools.Core/TapeDocumentIO.cs','src/MZTools.Core/Mzf.cs',
        'src/MZTools.Core/QuickDisk.cs','src/MZTools.Core/MZTools.Core.csproj')]
    report={'phase':'F4','date':'2026-10-29','suites':suites,'targetedFollowup':targeted,
            'passedCaseExecutions':sum(s['passed'] for s in suites.values()),
            'fixtureSkips':sum(len(s['skipped']) for s in suites.values()),'failed':failed,
            'binaryParity':binary,'native':native,
            'resolvedPriorLegacyFailure':initial_legacy,
            'unverified':['macOS','real OS drag/drop pointer bridge; computer-use native pipe unavailable',
                          'all physical monitor DPI combinations','screen reader','audio hardware'],
            'sourceHashes':{str(p.relative_to(REPO)).replace('\\','/'):hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(set(source_files))}}
    (REPO/'docs/F4_VALIDATION_2026-10-29.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    images={
        'windows-tape.png':'windows/native/native-tape.png',
        'windows-quickdisk.png':'windows/native/native-quickdisk.png',
        'windows-map.png':'windows/native/native-quickdisk-map.png',
        'linux-tape.png':'linux/native/native-tape.png',
        'linux-map.png':'linux/native/native-quickdisk-map.png',
        'compact.png':'windows/screenshots/tape-640x480-1.0.png',
        'metadata-validation.png':'windows/screenshots/metadata-validation.png',
        'unknown-host.png':'windows/screenshots/unknown-host.png',
        'scaled-200.png':'windows/screenshots/quickdisk-1366x768-2.0.png'
    }
    REVIEW.mkdir(parents=True,exist_ok=True);manifest=[]
    for name,source in images.items():
        path=RESULTS/source;shutil.copyfile(path,REVIEW/name)
        manifest.append({'file':name,'source':'TestResults/F4/'+source,'sha256':hashlib.sha256(path.read_bytes()).hexdigest()})
    (REVIEW/'images.json').write_text(json.dumps(manifest,indent=2)+'\n',encoding='utf-8')
    print(f'{len(suites)} complete suites; {report["passedCaseExecutions"]} passed; {report["fixtureSkips"]} skips; 20 matching binary outputs; {failed} unresolved failures.')

if __name__=='__main__':main()
