param([string]$Browser='C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe',[switch]$ChecksOnly,[switch]$ScreensOnly)
$ErrorActionPreference='Stop'
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start(); $port=([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
$profile=Join-Path ([IO.Path]::GetTempPath()) ('MZTools-F2-Review-'+[Guid]::NewGuid().ToString('N'))
$arguments=@('--headless=new','--disable-gpu','--disable-extensions','--disable-background-networking','--disable-component-update','--no-first-run','--no-default-browser-check','--allow-file-access-from-files','--remote-debugging-address=127.0.0.1',"--remote-debugging-port=$port","--user-data-dir=$profile",'about:blank')
$null=Start-Process -FilePath $Browser -ArgumentList $arguments -WindowStyle Hidden -PassThru
$deadline=[DateTime]::UtcNow.AddSeconds(15);$ready=$false
while([DateTime]::UtcNow -lt $deadline){try{$null=Invoke-RestMethod -Uri "http://127.0.0.1:$port/json/version" -TimeoutSec 1;$ready=$true;break}catch{Start-Sleep -Milliseconds 100}}
if(-not $ready){throw 'Isolated browser DevTools did not start.'}
$script=Join-Path $PSScriptRoot 'render_review.py'
if($ChecksOnly){py -3 $script --port $port --checks-only}
elseif($ScreensOnly){py -3 $script --port $port --screens-only}
else{py -3 $script --port $port}
if($LASTEXITCODE -ne 0){throw 'F2 renderer/checks failed; inspect TestResults/F2.'}
