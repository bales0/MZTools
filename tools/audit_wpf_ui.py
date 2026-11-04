"""Record the 2026-11-04 UI audit from actual test/render/package outputs."""
import hashlib
import html
import json
from pathlib import Path
import shutil
import subprocess
import tarfile
import xml.etree.ElementTree as ET
import zipfile

ROOT = Path(__file__).resolve().parents[1]
RESULTS = ROOT / 'TestResults/WpfParity-2026-11-04'
GALLERY = ROOT / 'docs/ui-design-system/wpf-parity-2026-11-04'
GALLERY.mkdir(parents=True, exist_ok=True)

def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()

report = {'date': '2026-11-04', 'fullWpfParity': False, 'tests': {}, 'native': {}, 'images': [], 'packages': [], 'sources': {}}
for platform in ('windows', 'linux'):
    trx = RESULTS / platform / 'desktop.trx' if platform == 'windows' else max((RESULTS / platform).glob('*.trx'), key=lambda p: p.stat().st_mtime)
    counters = ET.parse(trx).getroot().find('.//{*}Counters').attrib
    assert int(counters['failed']) == 0 and int(counters['passed']) == int(counters['total']), counters
    report['tests'][platform] = {'path': trx.relative_to(ROOT).as_posix(), 'sha256': digest(trx), 'passed': int(counters['passed']), 'failed': 0}
    native = []
    for name in ('native-smoke.json', 'native-media.json', 'native-disk.json', 'native-audio.json', 'native-recorder.json'):
        path = RESULTS / platform / 'native' / name
        data = json.loads(path.read_text(encoding='utf-8-sig'))
        assert data.get('exit', 0) == 0 and data['checks'] and all(c['pass'] for c in data['checks']), path
        native.append({'path': path.relative_to(ROOT).as_posix(), 'sha256': digest(path), 'passed': len(data['checks'])})
    report['native'][platform] = native
    runtime = RESULTS / platform / 'packaged-runtime.json'
    data = json.loads(runtime.read_text(encoding='utf-8-sig'))
    assert data['captureStarted'] is False
    report.setdefault('runtimes', {})[platform] = data

reference = json.loads((RESULTS / 'wpf-reference/wpf-reference.json').read_text(encoding='utf-8-sig'))
assert reference['exit'] == 0
report['wpfReference'] = reference

for directory, prefix in ((RESULTS / 'wpf-reference', 'wpf'), (RESULTS / 'windows/screenshots', 'avalonia-windows'), (RESULTS / 'linux/screenshots', 'avalonia-linux')):
    for source in sorted(directory.glob('*.png')):
        target = GALLERY / (prefix + '-' + source.name)
        shutil.copyfile(source, target)
        assert digest(source) == digest(target)
        report['images'].append({'name': target.name, 'source': source.relative_to(ROOT).as_posix(), 'sha256': digest(source), 'origin': prefix})

for name in ('MZTools-win-x64.zip', 'MZTools-linux-x64.tar.gz'):
    path = RESULTS / 'distribution' / name
    metadata = json.loads(path.with_suffix(path.suffix + '.json').read_text(encoding='utf-8'))
    assert digest(path) == metadata['sha256']
    if path.suffix == '.zip':
        with zipfile.ZipFile(path) as archive:
            hashes = json.loads(archive.read('MZTools/SHA256SUMS.json'))
            for filename, expected in hashes.items():
                assert hashlib.sha256(archive.read('MZTools/' + filename)).hexdigest() == expected, filename
    else:
        with tarfile.open(path) as archive:
            hashes = json.load(archive.extractfile('MZTools/SHA256SUMS.json'))
            for filename, expected in hashes.items():
                assert hashlib.sha256(archive.extractfile('MZTools/' + filename).read()).hexdigest() == expected, filename
            assert archive.getmember('MZTools/MZTools.Desktop').mode & 0o111
    report['packages'].append(metadata)

protected = ['src/MZTools.Core', 'MZTools.Capture', 'MZTools.Capture.Windows']
protected += [p.name for p in ROOT.glob('*.cs')] + [p.name for p in ROOT.glob('*.xaml')]
subprocess.run(['git', 'diff', '--exit-code', 'HEAD', '--', *protected], cwd=ROOT, check=True, capture_output=True)
report['coreCaptureAndOriginalWpfUnchanged'] = True
for directory in ('src/MZTools.Desktop', 'tests/MZTools.Desktop.Tests', 'tests/MZTools.Desktop.Smoke', 'MZTool.Tests/WpfParityUiSmoke'):
    for path in sorted((ROOT / directory).rglob('*')):
        if path.suffix not in ('.cs', '.axaml', '.csproj') or any(part in ('bin', 'obj', 'TestResults') for part in path.parts):
            continue
        report['sources'][path.relative_to(ROOT).as_posix()] = digest(path)

def image(filename, caption):
    matches = [entry for entry in report['images'] if entry['name'] == filename]
    assert len(matches) == 1, filename
    return '<figure><figcaption>' + html.escape(caption) + '</figcaption><a href="' + filename + '"><img loading="lazy" src="' + filename + '"></a></figure>'

pairs = [
    ('QuickDisk Disk Map', 'wpf-wpf-original-quickdisk-map.png', 'avalonia-windows-avalonia-quickdisk-map.png'),
    ('QuickDisk Visual Analysis', 'wpf-wpf-original-quickdisk-visual.png', 'avalonia-windows-avalonia-quickdisk-visual.png'),
    ('Visual Media Analysis — WAV', 'wpf-wpf-original-audio-waveform.png', 'avalonia-windows-avalonia-audio-analysis.png'),
    ('Visual Media Analysis — FLAC / stejné PCM', 'wpf-wpf-original-audio-flac.png', 'avalonia-windows-avalonia-waveform-flac.png'),
]
body = '<!doctype html><html lang="cs"><meta charset="utf-8"><title>WPF / Avalonia — audit 4. 11. 2026</title><style>body{font:16px system-ui;margin:24px;background:#eee;color:#111}section{display:grid;grid-template-columns:1fr 1fr;gap:16px}h2{grid-column:1/-1}figure{margin:0;background:white;padding:8px}img{width:100%;height:auto}figcaption{padding:8px}a{color:#174b89}@media(max-width:900px){section{grid-template-columns:1fr}}</style><h1>Skutečné WPF / Avalonia rendery</h1><p>4. 11. 2026. Obrázky nebyly retušovány. WPF používá skutečné DPI Windows, Avalonia testovací render 96 DPI. Zdrojový soubor a SHA-256 každého obrázku jsou v <a href="images.json">manifestu</a>. Kliknutí otevře plné rozlišení.</p><p>Shoda všech oken není potvrzena: <a href="../../WPF_PARITY_AUDIT_2026-11-04.md">konkrétní nálezy a zbývající rozdíly</a>. WAV/FLAC používají shodné stereo PCM, 48 kHz / 16 bit, zobrazené framy [100,400). U FLAC je vpravo export samotného grafu.</p>'
for title, wpf, avalonia in pairs:
    body += '<section><h2>' + html.escape(title) + '</h2>' + image(wpf, 'Původní WPF') + image(avalonia, 'Opravená Avalonia / Windows') + '</section>'
body += '<h2>Linux — stejné ověřované scénáře</h2><section>'
for entry in report['images']:
    if entry['origin'] == 'avalonia-linux' and 'avalonia-' in entry['name']:
        body += image(entry['name'], entry['source'])
body += '</section></html>'
(GALLERY / 'index.html').write_text(body, encoding='utf-8')
(GALLERY / 'images.json').write_text(json.dumps(report['images'], indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
(ROOT / 'docs/WPF_PARITY_AUDIT_VALIDATION_2026-11-04.json').write_text(json.dumps(report, indent=2, ensure_ascii=False) + '\n', encoding='utf-8')
print(json.dumps({'testsPassed': sum(row['passed'] for row in report['tests'].values()), 'nativeChecksPassed': sum(row['passed'] for rows in report['native'].values() for row in rows), 'images': len(report['images']), 'archivesVerified': len(report['packages']), 'fullWpfParity': False}))
