"""Bundle the Linux audio runtime and its non-OS dependencies from the build host.

glibc, the C++ runtime and desktop graphics remain OS components. Exact library
versions, required glibc and original package licence texts accompany the build.
"""
import ctypes.util
import hashlib
import json
from pathlib import Path
import platform
import re
import shutil
import struct
import subprocess
import sys


def bundle(product, rid):
    product = Path(product).resolve()
    if platform.system() != 'Linux' or not (product / 'MZTools.Desktop').is_file():
        raise RuntimeError('Linux bundle requires a Linux build host and published apphost')
    arch = 'arm64' if platform.machine() in ('aarch64', 'arm64') else 'x64' if platform.machine() == 'x86_64' else ''
    if rid != 'linux-' + arch:
        raise RuntimeError('Build native audio libraries on the matching Linux CPU architecture: ' + rid)
    cache = subprocess.check_output(['ldconfig', '-p'], text=True)
    libraries = dict(re.findall(r'^\s*(\S+)\s+\([^\n]+\)\s+=>\s+(\S+)', cache, re.M))
    os_library = re.compile(r'^(lib(c|m|pthread|dl|rt|resolv|util)\.so\.|libstdc\+\+\.so\.|libgcc_s\.so\.|ld-linux)')
    needed = {}
    for name in ('sndfile', 'portaudio'):
        soname = ctypes.util.find_library(name)
        if not soname or soname not in libraries:
            raise RuntimeError('Install the build-host libsndfile1/libportaudio2 packages before bundling: ' + name)
        source = Path(libraries[soname])
        needed[soname] = source
        dependencies = subprocess.check_output(['ldd', str(source)], text=True)
        if 'not found' in dependencies:
            raise RuntimeError('Unresolved audio dependency: ' + dependencies)
        for dependency, path in re.findall(r'^\s*(\S+)\s+=>\s+(/\S+)', dependencies, re.M):
            if not os_library.match(dependency):
                needed[dependency] = Path(path)
    native = product / 'native'
    native.mkdir()
    legal = product / 'third-party/linux-native'
    legal.mkdir(parents=True, exist_ok=True)
    records = []
    glibc = set()
    for soname, source in sorted(needed.items()):
        source = source.resolve(strict=True)
        data = source.read_bytes()
        if data[:6] != b'\x7fELF\x02\x01' or struct.unpack_from('<H', data, 18)[0] != (62 if arch == 'x64' else 183):
            raise RuntimeError('Wrong native architecture: ' + str(source))
        shutil.copyfile(source, native / soname)
        symbols = subprocess.check_output(['readelf', '--version-info', '-W', str(source)], text=True)
        glibc.update(re.findall(r'Name: GLIBC_(\d+\.\d+)', symbols))
        owner = subprocess.check_output(['dpkg-query', '-S', str(source)], text=True).splitlines()[0].split(': ')[0]
        package = owner.split(':')[0]
        version = subprocess.check_output(['dpkg-query', '-W', '-f=${Version}', owner], text=True)
        source_package = subprocess.check_output(['dpkg-query', '-W', '-f=${source:Package}', owner], text=True)
        source_version = subprocess.check_output(['dpkg-query', '-W', '-f=${source:Version}', owner], text=True)
        copyright_file = Path('/usr/share/doc') / package / 'copyright'
        if not copyright_file.is_file():
            raise RuntimeError('Native licence is missing: ' + package)
        shutil.copyfile(copyright_file, legal / (package + '-copyright.txt'))
        for name in set(re.findall(r'/usr/share/common-licenses/([\w.\-]+)', copyright_file.read_text(encoding='utf-8'))):
            common = Path('/usr/share/common-licenses') / name
            if common.is_file():
                shutil.copyfile(common, legal / (name + '.txt'))
        records.append({'file': 'native/' + soname, 'sha256': hashlib.sha256(data).hexdigest(), 'package': package,
                        'version': version, 'sourcePackage': source_package, 'sourceVersion': source_version,
                        'source': 'https://sources.debian.org/src/' + source_package + '/',
                        'licence': 'third-party/linux-native/' + package + '-copyright.txt'})
    minimum = max(glibc, key=lambda value: tuple(map(int, value.split('.'))))
    # Ensure dlopen resolves the bundled audio libraries and their dependencies
    # before system copies. Keep the standard apphost name as the public launcher.
    apphost = product / 'MZTools.Desktop'
    apphost.rename(product / 'MZTools.Desktop.bin')
    (product / 'MZTools.Desktop.bin').chmod(0o755)
    apphost.write_text('#!/bin/sh\nset -eu\napp_dir=$(CDPATH= cd -- "$(dirname -- "$0")" && pwd)\nexport LD_LIBRARY_PATH="$app_dir/native${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}"\nexec "$app_dir/MZTools.Desktop.bin" "$@"\n', encoding='utf-8')
    apphost.chmod(0o755)
    (legal / 'libraries.json').write_text(json.dumps({'minimumGlibc': minimum, 'libraries': records}, indent=2) + '\n', encoding='utf-8')
    (product / 'LINUX-RUNTIME.txt').write_text('Run: ./MZTools.Desktop\nSelf-contained .NET; no .NET SDK/runtime or Python installation is needed.\nAudio libraries are bundled from this Linux build host.\nRequires glibc >= ' + minimum + ' and the OS desktop graphics, font and audio-driver environment.\nDo not launch MZTools.Desktop.bin directly; MZTools.Desktop sets the local native library search path.\n', encoding='utf-8')
    print('Bundled Linux audio libraries: ' + str(len(records)) + '; glibc >= ' + minimum, flush=True)


if __name__ == '__main__':
    bundle(sys.argv[1], sys.argv[2])
