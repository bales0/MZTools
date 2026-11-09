"""Copy only imported VC runtime DLLs from an installed, redistributable MSVC CRT.

Never copy arbitrary DLLs from System32 or install a system-wide redistributable.
Set MZTOOLS_VC_REDIST_DIR to a matching Microsoft.VC*.CRT folder when necessary.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import shutil
import struct


def imports(path):
    data = path.read_bytes()
    if data[:2] != b'MZ':
        return []
    pe = struct.unpack_from('<I', data, 60)[0]
    count, size = struct.unpack_from('<H', data, pe + 6)[0], struct.unpack_from('<H', data, pe + 20)[0]
    optional = pe + 24
    magic = struct.unpack_from('<H', data, optional)[0]
    sections = [struct.unpack_from('<IIII', data, optional + size + i * 40 + 8) for i in range(count)]

    def offset(rva):
        for virtual_size, start, raw_size, raw in sections:
            if start <= rva < start + max(virtual_size, raw_size):
                return raw + rva - start
        return rva

    rva = struct.unpack_from('<I', data, optional + (112 if magic == 0x20b else 96) + 8)[0]
    names = []
    if rva:
        current = offset(rva)
        while any(data[current:current + 20]):
            start = offset(struct.unpack_from('<I', data, current + 12)[0])
            names.append(data[start:data.index(b'\0', start)].decode('ascii').lower())
            current += 20
    return names


def bundle(product, rid):
    product = Path(product)
    arch = rid.removeprefix('win-')
    candidates = []
    if os.environ.get('MZTOOLS_VC_REDIST_DIR'):
        candidates.append(Path(os.environ['MZTOOLS_VC_REDIST_DIR']))
    if os.environ.get('VCToolsRedistDir'):
        candidates.extend(Path(os.environ['VCToolsRedistDir']).glob(arch + '/Microsoft.VC*.CRT'))
    for base in [Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')), Path(os.environ.get('ProgramFiles', 'C:/Program Files')), Path('/mnt/c/Program Files (x86)'), Path('/mnt/c/Program Files')]:
        candidates.extend(base.glob('Microsoft Visual Studio/*/*/VC/Redist/MSVC/*/' + arch + '/Microsoft.VC*.CRT'))
    candidates = sorted(set(p for p in candidates if p.is_dir()), reverse=True)
    vc_name = re.compile(r'^(vcruntime140(?:_\d+)?|msvcp140(?:_\w+)?|concrt140|vccorlib140)\.dll$')
    present = {p.name.lower(): p for p in product.glob('*.dll')}
    pending = {name for path in present.values() for name in imports(path) if vc_name.match(name)} - present.keys()
    records = []
    while pending:
        name = sorted(pending)[0]
        source = next((p / name for p in candidates if (p / name).is_file()), None)
        if source is None:
            raise RuntimeError('Missing app-local ' + name + '. Install the matching Visual Studio C++ Redist build component, or set MZTOOLS_VC_REDIST_DIR to its redistributable CRT directory.')
        data = source.read_bytes()
        pe = struct.unpack_from('<I', data, 60)[0]
        if struct.unpack_from('<H', data, pe + 4)[0] != (0x8664 if arch == 'x64' else 0xaa64):
            raise RuntimeError('Wrong VC runtime architecture: ' + str(source))
        shutil.copyfile(source, product / name)
        present[name] = product / name
        pending.remove(name)
        pending.update(dependency for dependency in imports(source) if vc_name.match(dependency) and dependency not in present)
        records.append({'file': name, 'sha256': hashlib.sha256(data).hexdigest(), 'sourceDirectory': str(source.parent),
                        'redistributableList': 'https://learn.microsoft.com/visualstudio/releases/2022/redistribution'})
    if records:
        legal = product / 'third-party/windows-vc'
        legal.mkdir(parents=True, exist_ok=True)
        for candidate in candidates:
            for parent in candidate.parents:
                licences = parent / 'Licenses'
                if licences.is_dir():
                    redist = next(licences.glob('*/Redist.txt'), None)
                    if redist:
                        shutil.copyfile(redist, legal / 'Redist.txt')
                        break
            else:
                continue
            break
        (legal / 'libraries.json').write_text(json.dumps(records, indent=2) + '\n', encoding='utf-8')
    print('Bundled app-local VC runtime libraries: ' + str(len(records)), flush=True)
