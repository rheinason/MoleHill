// Create an isolated design experiment from the verified baseline archive.
// Never replace the baseline state or overwrite an existing design draft.
import fs from 'node:fs';
import path from 'node:path';
import {createHash} from 'node:crypto';
import transit from 'transit-js';
const here=import.meta.dirname,base='http://localhost:9001';
const baseline=JSON.parse(fs.readFileSync(path.join(here,'project.local.json'),'utf8'));
const credentials=JSON.parse(fs.readFileSync(path.join(here,'credentials.local.json'),'utf8'));
const draftPath=path.join(here,'design-draft.local.json');
const archive=fs.readFileSync(path.resolve(here,'../output/MoleHill-UI.penpot'));
const hash=createHash('sha256').update(archive).digest('hex');
let cookie='';
function plain(v){
    if(transit.isKeyword(v)||transit.isUUID(v))return v.toString().replace(/^:/,'');
    if(transit.isMap(v)){const result={};v.forEach((value,key)=>result[plain(key)]=plain(value));return result;}
    if(Array.isArray(v))return v.map(plain);
    if(v&&typeof v==='object')return Object.fromEntries(Object.entries(v).map(([key,value])=>[key,plain(value)]));
    return v;
}
const decode=s=>/"(?:~[:u#]|\^ )/.test(s)?plain(transit.reader('json').read(s)):JSON.parse(s);
async function rpc(command,body={},multipart=false){
    const response=await fetch(base+'/api/rpc/command/'+command,{method:'POST',headers:{Accept:'application/json',Origin:base,Referer:base+'/',Cookie:cookie,...(multipart?{}:{'Content-Type':'application/json'})},body:multipart?body:JSON.stringify(body),signal:AbortSignal.timeout(180000)});
    const cookies=response.headers.getSetCookie();if(cookies.length)cookie=cookies.map(s=>s.split(';')[0]).join('; ');
    const content=await response.text();if(!response.ok)throw Error(command+' failed ('+response.status+'): '+content.slice(0,500));
    return response.headers.get('content-type')?.includes('text/event-stream')?content:decode(content);
}
await rpc('login-with-password',{email:credentials.email,password:credentials.password});
if(fs.existsSync(draftPath)){
    const existing=JSON.parse(fs.readFileSync(draftPath,'utf8'));
    await rpc('get-file-stats',{id:existing.fileId});
    console.log('Existing independent design draft retained: '+existing.workspaceUrl);
    process.exit(0);
}
const sourceId=baseline.fileIds[0],before=await rpc('get-file-stats',{id:sourceId});
if(hash!==baseline.archiveHash||before.revn!==baseline.nativeStats.revn)throw Error('The saved archive no longer matches the baseline. Export the current baseline before creating a draft.');
const name='MoleHill • UI design experiments';
const form=new FormData();form.set('project-id',baseline.projectId);form.set('name',name);
form.set('file',new Blob([archive],{type:'application/octet-stream'}),'MoleHill-UI.penpot');
const result=await rpc('import-binfile',form,true);
fs.writeFileSync(path.join(here,'design-draft-import.local.log'),typeof result==='string'?result:JSON.stringify(result));
const events=typeof result==='string'?result.split(/\r?\n\r?\n/).filter(s=>s.includes('data:')).map(block=>{
    const lines=block.split(/\r?\n/);return {type:lines.find(s=>s.startsWith('event:'))?.slice(6).trim(),data:decode(lines.filter(s=>s.startsWith('data:')).map(s=>s.slice(5).trim()).join('\n'))};
}):[{type:'end',data:result}];
if(events.some(e=>e.type==='error'||e.data?.error))throw Error('Draft import failed; inspect design-draft-import.local.log.');
const id=events.findLast(e=>e.type==='end')?.data?.[0];
if(!id||id===sourceId)throw Error('Import did not return a separate file.');
const state={name,fileId:id,sourceFileId:sourceId,sourceRevision:before.revn,sourceArchiveHash:hash,projectId:baseline.projectId,teamId:baseline.teamId,createdAt:new Date().toISOString()};
// Save immediately so an interrupted verification cannot cause a second import.
fs.writeFileSync(draftPath,JSON.stringify(state,null,2));
const stats=await rpc('get-file-stats',{id});
for(const key of ['pageCount','componentCount','colorCount','typographyCount'])if(stats[key]!==before[key])throw Error('Draft coverage differs: '+key);
const after=await rpc('get-file-stats',{id:sourceId});
if(after.revn!==before.revn||after.updatedAt!==before.updatedAt)throw Error('Baseline changed during draft creation.');
const file=await rpc('get-file',{id,features:['fdata/path-data','fdata/shape-data-type','design-tokens/v1','variants/v1','layout/grid','components/v2','styles/v2','plugins/runtime']});
const pages=file.data.pagesIndex;
const pageId=Object.values(pages).find(p=>p.name==='05 • Modifiers screens')?.id??file.data.pages[0];
Object.assign(state,{nativeStats:stats,baselineUnchanged:true,workspaceUrl:base+'/#/workspace?team-id='+baseline.teamId+'&file-id='+id+'&page-id='+pageId});
fs.writeFileSync(draftPath,JSON.stringify(state,null,2));
console.log('Created independent design draft; baseline revision unchanged.');
console.log(JSON.stringify({pages:stats.pageCount,components:stats.componentCount,typographyStyles:stats.typographyCount}));
console.log(state.workspaceUrl);
