/* Opt-in browser checks for this F2 review, never production application tests. */
(async function(){
  if(!new URLSearchParams(location.search).has('check'))return;
  const results=[];const check=(name,ok,detail='')=>results.push({name,pass:Boolean(ok),detail});
  const tick=()=>new Promise(resolve=>setTimeout(resolve,20));
  const {model,setView,perform,zoom}=window.F2Review;
  check('Opening review does not start demo input',model.recorder==='idle');
  for(const view of ['tape','qd','disk','audio','recorder']){
    setView(view);
    check(view+': shared command bar',Boolean(document.querySelector('.commandbar [data-action="save-as"]')));
    check(view+': no page-wide horizontal overflow',document.documentElement.scrollWidth<=innerWidth+1,`width=${innerWidth}, scrollWidth=${document.documentElement.scrollWidth}`);
    check(view+': one active workspace',document.querySelectorAll('.nav-item[aria-current="page"]').length===1);
    const unnamed=[...document.querySelectorAll('button')].filter(b=>!b.textContent.trim()&&!b.getAttribute('aria-label'));
    check(view+': named buttons',unnamed.length===0);
  }
  setView('tape');document.querySelector('[data-row="2"]').click();setView('qd');setView('tape');
  check('Selection and inspector survive module switch',model.selected.tape===2&&document.querySelector('.detail-name').textContent.includes('PART 3'));
  const filter=document.querySelector('[data-filter]');filter.value='PART 6';filter.dispatchEvent(new Event('input'));
  check('Filter keeps source rows and narrows display',document.querySelectorAll('[data-row]:not([hidden])').length===1&&document.querySelectorAll('[data-row]').length===6);
  setView('audio');zoom(1.5);const before=model.zooms.audio;setView('disk');setView('audio');
  check('Zoom survives module switch',model.zooms.audio===before&&document.querySelector('[data-graph] g[clip-path]>g').getAttribute('transform').includes('scale(1.5'));
  check('Audio Save disabled',document.querySelector('[data-action="save"]').disabled);
  check('Audio Save As disabled',document.querySelector('[data-action="save-as"]').disabled);
  setView('disk');document.querySelector('[data-sector="12:6"]').click();check('Disk map selection updates sector inspector',document.querySelector('.detail-name').textContent.includes('C12')&&model.selected.disk===3);check('Disk map has one keyboard entry point',document.querySelectorAll('[data-sector][tabindex="0"]').length===1);perform('repair');const dialog=document.getElementById('review-dialog');dialog.close('cancel');await tick();
  check('Cancel repair leaves document unchanged',!model.repair&&!model.modified.disk);
  perform('repair');dialog.close('apply');await tick();
  check('Apply repair marks working copy only',model.repair&&model.modified.disk&&!model.names.disk);
  check('Repaired original cannot use Save',document.querySelector('[data-action="save"]').disabled);
  perform('save-as');document.getElementById('review-filename').value='CPM41-work.dsk';dialog.close('save');await tick();
  check('Save As rejects original repair name',!model.names.disk&&model.modified.disk);
  perform('save-as');document.getElementById('review-filename').value='CPM41-repaired.dsk';dialog.close('save');await tick();
  check('Save As binds only confirmed copy',model.names.disk==='CPM41-repaired.dsk'&&!model.modified.disk);
  setView('tape');check('Disk repair does not disable other document Save',!document.querySelector('[data-action="save"]').disabled);
  perform('save-as');dialog.close('cancel');await tick();check('Cancel Save As keeps tape name and modified state',!model.names.tape&&model.modified.tape);
  setView('recorder');const stop=document.querySelector('[data-action="stop"]').getBoundingClientRect();check('Stop is reachable without scrolling',stop.bottom<=innerHeight&&stop.right<=innerWidth);
  perform('monitor');check('Monitor is explicit and only simulated',model.recorder==='monitoring'&&document.querySelector('[data-recorder-setting]').disabled);
  setView('tape');check('Changing workspace retains active demo session',model.recorder==='monitoring');
  setView('recorder');perform('record');perform('close-policy');dialog.close('keep');await tick();check('Declined close retains demo session',model.recorder==='recording');
  perform('stop');check('Stop returns input settings',model.recorder==='idle'&&!document.querySelector('[data-recorder-setting]').disabled);
  check('A/B unqualified by default',!model.samePassage);const box=document.getElementById('same-passage');const handler=Boolean(box.onchange);box.click();await tick();check('A/B needs explicit passage confirmation',model.samePassage,JSON.stringify({oldConnected:box.isConnected,oldChecked:box.checked,newChecked:document.getElementById('same-passage').checked,handler,model:model.samePassage,dialogOpen:dialog.open}));
  const summary={scope:'F2 local HTML prototype only',viewport:{width:innerWidth,height:innerHeight,dpr:devicePixelRatio},passed:results.filter(r=>r.pass).length,failed:results.filter(r=>!r.pass),results};
  document.getElementById('review-checks').textContent=JSON.stringify(summary);document.documentElement.dataset.reviewResult=summary.failed.length?'FAIL':'PASS';
})().catch(error=>{document.getElementById('review-checks').textContent=JSON.stringify({fatal:String(error)});document.documentElement.dataset.reviewResult='ERROR';});
