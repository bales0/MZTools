"""Verify a bin/<RID> runtime from a copy outside the repository, without SDK paths."""
import argparse
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile

try:
    from .build_desktop import ROOT, host_rid
    from .bundle_windows_runtime import imports
except ImportError:
    from build_desktop import ROOT, host_rid
    from bundle_windows_runtime import imports


def verify(rid):
    product = ROOT / 'bin' / rid
    forbidden = []
    for path in product.rglob('*'):
        if not path.is_file():
            continue
        relative = path.relative_to(product)
        if path.suffix.lower() in ('.pdb', '.cs', '.csproj', '.axaml', '.png') or any(part.lower() in ('build', 'obj', 'src', 'tests', 'testresults', 'docs') for part in relative.parts):
            forbidden.append(relative.as_posix())
    if forbidden:
        raise RuntimeError('Non-runtime build artifacts: ' + ', '.join(forbidden))
    options = json.loads((product / 'MZTools.Desktop.runtimeconfig.json').read_text(encoding='utf-8'))['runtimeOptions']
    if not options.get('includedFrameworks') or 'framework' in options or 'frameworks' in options:
        raise RuntimeError('The output is not self-contained')
    if rid.startswith('win-'):
        dlls = {path.name.lower(): path for path in product.glob('*.dll')}
        required = {name for path in dlls.values() for name in imports(path) if re.match(r'^(vcruntime140|msvcp140|concrt140|vccorlib140)', name)}
        if required - dlls.keys():
            raise RuntimeError('VC runtime dependencies missing: ' + str(required - dlls.keys()))
    if rid != host_rid():
        raise RuntimeError('Run verification on the matching OS/CPU: ' + rid)
    with tempfile.TemporaryDirectory(prefix='mztools-portable-') as directory:
        temporary = Path(directory).resolve()
        if temporary.parent != Path(tempfile.gettempdir()).resolve():
            raise RuntimeError('Unexpected temporary verification root')
        copied = temporary / 'MZTools portable'
        shutil.copytree(product, copied)
        environment = {key: value for key, value in os.environ.items() if not key.upper().startswith(('DOTNET_', 'MZTOOLS_'))}
        environment['PATH'] = os.pathsep.join([os.environ.get('SystemRoot', 'C:/Windows') + '/System32']) if os.name == 'nt' else '/usr/bin:/bin'
        if rid.startswith('linux-'):
            environment['LD_DEBUG'] = 'libs'
        exe = copied / ('MZTools.Desktop.exe' if rid.startswith('win-') else 'MZTools.Desktop')
        result = subprocess.run([str(exe), '--validate-runtime'], cwd=temporary, env=environment, text=True, capture_output=True, check=True)
        diagnostics = json.loads(result.stdout)
        if rid.startswith('linux-') and str(copied / 'native/libsndfile.so.1') not in result.stderr:
            raise RuntimeError('The process did not load its bundled libsndfile')
    report = {'rid': rid, 'outsideRepository': True, 'selfContained': True, 'nonRuntimeFiles': forbidden, 'runtime': diagnostics}
    output = ROOT / 'obj/desktop/verification'
    output.mkdir(parents=True, exist_ok=True)
    (output / (rid + '.json')).write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
    print(json.dumps(report))
    return report


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', default=host_rid())
    verify(parser.parse_args().rid)
