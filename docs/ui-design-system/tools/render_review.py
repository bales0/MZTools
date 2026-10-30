"""Render/check the offline F2 prototype through local Chromium DevTools.
Python standard library only. Uses the isolated browser started by render_review.ps1.
"""
from pathlib import Path
import argparse, base64, json, os, socket, struct, time, urllib.request
ROOT=Path(__file__).resolve().parents[1]; REPO=ROOT.parents[1]
VIEWS=[('tape','01-tape'),('qd','02-quickdisk'),('disk','03-disk-map'),('audio','04-audio-analyzer'),('recorder','05-recorder')]
class CDP:
    def __init__(self,url):
        from urllib.parse import urlsplit
        u=urlsplit(url);self.sock=socket.create_connection((u.hostname,u.port),timeout=15);self.n=0;self.events=[]
        key=base64.b64encode(os.urandom(16)).decode();request=f'GET {u.path} HTTP/1.1\r\nHost: {u.hostname}:{u.port}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: {key}\r\nSec-WebSocket-Version: 13\r\n\r\n';self.sock.sendall(request.encode())
        header=b''
        while not header.endswith(b'\r\n\r\n'):header+=self.sock.recv(1)
        if b'101' not in header.split(b'\r\n')[0]:raise RuntimeError(header.decode())
    def read(self,n):
        result=b''
        while len(result)<n:
            chunk=self.sock.recv(n-len(result))
            if not chunk:raise EOFError('DevTools socket closed')
            result+=chunk
        return result
    def send(self,value):
        data=json.dumps(value).encode();n=len(data);mask=os.urandom(4);header=bytes([0x81,0x80|n]) if n<126 else bytes([0x81,0xfe])+struct.pack('!H',n) if n<65536 else bytes([0x81,0xff])+struct.pack('!Q',n)
        self.sock.sendall(header+mask+bytes(b^mask[i%4] for i,b in enumerate(data)))
    def receive(self):
        data=b''
        while True:
            a,b=self.read(2);op=a&15;n=b&127
            if n==126:n=struct.unpack('!H',self.read(2))[0]
            elif n==127:n=struct.unpack('!Q',self.read(8))[0]
            mask=self.read(4) if b&128 else None;part=self.read(n)
            if mask:part=bytes(x^mask[i%4] for i,x in enumerate(part))
            if op==8:raise EOFError('DevTools closed')
            if op==9:continue
            data+=part
            if a&128:return json.loads(data)
    def call(self,method,params=None):
        self.n+=1;self.send({'id':self.n,'method':method,'params':params or {}})
        while True:
            msg=self.receive()
            if msg.get('id')==self.n:
                if 'error' in msg:raise RuntimeError(msg['error'])
                return msg.get('result',{})
            self.events.append(msg)
    def evaluate(self,expression):
        r=self.call('Runtime.evaluate',{'expression':expression,'returnByValue':True,'awaitPromise':True})
        if 'exceptionDetails' in r:raise RuntimeError(r['exceptionDetails'])
        return r.get('result',{}).get('value')
    def wait(self,expression,timeout=15):
        deadline=time.monotonic()+timeout
        while time.monotonic()<deadline:
            if self.evaluate(expression):return
            time.sleep(.05)
        raise RuntimeError('Browser expression timed out: '+expression+'; '+str(self.events[-8:]))
def main():
    p=argparse.ArgumentParser();p.add_argument('--port',type=int,required=True);p.add_argument('--checks-only',action='store_true');p.add_argument('--screens-only',action='store_true');args=p.parse_args()
    out=REPO/'TestResults/F2';out.mkdir(parents=True,exist_ok=True)
    cdp=None
    try:
        port=args.port
        targets=json.load(urllib.request.urlopen(f'http://127.0.0.1:{port}/json/list',timeout=3));page=next(t for t in targets if t['type']=='page');cdp=CDP(page['webSocketDebuggerUrl']);cdp.call('Page.enable');cdp.call('Runtime.enable');cdp.call('Network.enable');cdp.call('Network.setCacheDisabled',{'cacheDisabled':True})
        base=(ROOT/'prototype/index.html').as_uri();screens=[];reports=[]
        if not args.checks_only:
            for view,name in VIEWS:
                cdp.call('Emulation.setDeviceMetricsOverride',{'width':1440,'height':960,'deviceScaleFactor':1,'mobile':False});cdp.evaluate('window.F2Review=undefined');cdp.call('Page.navigate',{'url':base+'?view='+view});cdp.wait('document.readyState==="complete" && Boolean(window.F2Review)');time.sleep(.1)
                data=base64.b64decode(cdp.call('Page.captureScreenshot',{'format':'png','captureBeyondViewport':False})['data']);target=ROOT/'previews'/f'{name}.png';target.write_bytes(data);w,h=struct.unpack('>II',data[16:24]);screens.append({'view':view,'file':str(target.relative_to(ROOT)),'width':w,'height':h});print(f'Rendered {name} ({w}x{h})',flush=True)
            (out/'screenshots.json').write_text(json.dumps(screens,indent=2)+'\n',encoding='utf-8')
        if not args.screens_only:
            for w,h,dpr in [(1440,960,1),(1366,768,1),(1920,1080,1),(920,640,1),(1366,768,1.5),(1366,768,2)]:
                cdp.call('Emulation.setDeviceMetricsOverride',{'width':w,'height':h,'deviceScaleFactor':dpr,'mobile':False});cdp.evaluate('delete document.documentElement.dataset.reviewResult');cdp.call('Page.navigate',{'url':base+'?check=1'});cdp.wait('Boolean(document.documentElement.dataset.reviewResult)');report=json.loads(cdp.evaluate('document.getElementById("review-checks").textContent'));reports.append(report);(out/'browser-checks.json').write_text(json.dumps(reports,ensure_ascii=False,indent=2)+'\n',encoding='utf-8');print('Checked '+str(report.get('viewport'))+': '+str(report.get('passed'))+' passed, '+str(len(report.get('failed',[])))+' failed',flush=True)
            if any(r.get('failed') or r.get('fatal') for r in reports):raise SystemExit('Prototype checks failed; see TestResults/F2/browser-checks.json')
    finally:
        if cdp:
            try:cdp.call('Browser.close')
            except (EOFError,OSError):pass
            cdp.sock.close()
if __name__=='__main__':main()
