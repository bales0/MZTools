"""Targeted picker regressions. FULL is explicit; FAST never copies or scans the media corpus."""
import argparse
import json
import os
import datetime
from pathlib import Path
import platform
import subprocess
import time

ROOT=Path(__file__).resolve().parents[1]
DESKTOP='tests/MZTools.Desktop.Tests/MZTools.Desktop.Tests.csproj'
CORE='tests/MZTools.Core.Tests/MZTools.Core.Tests.csproj'
CLASSES=['DeepAnalysisInputTests','AudioDocumentTests','SelectedAnalysisWindowTests','ReportedUiRegressionTests','Phase5WorkflowTests','FilePickerPolicyTests','OpenImportPickerTests','MediaDocumentTests','DiskDocumentTests','BatchParityTests']
EXCLUDED=['MZTools.Desktop.Tests.DiskDocumentTests.HfeMetadataEditKeepsOriginalAndCorePhysicalSerialization']
CORE_FILTER='FullyQualifiedName~DskPatchTests|FullyQualifiedName~BootstrapRawRangeTests'

def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--mode',choices=['fast','full'],default='fast')
    parser.add_argument('--core',action='store_true',help='Also run Core: targeted synthetic tests in FAST; complete Core in FULL')
    args=parser.parse_args()
    host=platform.system().lower()
    artifacts=ROOT/'obj/test-runs'/host/args.mode
    output=ROOT/'TestResults/PickerPolicy'/host/args.mode
    output.mkdir(parents=True,exist_ok=True)
    metadata={'head':subprocess.check_output(['git','rev-parse','HEAD'],cwd=ROOT,text=True).strip(),
              'sdk':subprocess.check_output(['dotnet','--version'],cwd=ROOT,text=True).strip(),
              'os':platform.platform(),'startedUtc':datetime.datetime.now(datetime.timezone.utc).isoformat(),
              'modifiedFiles':subprocess.check_output(['git','-c','core.autocrlf=true','-c','core.filemode=false','status','--short'],cwd=ROOT,text=True).splitlines(),
              'statusNormalization':'autocrlf=true, filemode=false (shared Windows/WSL checkout)'}
    environment=dict(os.environ,MZTOOLS_PHASE5_EVIDENCE=str(output/'fixtures'))
    results=[]
    def run(label,command):
        start=time.perf_counter()
        result=subprocess.run(command,cwd=ROOT,env=environment)
        results.append({'step':label,'seconds':round(time.perf_counter()-start,3),'exitCode':result.returncode,'command':command})
        (output/'timings.json').write_text(json.dumps(results,indent=2)+'\n',encoding='utf-8')
        if result.returncode:raise SystemExit(result.returncode)
    for project in [DESKTOP]+([CORE] if args.core else []):
        run('build '+project,['dotnet','build',project,'-m:1','-nr:false','--artifacts-path',str(artifacts),'-p:MZToolsPublishLatest=false','-p:MZToolsFastTests='+str(args.mode=='fast').lower()])
    # xUnit v3's in-process runner avoids the VSTest IPC dependency. Build just ran:
    # no --no-build shortcut can conceal a stale assembly.
    command=['dotnet',str(artifacts/'bin/MZTools.Desktop.Tests/debug/MZTools.Desktop.Tests.dll'),'-noLogo','-trx',str(output/'desktop.trx')]
    if args.mode=='fast':
        for name in CLASSES:command+=['-class','MZTools.Desktop.Tests.'+name]
        for name in EXCLUDED:command+=['-method-',name]
    run('desktop '+args.mode,command)
    if args.core:
        command=['dotnet','test',CORE,'--no-build','--no-restore','--artifacts-path',str(artifacts),'-p:MZToolsPublishLatest=false','-p:MZToolsFastTests='+str(args.mode=='fast').lower(),'--logger','trx','--results-directory',str(output/'core')]
        if args.mode=='fast':command+=['--filter',CORE_FILTER]
        run('core '+args.mode,command)
    # Inventory only this runner's output, never user folders or the source corpus.
    files=[p for folder in ['DSK','QD','MZF','ROM','LEP','WAV','ReferenceGolden','QDReference'] for p in (artifacts/'bin/MZTools.Core.Tests/debug'/folder).rglob('*') if p.is_file()]
    report={'metadata':metadata,'mode':args.mode,'coreRequested':args.core,'explicitExcludedMethods':EXCLUDED if args.mode=='fast' else [],'copiedCorpusFiles':len(files),'copiedCorpusBytes':sum(p.stat().st_size for p in files),'steps':results}
    (output/'summary.json').write_text(json.dumps(report,indent=2)+'\n',encoding='utf-8')
    if args.mode=='fast' and files:raise RuntimeError('FAST output unexpectedly contains a copied media corpus; use a clean FAST output directory.')
    print(json.dumps(report,indent=2))

if __name__=='__main__':main()
