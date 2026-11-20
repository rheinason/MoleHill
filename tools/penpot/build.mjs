import fs from 'node:fs';
import path from 'node:path';
import { randomUUID } from 'node:crypto';
import { createBuildContext, exportAsBytes } from '@penpot/library';
import { Resvg } from '@resvg/resvg-js';
import { inventory, themeInventory, read, root } from './inventory.mjs';
import { buildDialogs } from './dialogs.mjs';

const out = path.join(import.meta.dirname, 'output');
fs.mkdirSync(out, {recursive:true});
const cards = inventory(), tokens = themeInventory();
const themes = Object.fromEntries(['light','dark'].map(t => [t, Object.fromEntries(Object.entries(tokens).map(([k,v])=>[k,v[t]]))]));
for (const [key,t] of Object.entries(themes)) Object.assign(t, {theme:key,canvas:key==='dark'?'#303030':'#f0f0f0',border:key==='dark'?'#747474':'#a0a0a0'});
// Twelve semantic roles, with two scoped sets so both themes can coexist on a page.
// Specialised analysis/data colours stay literal in the faithful reference boards.
const roleSources={'surface.canvas':'canvas','surface.card':'CardBackground','surface.header':'HeaderBackground','surface.toolbar':'ToolbarBackground','surface.input':'InputBackground','surface.base':'BaseCardBackground','surface.selected':'ListSelectionBackground','text.primary':'PrimaryText','text.secondary':'MutedText','border.default':'border','warning.surface':'WarningBackground','warning.text':'WarningText'};
const semanticRoles=Object.fromEntries(Object.entries(roleSources).map(([role,source])=>[role,Object.fromEntries(['dark','light'].map(theme=>[theme,themes[theme][source]]))]));
const byKind = k => { const c=cards.find(c=>c.kind===k); if(!c) throw Error('Unknown card '+k); return c; };

// A small editable scene graph drives the Penpot file and the independent SVG previews.
function node(type,name,x,y,w,h,extra={}) { return {id:randomUUID(),type,name,x,y,w,h,...extra}; }
const rect=(name,x,y,w,h,fill,stroke=null)=>node('rect',name,x,y,w,h,{fill,stroke});
const group=(name,x,y,w,h,children=[])=>node('frame',name,x,y,w,h,{children});
function flex(n,direction='column',gap=0,padding={p1:0,p2:0,p3:0,p4:0}){n.layout={layout:'flex',layoutFlexDir:direction,layoutGap:{rowGap:gap,columnGap:gap},layoutGapType:'simple',layoutPaddingType:'multiple',layoutPadding:padding,layoutAlignItems:'stretch',layoutAlignContent:'start',layoutJustifyContent:'start',layoutWrapType:'nowrap'};return n;}
function txt(name,text,x,y,w,h=18,color='#e0e0e0',size=12,bold=false) { return node('text',name,x,y,w,h,{text:String(text),fill:color,size,bold}); }
const line=(name,points,color,width=1,fill=null)=>node('path',name,Math.min(...points.map(p=>p[0])),Math.min(...points.map(p=>p[1])),Math.max(.01,Math.max(...points.map(p=>p[0]))-Math.min(...points.map(p=>p[0]))),Math.max(.01,Math.max(...points.map(p=>p[1]))-Math.min(...points.map(p=>p[1]))),{points,color,lineWidth:width,fill});
const ellipsis=(s,w,size=12)=>s.length*size*.51>w ? s.slice(0,Math.max(1,Math.floor(w/(size*.51))-1))+'…' : s;
function text(children,label,x,y,w,t,{bold=false,muted=false,size=12,h=18}={}) { children.push(txt(label,ellipsis(label,w,size),x,y,w,h,muted?t.MutedText:t.PrimaryText,size,bold)); }
function wrap(children,label,x,y,w,t,size=12) {
    let y0=y, row='';
    for(const word of label.split(' ')) { if((row+' '+word).length*size*.51>w && row) { children.push(txt(row,row,x,y,w,18,t.PrimaryText,size)); y+=18; row=word; } else row+=(row?' ':'')+word; }
    if(row) { children.push(txt(row,row,x,y,w,18,t.PrimaryText,size)); y+=18; }
    return y-y0;
}
function icon(name,x,y,t,size=14) {
    const a=[], s=size/16, c=t.PrimaryText, p=points=>points.map(([px,py])=>[x+px*s,y+py*s]);
    const stroke=(points,w=2.2)=>a.push(line(name,p(points),c,w*s));
    const box=(px,py,w,h)=>a.push(rect(name,x+px*s,y+py*s,w*s,h*s,c));
    const poly=points=>a.push(line(name,p([...points,points[0]]),c,.01,c));
    if(name==='Add') { stroke([[3,8],[13,8]]); stroke([[8,3],[8,13]]); }
    else if(name==='Clear') { stroke([[4,4],[12,12]]); stroke([[12,4],[4,12]]); }
    else if(name==='Duplicate') { stroke([[2,6],[10,6],[10,14],[2,14],[2,6]],1.6); box(6,2,8,8); }
    else if(name==='Delete') { stroke([[2.5,4],[13.5,4]]); stroke([[6,4],[6.6,2.2],[9.4,2.2],[10,4]],1.6); stroke([[4.2,5.8],[4.9,13.8],[11.1,13.8],[11.8,5.8]]); stroke([[6.8,7.4],[7,11.8]],1.3); stroke([[9.2,7.4],[9,11.8]],1.3); }
    else if(name==='HideOff'||name==='HideOn') { const eye=[]; for(let i=0;i<=16;i++) eye.push([1.6+12.8*i/16,8-4.3*Math.sin(Math.PI*i/16)]); for(let i=15;i>=0;i--) eye.push([1.6+12.8*i/16,8+4.3*Math.sin(Math.PI*i/16)]); stroke(eye,1.6); if(name==='HideOff') a.push(node('circle',name,x+5.6*s,y+5.6*s,4.8*s,4.8*s,{fill:c})); else stroke([[3,13],[13,3]],1.6); }
    else if(name==='Lock'||name==='Unlock') { stroke(name==='Lock'?[[5,7],[5,5.25],[6.1,3.65],[8,3.1],[9.9,3.65],[11,5.25],[11,7]]:[[7.5,6.4],[7.5,5.25],[8.6,3.65],[10.5,3.1],[12.4,3.65],[13.5,5.25],[13.5,6.6]]); box(4.75,7,6.5,5.75); }
    else if(name==='Bake') { stroke([[1.8,1.8],[10.8,1.8],[10.8,10.8],[1.8,10.8],[1.8,1.8]],1.6); poly([[3.4,9.2],[6.3,4.4],[9.2,9.2]]); stroke([[10.4,10.4],[14,14],[14,10.6]]); stroke([[14,14],[10.6,14]]); }
    else if(name==='Rebuild') { const pts=[]; for(let i=0;i<=22;i++){const angle=(110+290*i/22)*Math.PI/180;pts.push([8+5*Math.cos(angle),8+5*Math.sin(angle)]);} stroke(pts); poly([[13,7],[13,11],[9.5,9]]); }
    else if(name==='ResetBuild') { stroke([[6,2.5],[3.5,5],[6,7.5]]); const pts=[[3.5,5],[9.5,5]];for(let i=0;i<=16;i++){let r=(-90+180*i/16)*Math.PI/180;pts.push([9.5+4.5*Math.cos(r),9.5+4.5*Math.sin(r)]);}pts.push([6.5,14]);stroke(pts); }
    else if(name==='ChevronDown') stroke([[4.25,6.25],[8,10],[11.75,6.25]]);
    else if(name==='ChevronRight') stroke([[6.25,4.25],[10,8],[6.25,11.75]]);
    else if(name==='AnalysisVisible') {box(3.4,9,2.4,4);box(6.8,6,2.4,7);box(10.2,3.4,2.4,9.6);}
    else if(name==='ZoneMeshes') {box(2.6,2.6,8.2,8.2);box(8.4,8.4,4.8,4.8);}
    else if(name==='More') for(const p of [3,7,11]) a.push(node('circle',name,x+p*s,y+7*s,2*s,2*s,{fill:c}));
    else {box(3,5,10,6);}
    return a;
}
function button(label,x,y,w,t,{action=null,selected=false,disabled=false}={}) {
    const a=[rect(label+' button',x,y,w,22,selected?t.ListSelectionBackground:t.ToolbarBackground,t.border)];
    if(action) a.push(...icon(action,x+(w-14)/2,y+4,t));
    else a.push(txt(label,label,x+4,y+3,w-8,18,disabled?t.MutedText:t.PrimaryText,12));
    return a;
}
function checkbox(label,x,y,t,checked=true) {
    const a=[rect('Checkbox',x,y+4,12,12,t.InputBackground,t.border)];
    a.push({...line('Checked',[[x+2,y+10],[x+5,y+13],[x+10,y+6]],t.PrimaryText,1.6),hidden:!checked});
    if(label) text(a,label,x+19,y+2,Math.ceil(label.length*6.2)+4,t);
    return a;
}
function field(value,x,y,w,t,{dropdown=false,stepper=false,unit='',unitSlot=false}={}) {
    const a=[rect('Input',x,y,w,26,t.InputBackground,t.border)];
    const reserve=dropdown||stepper?20:4;
    text(a,String(value),x+5,y+4,w-reserve-8,t);
    if(dropdown) a.push(...icon('ChevronDown',x+w-18,y+6,t));
    if(stepper) { a.push(line('Stepper separator',[[x+w-17,y],[x+w-17,y+26]],t.border)); text(a,'▴',x+w-13,y-1,12,t,{size:10});text(a,'▾',x+w-13,y+10,12,t,{size:10}); }
    if(unit||unitSlot) a.push(txt('Unit',unit,x+w+4,y+4,14,18,t.MutedText));
    return a;
}
function slider(x,y,w,t,value='.500') {
    const a=[rect('Slider track',x,y+12,Math.max(10,w-58),2,t.border),rect('Slider thumb',x+(w-58)*.48,y+6,7,14,t.PrimaryText)];
    a.push(...field(value,x+w-52,y,52,t));return a;
}
const sample = {
    Heading:'0.000',Scale:'1.000',Offset:'0.000',Seed:'1',Strength:'0.500',Iterations:'10',Spacing:'1.000',
    'Fill Slope':'1:3','Cut Slope':'Inherit','Max Distance':'0.000','Slope Limit':'0%',
    'Max Angle':'160.000','Max Edge':'0.000','Target Edge':'1.000','Edge Length':'1.000',
    'Contour Mode':'Auto (fast for large sets)','Detail Size':'0.250',
    'Compare To Terrain':'Existing Ground','Vertical Exaggeration':'5.00',
    Bindings:'1 source refs','Source':'Within region','Mode':'Auto',
    'Text Size':'Follow Style','Units':'Percent','Prefix':'','Suffix':'','Decimals':'1','Block Scale':'1.000',
};
function labelLines(label,w){const lines=[];let current='';for(const word of label.split(' ')){if(current&&(current+' '+word).length*6.1>w){lines.push(current);current=word;}else current+=(current?' ':'')+word;}if(current)lines.push(current);return lines;}
function rowMetrics(r,w){const labelSize=Math.ceil(r.label.length*6.1),choiceSize=r.type==='Choice'?String(rowValue(r)).length*6.1+28:0;const stacked=w<235||labelSize>105||choiceSize>w-105;return {stacked,labelW:stacked?w:105,labelH:stacked?labelLines(r.label,w).length*18:18};}
function rowHeight(r,w) { if(r.type==='ColorRamp')return 206;if(r.type==='BlockMix')return 88;if(r.type==='GoingTable')return 64;if(r.type==='Rule')return 24;const m=rowMetrics(r,w);return m.stacked?34+m.labelH:32; }
function rowValue(r){
    if(r.value!==undefined)return r.value;
    if(/Optional/.test(r.type)&&!r.defaultValue)return 'Inherit';
    if(r.key==='LabelFormat'&&/^F\d+$/.test(r.defaultValue??''))return r.defaultValue.slice(1)+' decimals';
    if(r.defaultValue!==undefined&&r.defaultValue!==null){
        if(/Slope/.test(r.type)&&typeof r.defaultValue==='number'){if(r.defaultValue===0)return '0%';return '1:'+(1/Math.tan(r.defaultValue*Math.PI/180)).toFixed(3).replace(/0+$/,'').replace(/\.$/,'');}
        if(typeof r.defaultValue==='number')return r.defaultValue.toFixed(r.decimalPlaces??3);
        if(typeof r.defaultValue==='string')return r.defaultValue;
    }
    return sample[r.label]??r.options?.[0]??(/Optional/.test(r.type)?'Inherit':r.type==='Text'?'':Number(r.minimum??0).toFixed(r.decimalPlaces??3));
}
function rawRow(r,x,y,w,t) {
    const a=[],h=rowHeight(r,w), {stacked,labelW,labelH}=rowMetrics(r,w);
    if(r.type==='Rule') { text(a,r.label,x,y+3,Math.min(w,170),t,{muted:true});a.push(rect('Section rule',x+Math.min(170,w*.45),y+13,Math.max(1,w-Math.min(170,w*.45)),1,t.RampDivider));return {children:a,h}; }
    if(r.type==='ColorRamp') return {children:ramp(x,y,w,t,false,r.title??'Slope',r.suffix??'%'),h};
    if(r.type==='GoingTable') { text(a,r.label,x,y+4,w-30,t);a.push(...button('Add going',x+w-22,y,22,t,{action:'Add'}));text(a,'Goings',x,y+34,90,t);text(a,'No limit',x+97,y+34,w-97,t,{muted:true});return {children:a,h}; }
    if(r.type==='BlockMix') {text(a,'Blocks',x,y,w,t); a.push(...button('Select Blocks…',x,y+23,104,t));text(a,'No blocks selected',x,y+54,w,t,{muted:true});return {children:a,h};}
    a.push(txt(r.label,stacked?labelLines(r.label,w).join('\n'):r.label,x,y+(stacked?3:5),labelW,labelH,t.PrimaryText));
    const fx=stacked?x:x+labelW, fy=stacked?y+labelH+5:y+2, fw=stacked?w:w-labelW;
    const value=rowValue(r);
    if(r.type==='Sources') { const gap=4,pw=(fw-gap)/2;a.push(...button(r.objects??'Objects',fx,fy,pw,t),...button(r.layers??'Layers',fx+pw+gap,fy,pw,t)); }
    else if(r.type==='Bool') a.push(...checkbox('',fx,fy,t,r.checked??r.defaultValue??false));
    else if(r.type==='ReadOnly') text(a,r.value??sample[r.label]??'Rebuild required',fx,fy+3,fw,t,{muted:true});
    else if(r.type==='Choice') a.push(...field(value,fx,fy,fw,t,{dropdown:true}));
    else if(r.type==='Color') {a.push(rect('Color swatch',fx,fy+3,18,18,r.value??'#43a047',t.border));a.push(...button(r.actionLabel??'Reset',fx+26,fy,Math.min(54,fw-26),t));a.push(txt('Hex value',r.hexText??'',fx+90,fy+4,Math.max(1,Math.min(100,fw-90)),18,t.MutedText));}
    else if(r.type==='FilePath') {a.push(...field('',fx,fy,Math.max(32,fw-62),t));a.push(...button('Browse',fx+fw-58,fy,58,t));}
    else if(r.type==='BlockPicker') a.push(...button('Select Block…',fx,fy,Math.min(fw,120),t));
    else if(r.type==='Action') a.push(...button(r.value??'Pick…',fx,fy,Math.min(fw,80),t));
    else if(/Slider/.test(r.type)) a.push(...slider(fx,fy,fw,t,value));
    else if(r.type==='OptionalSlope'||r.type==='OptionalNumber') {a.push(...checkbox('',fx,fy,t,false));a.push(...field(value,fx+20,fy,Math.max(40,fw-20),t));}
    else if(['Number','Slope','Text'].includes(r.type)) { const unit=r.unit==='ModelLength'?'m':r.unit==='Degrees'?'°':r.unit==='Percent'?'%':'',unitSlot=r.type==='Number';a.push(...field(value,fx,fy,fw-(unit||unitSlot?18:0),t,{stepper:r.type==='Number',unit,unitSlot})); }
    else throw Error('Unhandled schema control '+r.type);
    return {children:a,h};
}
function propertyScene(r,w,t){
    const raw=rawRow(r,0,0,w,t),scene=group(r.type+' property row',0,0,w,raw.h,raw.children);
    scene.theme=t.theme;
    if(['Rule','ColorRamp','BlockMix','GoingTable'].includes(r.type))return scene;
    const {stacked,labelW,labelH}=rowMetrics(r,w),fx=stacked?0:labelW,fy=stacked?labelH+5:2;
    const label=scene.children.shift();label.layoutItemHSizing=stacked?'fill':'fix';label.layoutItemVSizing='fix';
    const editor=group('Editor',fx,fy,stacked?w:w-labelW,26,scene.children.map(n=>({...n,x:n.x-fx,y:n.y-fy,...(n.points?{points:n.points.map(([x,y])=>[x-fx,y-fy])}:{})})));
    if(r.type==='Sources'){
        const pw=(editor.w-4)/2;
        editor.children=[0,1].map(i=>{const button=group(i?'Layers':'Objects',i*(pw+4),0,pw,22,editor.children.slice(i*2,i*2+2).map(n=>({...n,x:n.x-i*(pw+4),anchorH:'leftright'})));button.layoutItemHSizing='fill';return button;});
        flex(editor,'row',4);
    }
    editor.layoutItemHSizing='fill';editor.layoutItemVSizing='fix';
    for(const n of editor.children){n.anchorH=/Stepper|▴|▾|Chevron/.test(n.name)?'right':n.type==='rect'&&n.name==='Input'||n.type==='text'&&n.w>70?'leftright':'left';}
    scene.children=[label,editor];flex(scene,stacked?'column':'row',stacked?2:0,{p1:3,p2:0,p3:3,p4:0});
    scene.layoutItemHSizing='fill';scene.layoutItemVSizing='auto';return scene;
}
function overrideScene(source,target){
    if(source.type!==target.type||(source.children?.length??0)!==(target.children?.length??0))throw Error('Component topology differs: '+source.name+' / '+target.name);
    const v={...target,id:randomUUID(),ref:source.id,touched:['geometry-group','name-group']};
    if(target.type==='text'&&source.text!==target.text)v.touched.push('content-group');
    if(source.fill!==target.fill)v.touched.push('fill-group');
    if(source.stroke!==target.stroke||source.color!==target.color)v.touched.push('stroke-group');
    if(source.hidden!==target.hidden)v.touched.push('visibility-group');
    if(target.layout&&source.layout?.layoutFlexDir!==target.layout.layoutFlexDir)v.touched.push('layout-flex-dir');
    if(target.children)v.children=target.children.map((child,i)=>overrideScene(source.children[i],child));
    return v;
}
function row(r,x,y,w,t){
    const actual=propertyScene(r,w,t),family=({Slope:'Text',SlopeSlider:'Slider',OptionalSlope:'OptionalNumber',BlockPicker:'Action'})[r.type]??r.type;
    const key='Field / '+family;
    let main=components.get(key);
    if(!main){main=component(key,propertyScene({...r,type:family,label:r.type==='Sources'?'Sources':r.type==='Bool'?'Enabled':'Value'},400,themes.dark),'Fields');}
    const v=overrideScene(main,actual);v.componentId=main.componentId;v.instance=true;v.x=x;v.y=y;v.componentPath=main.componentPath;v.parameter={label:r.label,type:r.type};
    return {children:[v],h:v.h};
}
function ramp(x,y,w,t,expanded=false,title='Slope',suffix='%') {
    const a=[rect('Ramp surface',x,y,w,expanded?290:206,t.RampSurface),rect('Ramp header',x,y,w,24,t.RampHeader)];
    text(a,(expanded?'▾':'▸')+' '+title,x+6,y+4,w-80,t,{bold:true});text(a,'Continuous',x+w-78,y+4,72,t,{muted:true,size:11});
    const spectrum=read('src/MoleHill.Core/Analysis/ColorRampPresets.cs').split('Build("terrain-spectrum"')[1].split('}),')[0];
    const stops=[...spectrum.matchAll(/new\(([\d.]+),\s*(\d+),\s*(\d+),\s*(\d+)\)/g)].map(m=>({offset:+m[1],color:'#'+m.slice(2).map(n=>(+n).toString(16).padStart(2,'0')).join(''),opacity:1}));
    const colors=stops.map(s=>s.color),bw=(w-12)/9;
    for(let i=0;i<9;i++)a.push(rect('Histogram '+i,x+6+bw*i,y+34-(i%4)*3,bw-1,12+(i%4)*3,t.RampHistogram));
    const strip=rect('Terrain Spectrum ramp',x+6,y+48,w-12,18,null);strip.gradient={type:'linear',startX:0,startY:.5,endX:1,endY:.5,width:1,stops};a.push(strip);
    text(a,'0'+suffix,x+6,y+69,40,t,{muted:true,size:11});text(a,'25'+suffix,x+w*.46,y+69,40,t,{muted:true,size:11});text(a,'50'+suffix,x+w-35,y+69,33,t,{muted:true,size:11});
    text(a,'MAPPING',x+6,y+94,w-12,t,{muted:true,size:10});
    a.push(...field('Continuous',x+6,y+113,w-12,t,{dropdown:true}));
    a.push(...checkbox('Auto-fit range',x+6,y+145,t,true));
    a.push(...field('0'+suffix,x+6,y+174,(w-18)/2,t),...field('50'+suffix,x+12+(w-18)/2,y+174,(w-18)/2,t));
    if(expanded){text(a,'STOPS',x+6,y+208,w-12,t,{muted:true,size:10});for(let i=0;i<3;i++){a.push(rect('Stop swatch',x+6,y+230+i*18,12,12,colors[i*2]));text(a,["0%","22.5%","50%"][i],x+28,y+226+i*18,60,t);}}
    return a;
}
function cardRows(c,{catalog=false}={}) {
    let rows=c.rows.filter(r=>catalog||!r.conditional).map(r=>r.type==='ColorRamp'?{...r,title:c.name,suffix:['slope','gradient-compliance'].includes(c.kind)?'%':c.kind==='aspect'?'°':' m'}:r);
    if(c.kind==='triangulate') { const peel=rows.filter(r=>['PeelBoundaryTriangles','MaxBoundaryEdgeLength','MaxBoundaryAngleDegrees','MaxBoundarySlopeDegrees'].includes(r.key));rows=rows.filter(r=>!peel.includes(r));rows.splice(5,0,{type:'Rule',label:'Boundaries'});rows.push({type:'Rule',label:'Peel Border'},...peel); }
    if(c.family==='Object'&&c.kind!=='scatter') rows=rows.filter(r=>!r.key.startsWith('random'));
    if(c.family==='Annotation') rows.push({type:'ReadOnly',label:'Text Size',value:'Follow Style'});
    if(['terrain-section','cross-section-station','longitudinal-section'].includes(c.kind)) rows.unshift({type:'Sources',label:'Sources'},{type:'Rule',label:'Compare'},{type:'Choice',label:'Compare To Terrain',value:'None'},{type:'Sources',label:'Compare To'},{type:'Bool',label:'Cut / Fill'},{type:'ReadOnly',label:'Also Draw',value:'No additional terrains'},{type:'Rule',label:'Layout'},{type:'Action',label:'Origin',value:'Pick…'});
    if(['report-table','legend'].includes(c.kind)) rows.push({type:'Action',label:'Origin',value:'Pick…'});
    if(c.kind==='project-to') rows.unshift({type:'Choice',label:'Target Terrain',value:'None'});
    if(c.kind==='earthwork'||c.kind==='cut-fill') rows.unshift({type:'Choice',label:'Compare To Terrain',value:'Existing Ground'});
    return rows.filter((r,i,all)=>!r.key||all.findIndex(s=>s.key===r.key)===i);
}
function cardScene(c,w,t,{collapsed=false,catalog=false,warning=null}={}) {
    const rows=collapsed?[]:cardRows(c,{catalog}), bodyW=w-21, pinned=c.kind==='triangulate';
    const actionCount=pinned?0:c.family==='Zone'?1:2,titleWidth=w-99-actionCount*26-6;
    const secondTitle=!pinned&&c.name.length*6.1+18>titleWidth;
    const headerH=pinned?52:secondTitle?66:w<260?40:52;
    const extra=!collapsed&&c.family==='Object'&&c.kind!=='scatter'?104:0;
    const h=headerH+(collapsed?0:12+rows.reduce((n,r)=>n+rowHeight(r,bodyW),0)+extra+(warning?56:0));
    const a=[rect('Card header',0,0,w-5,headerH,pinned?t.BaseCardBackground:t.HeaderBackground)];
    text(a,pinned?'•':'⠿',7,10,12,t,{muted:true});text(a,collapsed?'▶':'▼',24,11,14,t,{size:10});
    a.push(rect('Icon plate',43,10,20,20,c.accent));
    if(c.icon) a.push(node('image',c.icon,46,13,14,14,{resource:c.icon}));
    a.push(...checkbox('',70,9,t,true));
    const titleX=secondTitle?8:91,titleY=secondTitle?34:5,titleW=secondTitle?w-26:titleWidth;
    if(collapsed)text(a,c.name,titleX,titleY+5,titleW,t,{bold:true});else a.push(...field(c.name,titleX,titleY,titleW,t));
    if(pinned)text(a,'Base',w-39,headerH-18,30,t,{muted:true,size:10,h:16});
    if(w>=290&&!secondTitle)text(a,pinned?'Base geometry':collapsed?'Enabled · last build':c.subtitle,91,32,w-105-(pinned?30:0),t,{muted:true,size:11});
    if(actionCount===2)a.push(...button('Duplicate',w-57,9,22,t,{action:'Duplicate'}));
    if(actionCount>0)a.push(...button('Delete',w-31,9,22,t,{action:'Delete'}));
    const header=group('Header',5,0,w-5,headerH,a);header.layoutItemHSizing='fill';header.layoutItemVSizing='fix';
    for(const child of a)child.anchorH=child.name==='Card header'?'leftright':/Duplicate|Delete/.test(child.name)?'right':'left';
    const body=[];let y=6;
    if(warning){const wa=[rect('Warning background',0,0,bodyW,48,t.WarningBackground)];wrap(wa,warning,6,4,bodyW-12,{...t,PrimaryText:t.WarningText},11);body.push(group('Warning',8,y,bodyW,56,wa));y+=56;}
    for(const r of rows){const result=row(r,8,y,bodyW,t);body.push(...result.children);y+=result.h;}
    if(extra){const ea=[];text(ea,'› Variation',0,4,bodyW,t,{muted:true});ea.push(...button('Show Base',0,27,65,t),...button('Change Base',69,27,80,t),...button('Release Selected',0,53,100,t),...button('Release All',104,53,72,t));text(ea,'Placement',0,84,90,t);text(ea,'4 placed · 0 skipped',97,84,bodyW-97,t);body.push(group('Object placement actions',8,y,bodyW,104,ea));y+=104;}
    const bodyFrame=flex(group('Parameters',5,headerH,w-5,Math.max(1,h-headerH),body),'column',0,{p1:6,p2:8,p3:6,p4:8});bodyFrame.layoutItemHSizing='fill';bodyFrame.layoutItemVSizing='auto';
    const accent=rect('Accent strip',0,0,5,h,c.accent);accent.layoutItemAbsolute=true;accent.layoutItemZIndex=0;accent.anchorV='topbottom';
    const scene=flex(group(c.name,0,0,w,h,[accent,header,...(collapsed?[]:[bodyFrame])]),'column',0,{p1:0,p2:0,p3:0,p4:5});scene.fill=pinned?t.BaseCardBackground:t.CardBackground;scene.theme=t.theme;scene.semanticRole=pinned?'surface.base':'surface.card';return scene;
}

const pages=[];
function page(name){const p={id:randomUUID(),name,boards:[]};pages.push(p);return p;}
function board(p,name,x,y,w,h,children=[],background='#303030'){const b=group(name,x,y,w,h,children);b.fill=background;if(background===themes.dark.canvas||background===themes.light.canvas)b.semanticRole='surface.canvas';p.boards.push(b);return b;}
function note(p,name,lines,x=0,y=0,w=880){const t=themes.light,a=[];text(a,name,24,18,w-48,t,{bold:true,size:24,h:32});let cy=66;for(const s of lines) cy+=wrap(a,s,24,cy,w-48,t,14)+16;return board(p,name,x,y,w,cy+16,a,'#ffffff');}
const intro=page('00 • Start here');
note(intro,'MoleHill / Rhino UI',[
    'Editable reference library generated from the working tree. All five tabs, every registered card type, both themes, and 240 / 320 / 480 logical-pixel panel layouts.',
    'Start with the Screens pages to sketch a workflow. Reuse assets from the Components pages. Main components and screen instances are linked; text, controls and vector actions remain editable.',
    'Labels, declared units, scalar model defaults, icons, theme colours and fixed metrics come from C#. Document-dependent references, chart distributions and measurements are illustrative. Source Sans Pro is the portable substitute for the Rhino system font.',
    'Native Rhino / Eto widgets are approximated with editable vector shapes. Typography uses Source Sans Pro for portable Penpot rendering; compare against Segoe UI in Rhino before shipping a visual change.',
    'Card atlases include conditional rows to make the complete schema available to designers. Some rows are mutually exclusive at runtime. The source inventory records these conditions and links each card to its descriptor.',
    'Analysis and Annotation are separate tabs. Analysis has its visibility toggle; Annotation does not. Text follows Rhino annotation styles. Layer output routing uses the terrain layer template.',
    'One main component per real card type, with shared field families. Theme and width examples are reference compositions. Twelve semantic colour roles have light/dark token values; specialised analysis colours remain source-accurate literals.',
]);
const foundations=page('01 • Foundations');
for(const [index,[key,t]] of Object.entries(themes).entries()){
    const a=[];text(a,key.toUpperCase()+' / UI THEME',20,16,440,t,{bold:true,size:20,h:28});let y=58;
    for(const [name,values] of Object.entries(semanticRoles)){a.push({...rect(name+' swatch',20,y,48,24,values[key],t.border),semanticRole:name});text(a,name,84,y+3,245,t);text(a,values[key],340,y+3,90,t,{muted:true});y+=32;}
    board(foundations,key+' palette',index*510,0,480,y+20,a,t.canvas);
}
note(foundations,'Sizing and layout contract',[
    'Spacing scale: 1 / 2 / 4 / 6 / 8 / 12 px. Standard control: 26 px. Compact control and icon box: 22 px. Action glyph: 14 px. Card accent: 5 px. Card padding: 8 px horizontal / 6 px vertical.',
    '240 px is the compact panel target. Horizontal scrolling is disabled. Property rows fill the editor column; input and action groups reflow independently.',
    'Character-based values are font-dependent: LabelColumn = 13 ch; RowBreak = 30 ch; ControlGroupBreak = 44 ch; HeaderStatusBreak = 42 ch. These boards use an approximate 7 px ch.',
    'At compact widths, secondary metadata yields to titles and primary actions. Tab captions disappear when the five full labels no longer fit.',
],1030,0,700);

const primitives=page('03 • Controls and actions');
const library=page('02 • Main components');
let libraryIndex=0;
const components=new Map();
function component(key,scene,pathName){
    if(components.has(key))return components.get(key);
    const column=libraryIndex%8, rowIndex=Math.floor(libraryIndex/8);libraryIndex++;
    scene.x=column*880;scene.y=rowIndex*1800;scene.componentId=randomUUID();scene.componentPath=pathName;scene.componentName=key;
    library.boards.push(scene);components.set(key,scene);return scene;
}
function instance(main,x,y){
    function clone(n){const c={...n,id:randomUUID(),ref:n.id};if(n.children)c.children=n.children.map(clone);return c;}
    const v=clone(main);v.x=x;v.y=y;v.main=false;v.instance=true;return v;
}
for(const [themeIndex,[key,t]] of Object.entries(themes).entries()){
    const a=[];let y=18;text(a,key+' / editable control vocabulary',16,y,416,t,{bold:true,size:18,h:26});y+=40;
    for(const [type,label,value] of [['Sources','Sources'],['Number','Offset','0.000'],['Slope','Fill Slope','1:3'],['OptionalSlope','Cut Slope','Inherit'],['Choice','Mode','Auto'],['Slider','Strength','0.500'],['Bool','Enabled'],['Color','Color'],['FilePath','Texture'],['BlockPicker','Block'],['ReadOnly','Bindings','1 source refs']]){
        const r={type,label,value,unit:label==='Offset'?'ModelLength':'None'}, scene=group(label,0,0,416,rowHeight(r,400),row(r,8,0,400,t).children);
        scene.x=16;scene.y=y;a.push(scene);y+=scene.h+12;
    }
    text(a,'22 × 22 action buttons · 14 px glyphs',16,y,420,t,{muted:true});y+=28;
    let x=16;for(const action of ['Add','Duplicate','Delete','Clear','Rebuild','ResetBuild','Bake','HideOff','HideOn','Lock','Unlock','More']){a.push(...button(action,x,y,22,t,{action}));x+=32;}y+=44;
    a.push(...ramp(16,y,416,t,true));y+=310;
    board(primitives,key+' controls',themeIndex*500,0,448,y,a,t.canvas);
}

// Component atlases include all declared fields, including conditional branches.
for(const family of ['Modifier','Object','Analysis','Annotation']){
    const p=page('04 • '+family+' card atlas'),items=cards.filter(c=>c.family===family);
    let column=0;
    for(const c of items){for(const key of ['dark','light']){
        const scene=cardScene(c,400,themes[key],{catalog:true});
        const main=component(c.name, key==='dark'?scene:cardScene(c,400,themes.dark,{catalog:true}),'Cards/'+family);
        const linked=key==='dark'?instance(main,10,42):overrideScene(main,scene);
        Object.assign(linked,{x:10,y:42,componentId:main.componentId,instance:true});
        const b=board(p,c.name+' • '+key,column*452,0,420,scene.h+80,[txt('Schema reference','SCHEMA REFERENCE · ALL DECLARED ROWS',10,12,400,18,themes[key].MutedText,10),linked],themes[key].canvas);
        column++;
    }}
}

function shell(w,t,selected,{settings=false,status=false,empty=false}={}){
    const a=[], inset=16, selectW=w-32-78;
    a.push(rect('Identity toolbar',8,8,w-16,34,t.ToolbarBackground));
    a.push(...field(empty?'No terrain':'Proposed Terrain',inset,14,selectW,t,{dropdown:true}));
    for(const [i,action]of ['Add','Duplicate','Delete'].entries())a.push(...button(action,w-90+i*26,16,22,t,{action,disabled:empty&&i>0}));
    a.push(...button('Rebuild',16,46,22,t,{action:'Rebuild'}),...button('Reset Build',44,46,22,t,{action:'ResetBuild'}),...checkbox(w>=260?'Live':'',72,46,t,true));
    for(const [i,action]of ['Bake','HideOff','Unlock'].entries())a.push(...button(action,w-90+i*26,46,22,t,{action,disabled:empty}));
    let y=78;
    const section=(name,expanded)=>{a.push(rect(name+' section',8,y,w-16,28,t.HeaderBackground));text(a,(expanded?'▼':'▶')+'  '+name,16,y+5,w-30,t);y+=32;};
    section('Terrain Settings',settings);
    if(settings){
        for(const r of [{type:'Color',label:'Terrain Color'},{type:'Slider',label:'Opacity',value:'100%'},{type:'Bool',label:'Show Wires'},{type:'Bool',label:'Warn Before Slow Builds',checked:true},{type:'Slider',label:'Preview Line Weight',value:'1.0×'},{type:'Number',label:'Detail Size',value:'0.250',unit:'ModelLength'},{type:'Choice',label:'Slope Units',value:'Ratio'},{type:'Bool',label:'Replace Previously Baked',checked:true}]){const r0=row(r,16,y,w-32,t);a.push(...r0.children);y+=r0.h;}
        a.push(...button('Untrack Selected',16,y,108,t),...button('Untrack All',130,y,80,t));y+=30;
        const layers=row({type:'ReadOnly',label:'Output Layers',value:'MoleHill'},16,y,w-32,t);a.push(...layers.children);y+=layers.h;
        a.push(...button('Edit…',16,y,58,t),...button('Apply',80,y,54,t));y+=30;
    }
    section('Status',status);
    if(status){a.push(...field('Ready · last final build complete',16,y,w-32,t));y+=34;a.push(...button('Copy Log',16,y,72,t),...button('Copy Case',94,y,78,t));y+=30;}
    const labels=['Modifiers','Objects','Zones','Analysis','Annotation'],icons=['TabModifiers','TabObjects','TabZones','TabAnalysis','TabAnnotation'],tw=(w-16)/5,full=w>=360;
    for(let i=0;i<5;i++){const tx=4+i*(tw+2);a.push(rect(labels[i]+' tab',tx,y,tw,28,selected===i?t.ListSelectionBackground:t.ToolbarBackground,t.HeaderBackground));a.push(node('image',icons[i],tx+6,y+7,14,14,{resource:icons[i]}));if(full)text(a,labels[i],tx+24,y+6,tw-27,t,{size:w>=480?11:9});if(i===2||i===3){a.push(...icon(i===2?'ZoneMeshes':'AnalysisVisible',tx+tw-16,y+8,t,10));}}
    y+=38;const title=['MODIFIER STACK','TERRAIN OBJECTS','ZONES','ANALYSIS','ANNOTATION'][selected];text(a,title,8,y,w-16,t,{muted:true,size:11});y+=25;
    a.push(...button(['Add Modifier','Add Object','Add Zone','Add Analysis','Add Annotation'][selected],8,y,[98,84,74,92,108][selected],t,{disabled:empty}));y+=32;
    if(selected===4){const r=row({type:'Choice',label:'Text Style',value:'MoleHill'},8,y,w-16,t);a.push(...r.children);y+=r.h;}
    const scene=group('Panel shell',0,0,w,y,a);scene.theme=t.theme;return scene;
}
const screenSets=[
    {tab:0,name:'Modifiers',kinds:['triangulate','grade-pad','smooth']},
    {tab:1,name:'Objects',kinds:['surface-oriented','lowest-point','scatter']},
    {tab:2,name:'Zones',kinds:[]},
    {tab:3,name:'Analysis',kinds:['slope','earthwork','elevation']},
    {tab:4,name:'Annotation',kinds:['contour','terrain-section','grade-callout']},
];
const zone={name:'Paved',family:'Zone',kind:'zone',subtitle:'Later zones win ties',accent:'#555555',rows:[{type:'Rule',label:'LAST BUILD'},{type:'ReadOnly',label:'Plan area',value:'1,240.00 m²'},{type:'ReadOnly',label:'Surface area',value:'1,260.30 m²'},{type:'ReadOnly',label:'Elevation',value:'12.4 / 13.1 / 14.2 m'},{type:'ReadOnly',label:'Slope',value:'0.0% / 3.2% / 9.5%'},{type:'ReadOnly',label:'Mesh output',value:'1 object · 2,480 triangles'},{type:'Sources',label:'Zone Area'},{type:'Bool',label:'Priority by elevation'}]};
for(const set of screenSets){const p=page('05 • '+set.name+' screens');let x=0;
    for(const key of ['dark','light'])for(const w of [240,320,480]){
        const t=themes[key],a=[],sh=shell(w,t,set.tab);
        a.push(sh);let y=sh.h+6;
        const list=set.tab===2?[zone,{...zone,name:'Soft Landscape'}]:set.kinds.map(byKind);
        for(const [i,c]of list.entries()){
            const collapsed=i>0, scene=cardScene(c,w-16,t,{collapsed});
            scene.x=8;scene.y=y;a.push(scene);y+=scene.h+8;
        }
        board(p,set.name+' / '+key+' / '+w,x,0,w,Math.max(900,y+16),a,t.canvas);x+=w+60;
    }
}

const states=page('06 • States and settings');
for(const [i,{name,options,tab}]of [{name:'Terrain Settings expanded',options:{settings:true},tab:0},{name:'Status expanded',options:{status:true},tab:0},{name:'No terrain',options:{empty:true},tab:0}].entries()){
    const t=themes.dark,sh=shell(320,t,tab,options),a=[sh];
    if(options.empty)wrap(a,'Create a terrain to begin. Set document units before building terrain.',16,sh.h+20,288,t);
    board(states,name,i*380,0,320,Math.max(900,sh.h+100),a,t.canvas);
}
for(const [i,message]of ['Not applied — no boundaries selected.','Set document units before creating or building terrain.','Build in progress · preview updating','Rebuild required'].entries()){
    const c=byKind(i===0?'grade-pad':'smooth'),t=themes.dark,scene=cardScene(c,400,t,{warning:message});board(states,message,1140+i*460,0,420,scene.h+20,[{...scene,x:10,y:10}],t.canvas);
}

// Secondary workflows are deliberately labelled layout references, not native captures.
const dialogs=page('07 • Dialog layout references');
const t=themes.dark;
function dialog(name,w,rows,x){const a=[rect('Title bar',0,0,w,32,t.HeaderBackground)];text(a,name,12,6,w-24,t,{bold:true});let y=48;for(const r of rows){const v=row(r,16,y,w-32,t);a.push(...v.children);y+=v.h;}a.push(...button('Cancel',w-170,y+20,72,t),...button('Keep',w-90,y+20,72,t));return board(dialogs,name,x,0,w,y+64,a,t.canvas);}
dialog('Terrain to Curves · layout reference',500,[{type:'Text',label:'Name',value:'Terrain curves'},{type:'Rule',label:'Outputs'},{type:'Bool',label:'Contours',checked:true},{type:'Bool',label:'Spot heights',checked:true},{type:'Bool',label:'Boundary',checked:true},{type:'Number',label:'Contour interval',value:'0.250',unit:'ModelLength'},{type:'Number',label:'Vertical tolerance',value:'0.050',unit:'ModelLength'},{type:'Number',label:'Recovery inputs per hectare',value:'1000'},{type:'Bool',label:'Preview',checked:true}],0);
dialog('Scan to Terrain · layout reference',500,[{type:'Text',label:'Name',value:'Scan terrain'},{type:'Rule',label:'Outputs'},{type:'Bool',label:'Contours',checked:true},{type:'Bool',label:'Spots',checked:true},{type:'Bool',label:'Buildings',checked:true},{type:'Bool',label:'Ground mesh'},{type:'Bool',label:'Show source'},{type:'Number',label:'Contour interval',value:'0.250',unit:'ModelLength'},{type:'Number',label:'Vertical tolerance',value:'0.050',unit:'ModelLength'},{type:'Rule',label:'Advanced'},{type:'ReadOnly',label:'Status',value:'Recompute to preview'}],560);
const survey=[];text(survey,'Import Survey Points',16,10,700,t,{bold:true,size:18,h:28});text(survey,'survey.csv',16,50,700,t,{bold:true});let sy=78;
for(const r of [{type:'Choice',label:'Format',value:'PNEZD'},{type:'Choice',label:'Separator',value:'Comma'},{type:'Number',label:'Header rows',value:'1'},{type:'Choice',label:'File units',value:'Metres'},{type:'Number',label:'Vertical offset',value:'0.000',unit:'ModelLength'}]){const v=row(r,16,sy,728,t);survey.push(...v.children);sy+=v.h;}
survey.push(rect('Preview grid',16,sy+12,728,260,t.InputBackground,t.border));for(let i=0;i<5;i++){text(survey,['Point','X','Y','Z','Code'][i],24+i*142,sy+20,130,t,{bold:true});for(let j=0;j<7;j++)text(survey,[String(j+1),(1000+j*3.5).toFixed(3),(2000+j*2.8).toFixed(3),(12+j*.1).toFixed(3),'SPOT'][i],24+i*142,sy+52+j*26,130,t);}survey.push(...button('Cancel',576,sy+292,76,t),...button('Import',664,sy+292,80,t));board(dialogs,'Import Survey Points · layout reference',1120,0,760,sy+340,survey,t.canvas);
const sculpt=[];let sx=10;for(const label of ['Raise','Lower','Erase','Smooth','Flatten','Grab','Clay','Noise']){sculpt.push(...button(label,sx,10,Math.max(44,label.length*7+8),t));sx+=Math.max(44,label.length*7+8)+2;}text(sculpt,'Radius',sx+10,12,42,t);sculpt.push(...slider(sx+58,8,140,t,'5.0'));text(sculpt,'Strength',sx+210,12,56,t);sculpt.push(...slider(sx+272,8,130,t,'50%'),...field('Smooth',sx+410,8,84,t,{dropdown:true}),...button('Done',sx+506,10,52,t));board(dialogs,'Sculpt floating toolbar',0,850,sx+572,44,sculpt,t.ToolbarBackground);
// Replace the initial abbreviated sketches with detailed source-backed secondary surfaces.
pages.splice(pages.indexOf(dialogs),1);
buildDialogs({page,board,themes,group,rect,text,txt,wrap,row,button,field,checkbox,line,read});
const sculptPage=page('08 • Sculpt toolbar');
board(sculptPage,'Sculpt floating toolbar',0,0,sx+572,44,sculpt,t.ToolbarBackground);

// Load the exact committed panel badge/tab bitmaps. Universal actions remain vector paths.
const context=createBuildContext({referer:'MoleHill source UI design generator'});
context.addFile({name:'MoleHill • Rhino UI reference'});
const fileId=context.currentFileId,media=new Map(),allScenes=pages.flatMap(p=>p.boards);
function walk(n,f){f(n);for(const c of n.children??[])walk(c,f);}
const resources=new Set();for(const s of allScenes)walk(s,n=>{if(n.resource)resources.add(n.resource);});
for(const name of resources){const filename=path.join(root,'src/MoleHill.Rhino/Resources',name+'.png');if(!fs.existsSync(filename))throw Error('Missing icon '+name);const bytes=fs.readFileSync(filename);const w=bytes.readUInt32BE(16),h=bytes.readUInt32BE(20);const id=context.addFileMedia({name,width:w,height:h},new Blob([bytes],{type:'image/png'}));media.set(name,{id,width:w,height:h,mtype:'image/png'});}
const colorIds=new Map(),typeIds=new Map();
const tokenSets=Object.fromEntries(['dark','light'].map(theme=>[theme,Object.fromEntries(Object.entries(semanticRoles).map(([role,values])=>[theme+'.'+role,{$type:'color',$value:values[theme]}]))]));
context.addTokensLib({...tokenSets,$metadata:{tokenSetOrder:['dark','light'],activeSets:['dark','light']}});
let spectrumGradient;for(const s of allScenes)walk(s,n=>{if(n.gradient&&!spectrumGradient)spectrumGradient=n.gradient;});
colorIds.set('Analysis/Terrain Spectrum',context.addLibraryColor({name:'Terrain Spectrum',path:'Analysis',gradient:spectrumGradient,opacity:1}));
const typography=new Set();for(const s of allScenes)walk(s,n=>{if(n.type==='text')typography.add(n.size+'/'+(n.bold?'700':'400'));});
for(const key of typography){const [size,weight]=key.split('/');typeIds.set(key,context.addLibraryTypography({name:(size==='12'?'Body':size==='11'?'Meta':'Text '+size)+(weight==='700'?' Bold':''),path:'MoleHill',fontId:'sourcesanspro',fontFamily:'Source Sans Pro',fontVariantId:weight==='700'?'bold':'regular',fontSize:size,fontWeight:weight,fontStyle:'normal',lineHeight:'1.2',letterSpacing:'0',textTransform:'none'}));}
function libraryFill(color,n,theme){
    let matches=Object.entries(tokens).filter(([,v])=>v[theme]===color);
    const preferred=/Histogram/.test(n.name)?'RampHistogram':/Input/.test(n.name)?'InputBackground':/button|toolbar/i.test(n.name)?'ToolbarBackground':/Ramp header/.test(n.name)?'RampHeader':/Card header|section|Title bar/.test(n.name)?'HeaderBackground':/Card surface/.test(n.name)?'CardBackground':/Warning/.test(n.name)?'WarningBackground':null;
    const key=matches.find(([k])=>k===preferred)?.[0]??matches[0]?.[0];
    return color?{fillColor:color,fillOpacity:key?tokens[key][theme+'Opacity']:1}:null;
}
function semanticToken(color,n,theme){
    if(n.semanticRole&&semanticRoles[n.semanticRole]?.[theme]===color)return theme+'.'+n.semanticRole;
    if(!color||/Histogram|Ramp header|Color swatch|Icon plate|Accent strip/.test(n.name))return null;
    const preferred=n.type==='text'?'text.primary':/Input/.test(n.name)?'surface.input':/button|toolbar/i.test(n.name)?'surface.toolbar':/Card header|section|Title bar/.test(n.name)?'surface.header':n.type==='frame'&&n.name!=='Triangulate'?'surface.card':'surface.canvas';
    const matches=Object.entries(semanticRoles).filter(([,values])=>values[theme]===color);
    const role=matches.find(([role])=>role===preferred)?.[0]??matches[0]?.[0];
    return role?theme+'.'+role:null;
}

// Actual components are exported on their source page before screen instances.
const ordered=[...pages.filter(p=>p===intro||p===foundations||p===library),...pages.filter(p=>p!==intro&&p!==foundations&&p!==library)];
function emit(n,dx=0,dy=0,inComponent=false,parentTheme='dark'){
    const theme=n.theme??(n.fill==='#ffffff'||n.fill===themes.light.canvas?'light':parentTheme);
    const x=n.x+dx,y=n.y+dy, attrs={id:n.id,name:n.name,x,y,width:n.w,height:n.h,fills:n.fill?[libraryFill(n.fill,n,theme)]:[],strokes:[],constraintsH:n.anchorH??'left',constraintsV:n.anchorV??'top'};
    const fillToken=semanticToken(n.fill,n,theme),strokeToken=semanticToken(n.type==='path'?n.color:n.stroke,n,theme);
    if(fillToken||strokeToken)attrs.appliedTokens={...(fillToken?{fill:fillToken}:{}),...(strokeToken?{strokeColor:strokeToken}:{})};
    if(n.hidden!==undefined)attrs.hidden=n.hidden;
    if(n.gradient)attrs.fills=[{fillColorGradient:n.gradient,fillOpacity:1,fillColorRefId:colorIds.get('Analysis/Terrain Spectrum'),fillColorRefFile:fileId}];
    for(const [key,value]of Object.entries(n))if(key.startsWith('layoutItem'))attrs[key]=value;
    if(n.touched)attrs.touched=n.touched;
    if(n.ref)attrs.shapeRef=n.ref;
    if(n.instance){attrs.componentId=n.componentId;attrs.componentFile=fileId;if(!inComponent)attrs.componentRoot=true;}
    if(n.stroke)attrs.strokes=[{strokeColor:n.stroke,strokeOpacity:1,strokeWidth:1,strokeAlignment:'inner',strokeStyle:'solid'}];
    if(n.type==='frame'){
        context.addBoard({...attrs,...n.layout,showContent:false});
        for(const c of n.children??[])emit(c,x,y,inComponent||!!n.componentId,theme);
        if(n.componentId&&!n.instance)context.addComponent({componentId:n.componentId,name:n.componentName,path:n.componentPath,frameId:n.id});
        context.closeBoard();
    }else if(n.type==='text'){
        const style={fontId:'sourcesanspro',fontFamily:'Source Sans Pro',fontVariantId:n.bold?'bold':'regular',fontWeight:n.bold?'700':'400',fontSize:String(n.size),fontStyle:'normal',lineHeight:'1.2',letterSpacing:'0',textTransform:'none',textDecoration:'none',typographyRefId:typeIds.get(n.size+'/'+(n.bold?'700':'400')),typographyRefFile:fileId,fills:[libraryFill(n.fill,n,theme)]};
        context.addText({...attrs,growType:'fixed',content:{type:'root',children:[{type:'paragraph-set',children:n.text.split('\n').map(text=>({type:'paragraph',textAlign:'left',...style,children:[{text,...style}]}))}]}});
    }else if(n.type==='path'){
        const points=n.points.map(([px,py])=>[px+dx,py+dy]);
        const ref=libraryFill(n.color,n,theme);
        context.addPath({...attrs,content:points.map(([px,py],i)=>({command:i?'line-to':'move-to',params:{x:px,y:py}})),strokes:[{strokeColor:n.color,strokeOpacity:1,strokeWidth:n.lineWidth,strokeStyle:'solid',strokeAlignment:'center',...(ref?.fillColorRefId?{strokeColorRefId:ref.fillColorRefId,strokeColorRefFile:fileId}:{})}]});
    }else if(n.type==='image')context.addRect({...attrs,fills:[{fillOpacity:1,fillImage:media.get(n.resource)}]});
    else if(n.type==='circle')context.addCircle(attrs);
    else context.addRect(attrs);
}
for(const p of ordered){context.addPage({id:p.id,name:p.name});for(const b of p.boards)emit(b);context.closePage();}

// Validate graph relationships, component references and positive geometry before export.
const state=context.getInternalState(),file=state.files[fileId],data=file.data;
let shapeCount=0,instanceCount=0;
for(const [pageId,p]of Object.entries(data.pagesIndex)){
    for(const [id,s]of Object.entries(p.objects)){
        shapeCount++;
        if(id==='00000000-0000-0000-0000-000000000000')continue;
        if(!p.objects[s.parentId])throw Error('Missing parent '+s.name);
        if(!(s.width>0&&s.height>0))throw Error('Invalid bounds '+s.name);
        if(s.componentId){if(!data.components[s.componentId])throw Error('Missing component '+s.name);if(!s.mainInstance)instanceCount++;}
        if(s.shapeRef){const main=Object.values(data.pagesIndex).some(page=>page.objects[s.shapeRef]);if(!main)throw Error('Missing shape ref '+s.name);}
        for(const child of s.shapes??[])if(p.objects[child]?.parentId!==id)throw Error('Parent-child mismatch '+s.name);
    }
}
context.closeFile();
const archive=await exportAsBytes(context);
fs.writeFileSync(path.join(out,'MoleHill-UI.penpot'),archive);
fs.writeFileSync(path.join(out,'source-inventory.json'),JSON.stringify({generatedAt:new Date().toISOString(),cards,tokens,limitations:['Native Penpot import not yet verified','Native Eto widgets approximated','Sample values are not runtime defaults','Secondary dialogs are partial layout references']},null,2));
fs.writeFileSync(path.join(out,'scene.json'),JSON.stringify(ordered));
const escape=s=>String(s).replace(/[&<>"']/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&apos;'}[c]));
function svg(n,dx=0,dy=0){const x=n.x+dx,y=n.y+dy;
    if(n.hidden)return '';
    if(n.type==='frame')return `<g id="${n.id}"><title>${escape(n.name)}</title>${n.fill?`<rect x="${x}" y="${y}" width="${n.w}" height="${n.h}" fill="${n.fill}"/>`:''}${(n.children??[]).map(c=>svg(c,x,y)).join('')}</g>`;
    if(n.type==='text')return `<text x="${x}" y="${y+n.size}" font-family="Source Sans Pro,Segoe UI,sans-serif" font-size="${n.size}" font-weight="${n.bold?700:400}" fill="${n.fill}">${n.text.split('\n').map((line,i)=>`<tspan x="${x}" dy="${i?18:0}">${escape(line)}</tspan>`).join('')}</text>`;
    if(n.type==='path')return `<path d="${n.points.map(([px,py],i)=>(i?'L':'M')+(px+dx)+','+(py+dy)).join(' ')}" fill="${n.fill??'none'}" stroke="${n.color}" stroke-width="${n.lineWidth}" stroke-linejoin="round" stroke-linecap="round"/>`;
    if(n.type==='image')return `<image x="${x}" y="${y}" width="${n.w}" height="${n.h}" href="data:image/png;base64,${fs.readFileSync(path.join(root,'src/MoleHill.Rhino/Resources',n.resource+'.png')).toString('base64')}"/>`;
    if(n.type==='circle')return `<ellipse cx="${x+n.w/2}" cy="${y+n.h/2}" rx="${n.w/2}" ry="${n.h/2}" fill="${n.fill}"/>`;
    if(n.gradient)return `<defs><linearGradient id="g${n.id}">${n.gradient.stops.map(s=>`<stop offset="${s.offset}" stop-color="${s.color}"/>`).join('')}</linearGradient></defs><rect x="${x}" y="${y}" width="${n.w}" height="${n.h}" fill="url(#g${n.id})"/>`;
    return `<rect x="${x}" y="${y}" width="${n.w}" height="${n.h}" fill="${n.fill??'none'}" ${n.stroke?`stroke="${n.stroke}"`:''}/>`;
}
const previewDir=path.join(out,'previews');fs.mkdirSync(previewDir,{recursive:true});
for(const p of pages.filter(p=>p!==library)){
    const w=Math.max(...p.boards.map(b=>b.x+b.w))+20,h=Math.max(...p.boards.map(b=>b.y+b.h))+20;
    fs.writeFileSync(path.join(previewDir,p.name.replace(/[^\w-]+/g,'-')+'.svg'),`<svg xmlns="http://www.w3.org/2000/svg" width="${w}" height="${h}" viewBox="0 0 ${w} ${h}">${p.boards.map(b=>svg(b)).join('')}</svg>`);
}
const rendered=[];
for(const p of pages.filter(p=>p.name.includes('screens')||p===primitives||p===states))for(const b of p.boards){
    const name=b.name.replace(/[^\w-]+/g,'-'),content=`<svg xmlns="http://www.w3.org/2000/svg" width="${b.w}" height="${b.h}" viewBox="0 0 ${b.w} ${b.h}">${svg({...b,x:0,y:0})}</svg>`;
    fs.writeFileSync(path.join(previewDir,name+'.svg'),content);
    fs.writeFileSync(path.join(previewDir,name+'.png'),new Resvg(content,{font:{loadSystemFonts:true,defaultFontFamily:'Segoe UI'}}).render().asPng());
    rendered.push({name:b.name,file:name+'.png'});
}
fs.writeFileSync(path.join(out,'preview.html'),`<!doctype html><meta charset="utf-8"><title>MoleHill UI reference</title><style>body{font:14px Segoe UI,sans-serif;background:#e8e8e8;color:#222;margin:32px}main{display:flex;flex-wrap:wrap;align-items:start;gap:24px}figure{margin:0}figcaption{margin:0 0 12px;font-weight:600}img{box-shadow:0 2px 8px #0002;max-width:480px}a{color:#1565c0}</style><h1>MoleHill UI reference</h1><p><a href="MoleHill-UI.penpot">Native Penpot file</a> · <a href="source-inventory.json">Source inventory</a> · Screens are editable reconstructions; native Penpot import has not yet been verified.</p><main>${rendered.map(r=>`<figure><figcaption>${escape(r.name)}</figcaption><a href="previews/${r.file}"><img src="previews/${r.file}" alt="${escape(r.name)}"></a></figure>`).join('')}</main>`);
const report={pages:ordered.length,boards:ordered.reduce((n,p)=>n+p.boards.length,0),components:components.size,fieldComponents:[...components.keys()].filter(k=>k.startsWith('Field / ')).length,instances:instanceCount,shapes:shapeCount,cardTypes:cards.length,semanticColorRoles:Object.keys(semanticRoles).length,colorTokenValues:Object.keys(semanticRoles).length*2,libraryColors:colorIds.size,archiveBytes:archive.length,nativeImportVerified:false};
fs.writeFileSync(path.join(out,'validation.json'),JSON.stringify(report,null,2));
console.log(JSON.stringify(report,null,2));
