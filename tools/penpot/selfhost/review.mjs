// Read-only native-file inspection and Penpot's normal PNG export API.
import fs from 'node:fs';
import path from 'node:path';
import transit from 'transit-js';
const here=import.meta.dirname,base='http://localhost:9001';
const out=path.resolve(here,'../../../.artifacts/penpot-selfhost/review');fs.mkdirSync(out,{recursive:true});
const credentials=JSON.parse(fs.readFileSync(path.join(here,'credentials.local.json'),'utf8'));
const state=JSON.parse(fs.readFileSync(path.join(here,'project.local.json'),'utf8'));
function plain(v){if(v instanceof Date)return v.toISOString();if(transit.isKeyword(v)||transit.isUUID(v))return v.toString().replace(/^:/,'');if(transit.isMap(v)){const o={};v.forEach((v,k)=>o[plain(k)]=plain(v));return o;}if(transit.isSet(v))return [...v.values()].map(plain);if(Array.isArray(v))return v.map(plain);if(v&&typeof v==='object')return Object.fromEntries(Object.entries(v).map(([k,v])=>[k,plain(v)]));return v;}
const decode=s=>/"(?:~[:u#]|\^ )/.test(s)?plain(transit.reader('json').read(s)):JSON.parse(s);
let cookie='';
async function request(route,body,isTransit=false){
    const r=await fetch(base+route,{method:'POST',headers:{Accept:'application/json',Origin:base,Referer:base+'/',Cookie:cookie,'Content-Type':isTransit?'application/transit+json':'application/json'},body:isTransit?transit.writer('json').write(body):JSON.stringify(body),signal:AbortSignal.timeout(180000)});
    const set=r.headers.getSetCookie();if(set.length)cookie=set.map(s=>s.split(';')[0]).join('; ');
    const s=await r.text();if(!r.ok)throw Error(route+' ('+r.status+'): '+s.slice(0,1600));return decode(s);
}
const rpc=(c,b={})=>request('/api/rpc/command/'+c,b);
const profile=await rpc('login-with-password',{email:credentials.email,password:credentials.password});
const file=await rpc('get-file',{id:state.fileIds[0],features:['fdata/path-data','fdata/shape-data-type','design-tokens/v1','variants/v1','layout/grid','components/v2','styles/v2','plugins/runtime']});
fs.writeFileSync(path.join(out,'native-file.json'),JSON.stringify(file));
console.log('Native file keys: '+Object.keys(file).join(', '));
const data=file.data,pages=data.pagesIndex??data['pages-index'];
if(!pages)throw Error('No resolved pages in native file.');
const components=data.components,issues=[],boardList=[];let instances=0,missingRefs=0,overflow=0;
const lookup=new Map(Object.values(pages).flatMap(p=>Object.entries(p.objects)));
for(const [pageId,page]of Object.entries(pages)){
    for(const s of Object.values(page.objects)){
        const parentId=s.parentId??s['parent-id'],ref=s.shapeRef??s['shape-ref'];
        if(s.id==='00000000-0000-0000-0000-000000000000')continue;
        if(!page.objects[parentId])issues.push({type:'missing-parent',page:page.name,shape:s.name});
        if(ref&&!lookup.has(ref)){missingRefs++;issues.push({type:'missing-component-shape-reference',shape:s.name});}
        if((s.componentId??s['component-id'])&&!(s.mainInstance??s['main-instance']))instances++;
        if(s.type==='frame'&&parentId==='00000000-0000-0000-0000-000000000000')boardList.push({pageId,page:page.name,id:s.id,name:s.name,x:s.x,y:s.y,width:s.width,height:s.height});
        const parent=page.objects[parentId];
        if(parent?.type==='frame'&&parentId!=='00000000-0000-0000-0000-000000000000'&&(s.x+s.width>parent.x+parent.width+2||s.y+s.height>parent.y+parent.height+2||s.x<parent.x-2||s.y<parent.y-2)){
            overflow++;if(issues.length<160)issues.push({type:'outside-parent-bounds',page:page.name,parent:parent.name,shape:s.name,x:s.x-parent.x,y:s.y-parent.y,width:s.width,height:s.height,parentWidth:parent.width,parentHeight:parent.height});
        }
    }
}
const serialized=JSON.stringify(data),shapes=[...lookup.values()];
let brokenStyleReferences=0;
function inspectStyles(value){if(!value||typeof value!=='object')return;for(const [key,id]of Object.entries(value)){
    const family=key==='fillColorRefId'||key==='strokeColorRefId'?'colors':key==='typographyRefId'?'typographies':null;
    if(family&&!data[family]?.[id])brokenStyleReferences++;
    if(['fillColorRefFile','strokeColorRefFile','typographyRefFile','componentFile'].includes(key)&&id!==file.id)brokenStyleReferences++;
    inspectStyles(id);
}}
inspectStyles(data);
const tokenNames=new Set();
function collectTokens(node,prefix=''){
    if(node&&typeof node==='object'&&Object.hasOwn(node,'$value')){tokenNames.add(prefix);return;}
    for(const [key,value]of Object.entries(node??{}))if(!key.startsWith('$'))collectTokens(value,prefix?prefix+'.'+key:key);
}
for(const [set,node]of Object.entries(data.tokensLib??{}))if(!set.startsWith('$'))collectTokens(node);
let tokenReferences=0,brokenTokenReferences=0;
for(const shape of shapes)for(const name of Object.values(shape.appliedTokens??{})){tokenReferences++;if(!tokenNames.has(name)){brokenTokenReferences++;issues.push({type:'missing-token',shape:shape.name,token:name});}}
const report={fileId:file.id,revision:file.revn,pages:Object.keys(pages).length,components:Object.keys(components).length,instances,missingRefs,brokenStyleReferences,outsideParentBounds:overflow,autoLayoutFrames:shapes.filter(s=>s.layout).length,nestedInstances:shapes.filter(s=>s.componentId&&!s.componentRoot&&!s.mainInstance).length,linkedColourReferences:(serialized.match(/fillColorRefId/g)||[]).length,linkedTypographyReferences:(serialized.match(/typographyRefId/g)||[]).length,issues,boards:boardList};
Object.assign(report,{colorTokenValues:tokenNames.size,tokenReferences,brokenTokenReferences});
fs.writeFileSync(path.join(out,'audit.json'),JSON.stringify(report,null,2));
console.log(JSON.stringify({pages:report.pages,components:report.components,instances,missingRefs,outsideParentBounds:overflow,boards:boardList.length}));
if(brokenTokenReferences||tokenNames.size!==24||!tokenReferences)throw Error('The semantic colour token audit failed.');
if(process.argv.includes('--audit-only')){console.log('Broken style/file references: '+brokenStyleReferences+'; tokens: '+tokenNames.size+'; token links: '+tokenReferences);process.exit(brokenStyleReferences||missingRefs||overflow?1:0);}
const chosen=boardList.filter(b=>(b.page.includes('screens')&&(/dark \/ 320$/.test(b.name)||/Modifiers \/ dark \/ 240$/.test(b.name)||/Analysis \/ light \/ 480$/.test(b.name)))||b.page.includes('Controls and actions')||b.page==='07 • Dialogs • dark'&&['MoleHill Layer Templates','Import Survey Points','Inspect Curve'].includes(b.name));
const exports=[];
for(const b of chosen){
    if(process.argv[2]&&!b.name.includes(process.argv[2]))continue;
    const kv=(o)=>transit.map(Object.entries(o).flatMap(([k,v])=>[transit.keyword(k),v]));
    const requestBody=kv({cmd:transit.keyword('export-shapes'),'profile-id':transit.uuid(profile.id),wait:true,exports:[kv({'file-id':transit.uuid(file.id),'page-id':transit.uuid(b.pageId),'object-id':transit.uuid(b.id),type:transit.keyword('png'),suffix:'',scale:1,name:b.name})]});
    console.log('Rendering '+b.name);
    const resource=await request('/api/export',requestBody,true);
    fs.writeFileSync(path.join(out,b.name.replace(/[^\w-]+/g,'-')+'.resource.json'),JSON.stringify(resource,null,2));
    const resourceValue=resource.uri??resource['resource-uri'],resourceUri=resourceValue?.rep??resourceValue;if(!resourceUri)throw Error('No export resource URI: '+JSON.stringify(resource));
    const uri=new URL(resourceUri,base);if(uri.hostname!=='localhost'&&uri.hostname!=='127.0.0.1')throw Error('Unexpected export download host.');
    const result=await fetch(uri,{headers:{Cookie:cookie}});if(!result.ok)throw Error('Preview download failed '+result.status);
    const bytes=new Uint8Array(await result.arrayBuffer());
    if(bytes[0]!==137||bytes[1]!==80||bytes[2]!==78||bytes[3]!==71)throw Error('Export download was not PNG: '+result.headers.get('content-type')+' at '+uri.pathname);
    const name=b.name.replace(/[^\w-]+/g,'-')+'.png';fs.writeFileSync(path.join(out,name),bytes);
    exports.push({fileId:file.id,pageId:b.pageId,objectId:b.id,name:b.name,png:name,width:Buffer.from(bytes).readUInt32BE(16),height:Buffer.from(bytes).readUInt32BE(20)});
    console.log('Saved '+name);
}
fs.writeFileSync(path.join(out,'exports.json'),JSON.stringify(exports,null,2));
