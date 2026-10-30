"""Export measured F3 results and review images; never substitute plans for results."""
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

REPO = Path(__file__).resolve().parents[2]
RESULTS = REPO / 'TestResults/F3'
REVIEW = REPO / 'docs/ui-design-system/avalonia-f3'
NS = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}

def trx(path):
    root = ET.parse(path).getroot()
    results = root.findall('.//t:UnitTestResult', NS)
    outcomes = [r.attrib['outcome'] for r in results]
    return {'source': str(path.relative_to(REPO)).replace('\\', '/'),
            'sha256': hashlib.sha256(path.read_bytes()).hexdigest(),
            'total': len(results), 'passed': outcomes.count('Passed'), 'failed': outcomes.count('Failed'),
            'skipped': [{'test': r.attrib['testName'], 'reason': r.findtext('.//t:Message', namespaces=NS)}
                        for r in results if r.attrib['outcome'] == 'NotExecuted']}

def main():
    suites = {}
    for path in sorted((RESULTS / 'windows/regression').glob('*.trx')):
        root = ET.parse(path).getroot()
        storage = root.find('.//t:UnitTest', NS).attrib['storage'].replace('\\', '/')
        name = storage.rsplit('/', 1)[-1]
        if name != 'mztools.desktop.tests.dll':
            suites['Windows/' + name] = trx(path)
    for name, path in {
        'Windows/Desktop': 'windows/desktop-final.trx', 'Linux/Desktop': 'linux/desktop-final-linux.trx',
        'Linux/Core': 'linux/core-linux.trx', 'Linux/Capture': 'linux/capture-linux.trx'
    }.items(): suites[name] = trx(RESULTS / path)
    native = {}
    for os in ('windows', 'linux'):
        native[os] = json.loads((RESULTS / os / 'native/native-smoke.json').read_text())
    report = {'phase': 'F3', 'date': '2026-10-29', 'suites': suites, 'native': native,
              'macOS': 'UNTESTED', 'audioHardware': 'UNTESTED', 'nativeAllDpiScales': 'UNTESTED',
              'wpfSmokeLogs': ['TestResults/F3/windows/' + name + '-smoke.log' for name in ('report', 'dsk', 'audio', 'recorder')]}
    output = REPO / 'docs/F3_VALIDATION_2026-10-29.json'
    output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    REVIEW.mkdir(parents=True, exist_ok=True)
    images = {
        'windows-native.png': 'windows/native/native-layout-1366x768.png',
        'linux-native.png': 'linux/native/native-layout-1366x768.png',
        'compact.png': 'windows/screenshots/headless-shell-640x480-1.0.png',
        'scaled-200.png': 'windows/screenshots/headless-shell-1366x768-2.0.png',
        'error.png': 'windows/screenshots/headless-error.png',
        'report.png': 'windows/screenshots/headless-report.png'
    }
    manifest = []
    for name, source in images.items():
        original = RESULTS / source; shutil.copyfile(original, REVIEW / name)
        manifest.append({'file': name, 'source': 'TestResults/F3/' + source, 'sha256': hashlib.sha256(original.read_bytes()).hexdigest()})
    (REVIEW / 'images.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
    failures = sum(s['failed'] for s in suites.values())
    failures += sum(not c['pass'] for n in native.values() for c in n['checks'])
    print(f'{len(suites)} suites; {sum(s["passed"] for s in suites.values())} passed cases; {failures} failures; {len(manifest)} review images.')
    if failures: raise SystemExit(1)

if __name__ == '__main__': main()
