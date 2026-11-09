"""The current, self-contained desktop is always bin/<RID>; build state stays in obj/desktop.

No source/test/evidence files are deployed. Replacement is staged and rolls back on failure.
Only directories recorded as managed by this builder may replace an existing build.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import platform
import shutil
import subprocess
import sys
import time

ROOT = Path(__file__).resolve().parents[1]
RIDS = ('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')


def host_rid():
    system = {'Windows': 'win', 'Linux': 'linux', 'Darwin': 'osx'}[platform.system()]
    cpu = {'AMD64': 'x64', 'x86_64': 'x64', 'ARM64': 'arm64', 'aarch64': 'arm64', 'arm64': 'arm64'}[platform.machine()]
    return system + '-' + cpu


def checked(path, parent):
    path, parent = Path(path).resolve(), Path(parent).resolve()
    if not parent.is_relative_to(ROOT) or parent == ROOT or not path.is_relative_to(parent) or path == parent:
        raise ValueError('Refusing to change a path outside the managed build directory: ' + str(path))
    return path


def remove_owned(path, parent):
    path = checked(path, parent)
    if path.exists():
        shutil.rmtree(path)


def source_fingerprint(rid, configuration, dotnet):
    digest = hashlib.sha256()
    sdk = subprocess.check_output([dotnet, '--version'], cwd=ROOT, text=True).strip()
    digest.update(json.dumps([rid, configuration, sdk]).encode())
    sources = []
    for directory in ('src', 'MZTools.Capture', 'MZTools.Capture.Windows', 'boot', 'images', 'docs/third-party/avalonia-f9'):
        for current, dirs, files in os.walk(ROOT / directory):
            dirs[:] = sorted(d for d in dirs if d.lower() not in ('bin', 'obj', 'artifacts', '.git'))
            sources.extend(Path(current) / name for name in files if name != 'packages.lock.json')
    sources.extend(ROOT / name for name in ('Directory.Build.props', 'Directory.Build.targets', 'LICENSE', 'THIRD_PARTY_NOTICES.md', 'tools/build_desktop.py', 'tools/bundle_linux_runtime.py', 'tools/bundle_windows_runtime.py'))
    for source in sorted(set(sources), key=lambda path: path.relative_to(ROOT).as_posix()):
        if source.is_file():
            digest.update(source.relative_to(ROOT).as_posix().encode())
            digest.update(source.read_bytes())
    return digest.hexdigest(), sdk


class BuildLock:
    def __init__(self, directory):
        directory.mkdir(parents=True, exist_ok=True)
        self.file = (directory / 'build.lock').open('a+b')

    def __enter__(self):
        # OS-owned lock: a crashed build releases it; no stale PID/lock-file guessing.
        if os.name == 'nt':
            import msvcrt
            self.file.seek(0)
            self.file.write(b'\0')
            self.file.flush()
            while True:
                try:
                    self.file.seek(0)
                    msvcrt.locking(self.file.fileno(), msvcrt.LK_NBLCK, 1)
                    break
                except OSError:
                    time.sleep(.2)
        else:
            import fcntl
            fcntl.flock(self.file.fileno(), fcntl.LOCK_EX)
        return self

    def __exit__(self, *_):
        self.file.close()


def managed_target(target, previous_state):
    if target.exists():
        if not previous_state or previous_state.get('directory') not in (str(target), target.relative_to(ROOT).as_posix()):
            raise RuntimeError('Existing output is not owned by this builder: ' + str(target))
        extras = {p.relative_to(target).as_posix() for p in target.rglob('*') if p.is_file()} - set(previous_state['files'])
        if extras:
            raise RuntimeError('Keep user files outside bin. Previous build preserved; unexpected files: ' + ', '.join(sorted(extras)))


def promote(payload, target, work, previous_state):
    target = checked(target, ROOT / 'bin')
    payload = checked(payload, ROOT / 'obj/desktop')
    backup = checked(work / 'previous', ROOT / 'obj/desktop')
    managed_target(target, previous_state)
    remove_owned(backup, ROOT / 'obj/desktop')
    moved = False
    try:
        if target.exists():
            target.rename(backup)
            moved = True
        target.parent.mkdir(parents=True, exist_ok=True)
        payload.rename(target)
    except OSError as error:
        if moved and not target.exists():
            backup.rename(target)
        raise RuntimeError('Cannot replace the current build. Close the running MZTools; the previous build was preserved.') from error


def build(rid=None, configuration='Release', dotnet='dotnet', force=False):
    rid = rid or host_rid()
    if rid not in RIDS:
        raise ValueError('Unsupported runtime: ' + rid)
    work = checked(ROOT / 'obj/desktop' / rid, ROOT / 'obj/desktop')
    target = checked(ROOT / 'bin' / rid, ROOT / 'bin')
    with BuildLock(work):
        fingerprint, sdk = source_fingerprint(rid, configuration, dotnet)
        state_file = work / 'latest.json'
        previous = json.loads(state_file.read_text(encoding='utf-8')) if state_file.exists() else None
        managed_target(target, previous)
        if not force and previous and previous.get('sourceFingerprint') == fingerprint and target.is_dir() and all((target / name).is_file() and hashlib.sha256((target / name).read_bytes()).hexdigest() == digest for name, digest in previous['files'].items()):
            print('Current runnable build: ' + str(target), flush=True)
            return target
        payload = work / 'payload'
        remove_owned(payload, ROOT / 'obj/desktop')
        subprocess.run([dotnet, 'publish', str(ROOT / 'src/MZTools.Desktop/MZTools.Desktop.csproj'), '-c', configuration, '-r', rid,
                        '--self-contained', 'true', '-p:MZToolsPublishLatest=false', '-p:UseAppHost=true', '-p:PublishSingleFile=false',
                        '-p:PublishTrimmed=false', '-p:DebugType=None', '-p:DebugSymbols=false', '-p:EnableWindowsTargeting=true',
                        '--artifacts-path', str(work / 'build'), '-o', str(payload)], cwd=ROOT, check=True)
        # Old publish files cannot leak into a new deployment; payload was empty before publish.
        for path in payload.rglob('*.pdb'):
            path.unlink()
        executable = payload / ('MZTools.Desktop.exe' if rid.startswith('win-') else 'MZTools.Desktop')
        if not executable.is_file():
            raise RuntimeError('Publish did not produce an apphost')
        if rid.startswith('win-'):
            try:
                from .bundle_windows_runtime import bundle
            except ImportError:
                from bundle_windows_runtime import bundle
            bundle(payload, rid)
        elif rid.startswith('linux-'):
            bundle = ROOT / 'tools/bundle_linux_runtime.py'
            if platform.system() == 'Linux':
                subprocess.run([sys.executable, str(bundle), str(payload), rid], check=True)
            elif os.name == 'nt':
                def wsl_path(path):
                    return '/mnt/' + path.drive[0].lower() + path.as_posix()[2:]
                subprocess.run(['wsl', '-d', 'Debian', '--exec', 'python3', wsl_path(bundle), wsl_path(payload), rid], check=True)
            else:
                raise RuntimeError('Linux native runtime must be assembled on Linux (or WSL Debian).')
        elif not rid.startswith('win-'):
            executable.chmod(0o755)
        if rid == host_rid():
            validation = subprocess.run([str(executable), '--validate-runtime'], capture_output=True, text=True, check=True)
            json.loads(validation.stdout)
        files = {p.relative_to(payload).as_posix(): hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(payload.rglob('*')) if p.is_file()}
        state = {'directory': target.relative_to(ROOT).as_posix(), 'rid': rid, 'configuration': configuration, 'sdk': sdk,
                 'selfContained': True, 'sourceFingerprint': fingerprint, 'files': files}
        temporary = work / 'latest.json.tmp'
        temporary.write_text(json.dumps(state, indent=2) + '\n', encoding='utf-8')
        promote(payload, target, work, previous)
        try:
            temporary.replace(state_file)
        except OSError:
            target.rename(payload)
            backup = work / 'previous'
            if backup.exists():
                backup.rename(target)
            raise
        try:
            remove_owned(work / 'previous', ROOT / 'obj/desktop')
        except OSError:
            print('Previous running build retained outside bin until the next build.', flush=True)
        print('Current runnable build: ' + str(target), flush=True)
        return target


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--rid', choices=RIDS, action='append', help='Repeat for multiple platforms; default: current OS/CPU')
    parser.add_argument('--configuration', choices=('Debug', 'Release'), default='Release')
    parser.add_argument('--dotnet', default='dotnet')
    parser.add_argument('--force', action='store_true', help='Republish even if source/configuration did not change')
    args = parser.parse_args()
    for rid in args.rid or [host_rid()]:
        build(rid, args.configuration, args.dotnet, args.force)
