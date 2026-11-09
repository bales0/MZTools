"""Update bin/<RID>. With --output, also create a minimal runtime-only archive.

Published archives are exclusive; build state and staging stay in obj/desktop.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import plistlib
import shutil
import struct
import subprocess
import sys
import tarfile
import tempfile
import zipfile

try:
    from .build_desktop import build, remove_owned
except ImportError:
    from build_desktop import build, remove_owned

ROOT=Path(__file__).resolve().parents[1]
RIDS=('win-x64','win-arm64','linux-x64','linux-arm64','osx-x64','osx-arm64')

def verify_apphost(path,rid):
    if rid.startswith('linux-') and path.read_bytes().startswith(b'#!/bin/sh'):
        path=path.with_name(path.name+'.bin')
    data=path.read_bytes()
    if rid.startswith('win-'):
        valid=data[:2]==b'MZ' and struct.unpack_from('<H',data,struct.unpack_from('<I',data,60)[0]+4)[0]==(0x8664 if rid.endswith('x64') else 0xaa64)
    elif rid.startswith('linux-'):
        valid=data[:6]==b'\x7fELF\x02\x01' and struct.unpack_from('<H',data,18)[0]==(62 if rid.endswith('x64') else 183)
    else:
        valid=data[:4]==b'\xcf\xfa\xed\xfe' and struct.unpack_from('<I',data,4)[0]==(0x1000007 if rid.endswith('x64') else 0x100000c)
    if not valid:raise RuntimeError('Invalid apphost architecture/content for '+rid)
def package(rid,output=None,dotnet='dotnet'):
    latest=build(rid,dotnet=dotnet)
    if output is None:return latest
    output=Path(output).resolve();output.mkdir(parents=True,exist_ok=True)
    archive=output/('MZTools-'+rid+('.zip' if rid.startswith(('win-','osx-')) else '.tar.gz'))
    if archive.exists():raise FileExistsError('Archive already exists: '+str(archive))
    work=ROOT/'obj/desktop'/rid
    product=work/'MZTools'
    remove_owned(product,ROOT/'obj/desktop')
    product.mkdir()
    publish=latest
    if rid.startswith('osx-'):
        app=product/'MZTools.app'/'Contents';(app/'MacOS').mkdir(parents=True);shutil.copytree(publish,app/'MacOS',dirs_exist_ok=True)
        with (app/'Info.plist').open('wb') as file:plistlib.dump({'CFBundleName':'MZTools','CFBundleDisplayName':'MZTools','CFBundleIdentifier':'cz.mztools.desktop','CFBundleExecutable':'MZTools.Desktop','CFBundlePackageType':'APPL','CFBundleShortVersionString':'0.9.9','CFBundleVersion':'0.9.9','NSHighResolutionCapable':True,'NSMicrophoneUsageDescription':'Audio input is used only when you explicitly choose Monitor or Record in MZTools.'},file)
        executable=app/'MacOS'/'MZTools.Desktop'
    else:
        shutil.copytree(publish,product,dirs_exist_ok=True);executable=product/('MZTools.Desktop.exe' if rid.startswith('win-') else 'MZTools.Desktop')
    if not executable.exists():raise RuntimeError('Publish did not produce the expected apphost')
    if not rid.startswith('win-'):executable.chmod(0o755)
    verify_apphost(executable,rid)
    hashes={p.relative_to(product).as_posix():hashlib.sha256(p.read_bytes()).hexdigest() for p in sorted(product.rglob('*')) if p.is_file()}
    (product/'SHA256SUMS.json').write_text(json.dumps(hashes,indent=2)+'\n',encoding='utf-8')
    archive=output/('MZTools-'+rid+('.zip' if rid.startswith(('win-','osx-')) else '.tar.gz'))
    if archive.exists():raise FileExistsError('Release already exists: '+str(archive))
    if archive.suffix=='.zip':
        with zipfile.ZipFile(archive,'x',zipfile.ZIP_DEFLATED) as file:
            for path in sorted(product.rglob('*')):
                if path.is_file():
                    info=zipfile.ZipInfo.from_file(path,path.relative_to(work).as_posix());info.compress_type=zipfile.ZIP_DEFLATED
                    if path==executable:info.external_attr=0o100755<<16
                    file.writestr(info,path.read_bytes())
    else:
        # Exclusive creation; retain execute permissions in the tar archive.
        def unix_permissions(info):
            if info.name in ('MZTools/MZTools.Desktop','MZTools/MZTools.Desktop.bin'):info.mode=0o755
            return info
        with archive.open('xb') as raw:
            with tarfile.open(fileobj=raw,mode='w:gz') as file:file.add(product,arcname='MZTools',filter=unix_permissions)
    result={'rid':rid,'archive':str(archive),'sha256':hashlib.sha256(archive.read_bytes()).hexdigest(),'files':len(hashes),'selfContained':True,'signed':False,'nativeHardwareTested':False,'staging':str(work)}
    archive.with_suffix(archive.suffix+'.json').write_text(json.dumps(result,indent=2)+'\n',encoding='utf-8');print(json.dumps(result))
    # Archive staging is outside bin; bin contains only the latest runnable product.
if __name__=='__main__':
    parser=argparse.ArgumentParser();parser.add_argument('--rid',choices=RIDS,required=True);parser.add_argument('--output',type=Path,default=None);parser.add_argument('--dotnet',default='dotnet');args=parser.parse_args();package(args.rid,args.output,args.dotnet)
