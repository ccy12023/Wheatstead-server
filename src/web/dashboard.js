'use strict';
let state = null;
let busy = false;
const byId = id => document.getElementById(id);
const time = seconds => new Date(seconds * 1000).toLocaleString('zh-CN', {timeZone:'Asia/Shanghai', hour12:false});
const duration = seconds => { const n = Math.floor(seconds); return [Math.floor(n/3600), Math.floor(n/60)%60, n%60].map(v=>String(v).padStart(2,'0')).join(':'); };
const actions = {disable:'禁用账号',enable:'恢复账号',delete_account:'删除账号',delete_score:'删除成绩',rebind:'生成重绑码'};
function cell(row,text,cls='') { const el=document.createElement('td');el.textContent=text;el.className=cls;row.append(el);return el; }
function button(parent,title,action,id) { const b=document.createElement('button');b.textContent=title;b.disabled=busy;if(action.startsWith('delete'))b.className='danger';b.onclick=()=>manage(action,id);parent.append(b); }
function empty(body,columns) { if(!body.children.length){const r=document.createElement('tr');const c=cell(r,'暂无记录');c.colSpan=columns;body.append(r);} }
function render() {
  if(!state)return;
  const query=byId('search').value.trim().toLocaleLowerCase();
  const accounts=state.accounts.filter(a=>a.name.toLocaleLowerCase().includes(query));
  const scores=state.scores.filter(a=>a.name.toLocaleLowerCase().includes(query));
  byId('overview').replaceChildren();
  for(const [key,title] of [['accounts','账号总数'],['scores','通关成绩'],['disabled','已禁用账号']]){const c=document.createElement('div');c.className='card';const t=document.createElement('span');t.textContent=title;const n=document.createElement('strong');n.textContent=state.overview[key];c.append(t,n);byId('overview').append(c);}
  const arows=byId('account-rows');arows.replaceChildren();
  for(const a of accounts){const r=document.createElement('tr');cell(r,a.name);cell(r,a.disabled?'已禁用':'正常');cell(r,a.scores);cell(r,time(a.created));const c=cell(r,'');button(c,a.disabled?'恢复':'禁用',a.disabled?'enable':'disable',a.id);if(!a.disabled)button(c,'设备重绑','rebind',a.id);button(c,'删除','delete_account',a.id);arows.append(r);}empty(arows,5);
  const srows=byId('score-rows');srows.replaceChildren();
  for(const s of scores){const r=document.createElement('tr');cell(r,s.name);cell(r,s.days);cell(r,duration(s.seconds));cell(r,`${s.hard?'困难':'简单'} / ${s.multiplayer?'多人':'单人'}`);cell(r,s.version);cell(r,time(s.submitted));button(cell(r,''),'删除','delete_score',s.id);srows.append(r);}empty(srows,7);
  const audit=byId('audit-rows');audit.replaceChildren();for(const a of state.audit){const r=document.createElement('tr');cell(r,actions[a.action]||a.action);cell(r,a.target,'id');cell(r,time(a.at));audit.append(r);}empty(audit,3);
}
async function request(url,options={}){const r=await fetch(url,{credentials:'same-origin',...options});if(r.status===401){location.href='/admin';throw Error('请重新登录');}if(!r.ok)throw Error(`操作未完成（HTTP ${r.status}），请刷新后重试。`);return r.json();}
async function refresh(){try{state=await request('/admin/data');render();byId('status').textContent='已更新 · '+new Date().toLocaleTimeString('zh-CN');byId('status').className='';}catch(e){byId('status').textContent=e.message;byId('status').className='error';}}
async function manage(action,id){if(busy)return;const extra=action==='delete_account'?'删除账号会同时删除其全部成绩，无法在后台撤销。':action==='rebind'?'重绑码有效期 15 分钟且仅能使用一次，成功绑定后原设备退出。':'公开榜将随操作立即更新。';if(!confirm(`${actions[action]}？\n${extra}`))return;busy=true;render();try{const result=await request('/admin/manage',{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-Token':state.csrf},body:JSON.stringify({action,id})});if(result.rebind_code){byId('code').hidden=false;byId('code').textContent=`一次性重绑码（15 分钟内有效）：${result.rebind_code}。请交给账号本人，在游戏登录处输入。`;}
  await refresh();}catch(e){byId('status').textContent=e.message;byId('status').className='error';}finally{busy=false;render();}}
for(const b of document.querySelectorAll('nav button'))b.onclick=()=>{for(const n of document.querySelectorAll('nav button'))n.classList.toggle('selected',n===b);for(const id of ['accounts','scores','audit'])byId(id).hidden=id!==b.dataset.view;};
byId('search').oninput=render;byId('refresh').onclick=refresh;byId('logout').onclick=async()=>{if(!state)return;await request('/admin/logout',{method:'POST',headers:{'X-CSRF-Token':state.csrf}});location.href='/admin';};
refresh();

byId("multiplayer").addEventListener("click", () => { location.href = "/admin/multiplayer"; });
