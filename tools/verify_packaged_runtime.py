"""Execute the actual extracted Windows/Linux x64 distribution, with no audio input."""
import json
from pathlib import Path
import subprocess
import tempfile
import zipfile

ROOT=Path(__file__).resolve().parents[1]
RESULTS=ROOT/'TestResults/Completion'
archive=RESULTS/'distribution/MZTools-win-x64.zip'
owned=Path(tempfile.mkdtemp(prefix='extracted-win-',dir=RESULTS))
with zipfile.ZipFile(archive) as file:file.extractall(owned)
result=subprocess.run([str(owned/'MZTools/MZTools.Desktop.exe'),'--validate-runtime'],capture_output=True,text=True,check=True)
json.loads(result.stdout)
(RESULTS/'windows/packaged-runtime.json').write_text(result.stdout,encoding='utf-8')
print(result.stdout)
archive=RESULTS/'distribution/MZTools-linux-x64.tar.gz'
wsl_path='/mnt/'+archive.drive[0].lower()+archive.as_posix()[2:]
script="""import json,pathlib,subprocess,sys,tarfile,tempfile
root=pathlib.Path(tempfile.mkdtemp(prefix='mztools-release-',dir='/tmp'))
with tarfile.open(sys.argv[1],'r:gz') as file:file.extractall(root,filter='data')
exe=root/'MZTools/MZTools.Desktop'
assert exe.stat().st_mode & 0o111
result=subprocess.run([str(exe),'--validate-runtime'],capture_output=True,text=True,check=True)
json.loads(result.stdout)
print(result.stdout,end='')
"""
result=subprocess.run(['wsl','-d','Debian','--exec','python3','-c',script,wsl_path],capture_output=True,text=True,check=True)
json.loads(result.stdout)
(RESULTS/'linux/packaged-runtime.json').write_text(result.stdout,encoding='utf-8')
print(result.stdout)
