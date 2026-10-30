"""Validate F2 token contrast, local links, screenshots and unchanged UI evidence."""
from pathlib import Path
from urllib.parse import unquote,urlsplit
import csv, hashlib, json, re, struct
ROOT=Path(__file__).resolve().parents[1];REPO=ROOT.parents[1]
def luminance(h):
    rgb=[int(h[i:i+2],16)/255 for i in (1,3,5)];linear=[v/12.92 if v<=.04045 else ((v+.055)/1.055)**2.4 for v in rgb]
    return sum(a*b for a,b in zip(linear,(.2126,.7152,.0722)))
def contrast(a,b):
    light,dark=sorted((luminance(a),luminance(b)),reverse=True);return (light+.05)/(dark+.05)
def main():
    tokens=json.loads((ROOT/'tokens.json').read_text(encoding='utf-8'));c=tokens['colors'];results=[]
    def check(name,ok,detail=''):results.append({'name':name,'pass':bool(ok),'detail':detail})
    pairs=[('text','surface',4.5),('text','canvas',4.5),('muted','surface',4.5),('muted','subtle',4.5),('on-accent','accent',4.5),('accent','accent-soft',4.5),('healthy','healthy-bg',4.5),('warning','warning-bg',4.5),('error','error-bg',4.5),('readonly','readonly-bg',4.5),('text','selected',4.5),('control-border','surface',3),('focus','surface',3),('wave-l','surface',3),('wave-r','surface',3),('pulse-sh','surface',3),('pulse-sl','surface',3),('pulse-lh','surface',3),('pulse-ll','surface',3),('disk-file','surface',3),('disk-system','surface',3),('disk-directory','surface',3),('disk-free-border','disk-free',3)]
    for foreground,background,minimum in pairs:
        ratio=contrast(c[foreground],c[background]);check(f'Contrast {foreground}/{background} >= {minimum}:1',ratio>=minimum,f'{ratio:.3f}:1')
    css=(ROOT/'prototype/tokens.css').read_text(encoding='utf-8');check('Generated CSS matches all palette tokens',all(f'--{name}: {value};' in css for name,value in c.items()))
    for p in [ROOT/'prototype/design.css',ROOT/'prototype/prototype.js']:
        check(p.name+': no scattered hex palette',not re.search(r'#[0-9a-fA-F]{6,8}\b',p.read_text(encoding='utf-8')))
    for p in sorted(ROOT.rglob('*.md'))+sorted(ROOT.rglob('*.html')):
        s=p.read_text(encoding='utf-8-sig');links=re.findall(r'\[[^\]]*\]\(([^)]+)\)',s) if p.suffix=='.md' else re.findall(r'(?:href|src)="([^"]+)"',s)
        for ref in links:
            url=urlsplit(ref)
            if url.scheme or ref.startswith('#'):continue
            target=(p.parent/unquote(url.path)).resolve();check(f'Local link {p.name}: {ref}',target.exists())
    for p in (ROOT/'prototype').glob('*.html'):
        check(p.name+': no remote asset requirement',not re.search(r'(?:src|href)="https?://',p.read_text(encoding='utf-8')))
    for p in ROOT.glob('tools/*.py'):compile(p.read_text(encoding='utf-8-sig'),str(p),'exec')
    check('Python helper syntax',True)
    for p in sorted((ROOT/'previews').glob('*.png')):
        b=p.read_bytes();w,h=struct.unpack('>II',b[16:24]);check(p.name+': valid reference PNG',b.startswith(bytes.fromhex('89504e470d0a1a0a')) and b[-8:]==bytes.fromhex('49454e44ae426082') and (w,h)==(1440,960),f'{w}x{h}')
    check('Five reference compositions',len(list((ROOT/'previews').glob('*.png')))==5)
    inventory=json.loads((ROOT/'source-inventory.json').read_text(encoding='utf-8'));check('Every audited UI source has a target family',not inventory['unmapped'])
    for source in inventory['sources']:check('Unchanged UI source: '+source['source'],hashlib.sha256((REPO/source['source']).read_bytes()).hexdigest()==source['sha256'])
    for name,key in [('wpf-controls.csv','controlRows'),('wpf-inputs.csv','inputRows')]:
        with (ROOT/name).open(encoding='utf-8-sig',newline='') as f: count=sum(1 for _ in csv.DictReader(f))
        check(name+': complete generated evidence rows',count==inventory[key],str(count))
    summary={'scope':'F2 static design checks, not application/OS/hardware certification','passed':sum(r['pass'] for r in results),'failed':[r for r in results if not r['pass']],'results':results}
    target=REPO/'TestResults/F2/static-checks.json';target.parent.mkdir(parents=True,exist_ok=True);target.write_text(json.dumps(summary,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(f"Static design checks: {summary['passed']} passed; {len(summary['failed'])} failed")
    for failed in summary['failed']:print(failed)
    if summary['failed']:raise SystemExit(1)
if __name__=='__main__':main()
