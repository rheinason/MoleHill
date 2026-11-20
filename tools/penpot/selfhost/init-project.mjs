import fs from 'node:fs';
import path from 'node:path';
import { randomBytes, createHash } from 'node:crypto';
import { spawnSync } from 'node:child_process';
import transit from 'transit-js';

const directory=import.meta.dirname;
const base='http://localhost:9001';
const credentialsPath=path.join(directory,'credentials.local.json');
const statePath=path.join(directory,'project.local.json');
const archivePath=path.resolve(directory,'../output/MoleHill-UI.penpot');
const refresh=process.argv.includes('--refresh');
const archiveHash=createHash('sha256').update(fs.readFileSync(archivePath)).digest('hex');
const compose=['compose','--project-name','molehill-penpot','--project-directory',directory,'-f',path.join(directory,'compose.yaml')];

function plain(v){
    if(v instanceof Date)return v.toISOString();
    if(transit.isKeyword(v)||transit.isUUID(v))return v.toString().replace(/^:/,'');
    if(transit.isMap(v)){const out={};v.forEach((value,key)=>out[plain(key)]=plain(value));return out;}
    if(transit.isSet(v))return [...v.values()].map(plain);
    if(Array.isArray(v))return v.map(plain);
    if(v&&typeof v==='object')return Object.fromEntries(Object.entries(v).map(([k,val])=>[k,plain(val)]));
    return v;
}
function decode(s){
    const raw=JSON.parse(s);
    return /"(?:~[:u#]|\^ )/.test(s)?plain(transit.reader('json').read(s)):raw;
}
function parseEvents(result){
    if(typeof result!=='string')return [{type:'end',data:result}];
    return result.split(/\r?\n\r?\n/).filter(s=>s.includes('data:')).map(block=>{
        const lines=block.split(/\r?\n/),type=lines.find(s=>s.startsWith('event:'))?.slice(6).trim()??'message';
        return {type,data:decode(lines.filter(s=>s.startsWith('data:')).map(s=>s.slice(5).trim()).join('\n'))};
    });
}
let cookie='';
async function rpc(command,body={},multipart=false){
    const headers={Accept:'application/json',Origin:base,Referer:base+'/'};
    if(cookie)headers.Cookie=cookie;
    if(!multipart)headers['Content-Type']='application/json';
    const result=await fetch(base+'/api/rpc/command/'+command,{method:'POST',headers,body:multipart?body:JSON.stringify(body),signal:AbortSignal.timeout(command==='import-binfile'?600000:30000)});
    const cookies=result.headers.getSetCookie();
    if(cookies.length)cookie=cookies.map(s=>s.split(';')[0]).join('; ');
    const content=await result.text();
    if(!result.ok)throw Error(command+' failed ('+result.status+'): '+content.slice(0,1600));
    if(result.headers.get('content-type')?.includes('text/event-stream'))return content;
    return content?decode(content):null;
}
function docker(args,input){
    const result=spawnSync('docker',[...compose,...args],{encoding:'utf8',input,timeout:60000,windowsHide:true});
    if(result.error)throw result.error;
    if(result.status!==0)throw Error('Penpot account setup failed: '+(result.stderr||result.stdout).slice(0,1800));
    return result.stdout;
}
async function verify(state){
    const stats=await rpc('get-file-stats',{id:state.fileIds[0]});
    const visualVerified=!!state.nativeVisualVerified&&state.nativeStats?.revn===stats.revn;
    const value=key=>stats[key]??stats[key.replace(/-([a-z])/g,(_,c)=>c.toUpperCase())];
    const expected=JSON.parse(fs.readFileSync(path.resolve(directory,'../output/validation.json'),'utf8'));
    if(value('page-count')!==expected.pages||value('component-count')!==expected.components||value('color-count')!==expected.libraryColors)throw Error('Native imported statistics do not match the generated reference.');
    state.nativeStats=stats;state.nativeImportVerified=true;state.nativeVisualVerified=visualVerified;
    fs.writeFileSync(statePath,JSON.stringify(state,null,2));
    fs.writeFileSync(path.resolve(directory,'../output/validation.json'),JSON.stringify({...expected,nativeImportVerified:true,nativeVisualVerified:visualVerified,nativeStats:stats},null,2));
    console.log('Verified native import: '+value('page-count')+' pages, '+value('component-count')+' components, '+value('color-count')+' library colours.');
    console.log('MoleHill project: '+state.projectUrl);
}

// Never reset an existing profile's password. Resume using the saved local credentials.
let credentials;
if(fs.existsSync(credentialsPath))credentials=JSON.parse(fs.readFileSync(credentialsPath,'utf8'));
else{
    credentials={url:base,email:'owner@molehill.local',password:randomBytes(24).toString('base64url')};
    fs.writeFileSync(credentialsPath,JSON.stringify(credentials,null,2),{flag:'wx'});
    const script="import json,sys,subprocess; p=json.load(sys.stdin); subprocess.run(['python3','manage.py','create-profile','--fullname','MoleHill Owner','--email',p['email'],'--password',p['password']],check=True)";
    docker(['exec','-T','penpot-backend','python3','-c',script],JSON.stringify(credentials));
    console.log('Created local owner account. Credentials saved in the ignored credentials.local.json.');
}
await rpc('login-with-password',{email:credentials.email,password:credentials.password});
const teams=await rpc('get-teams');
const team=teams.find(t=>t['is-default']||t.isDefault)??teams[0];
if(!team?.id)throw Error('No team available after login.');
const projects=await rpc('get-projects',{'team-id':team.id});
let project=projects.find(p=>p.name==='MoleHill'&&!p['deleted-at']);
if(!project)project=await rpc('create-project',{'team-id':team.id,name:'MoleHill'});
const projectUrl=base+'/#/dashboard?team-id='+team.id+'&project-id='+project.id;
const prior=fs.existsSync(statePath)?JSON.parse(fs.readFileSync(statePath,'utf8')):null;
if(prior?.importComplete&&prior.projectId===project.id&&(!refresh||prior.archiveHash===archiveHash)){await verify(prior);process.exit(0);}
const logPath=path.join(directory,'import.local.log');
// Recover a completed stream from an interrupted local setup without importing a duplicate.
const priorStream=fs.existsSync(logPath)?parseEvents(fs.readFileSync(logPath,'utf8')):[];
const priorEnd=refresh?null:priorStream.findLast(e=>e.type==='end');
if(priorEnd){
    const fileIds=priorEnd.data;
    await verify({projectId:project.id,teamId:team.id,projectUrl,importComplete:true,importedAt:new Date().toISOString(),fileIds});
    process.exit(0);
}
const form=new FormData();
form.set('project-id',project.id);form.set('name',refresh?'MoleHill • Rhino UI reference — revised':'MoleHill • Rhino UI reference');
form.set('file',new Blob([fs.readFileSync(archivePath)],{type:'application/octet-stream'}),'MoleHill-UI.penpot');
const result=await rpc('import-binfile',form,true);
fs.writeFileSync(path.join(directory,'import.local.log'),typeof result==='string'?result:JSON.stringify(result,null,2));
const events=parseEvents(result);
const errors=events.filter(e=>e.type==='error'||e.data?.error);
if(errors.length)throw Error('Penpot import reported an error; inspect import.local.log.');
const final=events.findLast(e=>e.type==='end');
if(!final)throw Error('Import stream has no completion event; inspect import.local.log.');
const state={projectId:project.id,teamId:team.id,projectUrl,importComplete:true,importedAt:new Date().toISOString(),fileIds:final.data,archiveHash,previousFileIds:[...new Set([...(prior?.previousFileIds??[]),...(prior?.fileIds??[])])].filter(id=>!final.data.includes(id))};
fs.writeFileSync(statePath,JSON.stringify(state,null,2));
await verify(state);
