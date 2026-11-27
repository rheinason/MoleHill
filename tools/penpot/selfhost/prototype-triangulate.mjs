// Add one isolated, editable comparison page. Existing pages and masters stay intact.
import fs from 'node:fs';
import path from 'node:path';
import {randomUUID,createHash} from 'node:crypto';
import transit from 'transit-js';
const here=import.meta.dirname,base='http://localhost:9001',zero='00000000-0000-0000-0000-000000000000';
const draftPath=path.join(here,'design-draft.local.json');
const draft=JSON.parse(fs.readFileSync(draftPath,'utf8'));
const anchoredImportStep=process.argv.includes('--anchored-import');
const panelAlignmentStep=process.argv.includes('--panel-alignment');
const layerTransferStep=process.argv.includes('--layer-transfer');
const definitionTransferStep=process.argv.includes('--definition-transfer');
const panelWalkthroughStep=process.argv.includes('--panel-walkthrough');
const mapAnalysisStep=process.argv.includes('--map-analyses');
const rampFeaturesStep=process.argv.includes('--ramp-features');
const curvatureStep=process.argv.includes('--curvature');
const gradientComplianceStep=process.argv.includes('--gradient-compliance');
const resultsHeadingStep=process.argv.includes('--results-heading');
const waterflowStep=process.argv.includes('--waterflow');
const drainageStep=process.argv.includes('--drainage');
const cutFillStep=process.argv.includes('--cut-fill');
const earthworksCopyStep=process.argv.includes('--earthworks-copy');
const earthworksStep=process.argv.includes('--earthworks');
const zoneSummaryStep=process.argv.includes('--zone-summary');
const sectionContextStep=process.argv.includes('--section-context');
const sectionHeadersStep=process.argv.includes('--section-headers');
const objectPlacementStep=process.argv.includes('--object-placement');
const scatterPlacementStep=process.argv.includes('--scatter-placement');
const scatterMixDisclosureStep=process.argv.includes('--scatter-mix-disclosure');
const scatterCompactMixStep=process.argv.includes('--scatter-compact-mix');
const scatterBlockFieldsStep=process.argv.includes('--scatter-block-fields');
const scatterControlSizesStep=process.argv.includes('--scatter-control-sizes');
const scatterOrderStep=process.argv.includes('--scatter-order');
const scatterLayoutStep=process.argv.includes('--scatter-layout');
const simplifyModesStep=process.argv.includes('--simplify-modes');
const displaceOriginStep=process.argv.includes('--displace-origin');
const displacePlacementStep=process.argv.includes('--displace-placement');
const displaceModesStep=process.argv.includes('--displace-modes');
const smoothBoundariesStep=process.argv.includes('--smooth-boundaries-first');
const smoothStrengthStep=process.argv.includes('--smooth-strength-first');
const remeshModeFirstStep=process.argv.includes('--remesh-mode-first');
const remeshModesStep=process.argv.includes('--remesh-modes');
const numericDefaultsStep=process.argv.includes('--numeric-defaults');
const inlineUnitsStep=process.argv.includes('--inline-units');
const gradePathStep=process.argv.includes('--grade-path');
const gradeLineStep=process.argv.includes('--grade-line');
const wallSlopeOverridesStep=process.argv.includes('--wall-slope-overrides');
const retainingWallStep=process.argv.includes('--retaining-wall');
const gradeCalloutStep=process.argv.includes('--grade-callout');
const legendStep=process.argv.includes('--legend');
const reportTableStep=process.argv.includes('--report-table');
const modeDropdownAuditStep=process.argv.includes('--mode-dropdown-audit');
const spotModeSelectorStep=process.argv.includes('--spot-mode-selector');
const spotGridPlacementStep=process.argv.includes('--spot-grid-placement');
const spotHeightPlacementStep=process.argv.includes('--spot-height-placement');
const sectionAutoSamplingStep=process.argv.includes('--section-auto-sampling');
const sectionAlongCurveStep=process.argv.includes('--section-along-curve');
const sectionLineStep=process.argv.includes('--section-line');
const sectionCutStep=process.argv.includes('--section-cut');
const crossSectionComparisonStep=process.argv.includes('--cross-section-comparison');
const crossSectionExpandedAnnotationsStep=process.argv.includes('--cross-section-expanded-annotations');
const crossSectionPathsStep=process.argv.includes('--cross-section-paths');
const crossSectionPickedPillStep=process.argv.includes('--cross-section-picked-pill');
const crossSectionAlignedInputsStep=process.argv.includes('--cross-section-aligned-inputs');
const crossSectionSimpleStep=process.argv.includes('--simple-cross-sections');
const contourFlushRowsStep=process.argv.includes('--contour-flush-rows');
const contourAlignedToggleStep=process.argv.includes('--contour-aligned-toggle');
const contourHierarchyStep=process.argv.includes('--contour-hierarchy');
const groupingCompareStep=process.argv.includes('--compare-header-groups');
const bareTypeIconStep=process.argv.includes('--bare-type-icon');
const borderlessActionsStep=process.argv.includes('--borderless-actions');
const compactControlsStep=process.argv.includes('--compact-controls');
const xHeightStep=process.argv.includes('--xheight-headers');
const headerAlignStep=process.argv.includes('--align-headers');
const headerOrderStep=process.argv.includes('--header-order');
const fixedHeaderStep=process.argv.includes('--fixed-headers');
const sourceSummaryStep=process.argv.includes('--source-summary');
const fullReviewStep=process.argv.includes('--full-review');
const wordingStep=process.argv.includes('--wording');
const softBorderStep=process.argv.includes('--soft-borders');
const annotationIconStep=process.argv.includes('--annotation-icons');
const earthTabStep=process.argv.includes('--earth-tabs')||annotationIconStep;
const tabIconStep=process.argv.includes('--tab-icons')||earthTabStep;
const nameExampleStep=process.argv.includes('--name-example');
const buttonLayoutStep=process.argv.includes('--button-layout')||softBorderStep;
const coreLayoutStep=process.argv.includes('--core-layout')||buttonLayoutStep;
const settingsStep=process.argv.includes('--settings')||coreLayoutStep,stepNumber=anchoredImportStep?'80':panelAlignmentStep?'79':layerTransferStep?'78':definitionTransferStep?'77':panelWalkthroughStep?'76':mapAnalysisStep?'75':rampFeaturesStep?'74':curvatureStep?'73':gradientComplianceStep?'72':resultsHeadingStep?'71':waterflowStep?'70':drainageStep?'69':cutFillStep?'68':earthworksCopyStep?'67':earthworksStep?'66':zoneSummaryStep?'65':sectionContextStep?'64':sectionHeadersStep?'63':objectPlacementStep?'62':scatterPlacementStep?'61':scatterMixDisclosureStep?'60':scatterCompactMixStep?'59':scatterBlockFieldsStep?'58':scatterControlSizesStep?'57':scatterOrderStep?'56':scatterLayoutStep?'55':simplifyModesStep?'54':displaceOriginStep?'53':displacePlacementStep?'52':displaceModesStep?'51':smoothBoundariesStep?'50':smoothStrengthStep?'49':remeshModeFirstStep?'48':remeshModesStep?'47':numericDefaultsStep?'46':inlineUnitsStep?'45':gradePathStep?'44':gradeLineStep?'43':wallSlopeOverridesStep?'42':retainingWallStep?'41':gradeCalloutStep?'40':legendStep?'39':reportTableStep?'38':modeDropdownAuditStep?'37':spotModeSelectorStep?'36':spotGridPlacementStep?'35':spotHeightPlacementStep?'34':sectionAutoSamplingStep?'33':sectionAlongCurveStep?'32':sectionLineStep?'31':sectionCutStep?'30':crossSectionComparisonStep?'29':crossSectionExpandedAnnotationsStep?'28':crossSectionPathsStep?'27':crossSectionPickedPillStep?'26':crossSectionAlignedInputsStep?'25':crossSectionSimpleStep?'24':contourFlushRowsStep?'23':contourAlignedToggleStep?'22':contourHierarchyStep?'21':groupingCompareStep?'20':bareTypeIconStep?'19':borderlessActionsStep?'18':compactControlsStep?'17':xHeightStep?'16':headerAlignStep?'15':headerOrderStep?'14':fixedHeaderStep?'13':sourceSummaryStep?'12':fullReviewStep?'11':wordingStep?'10':softBorderStep?'09':annotationIconStep?'08':earthTabStep?'07':tabIconStep?'06':nameExampleStep?'05':buttonLayoutStep?'04':coreLayoutStep?'03':settingsStep?'02':'01';
const credentials=JSON.parse(fs.readFileSync(path.join(here,'credentials.local.json'),'utf8'));
const out=path.resolve(here,'../../../.artifacts/penpot-selfhost/design-step-'+stepNumber);
fs.mkdirSync(out,{recursive:true});
const kw=transit.keyword,uuid=transit.uuid;
function plain(v){
    if(transit.isKeyword(v)||transit.isUUID(v))return v.toString().replace(/^:/,'');
    if(transit.isBinary(v))return new Uint8Array(v);
    if(v?.tag&&Object.hasOwn(v,'rep'))return plain(v.rep);
    if(transit.isMap(v)){const result={};v.forEach((value,key)=>result[plain(key)]=plain(value));return result;}
    if(transit.isSet(v))return [...v.values()].map(plain);
    if(Array.isArray(v))return v.map(plain);
    if(v instanceof Date)return v.toISOString();
    if(v&&typeof v==='object')return Object.fromEntries(Object.entries(v).map(([key,value])=>[key,plain(value)]));
    return v;
}
const map=o=>transit.map(Object.entries(o).flatMap(([k,v])=>[kw(k),v]));
let cookie='';
async function request(route,body){
    const r=await fetch(base+route,{method:'POST',headers:{Accept:'application/transit+json','Content-Type':'application/transit+json',Cookie:cookie,Origin:base,Referer:base+'/'},body:transit.writer('json').write(body),signal:AbortSignal.timeout(180000)});
    const set=r.headers.getSetCookie();if(set.length)cookie=set.map(s=>s.split(';')[0]).join('; ');
    const content=await r.text();if(!r.ok)throw Error(route+' ('+r.status+'): '+content.slice(0,1800));
    return transit.reader('json').read(content);
}
const rpc=(command,body)=>request('/api/rpc/command/'+command,map(body));
function rampMappingLayout(mapping, left, span, children) {
    const segments = children(mapping);
    const positions = [[mapping, left, mapping.y, span, mapping.height]];
    for (const [index, segment] of segments.entries()) {
        const x = left + span * index / segments.length;
        const width = span / segments.length;
        positions.push([segment, x, segment.y, width, segment.height]);
        for (const part of children(segment)) {
            positions.push(part.type === 'text'
                ? [part, x + 2, segment.y + 2, width - 4, segment.height - 4]
                : [part, x, segment.y, width, segment.height]);
        }
    }
    return positions;
}
function downDisclosure(arrow) {
    const cx = arrow.x + arrow.width / 2, cy = arrow.y + arrow.height / 2;
    const width = arrow.height, height = arrow.width, x = cx - width / 2, y = cy - height / 2;
    const content = arrow.content.slice();
    const path = new DataView(content.buffer, content.byteOffset, content.byteLength);
    for (let i = 0; i < path.byteLength; i += 28) {
        const px = path.getFloat32(i + 20, true), py = path.getFloat32(i + 24, true);
        path.setFloat32(i + 20, cx + cy - py, true);
        path.setFloat32(i + 24, cy + px - cx, true);
    }
    return {x, y, width, height, content,
        selrect: {x, y, width, height, x1: x, y1: y, x2: x + width, y2: y + height},
        points: [{x, y}, {x: x + width, y}, {x: x + width, y: y + height}, {x, y: y + height}]};
}
function curvatureFieldBounds(label, editor, surface, value) {
    const inset = 16;
    return [
        [label, label.x, label.width + inset],
        ...[editor, surface, value].map(shape => [shape, shape.x + inset, shape.width - inset]),
    ];
}
const features=['fdata/path-data','fdata/shape-data-type','design-tokens/v1','variants/v1','layout/grid','components/v2','styles/v2','plugins/runtime'];
const profile=plain(await rpc('login-with-password',{email:credentials.email,password:credentials.password}));
const getFile=id=>rpc('get-file',{id:uuid(id),features:transit.set(features)});
const baselineBefore=plain(await rpc('get-file-stats',{id:uuid(draft.sourceFileId)}));
const raw=await getFile(draft.fileId),file=plain(raw),pages=file.data['pages-index'];
if(!pages)throw Error('Expected resolved native pages.');
const pageName=anchoredImportStep?'88 • Step 80 — Import anchored to terrain selector':panelAlignmentStep?'87 • Step 79 — Full panel alignment':layerTransferStep?'86 • Step 78 — Terrain layer assignments':definitionTransferStep?'85 • Step 77 — Terrain definition transfer':panelWalkthroughStep?'84 • Step 76 — Full panel walkthrough':mapAnalysisStep?'83 • Step 75 — Slope Elevation and Aspect':rampFeaturesStep?'82 • Step 74 — Color ramp features':curvatureStep?'81 • Step 73 — Curvature and Landforms':gradientComplianceStep?'80 • Step 72 — Gradient Compliance rules':resultsHeadingStep?'79 • Step 71 — Results headings':waterflowStep?'78 • Step 70 — Waterflow length limit':drainageStep?'77 • Step 69 — Catchments and Ponding':cutFillStep?'76 • Step 68 — Cut and Fill drawing outputs':earthworksCopyStep?'75 • Step 67 — Selectable Earthworks results':earthworksStep?'74 • Step 66 — Earthworks comparison modes':zoneSummaryStep?'73 • Step 65 — Zone card and Last Build':sectionContextStep?'72 • Step 64 — Section headers in the Objects panel':sectionHeadersStep?'71 • Step 63 — Section header visibility':objectPlacementStep?'70 • Step 62 — Plant and Orient actions':scatterPlacementStep?'69 • Step 61 — Scatter placement modes':scatterMixDisclosureStep?'68 • Step 60 — Collapsible Scatter block mix':scatterCompactMixStep?'67 • Step 59 — Compact Scatter block mix':scatterBlockFieldsStep?'66 • Step 58 — Scatter block fields on the shared grid':scatterControlSizesStep?'65 • Step 57 — Scatter control sizes':scatterOrderStep?'64 • Step 56 — Scatter where what and how':scatterLayoutStep?'63 • Step 55 — Scatter systems and exclusions':simplifyModesStep?'62 • Step 54 — Simplify target modes':displaceOriginStep?'61 • Step 53 — Pick Displace origin':displacePlacementStep?'60 • Step 52 — Displace image placement and coverage':displaceModesStep?'59 • Step 51 — Displace noise and image':smoothBoundariesStep?'58 • Step 50 — Smooth boundaries before settings':smoothStrengthStep?'57 • Step 49 — Smooth strength first':remeshModeFirstStep?'56 • Step 48 — Remesh mode first':remeshModesStep?'55 • Step 47 — Remesh modes':numericDefaultsStep?'54 • Approved defaults — Numeric fields':inlineUnitsStep?'53 • Step 45 — Units inside aligned fields':gradePathStep?'52 • Step 44 — Grade Path variable width':gradeLineStep?'51 • Step 43 — Grade Line left and right slopes':wallSlopeOverridesStep?'50 • Step 42 — Retaining Wall slope overrides inherit when blank':retainingWallStep?'49 • Step 41 — Retaining Wall top and toe slopes':gradeCalloutStep?'48 • Step 40 — Grade Callout results and appearance':legendStep?'47 • Step 39 — Legend follows terrain colouring':reportTableStep?'46 • Step 38 — Report Table contents and appearance':modeDropdownAuditStep?'45 • Step 37 — Mode buttons across cards':spotModeSelectorStep?'44 • Step 36 — Spot placement mode buttons':spotGridPlacementStep?'43 • Step 35 — Grid placement for spot labels':spotHeightPlacementStep?'42 • Step 34 — Spot Heights placement options':sectionAutoSamplingStep?'41 • Step 33 — Section sampling follows detail size':sectionAlongCurveStep?'40 • Step 32 — Simplified Section Along Curve':sectionLineStep?'39 • Step 31 — Section Line and direct marks':sectionCutStep?'38 • Step 30 — Simplified Section Cut':crossSectionComparisonStep?'37 • Step 29 — Explicit cross-section comparison':crossSectionExpandedAnnotationsStep?'36 • Step 28 — Plan and section annotation expanded':crossSectionPathsStep?'35 • Step 27 — Cross-section Paths input':crossSectionPickedPillStep?'34 • Step 26 — Insertion picked and clear pattern':crossSectionAlignedInputsStep?'33 • Step 25 — Cross-section input edges aligned':crossSectionSimpleStep?'32 • Step 24 — Simple cross-sections and insertion point':contourFlushRowsStep?'31 • Step 23 — Contour settings on one property grid':contourAlignedToggleStep?'30 • Step 22 — Contour toggle in control column':contourHierarchyStep?'29 • Step 21 — Contour labels reveal settings':groupingCompareStep?'28 • Step 20 — Header grouping side by side':bareTypeIconStep?'27 • Step 19 — Bare card type icon':borderlessActionsStep?'26 • Step 18 — Borderless header actions':compactControlsStep?'25 • Step 17 — Smaller header controls':xHeightStep?'24 • Step 16 — X-height alignment and smaller badge':headerAlignStep?'23 • Step 15 — Aligned B headers':headerOrderStep?'22 • Step 14 — Header control order comparison':fixedHeaderStep?'21 • Step 13 — Fixed two-line collapsed headers':sourceSummaryStep?'20 • Step 12 — Compact source counts and summaries':fullReviewStep?'19 • Step 11 — Consolidated panel review':wordingStep?'18 • Step 10 — Approved wording':softBorderStep?'17 • Step 09 — Softer routine borders':annotationIconStep?'16 • Step 08 — Annotation icon alternatives':earthTabStep?'15 • Step 07 — Earthy tabs and zones icon':tabIconStep?'14 • Step 06 — Compact tab icon comparison':nameExampleStep?'13 • Step 05 — Custom card names comparison':buttonLayoutStep?'12 • Step 04 — Button alignment':coreLayoutStep?'11 • Step 03 — Fixed sources and stack order':settingsStep?'10 • Step 02 — Triangulate settings':'09 • Step 01 — Triangulate source groups';
const existing=Object.values(pages).find(p=>p.name===pageName);
const source=Object.values(pages).find(p=>p.name==='05 • Modifiers screens');
const sourceRaw=raw.get(kw('data')).get(kw('pages-index')).get(uuid(source.id));
const rawObjects=sourceRaw.get(kw('objects'));
const allObjects=Object.assign({},...Object.values(pages).map(p=>p.objects));
const allRawObjects=new Map();
raw.get(kw('data')).get(kw('pages-index')).forEach(p=>p.get(kw('objects')).forEach((s,id)=>allRawObjects.set(id.toString(),s)));
let comparisonPage;
if(existing){comparisonPage=existing;console.log('Retaining existing comparison page.');}
else {
    const pageId=randomUUID(),objects={},templates=new Map(),componentChanges=[];
    const textOf=n=>n?.text??(n?.children??[]).map(textOf).join('');
    const snapshot=new Map(),sourceInventory=fullReviewStep||sectionCutStep||modeDropdownAuditStep||reportTableStep||legendStep||gradeCalloutStep?JSON.parse(fs.readFileSync(path.resolve(here,'../output/source-inventory.json'),'utf8')):null;
    function buildSourceSummaries(){
        const reference=Object.values(pages).find(p=>p.name==='19 • Step 11 — Consolidated panel review');
        const inventory=JSON.parse(fs.readFileSync(path.resolve(here,'../output/source-inventory.json'),'utf8')).cards;
        const examples={Smooth:[0,1],'Grade Pad':[2,0],Plant:[2,1],Scatter:[0,1],Earthworks:[1,0],'Section Cut':[3,2],'Grade Callout':[2,0],'Soft Landscape':[0,1]};
        const sampleText=(n,singular)=>n+' '+singular+(n===1?'':'s');
        function labelOf(row){return (row.shapes??[]).map(id=>objects[id]).find(s=>s.type==='text');}
        for(const original of reference.objects[zero].shapes.map(id=>reference.objects[id])){
            const screen=cloneTree(original.id);screen.name=original.name.replace('Step 11','Step 12');
            const cards=screen.shapes.map(id=>objects[id]).filter(s=>s.name!=='Panel shell');
            for(const card of cards){
                const children=card.shapes.map(id=>objects[id]),header=children.find(s=>s.name==='Header'),body=children.find(s=>s.name==='Parameters');
                if(!header)continue;
                if(body){
                    const rows=body.shapes.map(id=>objects[id]);
                    const countRow=rows.find(r=>r.name==='ReadOnly property row'&&textOf(labelOf(r)?.content)==='Sources');
                    const picker=rows.find(r=>r.name==='Sources property row'&&textOf(labelOf(r)?.content)==='Sources');
                    if(countRow&&picker){
                        const texts=[];descendants(countRow,s=>{if(s.type==='text')texts.push(textOf(s.content));});
                        const total=texts.find(t=>/^\d+$/.test(t));if(total===undefined)throw Error('Missing source total.');
                        setText(labelOf(picker),'Sources — '+total);
                        const index=body.shapes.indexOf(countRow.id),removed=countRow.height;
                        for(const id of body.shapes.slice(index+1))move(objects[id],0,-removed);
                        body.shapes=body.shapes.filter(id=>id!==countRow.id);
                        bounds(body,body.x,body.y,body.width,body.height-removed);bounds(card,card.x,card.y,card.width,card.height-removed);
                        const accent=children.find(s=>s.name==='Accent strip');bounds(accent,accent.x,accent.y,accent.width,card.height);
                        const cardIndex=screen.shapes.indexOf(card.id);for(const id of screen.shapes.slice(cardIndex+1))move(objects[id],0,-removed);
                        console.log('Merged source count in '+screen.name+' / '+card.name);
                    }
                    continue;
                }
                const meta=header.shapes.map(id=>objects[id]).find(s=>s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-size']==='11'&&s.y>=header.y+30&&s.height>=18);
                if(!meta)continue; // Compact headers have no spare metadata line; do not add height.
                const descriptor=inventory.find(c=>c.name===card.name);
                const hasSources=descriptor?.rows.some(r=>r.type==='Sources')||descriptor?.kind==='terrain-section'||card.name==='Soft Landscape';
                const type=card.name==='Soft Landscape'?'Zone':descriptor?.name??card.name;
                if(hasSources){const [objectsCount,layersCount]=examples[card.name]??[1,0];setText(meta,type+' · '+sampleText(objectsCount,'object')+' · '+sampleText(layersCount,'layer'));}
                else setText(meta,type+' · '+textOf(meta.content));
            }
            objects[zero].shapes.push(screen.id);
        }
    }
    function buildFixedHeaders(){
        const reference=Object.values(pages).find(p=>p.name==='20 • Step 12 — Compact source counts and summaries');
        const screens=reference.objects[zero].shapes.map(id=>reference.objects[id]),tooltips=[];
        const fontSize=s=>s.content?.children?.[0]?.children?.[0]?.['font-size'];
        const fontWeight=s=>s.content?.children?.[0]?.children?.[0]?.['font-weight'];
        function fit(value,width,size){
            // Conservative prototype estimate. Runtime must measure using its actual font.
            const measure=text=>[...text].reduce((n,c)=>n+size*(/[ilI1'.,:;· ]/.test(c)?.28:/[MW@]/.test(c)?.86:/[A-Z]/.test(c)?.65:.52),0);
            if(measure(value)<=width)return value;let clipped=value;
            while(clipped&&measure(clipped+'…')>width)clipped=clipped.slice(0,-1);
            return clipped.trimEnd()+'…';
        }
        function fixedScreen(original,parent=zero,dx=0,dy=0){
            const screen=cloneTree(original.id,parent,dx,dy),parts=original.name.split(' / ');screen.name=original.name.replace('Step 12','Step 13');
            const normal=screens.find(s=>s.name===['Step 12',parts[1],parts[2],'320'].join(' / '));
            let heightDelta=0;
            for(const card of screen.shapes.map(id=>objects[id]).filter(s=>s.name!=='Panel shell'&&s.shapes&&!s.shapes.some(id=>objects[id].name==='Parameters'))){
                const header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header');
                if(!header)continue;
                const title=header.shapes.map(id=>objects[id]).find(s=>s.type==='text'&&fontWeight(s)==='700');
                const normalCard=normal.shapes.map(id=>reference.objects[id]).find(s=>s.name===card.name);
                const normalHeader=normalCard.shapes.map(id=>reference.objects[id]).find(s=>s.name==='Header');
                const summarySource=normalHeader.shapes.map(id=>reference.objects[id]).find(s=>s.type==='text'&&fontSize(s)==='11');
                if(!title||!summarySource)throw Error('Missing name/summary for fixed header: '+card.name);
                const fullName=textOf(title.content),fullSummary=textOf(summarySource.content),oldHeight=card.height,delta=52-oldHeight;
                header.shapes=header.shapes.filter(id=>!(objects[id].type==='text'&&fontSize(objects[id])==='11'));
                bounds(card,card.x,card.y,card.width,52);bounds(header,header.x,card.y,header.width,52);
                const accent=card.shapes.map(id=>objects[id]).find(s=>s.name==='Accent strip');bounds(accent,accent.x,card.y,accent.width,52);
                const background=header.shapes.map(id=>objects[id]).find(s=>s.name==='Card header');bounds(background,header.x,card.y,header.width,52);
                const actions=header.shapes.map(id=>objects[id]).filter(s=>s.name==='Duplicate button'||s.name==='Delete button').length;
                const nameWidth=Math.max(24,card.width-99-actions*26-6);
                bounds(title,header.x+91,card.y+10,nameWidth,18);delete title['position-data'];
                setText(title,fit(fullName,nameWidth,12));title.name=fullName;
                const summary=cloneTree(summarySource.id,header.id);bounds(summary,header.x+8,card.y+32,header.width-16,16);delete summary['position-data'];
                setText(summary,fit(fullSummary,summary.width,11));summary.name=fullSummary;header.shapes.push(summary.id);
                tooltips.push({screen:screen.name,card:card.name,fullName,displayName:textOf(title.content),fullSummary,displaySummary:textOf(summary.content)});
                const index=screen.shapes.indexOf(card.id);for(const id of screen.shapes.slice(index+1))move(objects[id],0,delta);
                heightDelta+=delta;
            }
            bounds(screen,screen.x,screen.y,screen.width,Math.max(900,screen.height+heightDelta));
            return screen;
        }
        for(const original of screens){const screen=fixedScreen(original);screen.name=original.name.replace('Step 12','Step 13');objects[zero].shapes.push(screen.id);}
        for(const [i,tab]of ['Modifiers','Objects','Annotation'].entries()){
            const original=screens.find(s=>s.name==='Step 12 / '+tab+' / dark / 240'),x=2800+i*600;
            const parent=newFrame('Step 13 / Compare '+tab+' / 240',x,0,536,original.height+64);
            parent.fills=structuredClone(original.fills);parent['applied-tokens']=structuredClone(original['applied-tokens']);
            const before=cloneTree(original.id,parent.id,x+16-original.x,48-original.y);
            const after=fixedScreen(original,parent.id,x+280-original.x,48-original.y);
            const labels=[caption('CURRENT',x+16,16,240,parent.id),caption('FIXED TWO LINES',x+280,16,240,parent.id)];
            parent.shapes=[...labels.map(s=>s.id),before.id,after.id];bounds(parent,parent.x,0,parent.width,Math.max(before.height,after.height)+64);
            objects[zero].shapes.push(parent.id);
        }
        fs.writeFileSync(path.join(out,'tooltip-references.json'),JSON.stringify(tooltips,null,2));
    }
    if(fullReviewStep||sectionCutStep||modeDropdownAuditStep||reportTableStep||legendStep||gradeCalloutStep){
        const archiveHash=createHash('sha256').update(fs.readFileSync(path.resolve(here,'../output/MoleHill-UI.penpot'))).digest('hex');
        if(archiveHash!==draft.sourceArchiveHash)throw Error('The source scene archive no longer matches this design baseline.');
        const scene=JSON.parse(fs.readFileSync(path.resolve(here,'../output/scene.json'),'utf8'));
        function index(node,px=0,py=0,theme='dark'){
            theme=node.theme??(node.fill==='#ffffff'||node.fill==='#f0f0f0'?'light':theme);
            snapshot.set(node.id,{node,px,py,x:px+node.x,y:py+node.y,theme});
            for(const child of node.children??[])index(child,px+node.x,py+node.y,theme);
        }
        for(const page of scene)for(const board of page.boards)index(board);
    }
    function restoreSnapshot(s,info){
        const {node:n,x,y,px,py,theme}=info;
        bounds(s,x,y,n.w,n.h);s.name=n.name;s.hidden=n.hidden??false;
        for(const key of Object.keys(s))if(key.startsWith('layout'))delete s[key];
        delete s['position-data'];delete s['applied-tokens'];
        const opacity=color=>Object.values(sourceInventory.tokens).find(v=>v[theme]===color)?.[theme+'Opacity']??1;
        if(n.fill)s.fills=[{'fill-color':n.fill,'fill-opacity':opacity(n.fill)}];
        else if(!n.resource&&!n.gradient)s.fills=[];
        if(n.stroke&&s.strokes?.length)s.strokes[0]['stroke-color']=n.stroke;
        if(n.type==='path'){
            const bytes=new Uint8Array(n.points.length*28),view=new DataView(bytes.buffer);
            n.points.forEach(([a,b],i)=>{view.setInt16(i*28,i?2:1,true);view.setFloat32(i*28+20,a+px,true);view.setFloat32(i*28+24,b+py,true);});s.content=bytes;
            s.strokes[0]['stroke-color']=n.color;s.strokes[0]['stroke-width']=n.lineWidth;
        }
        if(n.type==='text'){
            const paragraph=s.content.children[0].children[0],style={...paragraph};delete style.type;delete style.children;delete style.text;
            Object.assign(style,{'font-size':String(n.size),'font-weight':n.bold?'700':'400','font-variant-id':n.bold?'bold':'regular',fills:[{'fill-color':n.fill,'fill-opacity':opacity(n.fill)}]});
            const typography=Object.entries(file.data.typographies).find(([,t])=>t['font-size']===String(n.size)&&t['font-weight']===(n.bold?'700':'400'));
            if(typography){style['typography-ref-id']=typography[0];style['typography-ref-file']=draft.fileId;}
            s.content={type:'root',children:[{type:'paragraph-set',children:n.text.split('\n').map(text=>({type:'paragraph',...style,children:[{text,...style}]}))}]};
        }
    }
    function cloneTree(id,parentId=zero,dx=0,dy=0){
        const original=allObjects[id],s=structuredClone(original),newId=randomUUID();
        s.id=newId;s['page-id']=pageId;s['parent-id']=parentId;
        for(const key of ['component-id','component-file','component-root','main-instance','shape-ref','touched','remote-synced'])delete s[key];
        if((fullReviewStep||sectionCutStep||modeDropdownAuditStep||reportTableStep||legendStep||gradeCalloutStep)&&snapshot.has(id))restoreSnapshot(s,snapshot.get(id));
        objects[newId]=s;templates.set(newId,allRawObjects.get(id));
        s.shapes=(original.shapes??[]).map(child=>cloneTree(child,newId,dx,dy).id);
        if(!original.shapes)delete s.shapes;
        translateOne(s,dx,dy);
        return s;
    }
    function bounds(s,x=s.x,y=s.y,w=s.width,h=s.height){
        Object.assign(s,{x,y,width:w,height:h,selrect:{x,y,width:w,height:h,x1:x,y1:y,x2:x+w,y2:y+h},points:[{x,y},{x:x+w,y},{x:x+w,y:y+h},{x,y:y+h}]});
    }
    function translateOne(s,dx,dy){
        if(dx||dy){
            s.x+=dx;s.y+=dy;
            for(const p of s.points??[]){p.x+=dx;p.y+=dy;}
            if(s.selrect){for(const k of ['x','x1','x2'])s.selrect[k]+=dx;for(const k of ['y','y1','y2'])s.selrect[k]+=dy;}
            if(s.type==='path'&&typeof s.content==='string')s.content=s.content.replace(/([ML])(-?[\d.e+]+),(-?[\d.e+]+)/gi,(_,c,x,y)=>c+(+x+dx)+','+(+y+dy));
            if(s.type==='path'&&s.content instanceof Uint8Array){
                const view=new DataView(s.content.buffer,s.content.byteOffset,s.content.byteLength);
                for(let offset=0;offset<s.content.byteLength;offset+=28){
                    const type=view.getInt16(offset,true);
                    for(const pair of type===3?[[4,8],[12,16],[20,24]]:type===1||type===2?[[20,24]]:[]){
                        view.setFloat32(offset+pair[0],view.getFloat32(offset+pair[0],true)+dx,true);
                        view.setFloat32(offset+pair[1],view.getFloat32(offset+pair[1],true)+dy,true);
                    }
                }
            }
        }
    }
    function move(s,dx,dy){translateOne(s,dx,dy);for(const id of s.shapes??[])move(objects[id],dx,dy);}
    function descendants(s,fn){fn(s);for(const id of s.shapes??[])descendants(objects[id],fn);}
    function setText(s,label){
        let first=true;
        function visit(v){if(!v||typeof v!=='object')return;if(Object.hasOwn(v,'text')){v.text=first?label:'';first=false;}for(const child of v.children??[])visit(child);}
        visit(s.content);s.name=label;
    }
    const firstBoard=Object.values(source.objects).find(s=>s.name==='Modifiers / dark / 320');
    const rootTemplate=source.objects[zero];
    objects[zero]=structuredClone(rootTemplate);objects[zero].shapes=[];objects[zero]['page-id']=pageId;
    templates.set(zero,rawObjects.get(uuid(zero)));
    function newFrame(name,x,y,w,h,parent=zero){
        const s=cloneTree(firstBoard.id,parent);
        s.shapes=[];s.name=name;bounds(s,x,y,w,h);return s;
    }
    function caption(label,x,y,w,parent){
        const template=Object.values(source.objects).find(s=>s.type==='text'&&textOf(s.content)==='MODIFIER STACK');
        const s=cloneTree(template.id,parent);bounds(s,x,y,w,22);setText(s,label);return s;
    }
    function updateTriangulate(screen,expanded,{settings=false,expandedSettings=false,fixedSources=false,sourceHeading=true}={}){
        const tri=screen.shapes.map(id=>objects[id]).find(s=>s.name==='Triangulate');
        const body=tri.shapes.map(id=>objects[id]).find(s=>s.name==='Parameters');
        const rows=body.shapes.map(id=>objects[id]);
        const label=r=>(r.shapes??[]).map(id=>objects[id]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');
        const byLabel=new Map(rows.map(r=>[label(r),r]));
        const sourceRule=byLabel.get('Boundaries');
        if(!sourceRule)throw Error('Source card rows do not match the baseline: '+[...byLabel.keys()].join(' | '));
        function section(name,open){
            // Clone a small existing rule and use a disclosure heading in its place.
            const s=structuredClone(sourceRule),id=randomUUID();s.id=id;s.name=name+' section';s.shapes=[];
            objects[id]=s;templates.set(id,templates.get(sourceRule.id));
            bounds(s,body.x+8,body.y,body.width-16,28);s['parent-id']=body.id;
            const text=caption((fixedSources&&name==='Terrain Sources'?'':open?'▾ ':'▸ ')+name,s.x+2,s.y+5,s.width-4,id);
            s.shapes=[text.id];s['layout-item-h-sizing']='fill';s['layout-item-v-sizing']='fix';
            return s;
        }
        const arranged=[...(sourceHeading?[section('Terrain Sources',true)]:[]),...['Points','Contours','Breaklines'].map(k=>byLabel.get(k)),section('Additional Sources',expanded)];
        if(expanded)arranged.push(...['Exact TIN Mesh','DEM Surface'].map(k=>byLabel.get(k)));
        arranged.push(section('Boundaries',expanded));
        if(expanded)arranged.push(...['Outer','Hide','Show','Data Clip'].map(k=>byLabel.get(k)));
        const settingsRows=rows.slice(rows.findIndex(r=>label(r)==='Contour Mode'));
        if(settings)arranged.push(section('Settings',expandedSettings));
        if(!settings||expandedSettings)arranged.push(...settingsRows);
        let y=body.y+6;
        for(const r of arranged){if(!r)throw Error('A required source row is missing.');move(r,body.x+8-r.x,y-r.y);r['parent-id']=body.id;y+=r.height;}
        body.shapes=arranged.map(r=>r.id);
        const oldHeight=tri.height,newHeight=y+6-tri.y,delta=newHeight-oldHeight;
        bounds(body,body.x,body.y,body.width,y+6-body.y);bounds(tri,tri.x,tri.y,tri.width,newHeight);
        const accent=tri.shapes.map(id=>objects[id]).find(s=>s.name==='Accent strip');bounds(accent,accent.x,accent.y,accent.width,newHeight);
        const index=screen.shapes.indexOf(tri.id);for(const id of screen.shapes.slice(index+1))move(objects[id],0,delta);
        bounds(screen,screen.x,screen.y,screen.width,Math.max(900,screen.height+delta));
    }
    function bottomUp(screen){
        const cards=screen.shapes.map(id=>objects[id]).filter(s=>['Triangulate','Grade Pad','Smooth'].includes(s.name));
        if(cards.length!==3)throw Error('Expected three modifier cards.');
        let y=Math.min(...cards.map(s=>s.y));
        const reversed=[...cards].reverse();
        for(const card of reversed){move(card,0,y-card.y);y+=card.height+8;}
        screen.shapes=[...screen.shapes.filter(id=>!cards.some(c=>c.id===id)),...reversed.map(c=>c.id)];
    }
    function centreButtonLabels(screen){
        let count=0;
        descendants(screen,parent=>{
            const children=(parent.shapes??[]).map(id=>objects[id]);
            for(const button of children.filter(s=>s.type==='rect'&&s.name.endsWith(' button'))){
                const label=button.name.slice(0,-7);
                const text=children.find(s=>s.type==='text'&&textOf(s.content)===label&&s.x>=button.x&&s.x<button.x+button.width&&s.y>=button.y&&s.y<button.y+button.height);
                if(!text)continue; // Icon actions have vector glyphs instead of labels.
                bounds(text,button.x+2,button.y+2,button.width-4,button.height-4);
                text.content['vertical-align']='center';
                function align(node){if(node.type==='paragraph')node['text-align']='center';for(const child of node.children??[])align(child);}
                align(text.content);count++;
            }
        });
        if(!count)throw Error('No button labels were centred.');
        console.log('Centred '+count+' button labels in '+screen.name);
    }
    if(sourceSummaryStep)buildSourceSummaries();
    if(fixedHeaderStep)buildFixedHeaders();
    function styleSectionHeader(section, label) {
        const arrow = section.name[0];
        const title = section.name.slice(2);
        section.content = structuredClone(label.content);
        section.fills = structuredClone(label.fills);
        delete section['applied-tokens'];
        delete section['position-data'];
        setText(section, arrow + '\u2002' + title);
        function emphasize(node) {
            if (node['font-weight']) {
                node['font-weight'] = '700';
                node['font-variant-id'] = 'bold';
                delete node['typography-ref-id'];
                delete node['typography-ref-file'];
            }
            for (const child of node.children ?? []) emphasize(child);
        }
        emphasize(section.content);
        bounds(section, section.x, section.y + 2, section.width, section.height);
    }
    if(anchoredImportStep){
        const reference = Object.values(pages).find(p => p.name === '87 • Step 79 — Full panel alignment').objects;
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 79 / Panel alignment', 'Step 80 / Anchored import');
            const parts = children(board);
            const panel = parts.find(s => s.name === 'IMPORT PREVIEW' && s.type === 'frame');
            const shell = children(panel).find(s => s.name === 'Panel shell');
            const selector = children(shell).find(s => s.name === 'Input');
            const preview = children(panel).find(s => s.name === 'IMPORT PREVIEW');
            move(preview, selector.x - preview.x, selector.y + selector.height + 4 - preview.y);
            const label = parts.find(s => s.name === 'IMPORT PREVIEW' && s.type === 'text');
            setText(label, 'ANCHORED IMPORT');
            objects[zero].shapes.push(board.id);
        }
    }
    if(panelAlignmentStep){
        const reference = Object.values(pages).find(p => p.name === '86 • Step 78 — Terrain layer assignments').objects;
        const children = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name) => children(shape).find(s => s.name === name);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 78 / Terrain definition', 'Step 79 / Panel alignment');
            const [before, revised, importPanel] = children(board).filter(s => s.type === 'frame');
            setText(child(board, 'CURRENT TOOLBAR'), 'BEFORE');
            setText(child(board, 'TERRAIN MENU'), 'ALIGNED PANEL');
            before.name = 'Before';
            revised.name = 'Aligned panel';
            revised.shapes = revised.shapes.filter(id => objects[id].name !== 'TERRAIN MENU');
            for (const panel of [revised, importPanel]) {
                const shell = child(panel, 'Panel shell');
                const toolbar = child(shell, 'Identity toolbar');
                const left = toolbar.x, right = toolbar.x + toolbar.width;
                const input = child(shell, 'Input');
                const status = child(shell, '▶  Status');
                const message = child(shell, 'Collapsed status message');
                const identityEnd = input.x + input.width;
                const delta = input.x - left;
                const toolbarParts = children(shell).filter(s => s !== toolbar && s.y < shell.y + 74);
                for (const part of toolbarParts) {
                    if (part === input || part.name === 'Proposed Terrain') {
                        bounds(part, part.x - delta, part.y, part.width + delta * 2, part.height);
                    } else if (part.name === 'ChevronDown' || part.x >= identityEnd) {
                        move(part, delta, 0);
                    } else if (part.y >= shell.y + 44) {
                        move(part, -delta, 0);
                    }
                }
                shell.shapes = shell.shapes.filter(id => id !== toolbar.id);
                bounds(message, status.x + 55, status.y, right - status.x - 63, status.height);

                const tabs = children(shell).filter(s => s.name.endsWith(' tab'));
                const tabY = tabs[0].y, gap = 2;
                const tabWidth = (right - left - gap * (tabs.length - 1)) / tabs.length;
                const tabParts = children(shell).filter(s => s.y >= tabY && s.y < tabY + 28);
                for (const [index, tab] of tabs.entries()) {
                    const members = tabParts.filter(s => !tabs.includes(s) && s.x >= tab.x && s.x < tab.x + tab.width);
                    const x = left + index * (tabWidth + gap);
                    const shift = x - tab.x;
                    const label = members.find(s => s.type === 'text');
                    if (label) {
                        for (const member of members) {
                            const rightIcon = /^(ZoneMeshes|AnalysisVisible)$/.test(member.name);
                            move(member, shift + (rightIcon ? tabWidth - tab.width : 0), 0);
                        }
                        const badge = members.find(s => /^(ZoneMeshes|AnalysisVisible)$/.test(s.name));
                        const labelRight = badge ? badge.x - 3 : x + tabWidth - 3;
                        bounds(label, label.x, label.y, labelRight - label.x, label.height);
                    } else {
                        const groupLeft = Math.min(...members.map(s => s.x));
                        const groupRight = Math.max(...members.map(s => s.x + s.width));
                        for (const member of members) move(member, x + (tabWidth - groupRight + groupLeft) / 2 - groupLeft, 0);
                    }
                    bounds(tab, x, tab.y, tabWidth, tab.height);
                }

                const heading = child(shell, 'TERRAIN ANALYSES');
                const action = child(shell, 'Add Analysis button');
                const actionText = child(shell, 'Add Analysis');
                const actionY = tabY + 36;
                move(actionText, right - action.width - action.x, actionY - action.y);
                bounds(action, right - action.width, actionY, action.width, action.height);
                bounds(heading, left, actionY + 3, action.x - left - 8, heading.height);
                const cards = children(panel).filter(s => s.name === 'Earthworks' || s.name === 'Slope');
                const shift = actionY + action.height + 8 - cards[0].y;
                for (const card of cards) move(card, 0, shift);
                bounds(shell, shell.x, shell.y, shell.width, actionY + action.height + 2 - shell.y);
                bounds(panel, panel.x, panel.y, panel.width, panel.height + shift);

                const preview = child(panel, 'IMPORT PREVIEW');
                if (preview) {
                    move(preview, 0, panel.y + Math.round((panel.height - preview.height) / 2) - preview.y);
                    const name = child(preview, 'Proposed Terrain – copy');
                    const field = child(preview, 'Name input');
                    bounds(name, field.x + 6, name.y, field.width - 12, name.height);
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(layerTransferStep||definitionTransferStep){
        const page = Object.values(pages).find(p => p.name === '84 • Step 76 — Full panel walkthrough').objects;
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        for (const [i, id] of page[zero].shapes.entries()) {
            const source = page[id];
            const template = children(source, page).find(s => s.name === 'READY TO BAKE' && s.type === 'frame');
            const templateShell = child(template, 'Panel shell', page);
            const input = child(templateShell, 'Input', page);
            const label = child(templateShell, 'Proposed Terrain', page);
            const action = child(templateShell, 'Add Analysis button', page);
            const width = template.width, height = template.height;
            const board = newFrame('Step ' + (layerTransferStep ? '78' : '77') + ' / Terrain definition / dark / ' + width, 0, i * 780, width * 3 + 64, height + 64);
            board.fills = structuredClone(source.fills);

            for (const [column, title] of ['CURRENT TOOLBAR', 'TERRAIN MENU', 'IMPORT PREVIEW'].entries()) {
                const x = 16 + column * (width + 16), y = board.y + 48;
                board.shapes.push(caption(title, x, board.y + 14, width, board.id).id);
                const panel = cloneTree(template.id, board.id, x - template.x, y - template.y);
                panel.name = title;
                board.shapes.push(panel.id);
                if (column === 0) continue;
                const shell = child(panel, 'Panel shell');
                for (const part of children(shell).filter(s => s.type === 'rect' && s.name.endsWith(' button') && s.y < shell.y + 70)) {
                    part.strokes = [];
                    part.fills = [];
                    delete part['applied-tokens'];
                }
                const menuX = x + 8, menuY = y + (column === 1 ? 42 : 155), span = width - 16;
                const menu = newFrame(title, menuX, menuY, span, column === 1 ? 114 : 238, panel.id);
                menu.fills = [];
                panel.shapes.push(menu.id);
                function box(template, name, x, y, w, h) {
                    const shape = cloneTree(template.id, menu.id);
                    shape.name = name;
                    bounds(shape, x, y, w, h);
                    menu.shapes.push(shape.id);
                    return shape;
                }
                function line(value, top, inset = 10, w = span - 20) {
                    const text = box(label, value, menuX + inset, menuY + top, w, 18);
                    setText(text, value);
                    return text;
                }
                box(input, 'Surface', menuX, menuY, span, menu.height);
                if (column === 1) {
                    line('✓  Proposed Terrain', 7);
                    line('     Existing Terrain', 33);
                    line('Import Terrain Definition…', 63);
                    line('Export Terrain Definition…', 89);
                } else {
                    line('Import Terrain Definition', 10);
                    box(input, 'Name input', menuX + 10, menuY + 38, span - 20, 26);
                    line('Proposed Terrain – copy', 42, 16, span - 32);
                    line('Creates a new terrain', 77);
                    if (layerTransferStep) {
                        line('Layer assignments', 101);
                        line('✓ Survey::Points', 125);
                        line('✓ Survey::Contours', 149);
                        line('! Survey::Breaklines missing', 173);
                    } else {
                        line('5 modifiers · 2 analyses', 101);
                        line('Units: metres', 125);
                        line('3 sources need linking', 149);
                        line('Review source links after import.', 173);
                    }
                    const buttonWidth = (span - 26) / 2;
                    for (const [i, button] of ['Cancel', 'Import'].entries()) {
                        const inset = 10 + i * (buttonWidth + 6);
                        box(action, button + ' button', menuX + inset, menuY + 200, buttonWidth, 26);
                        const text = line(button, 204, inset + 3, buttonWidth - 6);
                        const paragraph = text.content.children[0].children[0];
                        paragraph['text-align'] = 'center';
                        for (const fragment of paragraph.children) fragment['text-align'] = 'center';
                    }
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(panelWalkthroughStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const context = pageObjects('72 • Step 64 — Section headers in the Objects panel');
        const modifiers = pageObjects('58 • Step 50 — Smooth boundaries before settings');
        const maps = pageObjects('83 • Step 75 — Slope Elevation and Aspect');
        const results = pageObjects('79 • Step 71 — Results headings');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const stages = ['EDIT MODIFIER', 'CHECK RESULTS', 'READY TO BAKE'];

        for (const width of [224, 400]) {
            const panelWidth = width + 16;
            const contextBoard = find(context, `Step 64 / Section context / dark / ${panelWidth}`);
            const panelTemplate = child(contextBoard, 'Objects / Clearer headers', context);
            const smoothBoard = find(modifiers, `Step 50 / Smooth / dark / ${width}`);
            const mapBoard = find(maps, `Step 75 / Map analyses / dark / ${width}`);
            const resultsBoard = find(results, `Step 71 / Results headings / dark / ${width}`);
            const smooth = child(smoothBoard, 'Smooth / Smooth', modifiers);
            const remesh = child(smoothBoard, 'Remesh / Collapsed', modifiers);
            const slope = child(mapBoard, 'Slope', maps);
            const earthworks = child(resultsBoard, 'Earthworks', results);
            const collapsedArrow = child(child(remesh, 'Header', modifiers), 'Collapse arrow', modifiers);
            const detailTemplate = child(child(earthworks, 'Parameters', results), 'Read-only results text box', results);
            const actionTemplate = child(child(child(panelTemplate, 'Orient', context), 'Parameters', context), 'Release actions', context);
            const board = newFrame(`Step 76 / Full panel walkthrough / dark / ${panelWidth}`,
                0, width === 224 ? 0 : 1160, 3 * panelWidth + 64, 1050);
            board.fills = structuredClone(contextBoard.fills);
            board['applied-tokens'] = structuredClone(contextBoard['applied-tokens']);
            let bottom = board.y;

            for (const [stage, title] of stages.entries()) {
                const x = board.x + 16 + stage * (panelWidth + 16), y = board.y + 44;
                const panel = cloneTree(panelTemplate.id, board.id, x - panelTemplate.x, y - panelTemplate.y);
                panel.name = title;
                const shell = child(panel, 'Panel shell');
                panel.shapes = [shell.id];
                const status = child(shell, '▶  Status');
                const tabs = child(shell, 'Objects tab'), active = child(shell, stage === 0 ? 'Modifiers tab' : 'Analysis tab');
                for (const key of ['fills', 'strokes']) [tabs[key], active[key]] = [active[key], tabs[key]];
                setText(child(shell, 'TERRAIN OBJECTS'), stage === 0 ? 'MODIFIER STACK' : 'TERRAIN ANALYSES');
                setText(child(shell, 'Add Object'), stage === 0 ? 'Add Modifier' : 'Add Analysis');
                child(shell, 'Add Object button').name = stage === 0 ? 'Add Modifier button' : 'Add Analysis button';

                function statusText(text, x, y, width, color, parent) {
                    const label = caption(text, x, y, width, parent);
                    const fill = [{'fill-color': color, 'fill-opacity': 1}];
                    label.fills = fill;
                    delete label['applied-tokens'];
                    const paragraph = label.content.children[0].children[0];
                    for (const part of [paragraph, ...paragraph.children]) {
                        part.fills = structuredClone(fill);
                        delete part['typography-ref-id'];
                        delete part['typography-ref-file'];
                    }
                    return label;
                }
                let added = 0;
                if (stage === 1) {
                    setText(status, '▼  Status');
                    added = 224;
                    for (const part of children(shell).filter(s => s.y >= shell.y + 142)) move(part, 0, added);
                    const log = cloneTree(detailTemplate.id, shell.id);
                    const surface = child(log, 'Text box surface');
                    const logX = x + 16, logY = y + 146, logWidth = panelWidth - 32;
                    bounds(log, logX, logY, logWidth, 180);
                    bounds(surface, logX, logY, logWidth, 180);
                    log.name = 'Selectable build log';
                    log.shapes = [surface.id];
                    for (const [i, line] of ['Grading diagnostics · 1 warning', '', 'Warning · Grade Pad',
                        'A pad boundary could not be', 'applied and was skipped.'].entries()) {
                        if (!line) continue;
                        log.shapes.push(statusText(line, logX + 8, logY + 8 + i * 17, logWidth - 16, '#e6aa50', log.id).id);
                    }
                    const actions = cloneTree(actionTemplate.id, shell.id, logX - actionTemplate.x, logY + 184 - actionTemplate.y);
                    setText(child(actions, 'Release Selected'), 'Copy Log');
                    setText(child(actions, 'Release All'), 'Copy Case');
                    bounds(actions, actions.x, actions.y, logWidth, actions.height);
                    for (const [i, part] of children(actions).entries()) {
                        const half = (logWidth - 4) / 2, isText = part.type === 'text';
                        bounds(part, logX + Math.floor(i / 2) * (half + 4) + (isText ? 2 : 0),
                            part.y, half - (isText ? 4 : 0), part.height);
                    }
                    shell.shapes.push(log.id, actions.id);
                    bounds(shell, shell.x, shell.y, shell.width, shell.height + added);
                } else {
                    const hint = stage === 0 ? 'Building · Smooth (3 of 5) · 4 s' : 'Grading: 3 info';
                    const label = statusText(hint, status.x + 55, status.y, panelWidth - 81,
                        stage === 0 ? '#a4c4e3' : '#949494', shell.id);
                    label.name = 'Collapsed status message';
                    shell.shapes.push(label.id);
                }
                let cursor = y + 243 + added;
                const stack = stage === 0 ? [[smooth, false], [remesh, false]] : [[earthworks, false], [slope, true]];
                for (const [template, collapsed] of stack) {
                    const card = cloneTree(template.id, panel.id, x + 8 - template.x, cursor - template.y);
                    panel.shapes.push(card.id);
                    const header = child(card, 'Header'), accent = child(card, 'Accent strip');
                    const arrow = child(header, 'Collapse arrow');
                    if (collapsed) {
                        const body = child(card, 'Parameters');
                        card.shapes = card.shapes.filter(id => id !== body.id);
                        const rightArrow = cloneTree(collapsedArrow.id, header.id,
                            arrow.x + (arrow.width - collapsedArrow.width) / 2 - collapsedArrow.x,
                            arrow.y + (arrow.height - collapsedArrow.height) / 2 - collapsedArrow.y);
                        header.shapes = header.shapes.filter(id => objects[id].name !== 'Input').map(id => id === arrow.id ? rightArrow.id : id);
                        bounds(card, card.x, card.y, card.width, header.height);
                    } else if (template === smooth && arrow.width < arrow.height) {
                        Object.assign(arrow, downDisclosure(arrow));
                    }
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    cursor += card.height + 8;
                }
                bounds(panel, x, y, panelWidth, cursor - y);
                board.shapes.push(caption(title, x, board.y + 14, panelWidth, board.id).id, panel.id);
                bottom = Math.max(bottom, cursor);
            }
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(mapAnalysisStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const ramps = pageObjects('82 • Step 74 — Color ramp features');
        const rules = pageObjects('80 • Step 72 — Gradient Compliance rules');
        const drainage = pageObjects('77 • Step 69 — Catchments and Ponding');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const spectrum = [[0, '#2c7bb6'], [0.2, '#2ab57d'], [0.45, '#afdd42'], [0.68, '#fdae32'], [1, '#d7191c']];
        const wheel = ['#3a60a8', '#2e969c', '#3fa854', '#96ba3c', '#e2bc37', '#de8530', '#c8484e', '#8d549e'];
        function spectrumColor(position) {
            const upper = spectrum.findIndex(([offset]) => offset > position);
            const [lo, a] = spectrum[upper - 1], [hi, b] = spectrum[upper];
            return '#' + [1, 3, 5].map(i => {
                const start = parseInt(a.slice(i, i + 2), 16), end = parseInt(b.slice(i, i + 2), 16);
                return Math.round(start + (end - start) * (position - lo) / (hi - lo)).toString(16).padStart(2, '0');
            }).join('');
        }

        for (const id of ramps[zero].shapes) {
            const source = ramps[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Curvature / Linear', ramps);
            const sampleRamp = child(child(sample, 'Parameters', ramps), 'ColorRamp property row', ramps);
            const stripTemplate = child(sampleRamp, 'Cool Warm ramp', ramps);
            const tickTemplate = child(sampleRamp, '0', ramps);
            const ruleBoard = find(rules, `Step 72 / Gradient Compliance / ${theme} / ${width}`);
            const ruleCard = child(ruleBoard, 'Gradient Compliance', rules);
            const unitPicker = child(child(ruleCard, 'Parameters', rules), 'Choice property row', rules);
            const drainageBoard = find(drainage, `Step 69 / Drainage / ${theme} / ${width}`);
            const drainageCard = child(drainageBoard, 'Catchments', drainage);
            const threshold = child(child(drainageCard, 'Parameters', drainage), 'Number property row', drainage);
            const zoneBoard = find(zones, `Step 65 / Zone card / ${theme} / ${width}`);
            const zone = child(zoneBoard, 'Zone / Fully expanded', zones);
            const resultTemplate = child(child(zone, 'Parameters', zones), 'Plan area result', zones);
            const board = newFrame(`Step 75 / Map analyses / ${theme} / ${width}`, source.x, source.y, 3 * width + 64, 565);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);
            const examples = [
                ['Slope', '#43a047', 'AnSlope', '%', '12.4% average', ['0%', '25%', '50%']],
                ['Elevation', '#1e88e5', 'AnElevation', 'Z', '12.1–28.6 m', ['10 m', '20 m', '30 m']],
                ['Aspect', '#00897b', null, 'N', 'Southeast', ['N', 'E', 'S', 'W', 'N']],
            ];

            for (const [column, [name, accent, iconName, glyph, summary, ticks]] of examples.entries()) {
                const compass = name === 'Aspect';
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = name;
                board.shapes.push(caption(name.toUpperCase(), x, board.y + 14, width, board.id).id, card.id);
                const header = child(card, 'Header');
                const stripe = child(card, 'Accent strip');
                const color = [{'fill-color': accent, 'fill-opacity': 1}];
                stripe.fills = structuredClone(color);
                setText(child(header, 'Site form'), name);
                setText(child(header, 'Curvature · Along'), name + ' · ' + summary);
                const oldIcon = child(header, 'CV');
                const artwork = iconName && find(allObjects, iconName);
                if (artwork) {
                    const icon = cloneTree(artwork.id, header.id, oldIcon.x - artwork.x, oldIcon.y - artwork.y);
                    bounds(icon, icon.x, icon.y + 2, 10, 10);
                    header.shapes = header.shapes.map(id => id === oldIcon.id ? icon.id : id);
                } else {
                    setText(oldIcon, glyph);
                    oldIcon.fills = structuredClone(color);
                    const p = oldIcon.content.children[0].children[0];
                    for (const part of [p, ...p.children]) part.fills = structuredClone(color);
                }

                const body = child(card, 'Parameters');
                const ramp = child(body, 'ColorRamp property row');
                const results = child(body, '▸ Results');
                const left = body.x + 8;
                let cursor = body.y + 6;
                body.shapes = [];
                function add(template) {
                    const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }
                if (name === 'Slope') {
                    const row = add(unitPicker);
                    setText(textChild(row), 'Slope Units');
                    setText(textChild(child(row, 'Editor')), 'Percent');
                }
                if (compass) {
                    const row = add(threshold);
                    setText(child(child(row, 'Editor'), '0.3'), '1.0');
                }
                move(ramp, 0, cursor - ramp.y);
                body.shapes.push(ramp.id);
                const parts = children(ramp);
                const strip = child(ramp, 'Cool Warm ramp');
                const title = child(ramp, '▸ Curvature (1/m)');
                setText(title, '▸ ' + name);
                const previousTicks = parts.filter(s => s.type === 'text' && s.y - ramp.y >= 68 && s.y - ramp.y < 90);
                const histogram = parts.filter(s => s.name.startsWith('Histogram '));
                for (const [i, bar] of histogram.entries()) {
                    const position = (i + 0.5) / histogram.length;
                    bar.fills = [{'fill-color': compass ? wheel[Math.floor(position * 8)] : spectrumColor(position), 'fill-opacity': 0.47}];
                    delete bar['applied-tokens'];
                }
                if (compass) {
                    setText(child(ramp, 'Linear'), 'Constant');
                    const mapping = child(ramp, 'Mapping modes');
                    const segments = children(mapping);
                    const first = children(segments[0]).find(s => s.type === 'rect');
                    const last = children(segments[2]).find(s => s.type === 'rect');
                    for (const key of ['fills', 'strokes', 'applied-tokens']) [first[key], last[key]] = [last[key], first[key]];
                    segments[0].name = 'Linear';
                    segments[2].name = 'Constant (selected)';
                    for (const shape of [first, last]) if (shape['applied-tokens'] == null) delete shape['applied-tokens'];
                    const kept = parts.filter(s => s.y - ramp.y < 175 && s !== strip && !previousTicks.includes(s));
                    ramp.shapes = kept.map(s => s.id);
                    for (const [i, hue] of wheel.entries()) {
                        const band = cloneTree(stripTemplate.id, ramp.id);
                        bounds(band, strip.x + strip.width * i / 8, strip.y, strip.width / 8, strip.height);
                        band.fills = [{'fill-color': hue, 'fill-opacity': 1}];
                        band.strokes = [];
                        delete band['applied-tokens'];
                        band.name = 'Compass sector ' + (i + 1);
                        ramp.shapes.push(band.id);
                    }
                    const north = cloneTree(stripTemplate.id, ramp.id);
                    bounds(north, strip.x + strip.width - 1, strip.y, 1, strip.height);
                    north.fills = [{'fill-color': wheel[0], 'fill-opacity': 1}];
                    north.strokes = [];
                    delete north['applied-tokens'];
                    north.name = 'North at 360 degrees';
                    ramp.shapes.push(north.id);
                    for (const [i, letter] of ticks.entries()) {
                        const tick = cloneTree(tickTemplate.id, ramp.id);
                        const centre = strip.x + strip.width * i / (ticks.length - 1);
                        bounds(tick, Math.max(strip.x, Math.min(strip.x + strip.width - 14, centre - 7)), previousTicks[0].y, 14, 18);
                        setText(tick, letter);
                        ramp.shapes.push(tick.id);
                    }
                    bounds(ramp, ramp.x, ramp.y, ramp.width, 181);
                    const surface = child(ramp, 'Ramp surface');
                    bounds(surface, surface.x, surface.y, surface.width, ramp.height);
                } else {
                    strip.fills = [{'fill-opacity': 1, 'fill-color-gradient': {
                        ...strip.fills[0]['fill-color-gradient'],
                        stops: spectrum.map(([offset, color]) => ({offset, color, opacity: 1})),
                    }}];
                    delete strip['applied-tokens'];
                    strip.name = 'Terrain Spectrum ramp';
                    for (const [i, tick] of previousTicks.entries()) setText(tick, ticks[i]);
                    for (const [i, bound] of parts.filter(s => s.type === 'text' && s.y - ramp.y >= 204).entries()) {
                        setText(bound, i === 0 ? ticks[0] : ticks[2]);
                    }
                }
                cursor += ramp.height + 4;
                move(results, 0, cursor - results.y);
                if (compass) setText(results, '▾ Results');
                body.shapes.push(results.id);
                cursor += 26;
                if (compass) {
                    for (const [label, value] of [['Facing', 'Southeast (138°)'], ['Flat', '120 of 1,000 (12%)']]) {
                        const row = add(resultTemplate);
                        const [name, reading] = children(row);
                        setText(name, label);
                        setText(reading, value);
                        row.name = label + ' result';
                    }
                }
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(stripe, stripe.x, stripe.y, stripe.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(rampFeaturesStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const curvature = pageObjects('81 • Step 73 — Curvature and Landforms');
        const gradient = pageObjects('80 • Step 72 — Gradient Compliance rules');
        const placement = pageObjects('70 • Step 62 — Plant and Orient actions');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const stops = [
            [0, '−0.08', [49, 54, 149]], [0.35, '−0.024', [69, 117, 180]],
            [0.5, '0', [224, 243, 248]], [0.7, '+0.032', [244, 165, 130]], [1, '+0.08', [165, 0, 38]],
        ];
        const modes = ['Linear', 'Stepped', 'Constant'];
        const hex = rgb => '#' + rgb.map(value => Math.round(value).toString(16).padStart(2, '0')).join('');
        function sampleColor(position, constant) {
            const upper = stops.findIndex(([offset]) => offset > position);
            if (upper < 0) return hex(stops.at(-1)[2]);
            const [low, , a] = stops[upper - 1], [high, , b] = stops[upper];
            const t = constant ? 0 : (position - low) / (high - low);
            return hex(a.map((value, i) => value + t * (b[i] - value)));
        }

        for (const id of curvature[zero].shapes) {
            const source = curvature[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Curvature / Along', curvature);
            const sampleBody = child(sample, 'Parameters', curvature);
            const rampTemplate = child(sampleBody, 'ColorRamp property row', curvature);
            const swatchTemplate = child(rampTemplate, 'Cool Warm ramp', curvature);
            const gradientBoard = find(gradient, `Step 72 / Gradient Compliance / ${theme} / ${width}`);
            const gradientCard = child(gradientBoard, 'Gradient Compliance', gradient);
            const mappingTemplate = child(child(gradientCard, 'Parameters', gradient), 'Level Areas rule selector', gradient);
            const plantBoard = find(placement, `Step 62 / Plant and Orient / ${theme} / ${width}`);
            const plant = child(plantBoard, 'Plant', placement);
            const actions = child(child(plant, 'Parameters', placement), 'Release actions', placement);
            const buttonTemplate = child(actions, 'Release Selected button', placement);
            const labelTemplate = child(actions, 'Release Selected', placement);
            const board = newFrame(`Step 74 / Ramp features / ${theme} / ${width}`,
                source.x, size === '224' ? 960 : 0, 3 * width + 64, 820);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);

            for (const [column, mode] of modes.entries()) {
                const open = mode === 'Constant';
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = 'Curvature / ' + mode;
                board.shapes.push(caption(open ? 'CONSTANT · STOP EDITOR' : mode.toUpperCase(), x, board.y + 14, width, board.id).id, card.id);
                const body = child(card, 'Parameters'), ramp = child(body, 'ColorRamp property row');
                const parts = children(ramp);
                const surface = child(ramp, 'Ramp surface');
                const bar = child(ramp, 'Cool Warm ramp');
                const oldHeight = ramp.height, left = bar.x, span = bar.width;
                const title = child(ramp, '▸ Curvature (1/m)');
                setText(title, (open ? '▾' : '▸') + ' Curvature (1/m)');
                const summary = parts.find(s => s.name === 'Continuous' && s.y - ramp.y < 24);
                setText(summary, open ? 'Constant' : mode);
                const ticks = parts.filter(s => s.type === 'text' && s.y - ramp.y >= 68 && s.y - ramp.y < 92);
                const bars = parts.filter(s => s.name.startsWith('Histogram '));
                const rangeControls = parts.filter(s => s.y - ramp.y >= 145);
                ramp.shapes = [surface, child(ramp, 'Ramp header'), title, summary, ...bars, ...ticks].map(s => s.id);
                const fill = (shape, color, opacity = 1) => {
                    shape.fills = [{'fill-color': color, 'fill-opacity': opacity}];
                    delete shape['applied-tokens'];
                };
                function rect(name, x, y, width, height, color, opacity = 1) {
                    const shape = cloneTree(swatchTemplate.id, ramp.id);
                    shape.name = name;
                    fill(shape, color, opacity);
                    shape.strokes = [];
                    bounds(shape, x, y, width, height);
                    ramp.shapes.push(shape.id);
                    return shape;
                }
                bars.forEach((bar, i) => {
                    fill(bar, sampleColor((i + 0.5) / bars.length, open), 0.47);
                    move(bar, 0, -2);
                });
                const barHeight = open ? 26 : bar.height;
                if (mode === 'Linear') ramp.shapes.push(bar.id);
                else {
                    const edges = open ? stops.map(([offset]) => offset) : Array.from({length: 9}, (_, i) => i / 8);
                    for (const [i, start] of edges.slice(0, -1).entries()) {
                        const end = edges[i + 1];
                        const color = sampleColor(open ? start : (start + end) / 2, open);
                        rect(mode + ' band ' + (i + 1), left + start * span, bar.y, (end - start) * span, barHeight, color);
                    }
                    if (open) rect('End stop colour', left + span - 1, bar.y, 1, barHeight, hex(stops.at(-1)[2]));
                }
                let cursor = bar.y + barHeight + 3;
                if (open) {
                    for (const [i, [offset, , rgb]] of stops.entries()) {
                        const stopX = left + offset * span;
                        rect('Stop guide ' + (i + 1), Math.min(left + span - 1, stopX), bar.y, 1, barHeight + 5, '#d0d0d0', 0.9);
                        const handleX = Math.max(left, Math.min(left + span - 10, stopX - 5));
                        rect('Stop handle ' + (i + 1), handleX, cursor, 10, 10, hex(rgb));
                    }
                    cursor += 14;
                }
                for (const tick of ticks) move(tick, 0, cursor - tick.y);
                cursor += 22;
                function text(label, x, y, width) {
                    const item = caption(label, x, y, width, ramp.id);
                    bounds(item, x, y, width, 18);
                    ramp.shapes.push(item.id);
                    return item;
                }
                function button(label, x, width) {
                    const shape = cloneTree(buttonTemplate.id, ramp.id);
                    shape.name = label + ' button';
                    bounds(shape, x, cursor, width, 26);
                    const labelShape = cloneTree(labelTemplate.id, ramp.id);
                    setText(labelShape, label);
                    bounds(labelShape, x + 2, cursor + 2, width - 4, 22);
                    ramp.shapes.push(shape.id, labelShape.id);
                    return shape;
                }
                if (open) {
                    for (const [i, [offset, value, rgb]] of stops.entries()) {
                        if (i === 2) {
                            const input = cloneTree(buttonTemplate.id, ramp.id);
                            input.name = 'Exact stop value input';
                            bounds(input, left + 18, cursor, span - 84, 20);
                            ramp.shapes.push(input.id);
                        }
                        rect('Stop color swatch ' + (i + 1), left, cursor + 4, 12, 12, hex(rgb));
                        text(value, left + 22, cursor + 2, span - 92);
                        text(Math.round(offset * 100) + '%', left + span - 54, cursor + 2, 33);
                        text('×', left + span - 14, cursor + 2, 14);
                        cursor += 22;
                    }
                    const toolbar = [['+', 22], ['−', 22], ['⇉', 22], ['⇄', 22], ['Presets', 47], ['Save', 36]];
                    let nextX = left;
                    for (const [label, w] of toolbar) {
                        const surface = button(label, nextX, w);
                        if (label.length === 1) {
                            surface.fills = [];
                            surface.strokes = [];
                            delete surface['applied-tokens'];
                        }
                        nextX += w + 4;
                    }
                    cursor += 32;
                }
                text('Mapping', left, cursor, span);
                cursor += 20;
                if (!open) {
                    const half = (span - 4) / 2;
                    button('Edit ramp', left, half);
                    button('Presets', left + half + 4, half);
                    cursor += 32;
                }
                const mapping = cloneTree(mappingTemplate.id, ramp.id, ramp.x - mappingTemplate.x, cursor - mappingTemplate.y);
                const segments = children(mapping);
                const previous = children(segments[1]).find(s => s.type === 'rect');
                const selected = children(segments[column]).find(s => s.type === 'rect');
                for (const key of ['fills', 'strokes', 'applied-tokens']) [previous[key], selected[key]] = [selected[key], previous[key]];
                for (const [i, segment] of segments.entries()) {
                    setText(children(segment).find(s => s.type === 'text'), modes[i]);
                    segment.name = modes[i] + (i === column ? ' (selected)' : '');
                }
                mapping.name = 'Mapping modes';
                for (const [shape, x, y, width, height] of rampMappingLayout(mapping, left, span, children)) {
                    bounds(shape, x, y, width, height);
                    if (shape['applied-tokens'] == null) delete shape['applied-tokens'];
                }
                ramp.shapes.push(mapping.id);
                cursor += mapping.height;
                if (mode === 'Stepped') {
                    text('Interval', left, cursor + 4, 76);
                    const value = cloneTree(buttonTemplate.id, ramp.id);
                    value.name = 'Automatic band interval';
                    bounds(value, left + span - 86, cursor, 86, 26);
                    ramp.shapes.push(value.id);
                    text('0.02*', value.x + 5, cursor + 4, 75);
                    cursor += 32;
                }
                for (const control of rangeControls) {
                    move(control, 0, cursor - (ramp.y + 145));
                    if (open && control.name === 'Checked') control.hidden = true;
                    ramp.shapes.push(control.id);
                }
                cursor += 63;
                bounds(ramp, ramp.x, ramp.y, ramp.width, cursor - ramp.y);
                bounds(surface, surface.x, surface.y, surface.width, ramp.height);
                const shift = ramp.height - oldHeight;
                const results = children(body).find(s => s.name.endsWith('Results'));
                move(results, 0, shift);
                bounds(body, body.x, body.y, body.width, body.height + shift);
                bounds(card, card.x, card.y, card.width, card.height + shift);
                const accent = child(card, 'Accent strip');
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(curvatureStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const gradient = pageObjects('80 • Step 72 — Gradient Compliance rules');
        const cutFill = pageObjects('76 • Step 68 — Cut and Fill drawing outputs');
        const earthworks = pageObjects('75 • Step 67 — Selectable Earthworks results');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const accentFill = [{'fill-color': '#ad1457', 'fill-opacity': 1}];
        const categories = [
            ['Convex', '#d66027', '25% (310 m²)'],
            ['Planar', '#e8e4d6', '45% (558 m²)'],
            ['Concave', '#2979c6', '30% (372 m²)'],
        ];

        for (const id of gradient[zero].shapes) {
            const source = gradient[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Gradient Compliance', gradient);
            const sampleBody = child(sample, 'Parameters', gradient);
            const numberTemplate = child(sampleBody, 'Number property row', gradient);
            const selectorTemplate = child(sampleBody, 'Level Areas rule selector', gradient);
            const cutFillBoard = find(cutFill, `Step 68 / Cut and Fill / ${theme} / ${width}`);
            const cutFillCard = child(cutFillBoard, 'Cut / Fill / Drawings off', cutFill);
            const rampTemplate = child(child(cutFillCard, 'Parameters', cutFill), 'ColorRamp property row', cutFill);
            const swatchTemplate = child(rampTemplate, 'Cool Warm ramp', cutFill);
            const earthworksBoard = find(earthworks, `Step 67 / Earthworks copy / ${theme} / ${width}`);
            const volumeCard = child(earthworksBoard, 'Earthworks / Original', earthworks);
            const resultsTemplate = child(child(volumeCard, 'Parameters', earthworks), 'Read-only results text box', earthworks);
            const board = newFrame(`Step 73 / Curvature / ${theme} / ${width}`, source.x, source.y, 2 * width + 48, 560);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);

            for (const [column, continuous] of [false, true].entries()) {
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = continuous ? 'Curvature / Along' : 'Landforms / Results open';
                board.shapes.push(caption(continuous ? 'CURVATURE' : 'LANDFORMS · RESULTS OPEN',
                    x, board.y + 14, width, board.id).id, card.id);
                const header = child(card, 'Header');
                const accent = child(card, 'Accent strip');
                accent.fills = structuredClone(accentFill);
                setText(child(header, 'Access'), 'Site form');
                const summary = child(header, 'Gradient Compliance · 2 runs over limit');
                setText(summary, continuous ? 'Curvature · Along' : 'Curvature · Landforms');
                const icon = child(header, 'GC');
                setText(icon, 'CV');
                icon.fills = structuredClone(accentFill);
                const iconParagraph = icon.content.children[0].children[0];
                for (const part of [iconParagraph, ...iconParagraph.children]) part.fills = structuredClone(accentFill);

                const body = child(card, 'Parameters');
                const left = body.x + 8, span = selectorTemplate.width;
                let cursor = body.y + 6;
                body.shapes = [];
                function add(template) {
                    const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }
                function selector(name, labels, selected) {
                    const row = add(selectorTemplate);
                    row.name = name + ' selector';
                    const segments = children(row).slice(0, labels.length);
                    const old = children(segments[1]).find(s => s.type === 'rect');
                    const active = children(segments[selected]).find(s => s.type === 'rect');
                    for (const key of ['fills', 'strokes', 'applied-tokens']) {
                        [old[key], active[key]] = [active[key], old[key]];
                    }
                    for (const [i, segment] of segments.entries()) {
                        const start = left + Math.floor(span * i / labels.length);
                        const end = left + Math.floor(span * (i + 1) / labels.length);
                        const delta = end - start - segment.width;
                        move(segment, start - segment.x, 0);
                        bounds(segment, start, segment.y, end - start, segment.height);
                        for (const part of children(segment)) bounds(part, part.x, part.y, part.width + delta, part.height);
                        setText(textChild(segment), labels[i]);
                        segment.name = labels[i] + (i === selected ? ' (selected)' : '');
                    }
                    row.shapes = segments.map(s => s.id);
                }
                selector('Show', ['Landforms', 'Curvature'], continuous ? 1 : 0);
                const size = add(numberTemplate);
                const fieldLabel = textChild(size);
                setText(fieldLabel, 'Landform Size');
                const editor = child(size, 'Editor');
                const auto = textChild(editor);
                setText(auto, 'Auto (20 m)');
                const unit = child(editor, 'm');
                editor.shapes = editor.shapes.filter(id => id !== unit.id);
                const autoParagraph = auto.content.children[0].children[0];
                for (const part of [autoParagraph, ...autoParagraph.children]) {
                    part.fills = structuredClone(summary.fills);
                    delete part['typography-ref-id'];
                    delete part['typography-ref-file'];
                }
                auto.fills = structuredClone(summary.fills);
                delete auto['applied-tokens'];

                const tolerance = add(numberTemplate);
                setText(textChild(tolerance), 'Flatness Tolerance');
                setText(textChild(child(tolerance, 'Editor')), '0.050');
                if (width === 224) {
                    for (const row of [size, tolerance]) {
                        const editor = child(row, 'Editor');
                        for (const [shape, x, width] of curvatureFieldBounds(textChild(row), editor, child(editor, 'Input'), textChild(editor))) {
                            bounds(shape, x, shape.y, width, shape.height);
                        }
                    }
                    bounds(auto, auto.x, auto.y, editor.width - 24, auto.height);
                }
                if (continuous) {
                    const measure = caption('Measure', left, cursor + 2, span, body.id);
                    measure.content = structuredClone(fieldLabel.content);
                    measure.fills = structuredClone(fieldLabel.fills);
                    delete measure['applied-tokens'];
                    setText(measure, 'Measure');
                    body.shapes.push(measure.id);
                    cursor += 22;
                    selector('Measure', ['Mean', 'Along', 'Across'], 1);
                    const ramp = add(rampTemplate);
                    setText(child(ramp, '▸ Depth Map'), '▸ Curvature (1/m)');
                    for (const [before, after] of [['−1 m', '−0.08'], ['+1 m', '+0.08']]) {
                        for (const shape of children(ramp).filter(s => s.name === before)) setText(shape, after);
                    }
                    for (const bar of children(ramp).filter(s => s.name.startsWith('Histogram '))) {
                        const headroom = Math.round(bar.height * 0.25);
                        bounds(bar, bar.x, bar.y + headroom, bar.width, bar.height - headroom);
                    }
                    cursor += 4;
                } else {
                    const legend = newFrame('Landform colour key', left, cursor, span, 30, body.id);
                    legend.fills = [];
                    for (const [i, [name, color]] of categories.entries()) {
                        const start = left + Math.floor(span * i / categories.length);
                        const swatch = cloneTree(swatchTemplate.id, legend.id);
                        bounds(swatch, start, cursor + 9, 12, 12);
                        swatch.fills = [{'fill-color': color, 'fill-opacity': 1}];
                        delete swatch['applied-tokens'];
                        const label = caption(name, start + 16, cursor + 6, Math.floor(span / categories.length) - 16, legend.id);
                        label.content = structuredClone(fieldLabel.content);
                        label.fills = structuredClone(fieldLabel.fills);
                        delete label['applied-tokens'];
                        setText(label, name);
                        legend.shapes.push(swatch.id, label.id);
                    }
                    body.shapes.push(legend.id);
                    cursor += legend.height;
                }
                const heading = caption(continuous ? '▸ Results' : '▾ Results', left, cursor, span, body.id);
                styleSectionHeader(heading, fieldLabel);
                body.shapes.push(heading.id);
                cursor += 26;
                if (!continuous) {
                    const results = add(resultsTemplate);
                    const text = child(results, 'Selectable Cut, Fill, Net and Mode');
                    const set = text.content.children[0];
                    set.children = categories.map(([name, , value]) => {
                        const paragraph = structuredClone(set.children[0]);
                        paragraph.children[0].text = name + ': ' + value;
                        return paragraph;
                    });
                    text.name = 'Selectable landform totals';
                    const surface = child(results, 'Text box surface');
                    bounds(surface, surface.x, surface.y, surface.width, 64);
                    bounds(text, text.x, text.y, text.width, 48);
                    bounds(results, results.x, results.y, results.width, 64);
                    cursor = results.y + results.height;
                }
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(gradientComplianceStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const waterflow = pageObjects('78 • Step 70 — Waterflow length limit');
        const earthworks = pageObjects('75 • Step 67 — Selectable Earthworks results');
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const accentFill = [{'fill-color': '#6a1b9a', 'fill-opacity': 1}];
        const modes = ['Off', 'Show', 'Warn'];

        for (const id of waterflow[zero].shapes) {
            const source = waterflow[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Waterflow / WITH A LIMIT', waterflow);
            const sampleBody = child(sample, 'Parameters', waterflow);
            const numberTemplate = child(sampleBody, 'Number property row', waterflow);
            const sourceTemplate = child(sampleBody, 'Sources property row', waterflow);
            const comparisonBoard = find(earthworks, `Step 67 / Earthworks copy / ${theme} / ${width}`);
            const comparison = child(child(comparisonBoard, 'Earthworks / Terrain', earthworks), 'Parameters', earthworks);
            const standardTemplate = child(comparison, 'Choice property row', earthworks);
            const modeTemplate = child(comparison, 'Density Mode mode selector', earthworks);
            const analysisBoard = find(atlas, `Step 37 / Analyses Atlas / ${theme}`);
            const gradient = child(child(analysisBoard, 'Gradient Compliance', atlas), 'Parameters', atlas);
            const rampLengths = child(gradient, 'GoingTable property row', atlas);
            const board = newFrame(`Step 72 / Gradient Compliance / ${theme} / ${width}`,
                source.x, source.y, 2 * width + 48, 750);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);

            for (const [column, open] of [false, true].entries()) {
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = open ? 'Gradient Compliance / Route Limits open' : 'Gradient Compliance';
                board.shapes.push(caption(open ? 'ROUTE LIMITS OPEN' : 'DEFAULT', x, board.y + 14, width, board.id).id, card.id);
                const header = child(card, 'Header');
                const accent = child(card, 'Accent strip');
                accent.fills = structuredClone(accentFill);
                setText(child(header, 'Site runoff'), 'Access');
                setText(child(header, 'Waterflow from Points · 8 paths'), 'Gradient Compliance · 2 runs over limit');
                const icon = child(header, 'WF');
                setText(icon, 'GC');
                icon.fills = structuredClone(accentFill);
                const iconParagraph = icon.content.children[0].children[0];
                for (const part of [iconParagraph, ...iconParagraph.children]) part.fills = structuredClone(accentFill);

                const body = child(card, 'Parameters');
                const left = body.x + 8, span = sourceTemplate.width;
                let cursor = body.y + 6;
                body.shapes = [];
                function add(template) {
                    const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }
                function number(label, value, unit) {
                    const row = add(numberTemplate);
                    setText(textChild(row), label);
                    const editor = child(row, 'Editor');
                    setText(textChild(editor), value);
                    setText(child(editor, 'Unit'), unit);
                }
                const standard = add(standardTemplate);
                const standardLabel = textChild(standard);
                setText(standardLabel, 'Standard');
                setText(textChild(child(standard, 'Editor')), 'Custom');
                number('Measure Over', '1.000', 'm');
                number('Level Limit', '2.00', '%');

                function heading(title, disclosure) {
                    const row = caption((disclosure ?? '▸') + ' ' + title, left, cursor, span, body.id);
                    styleSectionHeader(row, standardLabel);
                    if (!disclosure) setText(row, title);
                    body.shapes.push(row.id);
                    cursor += 26;
                }
                function rule(title, selected) {
                    const row = add(modeTemplate);
                    row.name = title + ' rule selector';
                    const segments = children(row).filter(s => s.type === 'frame');
                    const oldSelection = children(segments[1]).find(s => s.type === 'rect');
                    const selection = children(segments[selected]).find(s => s.type === 'rect');
                    for (const key of ['fills', 'strokes', 'applied-tokens']) {
                        [oldSelection[key], selection[key]] = [selection[key], oldSelection[key]];
                    }
                    for (const [index, segment] of segments.entries()) {
                        move(segment, 0, row.y + 3 - segment.y);
                        setText(textChild(segment), modes[index]);
                        segment.name = modes[index] + (index === selected ? ' (selected)' : '');
                    }
                    row.shapes = segments.map(s => s.id);
                    bounds(row, row.x, row.y, row.width, 32);
                    cursor = row.y + row.height;
                }
                heading('Level Areas');
                rule('Level Areas', 1);
                setText(textChild(add(sourceTemplate)), 'Boundaries');
                heading('Routes');
                rule('Routes', 2);
                setText(textChild(add(sourceTemplate)), 'Paths');
                number('Route Width', '1.500', 'm');
                heading('Route Limits', open ? '▾' : '▸');
                if (open) {
                    for (const [label, value, unit] of [
                        ['Walk Limit', '5.00', '%'], ['Ramp Limit', '8.00', '%'], ['Cross Limit', '2.00', '%'],
                        ['Landing Min', '1.500', 'm'], ['Walk Max Rise', '0.500', 'm'], ['Ramp Max Rise', '0.750', 'm'],
                    ]) number(label, value, unit);
                    const table = add(rampLengths);
                    const delta = span - table.width;
                    const title = child(table, 'Ramp Goings');
                    const placeholder = child(table, 'No limit');
                    setText(title, 'Ramp Run Lengths');
                    for (const part of children(table).filter(s => ['Add going button', 'Add'].includes(s.name))) move(part, delta, 0);
                    bounds(title, title.x, title.y, span - 30, title.height);
                    bounds(placeholder, left, table.y + 27, span, placeholder.height);
                    table.shapes = table.shapes.filter(id => objects[id].name !== 'Goings');
                    bounds(table, table.x, table.y, span, 52);
                    cursor = table.y + table.height;
                }
                heading('Results', '▸');
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(resultsHeadingStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const waterflow = pageObjects('78 • Step 70 — Waterflow length limit');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const earthworks = pageObjects('75 • Step 67 — Selectable Earthworks results');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);

        for (const id of waterflow[zero].shapes) {
            const source = waterflow[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const zoneBoard = find(zones, `Step 65 / Zone card / ${theme} / ${width}`);
            const earthworksBoard = find(earthworks, `Step 67 / Earthworks copy / ${theme} / ${width}`);
            const examples = [
                ['WATERFLOW', child(source, 'Waterflow / LAST BUILD OPEN', waterflow)],
                ['ZONE', child(zoneBoard, 'Zone / Open · default', zones)],
                ['EARTHWORKS', child(earthworksBoard, 'Earthworks / Original', earthworks)],
            ];
            const board = newFrame(`Step 71 / Results headings / ${theme} / ${width}`,
                source.x, source.y, 3 * width + 64, 375);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);
            for (const [column, [title, template]] of examples.entries()) {
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(template.id, board.id, x - template.x, y - template.y);
                card.name = title === 'ZONE' ? 'Zone / Results closed' : title === 'WATERFLOW' ? 'Waterflow / Results open' : 'Earthworks';
                board.shapes.push(caption(title, x, board.y + 14, width, board.id).id, card.id);
                const body = child(card, 'Parameters');
                const heading = children(body).find(s => s.type === 'text' && s.name.endsWith('Last Build'));
                setText(heading, heading.name.replace('Last Build', 'Results'));
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(waterflowStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const drainage = pageObjects('77 • Step 69 — Catchments and Ponding');
        const cutFill = pageObjects('76 • Step 68 — Cut and Fill drawing outputs');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const blue = [{'fill-color': '#1565c0', 'fill-opacity': 1}];

        for (const id of drainage[zero].shapes) {
            const source = drainage[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Catchments', drainage);
            const sampleBody = child(sample, 'Parameters', drainage);
            const colorTemplate = child(sampleBody, 'Color property row', drainage);
            const comparison = child(find(cutFill, `Step 68 / Cut and Fill / ${theme} / ${width}`),
                'Cut / Fill / Drawings on', cutFill);
            const comparisonBody = child(comparison, 'Parameters', cutFill);
            const sourceTemplate = child(comparisonBody, 'Sources property row', cutFill);
            const lengthTemplate = child(comparisonBody, 'Number property row', cutFill);
            const zone = child(find(zones, `Step 65 / Zone card / ${theme} / ${width}`), 'Zone / Fully expanded', zones);
            const resultTemplate = child(child(zone, 'Parameters', zones), 'Plan area result', zones);
            const board = newFrame(`Step 70 / Waterflow / ${theme} / ${width}`, source.x, source.y, 3 * width + 64, 375);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);

            for (const [column, title] of ['NO LIMIT', 'WITH A LIMIT', 'LAST BUILD OPEN'].entries()) {
                const limited = column === 1, resultsOpen = column === 2;
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = 'Waterflow / ' + title;
                board.shapes.push(caption(title, x, board.y + 14, width, board.id).id, card.id);
                const header = child(card, 'Header');
                const accent = child(card, 'Accent strip');
                accent.fills = structuredClone(blue);
                setText(child(header, 'Basins'), 'Site runoff');
                const summary = child(header, 'Catchments · 2 closed depressions');
                setText(summary, 'Waterflow from Points · 8 paths');
                const icon = child(header, 'CA');
                setText(icon, 'WF');
                icon.fills = structuredClone(blue);
                const iconParagraph = icon.content.children[0].children[0];
                for (const part of [iconParagraph, ...iconParagraph.children]) part.fills = structuredClone(blue);

                const body = child(card, 'Parameters');
                const left = body.x + 8, span = sourceTemplate.width;
                let cursor = body.y + 6;
                body.shapes = [];
                function add(template) {
                    const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }
                const points = add(sourceTemplate);
                setText(textChild(points), 'Start Points');
                const length = add(lengthTemplate);
                setText(textChild(length), 'Max Length');
                const editor = child(length, 'Editor');
                const value = textChild(editor);
                setText(value, limited ? '30.000' : 'No limit');
                if (!limited) {
                    const unit = child(editor, 'Unit');
                    editor.shapes = editor.shapes.filter(id => id !== unit.id);
                    const paragraph = value.content.children[0].children[0];
                    for (const part of [paragraph, ...paragraph.children]) {
                        part.fills = structuredClone(summary.fills);
                        delete part['typography-ref-id'];
                        delete part['typography-ref-file'];
                    }
                    value.fills = structuredClone(summary.fills);
                    delete value['applied-tokens'];
                }
                const color = add(colorTemplate);
                setText(textChild(color), 'Color');
                const swatch = child(child(color, 'Editor'), 'Color swatch');
                swatch.fills = structuredClone(blue);
                delete swatch['applied-tokens'];
                const disclosure = caption(resultsOpen ? '▾ Last Build' : '▸ Last Build', left, cursor, span, body.id);
                styleSectionHeader(disclosure, textChild(points));
                body.shapes.push(disclosure.id);
                cursor += 26;
                if (resultsOpen) {
                    for (const [label, count] of [['Reached terrain edge', '6'], ['Stopped at a low point', '2'], ['Starts outside terrain', '2']]) {
                        const result = add(resultTemplate);
                        const [name, reading] = children(result);
                        setText(name, label);
                        setText(reading, count);
                        bounds(name, name.x, name.y, span - 34, name.height);
                        bounds(reading, left + span - 30, reading.y, 30, reading.height);
                        result.name = label + ' result';
                    }
                }
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(drainageStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const cutFill = pageObjects('76 • Step 68 — Cut and Fill drawing outputs');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const children = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');

        for (const id of cutFill[zero].shapes) {
            const source = cutFill[id];
            const [, , theme, size] = source.name.split(' / ');
            const width = Number(size);
            const sample = child(source, 'Cut / Fill / Drawings on', cutFill);
            const template = child(sample, 'Parameters', cutFill);
            const zoneBoard = Object.values(zones).find(s => s.name === `Step 65 / Zone card / ${theme} / ${width}`);
            const zone = child(zoneBoard, 'Zone / Fully expanded', zones);
            const readout = child(child(zone, 'Parameters', zones), 'Plan area result', zones);
            const board = newFrame(`Step 69 / Drainage / ${theme} / ${width}`, source.x, source.y, 3 * width + 64, 540);
            board.fills = structuredClone(source.fills);
            board['applied-tokens'] = structuredClone(source['applied-tokens']);

            for (const [column, title] of ['CATCHMENTS', 'PONDING', 'PONDING · LAST BUILD OPEN'].entries()) {
                const ponding = column !== 0, detailsOpen = column === 2;
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = ponding ? (detailsOpen ? 'Ponding / Last Build open' : 'Ponding') : 'Catchments';
                board.shapes.push(caption(title, x, board.y + 14, width, board.id).id, card.id);

                const header = child(card, 'Header');
                const accent = child(card, 'Accent strip');
                const color = ponding ? '#d32f2f' : '#00897b';
                accent.fills = [{'fill-color': color, 'fill-opacity': 1}];
                setText(child(header, 'Cut / Fill'), ponding ? 'Ponding' : 'Basins');
                setText(child(header, 'Cut / Fill · Original'),
                    ponding ? 'Ponding · 2 depressions' : 'Catchments · 2 closed depressions');
                const previousIcon = child(header, 'AnCutFill') ?? child(header, '±');
                const icon = caption(ponding ? 'PD' : 'CA', previousIcon.x - 1, previousIcon.y - 3, 17, header.id);
                delete icon['applied-tokens'];
                const ink = [{'fill-color': color, 'fill-opacity': 1}];
                icon.fills = ink;
                const paragraph = icon.content.children[0].children[0];
                for (const part of [paragraph, ...paragraph.children]) {
                    part['font-size'] = '9';
                    part.fills = structuredClone(ink);
                    delete part['typography-ref-id'];
                    delete part['typography-ref-file'];
                }
                bounds(icon, icon.x, icon.y, 17, 16);
                header.shapes = header.shapes.map(id => id === previousIcon.id ? icon.id : id);

                const body = child(card, 'Parameters');
                const left = body.x + 8, span = template.width - 16;
                let cursor = body.y + 6;
                body.shapes = [];
                function add(rowTemplate) {
                    const row = cloneTree(rowTemplate.id, body.id, left - rowTemplate.x, cursor - rowTemplate.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }
                function number(label, value, unit) {
                    const row = add(child(template, 'Number property row', cutFill));
                    setText(textChild(row), label);
                    const editor = child(row, 'Editor');
                    setText(textChild(editor), value);
                    setText(child(editor, 'Unit'), unit);
                    return textChild(row);
                }
                function toggle(label, enabled, colorLabel) {
                    const row = add(child(template, 'Bool property row', cutFill));
                    setText(textChild(row), label);
                    child(child(row, 'Editor'), 'Checked').hidden = !enabled;
                    if (enabled) {
                        const picker = add(child(template, 'Color property row', cutFill));
                        setText(textChild(picker), colorLabel);
                    }
                }
                const fieldLabel = number('Flat Below', '0.3', '°');
                if (ponding) {
                    number('Ignore Below', '0.050', 'm');
                    const ramp = add(child(template, 'ColorRamp property row', cutFill));
                    setText(child(ramp, '▸ Depth Map'), '▸ Water Depth');
                    for (const [before, after] of [['−1 m', '0 m'], ['0', '0.2 m'], ['+1 m', '0.4 m']]) {
                        for (const part of children(ramp).filter(s => s.name === before)) setText(part, after);
                    }
                    toggle('Shorelines', true, 'Shoreline Color');
                    toggle('Spill Points', true, 'Spill Color');
                } else {
                    number('Merge Below', '1.00', '%');
                    toggle('Boundaries', true, 'Boundary Color');
                    toggle('Flow Paths', false, 'Path Color');
                }
                const resultHeader = caption(detailsOpen ? '▾ Last Build' : '▸ Last Build', left, cursor, span, body.id);
                styleSectionHeader(resultHeader, fieldLabel);
                body.shapes.push(resultHeader.id);
                cursor += 26;
                if (detailsOpen) {
                    for (const [label, value] of [['Deepest', '0.34 m'], ['Volume', '4.6 m³'], ['Wet area', '38 m²']]) {
                        const result = add(readout);
                        const [name, reading] = children(result);
                        setText(name, label);
                        setText(reading, value);
                        result.name = label + ' result';
                    }
                }
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(cutFillStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const earthworks = pageObjects('75 • Step 67 — Selectable Earthworks results');
        const analysis = pageObjects('19 • Step 11 — Consolidated panel review');
        const legend = pageObjects('47 • Step 39 — Legend follows terrain colouring');
        const fields = pageObjects('53 • Step 45 — Units inside aligned fields');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const children = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const orange = [{'fill-color': '#ef6c00', 'fill-opacity': 1}];
        const icon = find(allObjects, 'AnCutFill');
        const stops = [[0, '#313695'], [0.35, '#4575b4'], [0.5, '#e0f3f8'], [0.7, '#f4a582'], [1, '#a50026']];

        for (const id of earthworks[zero].shapes) {
            const originalBoard = earthworks[id];
            const [, , theme, size] = originalBoard.name.split(' / ');
            const width = Number(size);
            const sample = child(originalBoard, 'Earthworks / Original', earthworks);
            const analysisBoard = find(analysis, `Step 11 / Analysis / ${theme} / ${theme === 'dark' && width === 224 ? 240 : 320}`);
            const slope = child(analysisBoard, 'Slope', analysis);
            const rampTemplate = child(child(slope, 'Parameters', analysis), 'ColorRamp property row', analysis);
            const zone = child(find(zones, `Step 65 / Zone card / ${theme} / ${width}`), 'Zone / Open · default', zones);
            const toggleTemplate = child(child(zone, 'Parameters', zones), 'Bool property row', zones);
            const pathCard = child(find(fields, `Step 45 / Grade Path inline units / ${theme} / ${width}`),
                'Grade Path / CONSTANT WIDTH', fields);
            const intervalTemplate = child(child(pathCard, 'Parameters', fields), 'Number property row', fields);
            const legendCard = child(find(legend, `Step 39 / Legend / ${theme} / ${width}`), 'Legend / Appearance open', legend);
            const appearance = child(child(legendCard, 'Parameters', legend), 'Appearance heading', legend);
            const colorTemplate = child(appearance, 'Color property row', legend)
                ?? child(child(legendCard, 'Parameters', legend), 'Color property row', legend);

            const board = newFrame(`Step 68 / Cut and Fill / ${theme} / ${width}`,
                originalBoard.x, originalBoard.y * 2, 2 * width + 48, 550);
            board.fills = structuredClone(originalBoard.fills);
            board['applied-tokens'] = structuredClone(originalBoard['applied-tokens']);
            for (const [column, enabled] of [false, true].entries()) {
                const x = board.x + 16 + column * (width + 16), y = board.y + 48;
                const card = cloneTree(sample.id, board.id, x - sample.x, y - sample.y);
                card.name = enabled ? 'Cut / Fill / Drawings on' : 'Cut / Fill / Drawings off';
                board.shapes.push(caption(enabled ? 'DRAWINGS ON' : 'DRAWINGS OFF', x, board.y + 14, width, board.id).id, card.id);
                const header = child(card, 'Header');
                const accent = child(card, 'Accent strip');
                setText(child(header, 'Volumes'), 'Cut / Fill');
                setText(child(header, 'Earthworks · Net −18 m³'), 'Cut / Fill · Original');
                accent.fills = structuredClone(orange);
                const oldIcon = child(header, 'AnEarthwork');
                if (icon) {
                    oldIcon.fills = structuredClone(icon.fills);
                    oldIcon.name = icon.name;
                } else {
                    const mark = caption('±', oldIcon.x, oldIcon.y - 2, 12, header.id);
                    bounds(mark, mark.x, mark.y, 12, 16);
                    header.shapes = header.shapes.map(id => id === oldIcon.id ? mark.id : id);
                }

                const body = child(card, 'Parameters');
                const [comparison, boundary] = children(body);
                const left = boundary.x, span = boundary.width;
                let cursor = boundary.y + boundary.height;
                body.shapes = [comparison.id, boundary.id];
                function add(template, gap = 0) {
                    cursor += gap;
                    const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                    body.shapes.push(row.id);
                    cursor += row.height;
                    return row;
                }

                const ramp = add(rampTemplate, 4);
                const scale = span / ramp.width;
                for (const shape of children(ramp)) {
                    move(shape, (shape.x - ramp.x) * (scale - 1), 0);
                    if (shape.type !== 'path') bounds(shape, shape.x, shape.y, shape.width * scale, shape.height);
                }
                bounds(ramp, left, ramp.y, span, ramp.height);
                const autoRange = child(ramp, 'Auto-fit range');
                bounds(autoRange, autoRange.x, autoRange.y, Math.max(autoRange.width, 91), autoRange.height);
                setText(child(ramp, '▸ Slope'), '▸ Depth Map');
                const gradient = child(ramp, 'Terrain Spectrum ramp');
                gradient.fills = [{'fill-opacity': 1, 'fill-color-gradient': {
                    ...gradient.fills[0]['fill-color-gradient'],
                    stops: stops.map(([offset, color]) => ({offset, color, opacity: 1}))
                }}];
                gradient.name = 'Cool Warm ramp';
                delete gradient['applied-tokens'];
                for (const [tick, text] of [['0%', '−1 m'], ['25%', '0'], ['50%', '+1 m']]) {
                    const matches = children(ramp).filter(s => s.name === tick);
                    for (const shape of matches) setText(shape, text);
                }
                function toggle(label) {
                    const row = add(toggleTemplate);
                    setText(textChild(row), label);
                    child(child(row, 'Editor'), 'Checked').hidden = !enabled;
                }
                function color(label) {
                    const row = add(colorTemplate);
                    setText(textChild(row), label);
                }
                toggle('Balance Line');
                if (enabled) color('Balance Color');
                toggle('Depth Contours');
                if (enabled) {
                    const interval = add(intervalTemplate);
                    setText(textChild(interval), 'Interval');
                    setText(textChild(child(interval, 'Editor')), '0.500');
                    color('Contour Color');
                    const drawn = caption('Last Build · 8 contours · 1 balance line', left, cursor + 3, span, body.id);
                    body.shapes.push(drawn.id);
                    cursor += 25;
                }
                bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...children(board).map(s => s.y + s.height));
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(earthworksCopyStep){
        const reference = Object.values(pages).find(p => p.name === '74 • Step 66 — Earthworks comparison modes').objects;
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);

        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 66 / Earthworks', 'Step 67 / Earthworks copy');
            const originalCards = children(reference[id], reference).filter(s => s.type === 'frame');
            const cards = children(board).filter(s => s.type === 'frame');
            for (const [index, card] of cards.entries()) {
                const body = child(card, 'Parameters');
                const rows = children(body);
                const resultRows = rows.filter(s => s.name.endsWith(' result'));
                const basis = rows.find(s => s.name.startsWith('Last Build'));
                const estimated = basis.name.endsWith('Estimated');
                const lines = resultRows.map(row => children(row).map(s => textOf(s.content)).join(': '));
                lines.push(estimated ? 'Mode: Estimated from terrain delta' : 'Mode: Exact');
                setText(basis, 'Last Build');

                const first = resultRows[0], last = resultRows.at(-1);
                const field = newFrame('Read-only results text box', first.x, first.y, first.width, last.y + last.height - first.y, body.id);
                field.fills = [];
                const template = child(child(originalCards[index], 'Header', reference), 'Input', reference);
                const surface = cloneTree(template.id, field.id);
                bounds(surface, field.x, field.y, field.width, field.height);
                surface.name = 'Text box surface';

                const originalBody = child(originalCards[index], 'Parameters', reference);
                const resultLabel = child(child(originalBody, 'Cut result', reference), 'Cut', reference);
                const text = cloneTree(resultLabel.id, field.id);
                const textSet = text.content.children[0];
                textSet.children = lines.map(line => {
                    const paragraph = structuredClone(textSet.children[0]);
                    paragraph.children[0].text = line;
                    return paragraph;
                });
                delete text['position-data'];
                text.name = 'Selectable Cut, Fill, Net and Mode';
                bounds(text, field.x + 8, field.y + 8, field.width - 16, field.height - 16);
                field.shapes = [surface.id, text.id];
                body.shapes = rows.filter(s => !resultRows.includes(s) || s === first).map(s => s === first ? field.id : s.id);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(earthworksStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const scatter = pageObjects('69 • Step 61 — Scatter placement modes');
        const zones = pageObjects('73 • Step 65 — Zone card and Last Build');
        const reference = pageObjects('19 • Step 11 — Consolidated panel review');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape).find(s => s.type === 'text');
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const modes = ['Original', 'Terrain', 'Geometry'];

        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const analysis = find(reference, `Step 11 / Analysis / ${theme} / 320`);
            const earthworks = child(analysis, 'Earthworks', reference);
            const icon = child(child(earthworks, 'Header', reference), 'AnEarthwork', reference);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const curveBoard = find(scatter, `Step 61 / Along Curve / ${theme} / ${width}`);
                const sample = child(curveBoard, 'Kerbs', scatter);
                const sampleBody = child(sample, 'Parameters', scatter);
                const selector = child(sampleBody, 'Density Mode mode selector', scatter);
                const source = child(sampleBody, 'Sources property row', scatter);
                const regionBoard = find(scatter, `Step 61 / Region sources / ${theme} / ${width}`);
                const pattern = child(child(child(regionBoard, 'Canopy', scatter), 'Parameters', scatter), 'Choice property row', scatter);
                const zoneBoard = find(zones, `Step 65 / Zone card / ${theme} / ${width}`);
                const result = child(child(child(zoneBoard, 'Zone / Fully expanded', zones), 'Parameters', zones), 'Plan area result', zones);
                const x = themeIndex * 1400, y = widthIndex * 520;
                const board = newFrame(`Step 66 / Earthworks / ${theme} / ${width}`, x, y, 3 * width + 64, 440);
                board.fills = structuredClone(curveBoard.fills);
                board['applied-tokens'] = structuredClone(curveBoard['applied-tokens']);

                for (const [index, mode] of modes.entries()) {
                    const cardX = x + 16 + index * (width + 16);
                    const card = cloneTree(sample.id, board.id, cardX - sample.x, y + 48 - sample.y);
                    card.name = 'Earthworks / ' + mode;
                    board.shapes.push(caption(mode.toUpperCase(), cardX, y + 14, width, board.id).id, card.id);
                    const header = child(card, 'Header');
                    const accent = child(card, 'Accent strip');
                    const badge = child(header, 'ObjScatter');
                    const quantities = [['64', '82', '−18'], ['86', '74', '+12'], ['91', '78', '+13']][index];
                    setText(child(header, 'Kerbs'), 'Volumes');
                    setText(child(header, 'Scatter · 42 of 48 placed'), `Earthworks · Net ${quantities[2]} m³`);
                    badge.fills = structuredClone(icon.fills);
                    badge.name = icon.name;
                    accent.fills = structuredClone(child(earthworks, 'Accent strip', reference).fills);
                    delete accent['applied-tokens'];
                    const arrow = child(header, 'Collapse arrow');
                    Object.assign(arrow, downDisclosure(arrow));

                    const body = child(card, 'Parameters');
                    const left = body.x + 8, span = source.width;
                    let cursor = body.y + 6;
                    body.shapes = [];
                    function add(template) {
                        const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                        body.shapes.push(row.id);
                        cursor += row.height;
                        return row;
                    }
                    const comparison = add(selector);
                    const label = textChild(comparison);
                    setText(label, 'Compare To');
                    const segments = children(comparison).filter(s => s.type === 'frame');
                    // Use a full-width selector at each width.
                    bounds(label, left, comparison.y, span, 18);
                    bounds(comparison, left, comparison.y, span, 52);
                    cursor = comparison.y + comparison.height;
                    const activeSurface = children(segments[0]).find(s => s.type === 'rect');
                    const nextSurface = children(segments[index]).find(s => s.type === 'rect');
                    for (const key of ['fills', 'strokes', 'applied-tokens']) {
                        [activeSurface[key], nextSurface[key]] = [nextSurface[key], activeSurface[key]];
                    }
                    for (const [i, segment] of segments.entries()) {
                        const start = left + Math.floor(span * i / modes.length);
                        const end = left + Math.floor(span * (i + 1) / modes.length);
                        const delta = end - start - segment.width;
                        move(segment, start - segment.x, comparison.y + 22 - segment.y);
                        bounds(segment, start, segment.y, end - start, segment.height);
                        for (const part of children(segment)) bounds(part, part.x, part.y, part.width + delta, part.height);
                        setText(textChild(segment), modes[i]);
                        segment.name = modes[i] + (i === index ? ' (selected)' : '');
                    }
                    if (mode === 'Terrain') {
                        const row = add(pattern);
                        setText(textChild(row), 'Terrain');
                        setText(textChild(child(row, 'Editor')), 'Existing Site');
                    }
                    if (mode === 'Geometry') setText(textChild(add(source)), 'Geometry');
                    setText(textChild(add(source)), 'Boundary');
                    const basis = caption(mode === 'Original' ? 'Last Build · Estimated' : 'Last Build · Exact',
                        left, cursor + 4, span, body.id);
                    body.shapes.push(basis.id);
                    cursor += 26;
                    for (const [i, name] of ['Cut', 'Fill', 'Net'].entries()) {
                        const row = add(result);
                        const [nameLabel, value] = children(row);
                        setText(nameLabel, name);
                        setText(value, quantities[i] + ' m³');
                        row.name = name + ' result';
                    }
                    bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                    bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                }
                const bottom = Math.max(...children(board).map(s => s.y + s.height));
                bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(zoneSummaryStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const placement = pageObjects('70 • Step 62 — Plant and Orient actions');
        const scatter = pageObjects('69 • Step 61 — Scatter placement modes');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);
        const green = [{'fill-color': '#82915e', 'fill-opacity': 1}];
        const measurements = [
            ['Plan area', '1,240 m²'],
            ['Surface area', '1,268 m²'],
            ['Elevation', '12.1 / 13.4 / 14.8 m'],
            ['Slope', '1.2 / 4.8 / 9.6%'],
            ['Mesh output', '1 mesh · 2,480 faces'],
            ['Earthworks', 'Cut 64 m³', 'Fill 82 m³', 'Net −18 m³'],
        ];
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            for (const [widthIndex, width] of [400, 224].entries()) {
                const placementBoard = find(placement, `Step 62 / Plant and Orient / ${theme} / ${width}`);
                const sample = child(placementBoard, 'Plant', placement);
                const sampleBody = child(sample, 'Parameters', placement);
                const pickerTemplate = child(sampleBody, 'Sources property row', placement);
                const labelTemplate = child(pickerTemplate, 'Sources', placement);
                const regionBoard = find(scatter, `Step 61 / Region sources / ${theme} / ${width}`);
                const regionBody = child(child(regionBoard, 'Canopy', scatter), 'Parameters', scatter);
                const priorityTemplate = child(regionBody, 'Bool property row', scatter);
                const curveBoard = find(scatter, `Step 61 / Along Curve / ${theme} / ${width}`);
                const closedArrow = child(child(child(curveBoard, 'Kerbs', scatter), 'Header', scatter), 'Collapse arrow', scatter);
                const x = themeIndex * 1400, y = widthIndex * 520;
                const board = newFrame(`Step 65 / Zone card / ${theme} / ${width}`, x, y, 3 * width + 64, 440);
                board.fills = structuredClone(placementBoard.fills);
                board['applied-tokens'] = structuredClone(placementBoard['applied-tokens']);

                for (const [column, state] of ['Collapsed', 'Open · default', 'Fully expanded'].entries()) {
                    const cardX = x + 16 + column * (width + 16);
                    const card = cloneTree(sample.id, board.id, cardX - sample.x, y + 48 - sample.y);
                    card.name = 'Zone / ' + state;
                    board.shapes.push(caption(state.toUpperCase(), cardX, y + 14, width, board.id).id, card.id);
                    const header = child(card, 'Header');
                    const body = child(card, 'Parameters');
                    const accent = child(card, 'Accent strip');
                    const swatch = child(header, 'Icon plate');
                    const title = child(header, 'Trees');
                    const input = child(header, 'Input');
                    const duplicate = child(header, 'Duplicate button');
                    const widthGain = duplicate.width + 6;
                    setText(title, 'Planting');
                    setText(child(header, 'Plant · 12 placed'), 'Zone · 2 objects · 1 layer');
                    for (const shape of [accent, swatch]) {
                        shape.fills = structuredClone(green);
                        delete shape['applied-tokens'];
                    }
                    swatch.name = 'Zone colour swatch';
                    header.shapes = children(header).filter(s => !['ObjLowestPoint', 'Duplicate button', 'Duplicate'].includes(s.name)).map(s => s.id);
                    bounds(input, input.x, input.y, input.width + widthGain, input.height);
                    bounds(title, title.x, title.y, title.width + widthGain, title.height);
                    if (column === 0) {
                        const arrow = child(header, 'Collapse arrow');
                        const rightArrow = cloneTree(closedArrow.id, header.id,
                            arrow.x + (arrow.width - closedArrow.width) / 2 - closedArrow.x,
                            arrow.y + (arrow.height - closedArrow.height) / 2 - closedArrow.y);
                        header.shapes = header.shapes.filter(id => id !== input.id).map(id => id === arrow.id ? rightArrow.id : id);
                        card.shapes = card.shapes.filter(id => id !== body.id);
                        bounds(card, card.x, card.y, card.width, header.height);
                    } else {
                        const picker = child(body, 'Sources property row');
                        const pickerLabel = child(picker, 'Sources');
                        setText(pickerLabel, 'Zone Area');
                        const priority = cloneTree(priorityTemplate.id, body.id, picker.x - priorityTemplate.x,
                            picker.y + picker.height - priorityTemplate.y);
                        setText(child(priority, 'Exclusions'), 'Priority by Elevation');
                        child(child(priority, 'Editor'), 'Checked').hidden = false;
                        let cursor = priority.y + priority.height;
                        const resultHeader = caption(column === 2 ? '▾ Last Build' : '▸ Last Build',
                            picker.x, cursor, picker.width, body.id);
                        styleSectionHeader(resultHeader, pickerLabel);
                        body.shapes = [picker.id, priority.id, resultHeader.id];
                        cursor += 26;
                        if (column === 2) {
                            for (const [name, ...values] of measurements) {
                                const rowHeight = 8 + 18 * values.length;
                                const row = newFrame(name + ' result', picker.x, cursor, picker.width, rowHeight, body.id);
                                row.fills = [];
                                const nameLabel = cloneTree(labelTemplate.id, row.id);
                                setText(nameLabel, name);
                                bounds(nameLabel, row.x, cursor + 4, pickerLabel.width, 18);
                                row.shapes = [nameLabel.id];
                                for (const [index, value] of values.entries()) {
                                    const text = caption(value, row.x, cursor, row.width, row.id);
                                    text.content = structuredClone(pickerLabel.content);
                                    text.fills = structuredClone(pickerLabel.fills);
                                    delete text['applied-tokens'];
                                    delete text['position-data'];
                                    const paragraph = text.content.children[0].children[0];
                                    paragraph['text-align'] = 'right';
                                    for (const fragment of paragraph.children) fragment['text-align'] = 'right';
                                    setText(text, value);
                                    const valueX = row.x + pickerLabel.width + 4;
                                    bounds(text, valueX, cursor + 4 + index * 18, row.x + row.width - valueX, 18);
                                    row.shapes.push(text.id);
                                }
                                body.shapes.push(row.id);
                                cursor += row.height;
                            }
                        }
                        bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                        bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                    }
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                }
                const bottom = Math.max(...children(board).map(s => s.y + s.height));
                bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(sectionContextStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const panelPage = pageObjects('19 • Step 11 — Consolidated panel review');
        const objectPage = pageObjects('70 • Step 62 — Plant and Orient actions');
        const scatterPage = pageObjects('69 • Step 61 — Scatter placement modes');
        const children = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const find = (index, name) => Object.values(index).find(s => s.name === name);

        for (const [index, width] of [224, 400].entries()) {
            const panelWidth = width + 16;
            const panelTemplate = find(panelPage, `Step 11 / Objects / dark / ${width === 224 ? 240 : 480}`);
            const objectBoard = find(objectPage, `Step 62 / Plant and Orient / dark / ${width}`);
            const scatterBoard = find(scatterPage, `Step 61 / Along Curve / dark / ${width}`);
            const plant = child(objectBoard, 'Plant', objectPage);
            const orient = child(objectBoard, 'Orient', objectPage);
            const scatter = child(scatterBoard, 'Kerbs', scatterPage);
            const arrow = child(child(scatter, 'Header', scatterPage), 'Collapse arrow', scatterPage);
            const board = newFrame(`Step 64 / Section context / dark / ${panelWidth}`,
                index * 600, index * 1100, 2 * panelWidth + 48, 980);
            board.fills = structuredClone(objectBoard.fills);
            board['applied-tokens'] = structuredClone(objectBoard['applied-tokens']);
            let bottom = board.y;

            for (const [column, clearer] of [false, true].entries()) {
                const x = board.x + 16 + column * (panelWidth + 16), y = board.y + 44;
                const panel = cloneTree(panelTemplate.id, board.id, x - panelTemplate.x, y - panelTemplate.y);
                const shell = child(panel, 'Panel shell');
                panel.name = clearer ? 'Objects / Clearer headers' : 'Objects / Current headers';
                panel.shapes = [shell.id];
                const widthChange = panelWidth - shell.width;
                if (widthChange) {
                    let tab = -1;
                    const stretch = new Set(['Identity toolbar', 'Input', 'Proposed Terrain',
                        'Terrain Settings section', '▶  Terrain Settings', 'Status section', '▶  Status', 'TERRAIN OBJECTS']);
                    for (const shape of children(shell)) {
                        const offset = shape.y - shell.y;
                        if (offset >= 142 && offset < 170) {
                            const surface = shape.name.endsWith(' tab');
                            if (surface) tab++;
                            const tabChange = widthChange / 5;
                            const localX = shape.x - shell.x - 4 - tab * ((shell.width - 16) / 5 + 2);
                            move(shape, tab * tabChange + (localX > 72 ? tabChange : 0), 0);
                            if (surface || shape.type === 'text') bounds(shape, shape.x, shape.y, shape.width + tabChange, shape.height);
                        } else if (stretch.has(shape.name)) {
                            bounds(shape, shape.x, shape.y, shape.width + widthChange, shape.height);
                        } else if (offset < 70 && (shape.x - shell.x >= shell.width - 90 || shape.name === 'ChevronDown')) {
                            move(shape, widthChange, 0);
                        }
                    }
                    bounds(shell, shell.x, shell.y, panelWidth, shell.height);
                }
                let cursor = y + 243;
                for (const template of [plant, orient, scatter]) {
                    const card = cloneTree(template.id, panel.id, x + 8 - template.x, cursor - template.y);
                    panel.shapes.push(card.id);
                    const header = child(card, 'Header'), body = child(card, 'Parameters'), accent = child(card, 'Accent strip');
                    if (template === plant) {
                        card.shapes = card.shapes.filter(id => id !== body.id);
                        const expanded = child(header, 'Collapse arrow');
                        const collapsed = cloneTree(arrow.id, header.id,
                            expanded.x + (expanded.width - arrow.width) / 2 - arrow.x,
                            expanded.y + (expanded.height - arrow.height) / 2 - arrow.y);
                        header.shapes = header.shapes.map(id => id === expanded.id ? collapsed.id : id);
                        bounds(card, card.x, card.y, card.width, header.height);
                    }
                    if (template === scatter) {
                        const disclosure = child(header, 'Collapse arrow');
                        Object.assign(disclosure, downDisclosure(disclosure));
                        const heading = child(body, 'Block Mix heading');
                        setText(child(heading, '▾ Block Mix · 3'), '▸ Block Mix · 3');
                        const rows = children(body);
                        const from = rows.indexOf(child(body, 'Block order selector'));
                        const to = rows.indexOf(child(body, 'Density Mode mode selector'));
                        const shift = rows[to].y - rows[from].y;
                        for (const row of rows.slice(to)) move(row, 0, -shift);
                        body.shapes = [...body.shapes.slice(0, from), ...body.shapes.slice(to)];
                        bounds(body, body.x, body.y, body.width, body.height - shift);
                        bounds(card, card.x, card.y, card.width, card.height - shift);
                    }
                    if (clearer && template !== plant) {
                        const picker = child(body, 'Sources property row');
                        const label = children(picker).find(s => s.type === 'text');
                        for (const row of children(body)) {
                            if (row.type === 'text' && /^[▸▾] /.test(row.name)) styleSectionHeader(row, label);
                            if (row.name === 'Block Mix heading') styleSectionHeader(child(row, '▸ Block Mix · 3'), label);
                        }
                    }
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    cursor += card.height + 8;
                }
                bounds(panel, x, y, panelWidth, cursor - y);
                board.shapes.push(caption(clearer ? 'CLEARER HEADERS' : 'CURRENT HEADERS',
                    x, board.y + 14, panelWidth, board.id).id, panel.id);
                bottom = Math.max(bottom, cursor);
            }
            bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(sectionHeadersStep){
        const reference = Object.values(pages).find(p => p.name === '70 • Step 62 — Plant and Orient actions').objects;
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 62 / Plant and Orient', 'Step 63 / Section headers');
            const parts = board.shapes.map(id => objects[id]);
            const [current, proposed, expanded] = parts.filter(s => s.type === 'frame');
            const [beforeTitle, afterTitle, expandedTitle] = parts.filter(s => s.type === 'text');
            setText(beforeTitle, 'CURRENT');
            setText(afterTitle, 'CLEARER HEADERS');
            setText(expandedTitle, 'BASE OPEN');
            const sourceCard = reference[reference[id].shapes.find(id => reference[id].name === 'Orient')];
            const comparison = cloneTree(sourceCard.id, board.id, current.x - sourceCard.x);
            board.shapes = board.shapes.map(id => id === current.id ? comparison.id : id);
            for (const card of [proposed, expanded]) {
                const body = card.shapes.map(id => objects[id]).find(s => s.name === 'Parameters');
                const picker = objects[body.shapes[0]];
                const label = picker.shapes.map(id => objects[id]).find(s => s.type === 'text');
                const sections = body.shapes.map(id => objects[id]).filter(s => /^([▸▾]) (Base|Variation)$/.test(s.name));
                for (const section of sections) styleSectionHeader(section, label);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(objectPlacementStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const scatter = pageObjects('69 • Step 61 — Scatter placement modes');
        const fields = pageObjects('53 • Step 45 — Units inside aligned fields');
        const reference = pageObjects('19 • Step 11 — Consolidated panel review');
        const children = (shape, index) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => children(shape, index).find(s => s.name === name);
        const textChild = shape => children(shape, objects).find(s => s.type === 'text');
        const find = (index, name) => Object.values(index).find(s => s.name === name);

        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const objectsBoard = find(reference, `Step 11 / Objects / ${theme} / 320`);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const sampleBoard = find(scatter, `Step 61 / Along Curve / ${theme} / ${width}`);
                const sample = child(sampleBoard, 'Kerbs', scatter);
                const sampleBody = child(sample, 'Parameters', scatter);
                const fieldBoard = find(fields, `Step 45 / Grade Path inline units / ${theme} / ${width}`);
                const gradePath = child(fieldBoard, 'Grade Path / CONSTANT WIDTH', fields);
                const numeric = child(child(gradePath, 'Parameters', fields), 'Number property row', fields);
                const buttonRow = child(sampleBody, 'Block Mix heading', scatter);
                const surface = child(buttonRow, 'Add button', scatter);
                const buttonLabel = child(buttonRow, 'Add', scatter);
                const x = themeIndex * 1400, y = widthIndex * 520;
                const board = newFrame(`Step 62 / Plant and Orient / ${theme} / ${width}`, x, y, 3 * width + 64, 440);
                board.fills = structuredClone(sampleBoard.fills);
                board['applied-tokens'] = structuredClone(sampleBoard['applied-tokens']);

                for (const [column, name] of ['Plant', 'Orient', 'Orient'].entries()) {
                    const baseOpen = column === 2;
                    const cardX = x + 16 + column * (width + 16);
                    const heading = column === 2 ? 'ORIENT · BASE OPEN' : name.toUpperCase();
                    board.shapes.push(caption(heading, cardX, y + 14, width, board.id).id);
                    const card = cloneTree(sample.id, board.id, cardX - sample.x, y + 48 - sample.y);
                    card.name = name + (baseOpen ? ' / Base open' : '');
                    board.shapes.push(card.id);

                    const header = child(card, 'Header');
                    const plant = name === 'Plant';
                    setText(child(header, 'Kerbs'), plant ? 'Trees' : 'Benches');
                    setText(child(header, 'Scatter · 42 of 48 placed'), plant ? 'Plant · 12 placed' : 'Orient · 5 placed · 1 skipped');
                    const oldCard = child(objectsBoard, name, reference);
                    const oldHeader = child(oldCard, 'Header', reference);
                    const icon = child(oldHeader, plant ? 'ObjLowestPoint' : 'ObjSurfaceOriented', reference);
                    const badge = child(header, 'ObjScatter');
                    badge.fills = structuredClone(icon.fills);
                    badge.name = icon.name;
                    const accent = child(card, 'Accent strip');
                    accent.fills = structuredClone(child(oldCard, 'Accent strip', reference).fills);
                    delete accent['applied-tokens'];
                    const arrow = child(header, 'Collapse arrow');
                    Object.assign(arrow, downDisclosure(arrow));

                    const body = child(card, 'Parameters');
                    const picker = child(body, 'Sources property row');
                    const left = picker.x, span = picker.width;
                    let cursor = body.y + 6;
                    body.shapes = [];
                    function add(row, advance = row.height) {
                        move(row, 0, cursor - row.y);
                        body.shapes.push(row.id);
                        cursor += advance;
                    }
                    function actions(name, labels) {
                        const row = newFrame(name, left, cursor, span, 32, body.id);
                        row.fills = [];
                        const half = (span - 4) / 2;
                        for (const [index, title] of labels.entries()) {
                            const bx = left + index * (half + 4);
                            const button = cloneTree(surface.id, row.id);
                            bounds(button, bx, cursor + 2, half, 26);
                            button.name = title + ' button';
                            const label = cloneTree(buttonLabel.id, row.id);
                            bounds(label, bx + 2, cursor + 4, half - 4, 22);
                            setText(label, title);
                            row.shapes.push(button.id, label.id);
                        }
                        add(row);
                    }
                    setText(textChild(picker), 'Sources');
                    add(picker);
                    for (const [title, value, unitText] of [['Heading', '0.0', '°'], ['Scale', '1.00', ''], ['Offset', '0.000', 'm']]) {
                        const row = cloneTree(numeric.id, body.id, left - numeric.x, cursor - numeric.y);
                        const editor = child(row, 'Editor');
                        const unit = child(editor, 'Unit');
                        setText(textChild(row), title);
                        setText(textChild(editor), value);
                        if (unitText) {
                            setText(unit, unitText);
                            unit.name = 'Unit';
                        } else {
                            editor.shapes = editor.shapes.filter(id => id !== unit.id);
                        }
                        add(row);
                    }
                    add(caption(baseOpen ? '▾ Base' : '▸ Base', left, cursor, span, body.id), 26);
                    if (baseOpen) actions('Base actions', ['Show Base', 'Change Base']);
                    add(caption('▸ Variation', left, cursor, span, body.id), 26);
                    actions('Release actions', ['Release Selected', 'Release All']);
                    bounds(body, body.x, body.y, body.width, cursor + 4 - body.y);
                    bounds(card, card.x, card.y, card.width, cursor + 10 - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    centreButtonLabels(card);
                }
                const bottom = Math.max(...children(board, objects).map(s => s.y + s.height));
                bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(scatterPlacementStep){
        const reference = Object.values(pages).find(p => p.name === '68 • Step 60 — Collapsible Scatter block mix').objects;
        const children = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name) => children(shape).find(s => s.name === name);
        const label = shape => children(shape).find(s => s.type === 'text');

        function segmentText(segment, name, selected) {
            setText(label(segment), name);
            segment.name = name + (selected ? ' (selected)' : '');
        }

        function switchSelection(segments, selectedIndex) {
            if (selectedIndex === 0) return;
            const first = children(segments[0]).find(s => s.type === 'rect');
            const selected = children(segments[selectedIndex]).find(s => s.type === 'rect');
            for (const key of ['fills', 'strokes', 'applied-tokens']) {
                [first[key], selected[key]] = [selected[key], first[key]];
            }
        }

        for (const id of reference[zero].shapes) {
            const sourceBoard = reference[id];
            const originalCard = children(sourceBoard, reference).find(s => s.name === 'Canopy');
            const width = originalCard.width;
            const originalBody = children(originalCard, reference).find(s => s.name === 'Parameters');
            const originalHeader = children(originalCard, reference).find(s => s.name === 'Header');
            const grips = children(originalHeader, reference).filter(s => s.name === 'Drag grip dot');
            const modeTemplate = children(originalBody, reference).find(s => s.name === 'Source mode selector');
            for (const region of [false, true]) {
                const board = cloneTree(id);
                board.name = board.name.replace('Step 60 / Scatter', region ? 'Step 61 / Region sources' : 'Step 61 / Along Curve');
                const y = (width === 224 ? 1400 : 0) + (region ? 560 : 0);
                move(board, 0, y - board.y);
                board.shapes = [];
                for (const [column, alternate] of [false, true].entries()) {
                    const dx = column * (width + 16);
                    const headingText = region ? (alternate ? 'ZONES' : 'BOUNDARIES') : (alternate ? 'SEQUENCE' : 'RANDOM');
                    board.shapes.push(caption(headingText, originalCard.x + dx, board.y + 14, width, board.id).id);
                    const card = cloneTree(originalCard.id, board.id, dx, board.y - sourceBoard.y);
                    board.shapes.push(card.id);
                    card.name = region ? 'Canopy' : 'Kerbs';
                    const header = child(card, 'Header');
                    setText(child(header, 'Canopy'), card.name);
                    setText(child(header, 'Scatter · 10 blocks · 240 trees'),
                        region ? 'Scatter · 183 of 240 placed' : 'Scatter · 42 of 48 placed');
                    const body = child(card, 'Parameters');
                    const sourceMode = child(body, 'Source mode selector');
                    const sourcePicker = child(body, 'Sources property row');
                    const mix = child(body, 'Block Mix heading');
                    const columns = child(body, 'Block Mix columns');
                    const blocks = children(body).filter(s => s.name.endsWith(' block')).slice(0, 3);
                    const pattern = child(body, 'Choice property row');
                    const density = child(body, 'Density Mode mode selector');
                    const count = children(body).find(s => child(s, 'Total Count'));
                    const disclosures = children(body).filter(s => s.name.startsWith('▸'));
                    const modeRow = cloneTree(modeTemplate.id, body.id, sourceMode.x - modeTemplate.x);
                    const modeSegments = children(modeRow);
                    setText(child(mix, '▾ Block Mix · 10'), '▾ Block Mix · 3');
                    setText(child(columns, 'Mix'), 'Share');

                    if (region) {
                        modeRow.name = 'Region source selector';
                        switchSelection(modeSegments, Number(alternate));
                        segmentText(modeSegments[0], 'Boundaries', !alternate);
                        segmentText(modeSegments[1], 'Zones', alternate);
                        if (alternate) {
                            setText(label(sourcePicker), 'Zones');
                            const editor = child(sourcePicker, 'Editor');
                            const button = children(editor)[0];
                            editor.shapes = [button.id];
                            const extra = editor.width - button.width;
                            bounds(button, button.x, button.y, editor.width, button.height);
                            for (const part of children(button)) bounds(part, part.x, part.y, part.width + extra, part.height);
                            setText(label(button), '2 zones');
                        }
                    } else {
                        const modes = children(sourceMode);
                        switchSelection(modes, 1);
                        segmentText(modes[0], 'Within Region', false);
                        segmentText(modes[1], 'Along Curve', true);
                        setText(label(sourcePicker), 'Paths');
                        modeRow.name = 'Block order selector';
                        switchSelection(modeSegments, Number(alternate));
                        segmentText(modeSegments[0], 'Random', !alternate);
                        segmentText(modeSegments[1], 'Sequence', alternate);
                        const densitySegments = children(density).filter(s => s.type === 'frame');
                        segmentText(densitySegments[1], width === 224 ? 'Centre' : 'Centre Spacing', false);
                        segmentText(densitySegments[2], 'Edge Gap', false);
                        setText(label(child(count, 'Editor')), '48');
                    }

                    const names = region ? ['English Oak', 'Silver Birch', 'Field Maple'] : ['Straight', 'Drain', 'Corner'];
                    if (!region && alternate) {
                        const weight = child(columns, 'Weight');
                        const share = child(columns, 'Share');
                        const name = child(columns, 'Block');
                        columns.shapes = columns.shapes.filter(id => id !== weight.id && id !== share.id);
                        bounds(name, columns.x + 56, name.y, columns.width - 76, name.height);
                    }
                    for (const [index, block] of blocks.entries()) {
                        const parts = children(block);
                        const [name, share, value, remove] = parts.filter(s => s.type === 'text');
                        setText(name, names[index]);
                        setText(share, ['50%', '17%', '33%'][index]);
                        remove.name = 'Remove ' + names[index];
                        block.name = names[index] + ' block';
                        if (!region && alternate) {
                            block.shapes = parts.filter(s => s !== share && s !== value && s.name !== 'Input').map(s => s.id);
                            move(child(block, 'Editor'), 17, 0);
                            bounds(name, block.x + 56, name.y, block.width - 76, name.height);
                            const order = caption(String(index + 1), block.x + 38, block.y + 4, 16, block.id);
                            block.shapes.push(order.id);
                            const left = Math.min(...grips.map(s => s.x));
                            const top = Math.min(...grips.map(s => s.y));
                            for (const dot of grips) {
                                block.shapes.push(cloneTree(dot.id, block.id, block.x + 4 - left, block.y + 9 - top).id);
                            }
                        }
                    }
                    const exclusions = children(body).find(s => child(s, 'Exclusions'));
                    if (region) child(child(exclusions, 'Editor'), 'Checked').hidden = true;
                    const rows = [
                        sourceMode, ...(region ? [modeRow] : []), sourcePicker,
                        ...(region ? [exclusions] : []),
                        mix, ...(!region ? [modeRow] : []), columns, ...blocks,
                        ...(region ? [pattern] : []), density, count,
                        ...disclosures.filter(s => region || s.name !== '▸ Edge Settings'),
                    ];
                    let y = body.y + 6;
                    body.shapes = rows.map(row => {
                        move(row, 0, y - row.y);
                        y += row.height + (row.name.startsWith('▸') ? 4 : 0);
                        return row.id;
                    });
                    bounds(body, body.x, body.y, body.width, y + 6 - body.y);
                    bounds(card, card.x, card.y, card.width, y + 12 - card.y);
                    const accent = child(card, 'Accent strip');
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                }
                const bottom = Math.max(...children(board).map(s => s.y + s.height));
                bounds(board, board.x, board.y, board.width, bottom + 16 - board.y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(scatterMixDisclosureStep){
        const reference = Object.values(pages).find(p => p.name === '67 • Step 59 — Compact Scatter block mix').objects;
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name) => childrenOf(shape).find(s => s.name === name);
        for (const id of reference[zero].shapes) {
            const source = reference[id];
            const board = cloneTree(id);
            board.name = board.name.replace('Step 59', 'Step 60');
            board.shapes = [];
            const sample = childrenOf(source, reference).find(s => s.name === 'TEN BLOCKS');
            const columnShapes = childrenOf(source, reference).filter(s => s.x === sample.x);
            const gap = sample.width + 16;
            for (const [column, expanded] of [true, false].entries()) {
                const shapes = columnShapes.map(s => cloneTree(s.id, board.id, (column - 1) * gap));
                const caption = shapes.find(s => s.name === 'TEN BLOCKS');
                setText(caption, expanded ? 'EXPANDED BY DEFAULT' : 'COLLAPSED');
                const card = shapes.find(s => s.name === 'Canopy');
                const body = child(card, 'Parameters');
                const heading = child(body, 'Block Mix heading');
                const title = child(heading, 'Block Mix');
                setText(title, (expanded ? '▾' : '▸') + ' Block Mix · 10');
                if (!expanded) {
                    const rows = childrenOf(body);
                    const columns = rows.findIndex(s => s.name === 'Block Mix columns');
                    const pattern = rows.findIndex(s => s.name === 'Choice property row');
                    if (columns < 0 || pattern <= columns) throw Error('Expected the Block Mix table before Pattern.');
                    const removedHeight = rows[pattern].y - rows[columns].y;
                    for (const row of rows.slice(pattern)) move(row, 0, -removedHeight);
                    body.shapes = [...rows.slice(0, columns), ...rows.slice(pattern)].map(s => s.id);
                    bounds(body, body.x, body.y, body.width, body.height - removedHeight);
                    bounds(card, card.x, card.y, card.width, card.height - removedHeight);
                    const accent = child(card, 'Accent strip');
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    for (const sibling of shapes.filter(s => s.type === 'frame' && s !== card)) move(sibling, 0, -removedHeight);
                }
                board.shapes.push(...shapes.map(s => s.id));
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(scatterCompactMixStep){
        const reference = Object.values(pages).find(p => p.name === '66 • Step 58 — Scatter block fields on the shared grid').objects;
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const names = ['English Oak', 'Silver Birch', 'Field Maple', 'Hornbeam', 'Lime', 'Wild Cherry', 'Scots Pine', 'Rowan', 'Alder', 'Beech'];
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 58', 'Step 59');
            const originals = childrenOf(reference[id], reference).filter(s => s.name === 'Canopy');
            const cards = childrenOf(board).filter(s => s.name === 'Canopy');
            const captions = childrenOf(board).filter(s => s.type === 'text');
            for (const [column, card] of cards.entries()) {
                const count = column === 0 ? 2 : 10;
                const totalWeight = count === 2 ? 4 : 20;
                setText(captions[column], count === 2 ? 'TWO BLOCKS' : 'TEN BLOCKS');
                const summary = child(child(card, 'Header'), 'Scatter · 2 blocks · 240 trees');
                setText(summary, `Scatter · ${count} blocks · 240 trees`);
                const body = child(card, 'Parameters');
                const rows = childrenOf(body);
                const originalBody = child(originals[column], 'Parameters', reference);
                const template = child(originalBody, 'English Oak block', reference);
                const mix = child(body, 'Block Mix heading');
                const blockStart = rows.indexOf(mix) + 1;
                const patternIndex = rows.findIndex(s => s.name === 'Choice property row');
                const firstAfter = rows[patternIndex];
                const left = template.x, right = left + template.width;
                const weightX = right - 103, shareX = right - 49;
                let cursor = mix.y + mix.height;
                const headings = newFrame('Block Mix columns', left, cursor, template.width, 20, body.id);
                headings.fills = [];
                for (const [title, x, width] of [['Block', left + 20, weightX - left - 25], ['Weight', weightX, 47], ['Mix', shareX, 31]]) {
                    const text = caption(title, x, cursor, width, headings.id);
                    bounds(text, x, cursor, width, 20);
                    headings.shapes.push(text.id);
                }
                cursor += headings.height;
                const entries = [headings.id];
                for (const [index, name] of names.slice(0, count).entries()) {
                    const weight = index === 0 ? 3 : index === 1 ? 1 : 2;
                    const block = cloneTree(template.id, body.id, 0, cursor - template.y);
                    block.name = name + ' block';
                    const title = child(block, 'English Oak');
                    const share = child(block, '75% of mix');
                    const value = child(block, '3.00');
                    const input = child(block, 'Input');
                    const remove = child(block, 'Remove English Oak');
                    block.shapes = block.shapes.filter(id => objects[id].name !== 'Weight');
                    setText(title, name);
                    setText(share, `${100 * weight / totalWeight}%`);
                    setText(value, weight.toFixed(2));
                    remove.name = 'Remove ' + name;
                    bounds(title, left + 20, cursor + 5, weightX - left - 25, 18);
                    bounds(input, weightX, cursor + 1, 47, 26);
                    bounds(value, weightX + 5, cursor + 5, 37, 18);
                    bounds(share, shareX, cursor + 4, 31, 22);
                    bounds(block, left, cursor, block.width, 28);
                    entries.push(block.id);
                    cursor += block.height;
                }
                const shift = cursor - firstAfter.y;
                const oldBottom = card.y + card.height;
                for (const row of rows.slice(patternIndex)) move(row, 0, shift);
                body.shapes = [...body.shapes.slice(0, blockStart), ...entries, ...body.shapes.slice(patternIndex)];
                bounds(body, body.x, body.y, body.width, body.height + shift);
                bounds(card, card.x, card.y, card.width, card.height + shift);
                const accent = child(card, 'Accent strip');
                bounds(accent, accent.x, accent.y, accent.width, card.height);
                for (const sibling of childrenOf(board).filter(s => s.type === 'frame' && s.x === card.x && s.y >= oldBottom)) {
                    move(sibling, 0, shift);
                }
            }
            const bottom = Math.max(...childrenOf(board).filter(s => s.type === 'frame').map(card => card.y + card.height));
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            if (cards[0].width === 224) move(board, 0, 1100 - board.y);
            objects[zero].shapes.push(board.id);
        }
    }
    if(scatterBlockFieldsStep){
        const reference = Object.values(pages).find(p => p.name === '65 • Step 57 — Scatter control sizes').objects;
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 57', 'Step 58');
            const originals = childrenOf(reference[id], reference).filter(s => s.name === 'Canopy');
            const cards = childrenOf(board).filter(s => s.name === 'Canopy');
            let bottom = board.y;
            for (const [column, card] of cards.entries()) {
                const originalBody = child(originals[column], 'Parameters', reference);
                const originalPattern = childrenOf(originalBody, reference).find(s => s.name === 'Choice property row');
                const labelTemplate = child(originalPattern, 'Pattern', reference);
                const body = child(card, 'Parameters');
                const pattern = childrenOf(body).find(s => s.name === 'Choice property row');
                const editor = child(pattern, 'Editor');
                const mix = child(body, 'Block Mix heading');
                const title = child(mix, 'BLOCK MIX · 2 BLOCKS');
                const add = child(mix, 'Add button');
                const addLabel = child(mix, 'Add');
                setText(title, 'Block Mix');
                bounds(title, title.x, title.y, editor.x - title.x - 4, title.height);
                bounds(add, editor.x, add.y, editor.width, editor.height);
                bounds(addLabel, editor.x + 2, addLabel.y, editor.width - 4, addLabel.height);
                const rows = childrenOf(body).filter(s => s.name !== 'Weight');
                const blockRows = rows.filter(s => s.name.endsWith(' block'));
                let cursor = mix.y + mix.height;
                for (const block of blockRows) {
                    move(block, 0, cursor - block.y);
                    const input = child(block, 'Input');
                    const value = childrenOf(block).find(s => s.type === 'text' &&
                        s.x >= input.x && s.x + s.width <= input.x + input.width && s.y >= input.y && s.y < input.y + input.height);
                    const share = childrenOf(block).find(s => s.name.endsWith('% of mix'));
                    const label = cloneTree(labelTemplate.id, block.id, block.x - labelTemplate.x, block.y + 29 - labelTemplate.y);
                    setText(label, 'Weight');
                    bounds(label, block.x, label.y, editor.x - block.x - 2, label.height);
                    bounds(input, editor.x, block.y + 25, editor.width, editor.height);
                    bounds(value, editor.x + 5, input.y + 4, editor.width - 10, value.height);
                    const right = block.x + block.width;
                    const name = child(block, block.name.slice(0, -6));
                    bounds(name, name.x, name.y, right - 91 - name.x, name.height);
                    bounds(share, right - 86, block.y + 4, 69, share.height);
                    bounds(block, block.x, block.y, block.width, 54);
                    block.shapes.push(label.id);
                    cursor += block.height;
                }
                const oldBottom = card.y + card.height;
                const shift = cursor - pattern.y;
                for (const row of rows.slice(rows.indexOf(pattern))) move(row, 0, shift);
                body.shapes = rows.map(s => s.id);
                bounds(body, body.x, body.y, body.width, body.height + shift);
                bounds(card, card.x, card.y, card.width, card.height + shift);
                const accent = child(card, 'Accent strip');
                bounds(accent, accent.x, accent.y, accent.width, card.height);
                for (const sibling of childrenOf(board).filter(s => s.type === 'frame' && s.x === card.x && s.y >= oldBottom)) {
                    move(sibling, 0, shift);
                }
            }
            for (const card of childrenOf(board).filter(s => s.type === 'frame')) bottom = Math.max(bottom, card.y + card.height);
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(scatterControlSizesStep){
        const reference = Object.values(pages).find(p => p.name === '64 • Step 56 — Scatter where what and how').objects;
        const childrenOf = shape => shape.shapes.map(id => objects[id]);
        const child = (shape, name) => childrenOf(shape).find(s => s.name === name);
        function compactCaption(content) {
            if (Object.hasOwn(content, 'font-size')) {
                content['font-size'] = '11';
                delete content['typography-ref-id'];
                delete content['typography-ref-file'];
            }
            for (const child of content.children ?? []) compactCaption(child);
        }
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 56', 'Step 57');
            for (const card of childrenOf(board).filter(s => s.name === 'Canopy')) {
                const body = child(card, 'Parameters');
                for (const row of childrenOf(body).filter(s => s.name === 'Sources property row')) {
                    const editor = child(row, 'Editor');
                    for (const button of childrenOf(editor)) {
                        const surface = child(button, button.name + ' button');
                        bounds(button, button.x, button.y, button.width, editor.height);
                        bounds(surface, surface.x, surface.y, surface.width, editor.height);
                    }
                }
                const mix = child(body, 'Block Mix heading');
                const weight = child(child(body, 'English Oak block'), 'Input');
                const add = child(mix, 'Browse button');
                const addLabel = child(mix, 'Add');
                move(addLabel, weight.x - add.x, 0);
                bounds(add, weight.x, add.y, weight.width, weight.height);
                add.name = 'Add button';
                const title = childrenOf(mix).find(s => s.type === 'text' && s !== addLabel);
                bounds(title, title.x, title.y, add.x - title.x - 4, title.height);
                const density = child(body, 'Density Mode mode selector');
                const segments = childrenOf(density).filter(s => s.type === 'frame');
                const start = segments[0].x;
                const span = density.x + density.width - start;
                for (const [index, segment] of segments.entries()) {
                    const x = start + Math.floor(span * index / segments.length);
                    const end = start + Math.floor(span * (index + 1) / segments.length);
                    const delta = end - x - segment.width;
                    move(segment, x - segment.x, 0);
                    bounds(segment, x, segment.y, end - x, segment.height);
                    for (const part of childrenOf(segment)) {
                        bounds(part, part.x, part.y, part.width + delta, part.height);
                        if (part.type === 'text' && card.width === 224) compactCaption(part.content);
                    }
                }
                centreButtonLabels(card);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(scatterOrderStep){
        const reference = Object.values(pages).find(p => p.name === '63 • Step 55 — Scatter systems and exclusions').objects;
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 55', 'Step 56');
            for (const card of board.shapes.map(id => objects[id]).filter(s => s.name === 'Canopy')) {
                const body = card.shapes.map(id => objects[id]).find(s => s.name === 'Parameters');
                const rows = body.shapes.map(id => objects[id]);
                const pattern = rows.findIndex(s => s.name === 'Choice property row' && s.shapes.some(id => objects[id].name === 'Pattern'));
                const mix = rows.findIndex(s => s.name === 'Block Mix heading');
                const lastBlock = rows.findLastIndex(s => s.name.endsWith(' block'));
                if (pattern < 0 || mix <= pattern || lastBlock <= mix) throw Error('Expected Scatter pattern and Block Mix.');
                const distribution = rows.slice(pattern, mix);
                const blocks = rows.slice(mix, lastBlock + 1);
                const distributionHeight = blocks[0].y - distribution[0].y;
                const mixEnd = blocks.at(-1).y + blocks.at(-1).height;
                const mixHeight = mixEnd - blocks[0].y;
                for (const row of blocks) move(row, 0, -distributionHeight);
                for (const row of distribution) move(row, 0, mixHeight);
                body.shapes = [...rows.slice(0, pattern), ...blocks, ...distribution, ...rows.slice(lastBlock + 1)]
                    .map(row => row.id);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(scatterLayoutStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const displace = pageObjects('59 • Step 51 — Displace noise and image');
        const childrenOf = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const firstText = (shape, index = objects) => childrenOf(shape, index).find(s => s.type === 'text');
        const locate = (index, name) => Object.values(index).find(s => s.name === name);
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const scatter = child(locate(atlas, `Step 37 / Objects Atlas / ${theme}`), 'Scatter', atlas);
            const source = child(scatter, 'Parameters', atlas);
            const scatterIcon = child(child(scatter, 'Header', atlas), 'ObjScatter', atlas);
            const modes = child(source, 'Source mode selector', atlas);
            const densities = child(source, 'Density Mode mode selector', atlas);
            const pattern = child(source, 'Choice property row', atlas);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const sourceBoard = locate(displace, `Step 51 / Displace / ${theme} / ${width}`);
                const shell = child(sourceBoard, 'Displace / Noise', displace);
                const shellBody = child(shell, 'Parameters', displace);
                const picker = child(shellBody, 'Sources property row', displace);
                const number = child(shellBody, 'Number property row', displace);
                const check = child(shellBody, 'Bool property row', displace);
                const slider = child(shellBody, 'Slider property row', displace);
                const sliderEditor = child(slider, 'Editor', displace);
                const checkEditor = child(check, 'Editor', displace);
                const imageCard = child(sourceBoard, 'Displace / Manual Image', displace);
                const fileEditor = child(child(child(imageCard, 'Parameters', displace), 'FilePath property row', displace), 'Editor', displace);
                const x = themeIndex * 920, y = widthIndex * 930;
                const board = newFrame(`Step 55 / Scatter / ${theme} / ${width}`, x, y, 2 * width + 48, 880);
                board.fills = structuredClone(sourceBoard.fills);
                board['applied-tokens'] = structuredClone(sourceBoard['applied-tokens']);
                let bottom = y;
                for (const [column, enabled] of [false, true].entries()) {
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(enabled ? 'EXCLUSIONS ON' : 'EXCLUSIONS OFF', cardX, y + 14, width, board.id).id);
                    function scatterCard(name, summary, top) {
                        const card = cloneTree(shell.id, board.id, cardX - shell.x, top - shell.y);
                        card.name = name;
                        const header = child(card, 'Header');
                        setText(child(header, 'Displace'), width === 224 && name === 'Groundcover' ? 'Ground…' : name);
                        setText(child(header, 'Displace · Noise'), summary);
                        const badge = child(header, 'ModDisplace');
                        badge.name = scatterIcon.name;
                        badge.fills = structuredClone(scatterIcon.fills);
                        const accent = child(card, 'Accent strip');
                        accent.fills = structuredClone(child(scatter, 'Accent strip', atlas).fills);
                        board.shapes.push(card.id);
                        return card;
                    }
                    const card = scatterCard('Canopy', 'Scatter · 2 blocks · 240 trees', cardY);
                    const body = child(card, 'Parameters');
                    body.shapes = [];
                    const left = body.x + 8, rowWidth = width - 21, right = left + rowWidth;
                    const editorInset = child(picker, 'Editor', displace).x - picker.x;
                    let cursor = body.y + 6;
                    function row(template, label) {
                        const result = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                        if (label) setText(firstText(result), label);
                        return result;
                    }
                    function add(result, height = result.height) {
                        bounds(result, left, cursor, rowWidth, height);
                        body.shapes.push(result.id);
                        cursor += height;
                    }
                    function toggle(label, checked) {
                        const result = row(check, label);
                        child(child(result, 'Editor'), 'Checked').hidden = !checked;
                        add(result);
                    }
                    function disclosure(label) {
                        const title = caption('▸ ' + label, left, cursor + 4, rowWidth, body.id);
                        body.shapes.push(title.id);
                        cursor += 26;
                    }
                    function segments(template, options, label) {
                        const result = row(template, label);
                        const stacked = label && width === 224;
                        const text = label ? firstText(result) : null;
                        if (text) bounds(text, left, text.y, stacked ? rowWidth : editorInset - 2, text.height);
                        result.shapes = text ? [text.id] : [];
                        const start = label && !stacked ? left + editorInset : left;
                        const templates = childrenOf(template, atlas).filter(s => s.type === 'frame');
                        const selected = templates.find(s => s.name.endsWith('(selected)'));
                        const idle = templates.find(s => s !== selected);
                        const divisions = stacked ? [0, 0.28, 0.60, 1] :
                            Array.from({length: options.length + 1}, (_, index) => index / options.length);
                        for (const [index, option] of options.entries()) {
                            const style = index === 0 ? selected : idle;
                            const x = start + Math.floor((right - start) * divisions[index]);
                            const next = start + Math.floor((right - start) * divisions[index + 1]);
                            const y = cursor + (stacked ? 22 : 3);
                            const segment = cloneTree(style.id, result.id, x - style.x, y - style.y);
                            segment.name = option + (index === 0 ? ' (selected)' : '');
                            bounds(segment, x, y, next - x, segment.height);
                            for (const part of childrenOf(segment)) {
                                bounds(part, part.x, part.y, part.width + segment.width - style.width, part.height);
                                if (part.type === 'text') setText(part, option);
                            }
                            result.shapes.push(segment.id);
                        }
                        add(result, stacked ? 52 : 32);
                    }
                    segments(modes, ['Within Region', 'Along Curve']);
                    add(row(picker, 'Boundaries'));
                    toggle('Use Terrain Zones', false);
                    toggle('Exclusions', enabled);
                    if (enabled) {
                        add(row(picker, 'Exclude Curves'));
                        const clearance = row(number, 'Clearance');
                        setText(firstText(child(clearance, 'Editor')), '0.500');
                        add(clearance);
                    }

                    const patternRow = row(pattern, 'Pattern');
                    const patternEditor = child(patternRow, 'Editor');
                    const delta = left + editorInset - patternEditor.x;
                    const patternWidth = right - left - editorInset;
                    const change = patternWidth - patternEditor.width;
                    move(patternEditor, delta, 0);
                    bounds(patternEditor, patternEditor.x, patternEditor.y, patternWidth, patternEditor.height);
                    for (const part of childrenOf(patternEditor)) {
                        if (part.type === 'path') move(part, change, 0);
                        else bounds(part, part.x, part.y, part.width + change, part.height);
                    }
                    setText(firstText(patternEditor), 'Random');
                    add(patternRow);
                    segments(densities, ['Count', 'Per Area', 'Min Spacing'], 'Density Mode');
                    const count = row(number, 'Total Count');
                    const countEditor = child(count, 'Editor');
                    setText(firstText(countEditor), '240');
                    countEditor.shapes = countEditor.shapes.filter(id => objects[id].name !== 'm');
                    add(count);

                    const blockHeader = newFrame('Block Mix heading', left, cursor, rowWidth, 28, body.id);
                    blockHeader.fills = [];
                    blockHeader.shapes.push(caption('BLOCK MIX · 2 BLOCKS', left, cursor + 3, rowWidth - 56, blockHeader.id).id);
                    for (const name of ['Browse button', 'Browse']) {
                        const template = child(fileEditor, name, displace);
                        const part = cloneTree(template.id, blockHeader.id, right - 52 + (template.type === 'text' ? 4 : 0) - template.x,
                            cursor + (template.type === 'text' ? 5 : 2) - template.y);
                        if (part.type === 'text') setText(part, 'Add');
                        blockHeader.shapes.push(part.id);
                    }
                    add(blockHeader);
                    body.shapes.push(caption('Weight', right - 71, cursor, 52, body.id).id);
                    cursor += 16;
                    for (const [name, weight, share] of [['English Oak', '3.00', '75% of mix'], ['Silver Birch', '1.00', '25% of mix']]) {
                        const block = newFrame(name + ' block', left, cursor, rowWidth, 42, body.id);
                        block.fills = [];
                        const included = cloneTree(checkEditor.id, block.id, left - checkEditor.x, cursor + 2 - checkEditor.y);
                        const nameShape = cloneTree(firstText(picker, displace).id, block.id);
                        bounds(nameShape, left + 20, cursor + 5, rowWidth - 93, 18);
                        setText(nameShape, name);
                        const mix = caption(share, left + 20, cursor + 21, rowWidth - 93, block.id);
                        block.shapes.push(included.id, nameShape.id, mix.id);
                        for (const template of [child(sliderEditor, 'Input', displace), firstText(sliderEditor, displace)]) {
                            const part = cloneTree(template.id, block.id);
                            const isText = part.type === 'text';
                            bounds(part, right - 71 + (isText ? 5 : 0), cursor + (isText ? 6 : 2), part.width, part.height);
                            if (isText) setText(part, weight);
                            block.shapes.push(part.id);
                        }
                        const remove = caption('×', right - 13, cursor + 5, 12, block.id);
                        remove.name = 'Remove ' + name;
                        block.shapes.push(remove.id);
                        add(block);
                    }
                    disclosure('Edge Settings');
                    disclosure('Placement Filters');
                    disclosure('Transforms');
                    disclosure('Preview');
                    const end = cursor + 6;
                    bounds(body, body.x, body.y, body.width, end - body.y);
                    bounds(card, card.x, card.y, width, end - card.y);
                    const accent = child(card, 'Accent strip');
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    let next = end + 8;
                    for (const [name, summary] of [['Understory', 'Scatter · 3 blocks · 860 shrubs'], ['Groundcover', 'Scatter · 4 blocks · 2,400 plants']]) {
                        const sibling = scatterCard(name, summary, next);
                        const strip = child(sibling, 'Accent strip');
                        sibling.shapes = [strip.id, child(sibling, 'Header').id];
                        bounds(sibling, sibling.x, sibling.y, width, 52);
                        bounds(strip, strip.x, strip.y, strip.width, 52);
                        next += 60;
                    }
                    bottom = Math.max(bottom, next - 8);
                }
                bounds(board, x, y, board.width, bottom - y + 16);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(simplifyModesStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const reference = pageObjects('56 • Step 48 — Remesh mode first');
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const firstText = shape => childrenOf(shape).find(s => s.type === 'text');
        const modes = [
            {name: 'Deviation', label: 'Max Deviation', value: '0.050', unit: 'm', summary: 'Max Deviation 0.05 m'},
            {name: 'Vertices', label: 'Target Vertices', value: '50,000', unit: '', summary: 'At most 50,000 vertices'},
            {name: 'Keep %', label: 'Retain', value: '50.00', unit: '%', summary: 'Retain 50%'}
        ];
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 48 / Remesh', 'Step 54 / Simplify');
            const [, , theme] = board.name.split(' / ');
            const atlasBoard = Object.values(atlas).find(s => s.name === `Step 37 / Modifiers Atlas / ${theme}`);
            const original = child(atlasBoard, 'Simplify', atlas);
            const originalIcon = child(child(original, 'Header', atlas), 'ModSimplify', atlas);
            const accentStyle = child(original, 'Accent strip', atlas);
            const titles = childrenOf(board).filter(s => s.type === 'text');
            const cards = childrenOf(board).filter(s => s.type === 'frame');
            for (const [column, card] of cards.entries()) {
                const mode = modes[column];
                setText(titles[column], mode.name.toUpperCase());
                card.name = 'Simplify / ' + mode.name;
                const header = child(card, 'Header');
                setText(child(header, 'Remesh'), 'Simplify');
                setText(child(header, 'Remesh · 2 objects · 0 layers'), 'Simplify · ' + mode.summary);
                const icon = child(header, 'ModRemesh');
                icon.name = originalIcon.name;
                icon.fills = structuredClone(originalIcon.fills);
                const accent = child(card, 'Accent strip');
                accent.fills = structuredClone(accentStyle.fills);
                const body = child(card, 'Parameters');
                const selector = child(body, 'Mode mode selector');
                for (const [index, segment] of childrenOf(selector).entries()) {
                    const option = modes[index].name;
                    segment.name = option + (index === column ? ' (selected)' : '');
                    const start = selector.x + Math.floor(selector.width * index / modes.length);
                    const end = selector.x + Math.floor(selector.width * (index + 1) / modes.length);
                    const delta = end - start - segment.width;
                    move(segment, start - segment.x, 0);
                    bounds(segment, start, segment.y, end - start, segment.height);
                    for (const part of childrenOf(segment)) {
                        bounds(part, part.x, part.y, part.width + delta, part.height);
                        if (part.type === 'text') setText(part, option);
                    }
                }
                const target = child(body, 'Number property row');
                move(target, 0, selector.y + selector.height - target.y);
                setText(firstText(target), mode.label);
                const editor = child(target, 'Editor');
                setText(firstText(editor), mode.value);
                setText(child(editor, 'm'), mode.unit);
                body.shapes = [selector.id, target.id];
                const end = target.y + target.height + 6;
                bounds(body, body.x, body.y, body.width, end - body.y);
                bounds(card, card.x, card.y, card.width, end - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }
            const bottom = Math.max(...cards.map(card => card.y + card.height));
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(displaceOriginStep){
        const reference = Object.values(pages).find(p => p.name === '60 • Step 52 — Displace image placement and coverage').objects;
        const picker = Object.values(pages).find(p => p.name === '46 • Step 38 — Report Table contents and appearance').objects;
        const childrenOf = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 52', 'Step 53');
            setText(child(board, 'TYPED SIZE AND POSITION'), 'SIZE AND ORIGIN');
            const [, , theme, width] = board.name.split(' / ');
            const pickerBoard = Object.values(picker).find(s => s.name === `Step 38 / Report Table / ${theme} / ${width}`);
            let bottom = board.y;
            for (const card of childrenOf(board).filter(s => s.type === 'frame')) {
                const body = child(card, 'Parameters');
                const rows = childrenOf(body);
                const index = rows.findIndex(row => child(row, 'Origin X'));
                if (index !== -1) {
                    const picked = card.name === 'Displace / Typed size and position';
                    if (picked) {
                        card.name = 'Displace / Size and origin';
                        setText(child(body, 'Enter the size and position below.'), 'Set the size and origin below.');
                    }
                    const example = child(pickerBoard, picked ? 'Report Table / Appearance open' : 'Report Table / Default', picker);
                    const source = child(child(example, 'Parameters', picker), 'Insertion point row', picker);
                    const originX = rows[index];
                    if (!child(rows[index + 1], 'Origin Y')) throw Error('Expected the paired origin rows.');
                    const origin = cloneTree(source.id, body.id, originX.x - source.x, originX.y - source.y);
                    origin.name = 'Origin point picker — world XY';
                    setText(child(origin, 'Insertion Point'), 'Origin');
                    const button = childrenOf(origin).find(s => s.name.startsWith(picked ? 'Picked point' : 'Pick point'));
                    const shift = child(originX, 'Editor').x - button.x;
                    const right = button.x + button.width;
                    bounds(button, button.x + shift, button.y, right - button.x - shift, button.height);
                    for (const part of childrenOf(button)) bounds(part, part.x + shift, part.y, part.width - shift, part.height);
                    button.name = picked ? 'Picked origin — click to replace' : 'Pick origin — defaults to world 0,0';
                    const clear = childrenOf(origin).find(s => s.name.startsWith('Clear picked insertion'));
                    if (clear) clear.name = 'Clear origin — use world 0,0';
                    rows.splice(index, 2, origin);
                    let cursor = origin.y + origin.height;
                    for (const row of rows.slice(index + 1)) {
                        move(row, 0, cursor - row.y);
                        cursor += row.height;
                    }
                    body.shapes = rows.map(row => row.id);
                    const end = cursor + 6;
                    bounds(body, body.x, body.y, body.width, end - body.y);
                    bounds(card, card.x, card.y, card.width, end - card.y);
                    const accent = child(card, 'Accent strip');
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                }
                bottom = Math.max(bottom, card.y + card.height);
            }
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(displacePlacementStep){
        const reference = Object.values(pages).find(p => p.name === '59 • Step 51 — Displace noise and image').objects;
        const childrenOf = shape => shape.shapes.map(id => objects[id]);
        const firstText = shape => childrenOf(shape).find(s => s.type === 'text');
        const labels = new Map([
            ['MANUAL IMAGE', 'TYPED SIZE AND POSITION'],
            ['MAPPED IMAGE', 'PICKED RECTANGLE']
        ]);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 51', 'Step 52');
            for (const title of childrenOf(board).filter(s => labels.has(s.name))) setText(title, labels.get(title.name));
            let bottom = board.y;
            for (const card of childrenOf(board).filter(s => s.type === 'frame')) {
                if (card.name === 'Displace / Noise') {
                    bottom = Math.max(bottom, card.y + card.height);
                    continue;
                }
                const mapped = card.name === 'Displace / Mapped Image';
                card.name = mapped ? 'Displace / Picked rectangle' : 'Displace / Typed size and position';
                const body = childrenOf(card).find(s => s.name === 'Parameters');
                const rows = childrenOf(body);
                const rectangle = rows.find(row => firstText(row)?.name === (mapped ? 'Mapping · 1' : 'Mapping'));
                setText(firstText(rectangle), mapped ? 'Rectangle · 1' : 'Rectangle');
                const rename = new Map([
                    ['Tile Width', 'Image Width'],
                    ['Tile Height', 'Image Height'],
                    ['Wrap', 'Outside Image'],
                    ['Show Mapping', 'Show Image Guides']
                ]);
                for (const row of rows) {
                    const label = firstText(row);
                    if (label && rename.has(label.name)) setText(label, rename.get(label.name));
                }
                const wrap = rows.find(s => s.name === 'Wrap mode selector');
                wrap.name = 'Outside Image mode selector';
                const options = childrenOf(wrap).filter(s => s.type === 'frame');
                for (const [index, label] of ['Repeat', 'Extend', 'No Effect'].entries()) {
                    setText(firstText(options[index]), label);
                    options[index].name = label + (index === 0 ? ' (selected)' : '');
                }
                const rectangleIndex = rows.indexOf(rectangle);
                const heading = caption('IMAGE PLACEMENT', rectangle.x, rectangle.y, rectangle.width, body.id);
                const explanation = caption(mapped ? 'Uses the selected rectangle.' : 'Enter the size and position below.',
                    rectangle.x, rectangle.y, rectangle.width, body.id);
                rows.splice(rectangleIndex, 1, heading, rectangle, explanation);
                let cursor = rows[0].y;
                for (const row of rows) {
                    move(row, 0, cursor - row.y);
                    cursor += row.height;
                }
                body.shapes = rows.map(s => s.id);
                const end = cursor + 6;
                bounds(body, body.x, body.y, body.width, end - body.y);
                bounds(card, card.x, card.y, card.width, end - card.y);
                const accent = childrenOf(card).find(s => s.name === 'Accent strip');
                bounds(accent, accent.x, accent.y, accent.width, card.height);
                bottom = Math.max(bottom, end);
            }
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(displaceModesStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const smooth = pageObjects('58 • Step 50 — Smooth boundaries before settings');
        const remesh = pageObjects('56 • Step 48 — Remesh mode first');
        const gradePath = pageObjects('53 • Step 45 — Units inside aligned fields');
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const firstText = (shape, index = objects) => childrenOf(shape, index).find(s => s.type === 'text');
        const locate = (index, name) => Object.values(index).find(s => s.name === name);
        const states = ['Noise', 'Manual Image', 'Mapped Image'];
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const atlasCard = child(locate(atlas, `Step 37 / Modifiers Atlas / ${theme}`), 'Displace', atlas);
            const atlasBody = child(atlasCard, 'Parameters', atlas);
            const icon = child(child(atlasCard, 'Header', atlas), 'ModDisplace', atlas);
            const sourceMode = child(atlasBody, 'Source mode selector', atlas);
            const wrapMode = child(atlasBody, 'Wrap mode selector', atlas);
            const imageRow = child(atlasBody, 'FilePath property row', atlas);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const shell = child(locate(smooth, `Step 50 / Smooth / ${theme} / ${width}`), 'Smooth / Smooth', smooth);
                const shellBody = child(shell, 'Parameters', smooth);
                const sourcePicker = child(shellBody, 'Sources property row', smooth);
                const scrub = child(shellBody, 'Slider property row', smooth);
                const remeshCard = child(locate(remesh, `Step 48 / Remesh / ${theme} / ${width}`), 'Remesh / Even Triangles', remesh);
                const number = child(child(remeshCard, 'Parameters', remesh), 'Number property row', remesh);
                const pathCard = child(locate(gradePath, `Step 45 / Grade Path inline units / ${theme} / ${width}`), 'Grade Path / VARIABLE WIDTH', gradePath);
                const check = child(child(pathCard, 'Parameters', gradePath), 'Bool property row', gradePath);
                const inset = child(sourcePicker, 'Editor', smooth).x - sourcePicker.x;
                const x = themeIndex * 1320, y = widthIndex * 730;
                const board = newFrame(`Step 51 / Displace / ${theme} / ${width}`, x, y, 3 * width + 64, 680);
                const shellBoard = smooth[shell['parent-id']];
                board.fills = structuredClone(shellBoard.fills);
                board['applied-tokens'] = structuredClone(shellBoard['applied-tokens']);
                let bottom = y;
                for (const [column, state] of states.entries()) {
                    const image = column > 0, mapped = column === 2;
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(state.toUpperCase(), cardX, y + 14, width, board.id).id);
                    const card = cloneTree(shell.id, board.id, cardX - shell.x, cardY - shell.y);
                    card.name = 'Displace / ' + state;
                    board.shapes.push(card.id);
                    const header = child(card, 'Header');
                    setText(child(header, 'Smooth'), 'Displace');
                    setText(child(header, 'Smooth · 2 objects · 0 layers'), image ? 'Displace · Image' : 'Displace · Noise');
                    const badge = child(header, 'ModSmooth');
                    badge.name = icon.name;
                    badge.fills = structuredClone(icon.fills);
                    const accent = child(card, 'Accent strip');
                    accent.fills = structuredClone(child(atlasCard, 'Accent strip', atlas).fills);
                    const body = child(card, 'Parameters');
                    body.shapes = [];
                    const left = body.x + 8, rowWidth = width - 21, right = left + rowWidth;
                    let cursor = body.y + 6;
                    function row(template, label) {
                        const result = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                        if (label) setText(firstText(result), label);
                        return result;
                    }
                    function add(result, height = result.height) {
                        bounds(result, left, cursor, rowWidth, height);
                        body.shapes.push(result.id);
                        cursor += height;
                    }
                    function numeric(label, value, unit = 'm') {
                        const field = row(number, label);
                        const editor = child(field, 'Editor');
                        setText(child(editor, '0.000'), value);
                        setText(child(editor, 'm'), unit);
                        add(field);
                    }
                    function slider(label, value, fraction) {
                        const field = row(scrub, label);
                        const editor = child(field, 'Editor');
                        const track = child(editor, 'Slider track');
                        const thumb = child(editor, 'Slider thumb');
                        move(thumb, track.x + (track.width - thumb.width) * fraction - thumb.x, 0);
                        setText(firstText(editor), value);
                        add(field);
                    }
                    function segmented(template, options, selectedIndex, label) {
                        const field = row(template, label);
                        const stacked = label && width === 224;
                        const labelShape = label ? firstText(field) : null;
                        field.shapes = labelShape ? [labelShape.id] : [];
                        if (labelShape) bounds(labelShape, left, labelShape.y, stacked ? rowWidth : inset - 2, labelShape.height);
                        const start = label && !stacked ? left + inset : left;
                        const available = right - start;
                        const native = childrenOf(template, atlas).filter(s => s.type === 'frame');
                        const selected = native.find(s => s.name.endsWith('(selected)'));
                        const idle = native.find(s => s !== selected);
                        for (const [index, option] of options.entries()) {
                            const style = index === selectedIndex ? selected : idle;
                            const x = start + Math.floor(available * index / options.length);
                            const next = start + Math.floor(available * (index + 1) / options.length);
                            const y = cursor + (stacked ? 22 : 3);
                            const segment = cloneTree(style.id, field.id, x - style.x, y - style.y);
                            segment.name = option + (index === selectedIndex ? ' (selected)' : '');
                            bounds(segment, x, y, next - x, segment.height);
                            for (const part of childrenOf(segment)) {
                                bounds(part, part.x, part.y, part.width + segment.width - style.width, part.height);
                                if (part.type === 'text') setText(part, option);
                            }
                            field.shapes.push(segment.id);
                        }
                        add(field, stacked ? 52 : 32);
                    }
                    segmented(sourceMode, ['Noise', 'Image'], image ? 1 : 0);
                    add(row(sourcePicker, 'Boundaries'));
                    numeric('Height', '0.500');
                    add(row(sourcePicker, 'Protect'));
                    numeric('Feather', '2.000');
                    add(row(check, 'Hold Border'));
                    if (image) {
                        const file = row(imageRow, 'Image');
                        const editor = child(file, 'Editor');
                        const input = child(editor, 'Input');
                        const name = childrenOf(editor).find(s => s.type === 'text' && s.name !== 'Browse');
                        const start = left + inset, browse = right - 52;
                        bounds(editor, start, editor.y, right - start, editor.height);
                        bounds(input, start, input.y, browse - start - 4, input.height);
                        bounds(name, start + 5, name.y, input.width - 9, name.height);
                        setText(name, 'tile.png');
                        const button = child(editor, 'Browse button'), caption = child(editor, 'Browse');
                        bounds(button, browse, button.y, 52, button.height);
                        bounds(caption, browse + 4, caption.y, 44, caption.height);
                        add(file);
                        add(row(sourcePicker, mapped ? 'Mapping · 1' : 'Mapping'));
                        if (!mapped) {
                            numeric('Tile Width', '20.000');
                            numeric('Tile Height', '20.000');
                        }
                    } else {
                        numeric('Noise Size', '10.000');
                        slider('Octaves', '4', 3 / 7);
                        slider('Roughness', '0.50', 0.5);
                        numeric('Seed', '1', '');
                    }
                    if (!mapped) {
                        numeric('Rotation', '0.00', '°');
                        numeric('Origin X', '0.000');
                        numeric('Origin Y', '0.000');
                    }
                    if (image) {
                        slider('Neutral', '0.50', 0.5);
                        segmented(wrapMode, ['Repeat', 'Clamp', 'Once'], 0, 'Wrap');
                        add(row(check, 'Show Mapping'));
                    }
                    const end = cursor + 6;
                    bounds(body, body.x, body.y, body.width, end - body.y);
                    bounds(card, card.x, card.y, card.width, end - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    bottom = Math.max(bottom, end);
                }
                bounds(board, x, y, board.width, bottom - y + 16);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(smoothBoundariesStep){
        const reference = Object.values(pages).find(p => p.name === '57 • Step 49 — Smooth strength first').objects;
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 49', 'Step 50');
            for (const card of board.shapes.map(id => objects[id]).filter(s => s.name.startsWith('Smooth / '))) {
                const body = card.shapes.map(id => objects[id]).find(s => s.name === 'Parameters');
                const rows = body.shapes.map(id => objects[id]);
                const index = rows.findIndex(row => row.shapes.some(id => objects[id].name === 'Boundaries'));
                if (index < 1 || rows[0].name !== 'Mode mode selector') throw Error('Expected a mode selector and Boundaries.');
                const [boundaries] = rows.splice(index, 1);
                rows.splice(1, 0, boundaries);
                let cursor = rows[0].y;
                for (const row of rows) {
                    move(row, 0, cursor - row.y);
                    cursor += row.height;
                }
                body.shapes = rows.map(row => row.id);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(smoothStrengthStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const remesh = pageObjects('56 • Step 48 — Remesh mode first');
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const firstText = (shape, index = objects) => childrenOf(shape, index).find(s => s.type === 'text');
        const modes = ['Smooth', 'Round Creases'];
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const atlasBoard = Object.values(atlas).find(s => s.name === `Step 37 / Modifiers Atlas / ${theme}`);
            const original = child(atlasBoard, 'Smooth', atlas);
            const icon = child(child(original, 'Header', atlas), 'ModSmooth', atlas);
            const atlasRows = childrenOf(child(original, 'Parameters', atlas), atlas);
            const selector = atlasRows.find(s => s.name === 'Mode mode selector');
            const selected = child(selector, 'Smooth (selected)', atlas);
            const unselected = child(selector, 'Round Creases', atlas);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const sourceBoard = Object.values(remesh).find(s => s.name === `Step 48 / Remesh / ${theme} / ${width}`);
                const shell = child(sourceBoard, 'Remesh / Even Triangles', remesh);
                const shellBody = child(shell, 'Parameters', remesh);
                const picker = child(shellBody, 'Sources property row', remesh);
                const numeric = child(shellBody, 'Number property row', remesh);
                const x = themeIndex * 960, y = widthIndex * 430;
                const board = newFrame(`Step 49 / Smooth / ${theme} / ${width}`, x, y, 2 * width + 48, 390);
                board.fills = structuredClone(sourceBoard.fills);
                board['applied-tokens'] = structuredClone(sourceBoard['applied-tokens']);
                let bottom = y;
                for (const [column, mode] of modes.entries()) {
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(mode.toUpperCase(), cardX, y + 14, width, board.id).id);
                    const card = cloneTree(shell.id, board.id, cardX - shell.x, cardY - shell.y);
                    card.name = 'Smooth / ' + mode;
                    board.shapes.push(card.id);
                    const header = child(card, 'Header');
                    setText(child(header, 'Remesh'), 'Smooth');
                    setText(child(header, 'Remesh · 2 objects · 0 layers'), 'Smooth · 2 objects · 0 layers');
                    const badge = child(header, 'ModRemesh');
                    badge.name = icon.name;
                    badge.fills = structuredClone(icon.fills);
                    const accent = child(card, 'Accent strip');
                    accent.fills = structuredClone(child(original, 'Accent strip', atlas).fills);
                    const body = child(card, 'Parameters');
                    body.shapes = [];
                    const left = body.x + 8, rowWidth = width - 21;
                    let cursor = body.y + 6;
                    function add(row) {
                        bounds(row, left, cursor, rowWidth, row.height);
                        body.shapes.push(row.id);
                        cursor += row.height;
                    }
                    function cloneRow(template, label) {
                        const row = cloneTree(template.id, body.id, left - template.x, cursor - template.y);
                        setText(firstText(row), label);
                        return row;
                    }
                    function addSlider(originalLabel, label, fraction) {
                        const template = atlasRows.find(s => s.name === 'Slider property row' && firstText(s, atlas).name === originalLabel);
                        const row = cloneRow(template, label);
                        const templateEditor = child(picker, 'Editor', remesh);
                        const editor = child(row, 'Editor');
                        const title = firstText(row);
                        const editorInset = label === 'Height Protection' ? 104 : 0;
                        const editorX = left + Math.max(templateEditor.x - picker.x, editorInset);
                        const right = left + rowWidth;
                        bounds(title, title.x, title.y, editorX - left - 2, title.height);
                        bounds(editor, editorX, editor.y, right - editorX, editor.height);
                        const track = child(editor, 'Slider track');
                        const thumb = child(editor, 'Slider thumb');
                        const input = child(editor, 'Input');
                        const value = firstText(editor);
                        const trackWidth = editor.width - input.width - 6;
                        bounds(track, editorX, track.y, trackWidth, track.height);
                        bounds(thumb, editorX + (trackWidth - thumb.width) * fraction, thumb.y, thumb.width, thumb.height);
                        bounds(input, right - input.width, input.y, input.width, input.height);
                        bounds(value, input.x + 5, value.y, value.width, value.height);
                        add(row);
                    }
                    const switcher = cloneTree(selector.id, body.id, left - selector.x, cursor - selector.y);
                    switcher.shapes = [];
                    for (const [index, option] of modes.entries()) {
                        const template = index === column ? selected : unselected;
                        const start = left + Math.floor(rowWidth * index / 2);
                        const end = left + Math.floor(rowWidth * (index + 1) / 2);
                        const segment = cloneTree(template.id, switcher.id, start - template.x, cursor + 3 - template.y);
                        segment.name = option + (index === column ? ' (selected)' : '');
                        bounds(segment, start, segment.y, end - start, segment.height);
                        for (const part of childrenOf(segment)) {
                            bounds(part, part.x, part.y, part.width + segment.width - template.width, part.height);
                            if (part.type === 'text') setText(part, option);
                        }
                        switcher.shapes.push(segment.id);
                    }
                    add(switcher);
                    if (mode === 'Smooth') {
                        addSlider('Strength', 'Strength', 0.2);
                        addSlider('Iterations', 'Iterations', 1 / 12);
                    } else {
                        for (const [label, value, unit] of [['Rounding Length', '2.000', 'm'], ['Crease Angle', '10.0', '°']]) {
                            const row = cloneRow(numeric, label);
                            const editor = child(row, 'Editor');
                            setText(child(editor, '0.000'), value);
                            setText(child(editor, 'm'), unit);
                            add(row);
                        }
                    }
                    add(cloneRow(picker, 'Boundaries'));
                    add(cloneRow(picker, 'Protect'));
                    if (mode === 'Smooth') addSlider('Protect Hold', 'Height Protection', 1);
                    const end = cursor + 6;
                    bounds(body, body.x, body.y, body.width, end - body.y);
                    bounds(card, card.x, card.y, card.width, end - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);

                    const previous = cloneTree(shell.id, board.id, cardX - shell.x, end + 8 - shell.y);
                    previous.name = 'Remesh / Collapsed';
                    previous.shapes = [child(previous, 'Accent strip').id, child(previous, 'Header').id];
                    bounds(previous, previous.x, previous.y, width, 52);
                    const previousAccent = child(previous, 'Accent strip');
                    bounds(previousAccent, previousAccent.x, previousAccent.y, previousAccent.width, previous.height);
                    board.shapes.push(previous.id);
                    bottom = Math.max(bottom, previous.y + previous.height);
                }
                bounds(board, x, y, board.width, bottom - y + 16);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(remeshModeFirstStep){
        const reference = Object.values(pages).find(p => p.name === '55 • Step 47 — Remesh modes').objects;
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 47', 'Step 48');
            for (const card of board.shapes.map(id => objects[id]).filter(s => s.name.startsWith('Remesh / '))) {
                const body = card.shapes.map(id => objects[id]).find(s => s.name === 'Parameters');
                const [sources, mode, ...rest] = body.shapes.map(id => objects[id]);
                if (sources.name !== 'Sources property row' || mode.name !== 'Mode mode selector') {
                    throw Error('Expected Breaklines and Remesh mode at the top of the card.');
                }
                move(mode, 0, sources.y - mode.y);
                move(sources, 0, mode.height);
                body.shapes = [mode, sources, ...rest].map(row => row.id);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(remeshModesStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const fields = pageObjects('53 • Step 45 — Units inside aligned fields');
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const firstText = shape => childrenOf(shape).find(s => s.type === 'text');
        const modes = ['Even Triangles', 'Full Rebuild', 'Local Refine'];
        function compactCaption(content) {
            if (Object.hasOwn(content, 'font-size')) {
                content['font-size'] = '11';
                delete content['typography-ref-id'];
                delete content['typography-ref-file'];
            }
            for (const child of content.children ?? []) compactCaption(child);
        }
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const atlasBoard = Object.values(atlas).find(s => s.name === `Step 37 / Modifiers Atlas / ${theme}`);
            const original = child(atlasBoard, 'Remesh', atlas);
            const icon = child(child(original, 'Header', atlas), 'ModRemesh', atlas);
            const modeRow = child(child(original, 'Parameters', atlas), 'Mode mode selector', atlas);
            const selectedMode = child(modeRow, 'Isotropic (selected)', atlas);
            const plainMode = child(modeRow, 'Full Rebuild', atlas);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const sourceBoard = Object.values(fields).find(s => s.name === `Step 45 / Grade Path inline units / ${theme} / ${width}`);
                const shell = child(sourceBoard, 'Grade Path / CONSTANT WIDTH', fields);
                const sourceBody = child(shell, 'Parameters', fields);
                const sourcePicker = child(sourceBody, 'Sources property row', fields);
                const numeric = child(sourceBody, 'Number property row', fields);
                const x = themeIndex * 1340, y = widthIndex * 400;
                const board = newFrame(`Step 47 / Remesh / ${theme} / ${width}`, x, y, 3 * width + 64, 340);
                board.fills = structuredClone(sourceBoard.fills);
                board['applied-tokens'] = structuredClone(sourceBoard['applied-tokens']);
                let bottom = y;
                for (const [column, mode] of modes.entries()) {
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(mode.toUpperCase(), cardX, y + 14, width, board.id).id);
                    const card = cloneTree(shell.id, board.id, cardX - shell.x, cardY - shell.y);
                    card.name = 'Remesh / ' + mode;
                    board.shapes.push(card.id);
                    const header = child(card, 'Header');
                    setText(child(header, 'Grade Path'), 'Remesh');
                    setText(child(header, 'Grade Path · 2 objects · 0 layers'), 'Remesh · 2 objects · 0 layers');
                    const badge = child(header, 'ModGradePath');
                    badge.name = icon.name;
                    badge.fills = structuredClone(icon.fills);
                    const accent = child(card, 'Accent strip');
                    accent.fills = structuredClone(child(original, 'Accent strip', atlas).fills);
                    const body = child(card, 'Parameters');
                    const left = body.x + 8, rowWidth = width - 21;
                    body.shapes = [];
                    let cursor = body.y + 6;
                    function add(row) {
                        bounds(row, left, cursor, rowWidth, row.height);
                        body.shapes.push(row.id);
                        cursor += row.height;
                    }
                    const picker = cloneTree(sourcePicker.id, body.id, left - sourcePicker.x, cursor - sourcePicker.y);
                    setText(firstText(picker), 'Breaklines');
                    add(picker);
                    const selector = cloneTree(modeRow.id, body.id, left - modeRow.x, cursor - modeRow.y);
                    selector.shapes = [];
                    const splits = width === 224 ? [0, 77, 139, rowWidth] : [0, 126, 252, rowWidth];
                    for (const [index, option] of modes.entries()) {
                        const template = index === column ? selectedMode : plainMode;
                        const start = left + splits[index];
                        const size = splits[index + 1] - splits[index];
                        const segment = cloneTree(template.id, selector.id, start - template.x, cursor + 3 - template.y);
                        segment.name = option + (index === column ? ' (selected)' : '');
                        bounds(segment, start, segment.y, size, segment.height);
                        for (const part of childrenOf(segment)) {
                            bounds(part, part.x, part.y, part.width + size - template.width, part.height);
                            if (part.type === 'text') {
                                setText(part, option);
                                if (width === 224) compactCaption(part.content);
                            }
                        }
                        selector.shapes.push(segment.id);
                    }
                    add(selector);
                    const parameters = [['Edge Length', '0.000', 'm'], ['Crease Angle', '30.0', '°']];
                    if (mode === 'Full Rebuild') parameters.push(['Min Angle', '0.0', '°'], ['Max Area', '0.000', 'm²']);
                    for (const [label, value, unit] of parameters) {
                        const row = cloneTree(numeric.id, body.id, left - numeric.x, cursor - numeric.y);
                        setText(firstText(row), label);
                        const editor = child(row, 'Editor');
                        setText(child(editor, '2.000'), value);
                        const suffix = child(editor, 'Unit');
                        setText(suffix, unit);
                        if (unit === 'm²') {
                            const field = child(editor, value);
                            bounds(suffix, suffix.x - 5, suffix.y, suffix.width + 5, suffix.height);
                            bounds(field, field.x, field.y, field.width - 5, field.height);
                        }
                        add(row);
                    }
                    const end = cursor + 6;
                    bounds(body, body.x, body.y, body.width, end - body.y);
                    bounds(card, card.x, card.y, card.width, end - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    bottom = Math.max(bottom, end);
                }
                bounds(board, x, y, board.width, bottom - y + 16);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(numericDefaultsStep){
        const reference = Object.values(pages).find(p => p.name === '53 • Step 45 — Units inside aligned fields').objects;
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const path = 'Approved / Numeric fields';
        for (const [column, theme] of ['dark', 'light'].entries()) {
            const width = theme === 'dark' ? 400 : 224;
            const template = Object.values(reference).find(s => s.name === `Step 45 / Grade Path inline units / ${theme} / ${width}`);
            const original = child(template, 'Grade Path / CONSTANT WIDTH', reference);
            const body = child(original, 'Parameters', reference);
            const x = column * 460;
            const board = newFrame(`Approved numeric fields / ${theme}`, x, 0, width + 24, 158);
            board.fills = structuredClone(template.fills);
            board['applied-tokens'] = structuredClone(template['applied-tokens']);
            board.shapes.push(caption(theme === 'dark' ? 'APPROVED FIELD COMPONENTS' : 'COMPACT FIELD EXAMPLES',
                x + 16, 14, board.width - 32, board.id).id);
            const fields = [
                ['Length', 'Number property row', 'Value with unit and stepper'],
                ['Slope', 'Slope property row', 'Value with unit']
            ];
            for (const [index, [label, type, name]] of fields.entries()) {
                const originalEditor = child(child(body, type, reference), 'Editor', reference);
                const y = 48 + index * 38;
                const editor = cloneTree(originalEditor.id, board.id, x + board.width - 16 - originalEditor.width - originalEditor.x, y - originalEditor.y);
                editor.name = name;
                for (const key of Object.keys(editor)) if (key.startsWith('layout')) delete editor[key];
                for (const part of childrenOf(editor)) {
                    part['constraints-h'] = ['Stepper separator', '▴', '▾', 'Unit', 'V:H'].includes(part.name) ? 'right' : 'leftright';
                    part['constraints-v'] = 'top';
                }
                if (theme === 'dark') {
                    const id = randomUUID();
                    Object.assign(editor, {'component-id': id, 'component-file': draft.fileId, 'component-root': true, 'main-instance': true});
                    componentChanges.push(map({
                        type: kw('add-component'), id: uuid(id), name, path,
                        'main-instance-id': uuid(editor.id), 'main-instance-page': uuid(pageId)
                    }));
                }
                board.shapes.push(caption(label, x + 16, y + 4, 85, board.id).id, editor.id);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(inlineUnitsStep){
        const reference = Object.values(pages).find(p => p.name === '52 • Step 44 — Grade Path variable width').objects;
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        for (const id of reference[zero].shapes) {
            const originalCard = child(reference[id], 'Grade Path / CONSTANT WIDTH', reference);
            const originalWidth = child(child(originalCard, 'Parameters', reference), 'Number property row', reference);
            const unitTemplate = child(child(originalWidth, 'Editor', reference), 'Unit', reference);
            const board = cloneTree(id);
            board.name = board.name.replace('Step 44 / Grade Path', 'Step 45 / Grade Path inline units');
            for (const card of childrenOf(board).filter(s => s.type === 'frame')) {
                const rows = childrenOf(child(card, 'Parameters'));
                const fields = rows.filter(row => child(row, 'Editor') && !child(child(row, 'Editor'), 'Checkbox'));
                const editorInset = card.width === 224 ? 90 : 107;
                for (const row of fields) {
                    const editor = child(row, 'Editor');
                    const start = row.x + editorInset;
                    const end = row.x + row.width;
                    bounds(editor, start, editor.y, end - start, editor.height);
                    if (row.name === 'Sources property row') {
                        for (const [index, button] of childrenOf(editor).entries()) {
                            const width = (editor.width - 4) / 2;
                            const x = start + index * (width + 4);
                            const delta = width - button.width;
                            move(button, x - button.x, 0);
                            bounds(button, x, button.y, width, button.height);
                            for (const part of childrenOf(button)) bounds(part, part.x, part.y, part.width + delta, part.height);
                        }
                        continue;
                    }

                    const input = child(editor, 'Input');
                    const separator = child(editor, 'Stepper separator');
                    const parts = childrenOf(editor);
                    const unit = child(editor, 'Unit') ?? cloneTree(unitTemplate.id, editor.id);
                    const value = parts.find(part => part.type === 'text' && !['▴', '▾', 'Unit'].includes(part.name));
                    const inputEnd = input.x + input.width;
                    bounds(input, start, input.y, end - start, input.height);
                    if (separator) {
                        for (const part of parts.filter(s => ['Stepper separator', '▴', '▾'].includes(s.name))) {
                            move(part, end - inputEnd, 0);
                        }
                    } else {
                        setText(unit, 'V:H');
                        editor.shapes.push(unit.id);
                    }
                    const unitWidth = separator ? 11 : 23;
                    const unitEnd = (separator?.x ?? end) - 5;
                    bounds(unit, unitEnd - unitWidth, input.y + 4, unitWidth, 18);
                    bounds(value, start + 5, value.y, unit.x - start - 8, value.height);
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(gradePathStep){
        const reference = Object.values(pages).find(p => p.name === '51 • Step 43 — Grade Line left and right slopes').objects;
        const pathIcon = Object.values(allObjects).find(s => s.name === 'ModGradePath' && s.fills?.some(f => f['fill-image']));
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const label = shape => childrenOf(shape).find(s => s.type === 'text');
        for (const id of reference[zero].shapes) {
            const template = reference[id];
            const original = child(template, 'Grade Line / Shared slopes', reference);
            const board = newFrame(template.name.replace('Step 43 / Grade Line', 'Step 44 / Grade Path'),
                template.x, template.y, template.width, template.height);
            board.fills = structuredClone(template.fills);
            board['applied-tokens'] = structuredClone(template['applied-tokens']);
            let bottom = board.y;
            for (const [column, heading] of ['CONSTANT WIDTH', 'VARIABLE WIDTH'].entries()) {
                const variable = column === 1;
                const x = original.x + column * (original.width + 16);
                const card = cloneTree(original.id, board.id, x - original.x, 0);
                card.name = 'Grade Path / ' + heading;
                board.shapes.push(caption(heading, x, board.y + 14, card.width, board.id).id, card.id);
                const header = child(card, 'Header');
                setText(child(header, 'Grade Line'), 'Grade Path');
                setText(child(header, 'Grade Line · 2 objects · 0 layers'), 'Grade Path · 2 objects · 0 layers');
                const icon = child(header, 'ModGradeLine');
                icon.name = 'ModGradePath';
                icon.fills = structuredClone(pathIcon.fills);
                const accent = child(card, 'Accent strip');
                accent.fills = [{ 'fill-color': '#5d4037', 'fill-opacity': 1 }];
                const body = child(card, 'Parameters');
                const originals = childrenOf(child(original, 'Parameters', reference), reference);
                const rows = childrenOf(body);
                const centre = rows.shift();
                const variableToggle = rows.pop();
                setText(label(centre), 'Centerlines');
                setText(label(variableToggle), 'Variable Width');
                child(child(variableToggle, 'Editor'), 'Checked').hidden = !variable;

                function field(template, name, value) {
                    const row = cloneTree(template.id, body.id, centre.x - template.x);
                    setText(label(row), name);
                    if (value) {
                        const editor = child(row, 'Editor');
                        const text = childrenOf(editor).find(s => s.type === 'text' && s.name === '0.000');
                        setText(text, value);
                    }
                    return row;
                }
                const distance = originals.find(s => s.name === 'Number property row');
                const source = originals.find(s => s.name === 'Sources property row');
                const width = field(distance, 'Width', '2.000');
                const widthRows = [centre, width, variableToggle];
                if (variable) {
                    const edges = field(source, 'Width Edges');
                    const match = field(distance, 'Edge Match Distance', 'Auto');
                    match.name = 'Edge Match Distance — Auto uses four times Width';
                    const title = label(match);
                    const editor = child(match, 'Editor');
                    if (card.width === 224) {
                        bounds(title, title.x, title.y, match.width, title.height);
                        move(editor, 0, 20);
                        bounds(match, match.x, match.y, match.width, match.height + 20);
                    } else {
                        bounds(title, title.x, title.y, 105, title.height);
                        const input = child(editor, 'Input');
                        const auto = child(editor, 'Auto');
                        bounds(editor, editor.x + 17, editor.y, editor.width - 17, editor.height);
                        bounds(input, input.x + 17, input.y, input.width - 17, input.height);
                        bounds(auto, auto.x + 17, auto.y, auto.width - 17, auto.height);
                    }
                    widthRows.push(edges, match);
                }
                const arranged = [...widthRows, ...rows];
                let cursor = body.y + 6;
                for (const row of arranged) {
                    move(row, 0, cursor - row.y);
                    cursor += row.height;
                }
                body.shapes = arranged.map(row => row.id);
                const end = cursor + 6;
                bounds(body, body.x, body.y, body.width, end - body.y);
                bounds(card, card.x, card.y, card.width, end - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
                bottom = Math.max(bottom, end);
            }
            bounds(board, board.x, board.y, board.width, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(gradeLineStep){
        const reference = Object.values(pages).find(p => p.name === '50 • Step 42 — Retaining Wall slope overrides inherit when blank').objects;
        const gradeIcon = Object.values(allObjects).find(s => s.name === 'ModGradeLine' && s.fills?.some(f => f['fill-image']));
        const childrenOf = (shape, index = objects) => shape.shapes.map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const rowLabel = row => childrenOf(row).find(s => s.type === 'text');
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 42 / Retaining Wall', 'Step 43 / Grade Line');
            const shared = child(reference[id], 'Retaining Wall / SHARED SLOPES', reference);
            const unchecked = child(child(shared, 'Parameters', reference), 'Bool property row', reference);
            const cards = childrenOf(board).filter(s => s.type === 'frame' && !s.name.endsWith('BREAKLINES ONLY'));
            board.shapes = [];
            let bottom = board.y;
            for (const [column, card] of cards.entries()) {
                const separate = column === 1;
                move(card, -card.width - 16, 0);
                card.name = separate ? 'Grade Line / Separate slopes' : 'Grade Line / Shared slopes';
                const title = separate ? 'SEPARATE LEFT AND RIGHT' : 'SHARED SLOPES';
                board.shapes.push(caption(title, card.x, board.y + 14, card.width, board.id).id, card.id);
                const header = child(card, 'Header');
                setText(child(header, 'Kerbs'), 'Grade Line');
                setText(child(header, 'Retaining Wall · 2 objects · 0 layers'), 'Grade Line · 2 objects · 0 layers');
                const icon = child(header, 'ModRetainingWall');
                icon.name = 'ModGradeLine';
                icon.fills = structuredClone(gradeIcon.fills);
                const accent = child(card, 'Accent strip');
                accent.fills = [{ 'fill-color': '#787878', 'fill-opacity': 1 }];

                const body = child(card, 'Parameters');
                const rows = childrenOf(body).filter(row =>
                    row.name !== 'Mode mode selector' && rowLabel(row)?.name !== 'Max Wall Width');
                setText(rowLabel(rows[0]), 'Design Lines');
                const toggle = rows.find(row => row.name === 'Bool property row');
                const through = cloneTree(unchecked.id, body.id, toggle.x - unchecked.x, toggle.y - unchecked.y);
                setText(rowLabel(through), 'Grade Through Breaklines');
                through.name = 'Grade Through Breaklines row';
                rows.splice(rows.indexOf(toggle), 0, through);
                setText(rowLabel(toggle), 'Separate Left and Right Slopes');
                if (separate) {
                    for (const [from, to] of [['Top', 'Left'], ['Toe', 'Right']]) {
                        const heading = rows.find(s => s.name === from + ' slopes heading');
                        setText(rowLabel(heading), to);
                        heading.name = to + ' slopes — tooltip: look from the curve start toward its end';
                    }
                }
                let cursor = body.y + 6;
                for (const row of rows) {
                    move(row, 0, cursor - row.y);
                    cursor += row.height;
                }
                body.shapes = rows.map(row => row.id);
                const end = cursor + 6;
                bounds(body, body.x, body.y, body.width, end - body.y);
                bounds(card, card.x, card.y, card.width, end - card.y);
                bounds(accent, accent.x, accent.y, accent.width, card.height);
                bottom = Math.max(bottom, end);
            }
            bounds(board, board.x, board.y, cards[0].width * 2 + 48, bottom - board.y + 16);
            objects[zero].shapes.push(board.id);
        }
    }
    if(wallSlopeOverridesStep){
        const reference = Object.values(pages).find(p => p.name === '49 • Step 41 — Retaining Wall top and toe slopes').objects;
        const childrenOf = shape => shape.shapes.map(id => objects[id]);
        for (const id of reference[zero].shapes) {
            const board = cloneTree(id);
            board.name = board.name.replace('Step 41', 'Step 42');
            descendants(board, row => {
                if (row.name !== 'OptionalSlope property row') return;
                const editor = childrenOf(row).find(s => s.name === 'Editor');
                const parts = childrenOf(editor);
                editor.shapes = parts.filter(s => s.name !== 'Checkbox' && s.name !== 'Checked').map(s => s.id);
                for (const part of childrenOf(editor)) {
                    const offset = part.type === 'text' ? 5 : 0;
                    const left = editor.x + offset;
                    bounds(part, left, part.y, part.width + part.x - left, part.height);
                    if (part.type !== 'text' || textOf(part.content) !== 'Inherit') continue;
                    setText(part, '1:1.54');
                    part.name = 'Inherited slope placeholder';
                    function mute(content) {
                        for (const fill of content.fills ?? []) fill['fill-opacity'] *= 0.68;
                        for (const child of content.children ?? []) mute(child);
                    }
                    mute(part.content);
                }
            });
            const note = caption('Blank fields show the inherited slope. Type a value to override it.',
                board.x + 16, board.y + board.height, board.width - 32, board.id);
            board.shapes.push(note.id);
            bounds(board, board.x, board.y, board.width, board.height + 32);
            objects[zero].shapes.push(board.id);
        }
    }
    if(retainingWallStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const atlas = pageObjects('45 • Step 37 — Mode buttons across cards');
        const headers = pageObjects('28 • Step 20 — Header grouping side by side');
        const callout = pageObjects('48 • Step 40 — Grade Callout results and appearance');
        const childrenOf = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const textChild = (shape, index = objects) => childrenOf(shape, index).find(s => s.type === 'text');
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const atlasBoard = Object.values(atlas).find(s => s.name === `Step 37 / Modifiers Atlas / ${theme}`);
            const wall = child(atlasBoard, 'Retaining Wall', atlas);
            const wallIcon = child(child(wall, 'Header', atlas), 'ModRetainingWall', atlas);
            const originalBody = child(wall, 'Parameters', atlas);
            const mode = child(originalBody, 'Mode mode selector', atlas);
            const originalRows = new Map(childrenOf(originalBody, atlas).filter(s => s !== mode)
                .map(s => [textOf(textChild(s, atlas).content), s]));
            const headerBoard = Object.values(headers).find(s => s.name === `Step 20 / header group comparison / ${theme}`);
            const calloutBoard = Object.values(callout).find(s => s.name === `Step 40 / Grade Callout / ${theme} / 224`);
            const details = child(calloutBoard, 'Grade Callout / Details open', callout);
            const group = child(child(details, 'Parameters', callout), 'Results heading', callout);
            for (const [widthIndex, width] of [400, 224].entries()) {
                const headerTemplate = child(headerBoard, `A current expanded ${width === 400 ? 'wide' : 'narrow'} header`, headers);
                const x = themeIndex * 1350, y = widthIndex * 650;
                const board = newFrame(`Step 41 / Retaining Wall / ${theme} / ${width}`, x, y, 3 * width + 64, 520);
                board.fills = structuredClone(atlasBoard.fills);
                board['applied-tokens'] = structuredClone(atlasBoard['applied-tokens']);
                let bottom = y;
                const states = ['BREAKLINES ONLY', 'SHARED SLOPES', 'SEPARATE TOP AND TOE'];
                for (const [column, state] of states.entries()) {
                    const grades = column > 0, separate = column === 2;
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(state, cardX, y + 14, width, board.id).id);
                    const card = cloneTree(headerTemplate.id, board.id, cardX - headerTemplate.x, cardY - headerTemplate.y);
                    card.name = 'Retaining Wall / ' + state;
                    const header = child(card, 'Header');
                    setText(child(header, 'Building Retaining Walls'), 'Kerbs');
                    setText(child(header, 'Retaining Wall'), 'Retaining Wall · 2 objects · 0 layers');
                    const icon = child(header, 'ModGradePad');
                    icon.fills = structuredClone(wallIcon.fills);
                    icon.name = wallIcon.name;
                    const accent = child(card, 'Accent strip');
                    accent.fills = structuredClone(child(wall, 'Accent strip', atlas).fills);
                    delete accent['applied-tokens'];

                    const body = cloneTree(originalBody.id, card.id, cardX + 5 - originalBody.x, cardY + 52 - originalBody.y);
                    body.shapes = [];
                    card.shapes.push(body.id);
                    const rowX = body.x + 8, rowWidth = width - 21, editorX = rowX + 90, editorWidth = rowWidth - 90;
                    let cursor = body.y + 6;
                    function append(row, height) {
                        bounds(row, rowX, cursor, rowWidth, height);
                        body.shapes.push(row.id);
                        cursor += height;
                    }
                    function field(name, label = name, value) {
                        const original = originalRows.get(name);
                        const row = cloneTree(original.id, body.id, rowX - original.x, cursor - original.y);
                        const title = textChild(row), editor = child(row, 'Editor');
                        const isToggle = name === 'Asymmetric Sides';
                        const targetX = isToggle ? rowX + rowWidth - 16 : editorX;
                        const targetWidth = isToggle ? 16 : editorWidth;
                        const change = targetWidth - editor.width;
                        setText(title, label);
                        bounds(title, rowX, title.y, isToggle ? rowWidth - 22 : 88, 18);
                        move(editor, targetX - editor.x, 0);
                        bounds(editor, targetX, editor.y, targetWidth, editor.height);
                        for (const [index, part] of childrenOf(editor).entries()) {
                            if (name === 'Wall Curves') {
                                const buttonWidth = (editorWidth - 4) / 2;
                                const buttonX = editorX + index * (buttonWidth + 4);
                                const delta = buttonWidth - part.width;
                                move(part, buttonX - part.x, 0);
                                bounds(part, buttonX, part.y, buttonWidth, part.height);
                                for (const item of childrenOf(part)) bounds(item, item.x, item.y, item.width + delta, item.height);
                            } else if (part.name === 'Checked') {
                                part.hidden = isToggle ? !separate : value === undefined;
                            } else if (['Unit', 'Stepper separator', '▴', '▾'].includes(part.name)) {
                                move(part, change, 0);
                                if (part.name === 'Unit' && name === 'Max Wall Width') setText(part, 'm');
                            } else if (part.name === 'Input' || part.type === 'text') {
                                bounds(part, part.x, part.y, part.width + change, part.height);
                                if (value !== undefined && part.type === 'text') setText(part, value);
                            }
                        }
                        append(row, 32);
                    }
                    function heading(label) {
                        const row = cloneTree(group.id, body.id, rowX - group.x, cursor - group.y);
                        const text = textChild(row);
                        bounds(text, text.x, text.y, rowWidth - 4, text.height);
                        setText(text, label);
                        row.name = label + ' slopes heading';
                        append(row, 26);
                    }

                    field('Wall Curves');
                    field('Max Wall Width', 'Max Wall Width', '1.000');
                    const selector = cloneTree(mode.id, body.id, rowX - mode.x, cursor - mode.y);
                    const options = ['Breaklines Only', 'Grade Terrain'];
                    for (const [index, segment] of childrenOf(selector).entries()) {
                        const selected = index === Number(grades);
                        const style = child(mode, selected ? 'Breaklines Only (selected)' : 'Grade Terrain', atlas);
                        const rect = childrenOf(segment).find(s => s.type === 'rect');
                        const styleRect = childrenOf(style, atlas).find(s => s.type === 'rect');
                        rect.fills = structuredClone(styleRect.fills);
                        delete rect['applied-tokens'];
                        segment.name = options[index] + (selected ? ' (selected)' : '');
                        const left = rowX + Math.floor(rowWidth * index / 2);
                        const size = rowX + Math.floor(rowWidth * (index + 1) / 2) - left;
                        const delta = size - segment.width;
                        move(segment, left - segment.x, 0);
                        bounds(segment, left, segment.y, size, segment.height);
                        for (const item of childrenOf(segment)) bounds(item, item.x, item.y, item.width + delta, item.height);
                    }
                    append(selector, 32);
                    if (grades) {
                        field('Fill Slope');
                        field('Cut Slope');
                        field('Max Distance');
                        field('Asymmetric Sides', 'Separate Top and Toe Slopes');
                    }
                    if (separate) {
                        heading('Top');
                        field('Top Cut Slope', 'Cut Slope');
                        field('Top Fill Slope', 'Fill Slope');
                        heading('Toe');
                        field('Toe Cut Slope', 'Cut Slope');
                        field('Toe Fill Slope', 'Fill Slope', '1:3');
                    }
                    bottom = Math.max(bottom, cursor + 6);
                    bounds(body, body.x, body.y, width - 5, cursor + 6 - body.y);
                    bounds(card, cardX, cardY, width, cursor + 6 - cardY);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    board.shapes.push(card.id);
                }
                bounds(board, x, y, board.width, bottom + 16 - y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(reportTableStep||legendStep||gradeCalloutStep){
        const pageObjects = name => Object.values(pages).find(p => p.name === name).objects;
        const section = pageObjects('41 • Step 33 — Section sampling follows detail size');
        const insertion = pageObjects('34 • Step 26 — Insertion picked and clear pattern');
        const modes = legendStep ? pageObjects('45 • Step 37 — Mode buttons across cards') : null;
        const childrenOf = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const type = gradeCalloutStep ? 'Grade Callout' : legendStep ? 'Legend' : 'Report Table';
        const iconName = gradeCalloutStep ? 'AnGradeCallout' : legendStep ? 'AnLegend' : 'AnReportTable';
        const headings = gradeCalloutStep ? ['DEFAULT', 'DETAILS OPEN'] :
            legendStep ? ['VERTICAL · AUTO TITLE', 'HORIZONTAL · CUSTOM TITLE'] : ['TERRAIN ONLY', 'ZONES + EARTHWORKS'];
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const reference = [...snapshot.values()].find(info =>
                info.theme === theme && info.node.name === type && info.node.children?.some(s => s.name === 'Parameters')).node;
            const originalIcon = allObjects[reference.children.find(s => s.name === 'Header').children.find(s => s.name === iconName).id];
            const sourceRows = reference.children.find(s => s.name === 'Parameters').children;
            const layout = legendStep ? child(child(child(
                Object.values(modes).find(s => s.name === `Step 37 / Annotations Atlas / ${theme}`), type, modes),
                'Parameters', modes), 'Layout mode selector', modes) : null;
            for (const [widthIndex, width] of [400, 224].entries()) {
                const shell = Object.values(section).find(s => s.name === `Step 33 / Section Sampling / ${theme} / ${width}`);
                const original = child(shell, 'Section Along Curve / Auto', section);
                const originalBody = child(original, 'Parameters', section);
                const group = child(originalBody, 'Section Annotation group', section);
                const pickBoard = Object.values(insertion).find(s => s.name === `Step 26 / Cross-Sections / ${theme} / 224`);
                const pickedCard = childrenOf(pickBoard, insertion).find(s => s.name.endsWith('/ Picked'));
                const pickedRow = child(child(pickedCard, 'Parameters', insertion), 'Insertion point row', insertion);
                const x = themeIndex * 1000, y = widthIndex * 620;
                const board = newFrame(`Step ${stepNumber} / ${type} / ${theme} / ${width}`, x, y, width * 2 + 48, 550);
                board.fills = structuredClone(shell.fills);
                board['applied-tokens'] = structuredClone(shell['applied-tokens']);
                let bottom = y;
                for (const [column, heading] of headings.entries()) {
                    const expanded = column === 1;
                    const cardX = x + 16 + column * (width + 16), cardY = y + 48;
                    board.shapes.push(caption(heading, cardX, y + 14, width, board.id).id);
                    const card = cloneTree(original.id, board.id, cardX - original.x, cardY - original.y);
                    card.name = type + (expanded ? ' / Details open' : ' / Default');
                    const header = child(card, 'Header');
                    const title = width === 224 && !legendStep ? (gradeCalloutStep ? 'Grade…' : 'Report…') : type;
                    setText(child(header, 'Section Along Curve'), title);
                    setText(child(header, 'Section Along Curve type'), gradeCalloutStep ? 'Grade Callout · 3 callouts' : legendStep ? 'Legend · Elevation' :
                        expanded ? 'Report Table · 3 tables' : 'Report Table · 1 table');
                    const icon = child(header, 'AnLongSection');
                    icon.fills = structuredClone(originalIcon.fills);
                    icon.name = iconName;
                    const accent = child(card, 'Accent strip');
                    accent.fills = [{ 'fill-color': reference.children.find(s => s.name === 'Accent strip').fill, 'fill-opacity': 1 }];
                    delete accent['applied-tokens'];

                    const body = child(card, 'Parameters');
                    body.shapes = [];
                    const rowX = body.x + 8, rowWidth = width - 21;
                    const editorX = rowX + 90, editorWidth = rowWidth - 90;
                    let cursor = body.y + 6;
                    function addRow(row, height) {
                        bounds(row, rowX, cursor, rowWidth, height);
                        body.shapes.push(row.id);
                        cursor += height;
                    }
                    function headingRow(label) {
                        const row = cloneTree(group.id, body.id, rowX - group.x, cursor - group.y);
                        const title = childrenOf(row).find(s => s.type === 'text');
                        bounds(title, title.x, title.y, rowWidth - 4, title.height);
                        setText(title, label);
                        row.name = label.replace(/^[▸▾] /, '') + ' heading';
                        addRow(row, 28);
                    }
                    function field(label, captionText = label, value) {
                        const native = sourceRows.find(r => r.children[0].text === label);
                        const info = snapshot.get(native.id);
                        const row = cloneTree(native.id, body.id, rowX - info.x, cursor - info.y);
                        const title = childrenOf(row).find(s => s.type === 'text');
                        const editor = child(row, 'Editor');
                        const check = child(editor, 'Checkbox');
                        const widthChange = editorWidth - editor.width;
                        bounds(title, rowX, cursor + 5, check ? rowWidth - 22 : 88, 18);
                        setText(title, captionText);
                        if (check) {
                            const checkX = rowX + rowWidth - 16;
                            move(editor, checkX - editor.x, 0);
                            bounds(editor, checkX, editor.y, 16, editor.height);
                        } else {
                            move(editor, editorX - editor.x, 0);
                            bounds(editor, editorX, editor.y, editorWidth, editor.height);
                            for (const [index, part] of childrenOf(editor).entries()) {
                                if (row.name === 'Sources property row') {
                                    const buttonWidth = (editorWidth - 4) / 2;
                                    const buttonX = editorX + index * (buttonWidth + 4);
                                    const grow = buttonWidth - part.width;
                                    move(part, buttonX - part.x, 0);
                                    bounds(part, part.x, part.y, buttonWidth, part.height);
                                    for (const item of childrenOf(part)) bounds(item, item.x, item.y, item.width + grow, item.height);
                                    continue;
                                }
                                if (['Hex value', 'Unit'].includes(part.name)) {
                                    editor.shapes = editor.shapes.filter(id => id !== part.id);
                                } else if (['ChevronDown', 'Stepper separator', '▴', '▾'].includes(part.name)) {
                                    move(part, widthChange + (row.name === 'Number property row' ? 18 : 0), 0);
                                } else if (part.name === 'Input' || part.type === 'text' && label !== 'Color') {
                                    bounds(part, part.x, part.y, part.width + widthChange + (row.name === 'Number property row' ? 18 : 0), part.height);
                                    if (part.type === 'text' && value) setText(part, value);
                                }
                            }
                        }
                        addRow(row, check ? 26 : 32);
                        return row;
                    }

                    if (!gradeCalloutStep) {
                        const sourceRow = expanded ? pickedRow : child(originalBody, 'Insertion point row', section);
                        const placement = cloneTree(sourceRow.id, body.id, rowX - sourceRow.x, cursor - sourceRow.y);
                        setText(childrenOf(placement).find(s => s.type === 'text'), 'Insertion Point');
                        const button = childrenOf(placement).find(s => s.name.startsWith(expanded ? 'Picked point' : 'Pick point'));
                        const grow = rowWidth - sourceRow.width;
                        bounds(button, button.x, button.y, button.width + grow, button.height);
                        for (const part of childrenOf(button)) bounds(part, part.x, part.y, part.width + grow, part.height);
                        const clear = childrenOf(placement).find(s => s.name.startsWith('Clear picked insertion'));
                        if (clear) move(clear, grow, 0);
                        addRow(placement, 32);
                    }
                    if (legendStep) {
                        field('Text Size', 'Colouring', 'Elevation').name = 'Active colouring analysis';
                        const title = field('Title', 'Title', expanded ? 'Site Levels' : 'Elevation (m)');
                        if (!expanded) {
                            const placeholder = childrenOf(child(title, 'Editor')).find(s => s.type === 'text');
                            placeholder.name = 'Automatic title placeholder';
                            function mute(content) {
                                if (content.fills) for (const fill of content.fills) fill['fill-opacity'] *= 0.65;
                                for (const child of content.children ?? []) mute(child);
                            }
                            mute(placeholder.content);
                        }
                        const row = cloneTree(layout.id, body.id, rowX - layout.x, cursor - layout.y);
                        const label = child(row, 'Layout');
                        const compact = width === 224;
                        bounds(label, rowX, cursor + 5, compact ? rowWidth : 88, 18);
                        row.shapes = [label.id];
                        const start = compact ? rowX : editorX;
                        const fullWidth = compact ? rowWidth : editorWidth;
                        const top = cursor + (compact ? 24 : 3);
                        for (const [index, option] of ['Vertical', 'Horizontal'].entries()) {
                            const selected = index === column;
                            const template = child(layout, selected ? 'Vertical (selected)' : 'Horizontal', modes);
                            const segmentX = start + Math.floor(fullWidth * index / 2);
                            const segmentWidth = start + Math.floor(fullWidth * (index + 1) / 2) - segmentX;
                            const segment = cloneTree(template.id, row.id, segmentX - template.x, top - template.y);
                            segment.name = option + (selected ? ' (selected)' : '');
                            bounds(segment, segmentX, top, segmentWidth, 26);
                            for (const part of childrenOf(segment)) {
                                bounds(part, part.x, part.y, part.width + segmentWidth - template.width, part.height);
                                if (part.type === 'text') setText(part, option);
                            }
                            row.shapes.push(segment.id);
                        }
                        addRow(row, compact ? 54 : 32);
                    } else if (gradeCalloutStep) {
                        field('Sources', 'Curves');
                        cursor += 6;
                        headingRow(expanded ? '▾ Results' : '▸ Results');
                        if (expanded) {
                            field('Text Size', 'Curves resolved', '4');
                            field('Text Size', 'Minimum Grade', '2.0%');
                            field('Text Size', 'Average Grade', '4.5%');
                            field('Text Size', 'Maximum Grade', '7.0%');
                        }
                    } else {
                        headingRow('Contents');
                        field('Terrain');
                        if (expanded) {
                            field('Zone Schedule');
                            field('Earthworks');
                        }
                    }
                    cursor += 6;
                    headingRow(expanded ? '▾ Appearance' : '▸ Appearance');
                    if (expanded) {
                        if (gradeCalloutStep) {
                            field('Decimals', 'Decimals', '1 decimal place');
                            field('Prefix');
                            field('Suffix');
                        } else if (legendStep) {
                            field('Swatch Size');
                            field('Strip Length');
                        } else {
                            field('Units', 'Slope Units', 'Terrain Default');
                            field('Rules');
                            field('Column Gap');
                            field('Row Spacing');
                        }
                        field('Color');
                        field('Text Size');
                    }
                    bottom = Math.max(bottom, cursor + 6);
                    bounds(body, body.x, body.y, body.width, cursor + 6 - body.y);
                    bounds(card, card.x, card.y, card.width, cursor + 6 - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    board.shapes.push(card.id);
                }
                bounds(board, x, y, board.width, bottom + 16 - y);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(modeDropdownAuditStep){
        const rules = ['Off', 'Report', 'Warn'];
        const curveDensity = ['Count', 'Centre Spacing', 'Edge to Edge'];
        const regionDensity = ['Count', 'Per Area', 'Min Spacing'];
        const mappingModes = ['Linear', 'Stepped', 'Constant'];
        // Entries are in the underlying choice order. A selected value can illustrate
        // dependent fields that are otherwise hidden in the default state.
        const atlas = {
            Modifiers: {
                Displace: [['Source', ['Noise', 'Image'], 'Image'], ['Wrap', ['Repeat', 'Clamp', 'Once'], 'Repeat']],
                Remesh: [['Mode', ['Isotropic', 'Full Rebuild', 'Local Refine'], 'Isotropic']],
                'Retaining Wall': [['Mode', ['Breaklines Only', 'Grade Terrain'], 'Breaklines Only']],
                Simplify: [['Mode', ['Deviation', 'Vertex Count', 'Percentage'], 'Deviation']],
                Smooth: [['Mode', ['Smooth', 'Round Creases'], 'Smooth']]
            },
            Objects: {
                Scatter: [['Source', ['Within Region', 'Along Curve'], 'Along Curve'], ['Density Mode', curveDensity, 'Count'], ['Block order', ['Random', 'Sequence'], 'Random']]
            },
            Analyses: {
                'Gradient Compliance': [['Level Rule', rules, 'Warn'], ['Route Rule', rules, 'Warn']],
                Curvature: [['Show', ['Landforms', 'Curvature'], 'Curvature'], ['Measure', ['Mean', 'Profile', 'Plan'], 'Mean']]
            },
            Annotations: {
                Legend: [['Layout', ['Vertical', 'Horizontal'], 'Vertical']]
            }
        };
        const contour = ['Contour Mode', ['Auto', 'Constrained', 'Vertices Only'], 'Auto'];
        const sampling = ['Sampling', ['Auto', 'Fixed Interval']];
        const inspector = [
            ['Maximum Grade', rules, 'Warn'],
            ['Minimum Plan Radius', rules, 'Warn'],
            ['Vertical Grade Change', rules, 'Report'],
            ['Terrain Coverage', rules, 'Warn']
        ];
        const childrenOf = (shape, index = objects) => (shape.shapes ?? []).map(id => index[id]);
        const child = (shape, name, index = objects) => childrenOf(shape, index).find(s => s.name === name);
        const labelOf = row => childrenOf(row).find(s => s.type === 'text');
        const page = name => Object.values(pages).find(p => p.name === name);
        const spot = page('44 • Step 36 — Spot placement mode buttons').objects;
        const section = page('41 • Step 33 — Section sampling follows detail size').objects;
        const tri = page('12 • Step 04 — Button alignment').objects;
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            const spotBoard = Object.values(spot).find(s => s.name === `Step 36 / Spot Modes / ${theme} / 224`);
            const spotCard = child(spotBoard, 'Spot Heights / Points', spot);
            const prototype = child(child(spotCard, 'Parameters', spot), 'Placement mode selector', spot);
            const on = child(prototype, 'Points mode (selected)', spot);
            const off = child(prototype, 'Curves mode', spot);
            const y = themeIndex * 1900;

            function board(name, x, width, height) {
                const frame = newFrame(`Step 37 / ${name} / ${theme}`, x, y, width, height);
                frame.fills = structuredClone(spotBoard.fills);
                frame['applied-tokens'] = structuredClone(spotBoard['applied-tokens']);
                objects[zero].shapes.push(frame.id);
                return frame;
            }
            function add(parent, shape) {
                parent.shapes.push(shape.id);
            }
            function segments(row, name, options, selected, x, y, width) {
                if (!options.includes(selected)) throw Error('Missing selected mode for ' + name);
                row.name = name + ' mode selector';
                // The compact curve density row gives the longer caption room to stay on one line.
                const divisions = options === curveDensity && width <= 224
                    ? [0, 0.24, 0.65, 1]
                    : Array.from({length: options.length + 1}, (_, i) => i / options.length);
                for (const [index, option] of options.entries()) {
                    const template = option === selected ? on : off;
                    const left = Math.round(width * divisions[index]);
                    const segmentWidth = Math.round(width * divisions[index + 1]) - left;
                    const segmentX = x + left;
                    const segment = cloneTree(template.id, row.id, segmentX - template.x, y - template.y);
                    segment.name = option + (option === selected ? ' (selected)' : '');
                    const surface = childrenOf(segment).find(s => s.type === 'rect');
                    const title = labelOf(segment);
                    bounds(segment, segmentX, y, segmentWidth, 26);
                    bounds(surface, segmentX, y, segmentWidth, 26);
                    bounds(title, segmentX + 2, y + 4, segmentWidth - 4, 18);
                    setText(title, option);
                    add(row, segment);
                }
            }
            function convert(card, specs) {
                const body = child(card, 'Parameters');
                const pending = new Map(specs.map(spec => [spec[0], spec]));
                let delta = 0;
                for (const row of childrenOf(body)) {
                    move(row, 0, delta);
                    const label = labelOf(row);
                    const field = label && textOf(label.content);
                    if (!pending.has(field)) continue;
                    const [name, options, selected] = pending.get(field);
                    pending.delete(field);
                    const primary = name === 'Mode' || name === 'Source';
                    const stacked = !primary && row.width < 340;
                    const editor = childrenOf(row).find(s => s.type === 'frame');
                    const inset = primary || stacked ? 0 : editor.x - row.x;
                    row.shapes = primary ? [] : [label.id];
                    if (stacked) bounds(label, row.x, row.y + 2, row.width, 18);
                    segments(row, name, options, selected, row.x + inset, row.y + (stacked ? 22 : 3), row.width - inset);
                    const height = stacked ? 51 : 32;
                    delta += height - row.height;
                    bounds(row, row.x, row.y, row.width, height);
                }
                if (pending.size) throw Error(card.name + ' is missing mode rows: ' + [...pending.keys()].join(', '));
                bounds(body, body.x, body.y, body.width, body.height + delta);
                bounds(card, card.x, card.y, card.width, card.height + delta);
                const accent = child(card, 'Accent strip');
                bounds(accent, accent.x, accent.y, accent.width, card.height);
            }

            const overview = board('Mode Overview', 0, 736, 550);
            add(overview, caption('MODE BUTTONS AT COMPACT WIDTH', 16, y + 14, 704, overview.id));
            const catalog = Object.values(atlas).flatMap(group =>
                Object.entries(group).flatMap(([name, specs]) => specs.map(([field, ...rest]) => [name + ' / ' + field, ...rest])));
            catalog.splice(6, 0, ['Triangulate / ' + contour[0], ...contour.slice(1)]);
            const densityIndex = catalog.findIndex(([name]) => name === 'Scatter / Density Mode');
            catalog[densityIndex][0] = 'Scatter / Density · Curve';
            catalog.splice(densityIndex + 1, 0, ['Scatter / Density · Region', regionDensity, 'Count']);
            catalog.push(['Section Along Curve / Sampling', sampling[1], 'Auto']);
            catalog.push(['Colour Ramp / Mapping', mappingModes, 'Linear']);
            for (const [index, [name, options, selected]] of catalog.entries()) {
                const x = 16 + Math.floor(index / 6) * 240;
                const rowY = y + 48 + index % 6 * 60;
                const row = newFrame(name, x, rowY, 224, 54, overview.id);
                row.fills = [];
                add(row, caption(name, x, rowY, 224, row.id));
                segments(row, name, options, selected, x, rowY + 22, 224);
                add(overview, row);
            }
            add(overview, caption('CURVE INSPECTOR · RULE LIMITS', 16, y + 418, 704, overview.id));
            for (const [index, [name, options, selected]] of inspector.entries()) {
                const x = 16 + index % 3 * 240;
                const rowY = y + 444 + Math.floor(index / 3) * 54;
                const row = newFrame(name, x, rowY, 224, 50, overview.id);
                row.fills = [];
                add(row, caption(name, x, rowY, 224, row.id));
                segments(row, name, options, selected, x, rowY + 20, 224);
                add(overview, row);
            }

            let boardX = 800;
            for (const [family, cards] of Object.entries(atlas)) {
                const names = Object.keys(cards);
                const columns = Math.min(names.length, 3);
                const frame = board(family + ' Atlas', boardX, columns * 416 + 16, 1400);
                add(frame, caption(family.toUpperCase() + ' · CARD ATLAS', boardX + 16, y + 14, frame.width - 32, frame.id));
                const nextY = Array(columns).fill(y + 48);
                for (const [index, name] of names.entries()) {
                    const info = [...snapshot.values()].find(({node, theme: skin}) =>
                        skin === theme && node.name === name && node.w === 400 && node.children?.some(s => s.name === 'Parameters'));
                    if (!info) throw Error('Missing atlas card: ' + name + ' / ' + theme);
                    const column = index % columns;
                    const card = cloneTree(info.node.id, frame.id, boardX + 16 + column * 416 - info.x, nextY[column] - info.y);
                    convert(card, cards[name]);
                    if (name === 'Curvature') {
                        // The reference predates the live ramp's Linear / Stepped / Constant buttons.
                        const ramp = child(child(card, 'Parameters'), 'ColorRamp property row');
                        const mapping = child(ramp, 'MAPPING');
                        const stale = childrenOf(ramp).filter(s => s.y >= mapping.y + 19 && s.y < mapping.y + 45);
                        ramp.shapes = ramp.shapes.filter(id => !stale.some(s => s.id === id));
                        const row = newFrame('Mapping', mapping.x, mapping.y + 19, mapping.width, 26, ramp.id);
                        row.fills = [];
                        segments(row, 'Mapping', mappingModes, 'Linear', row.x, row.y, row.width);
                        add(ramp, row);
                        setText(child(ramp, 'Continuous'), 'Linear');
                    }
                    add(frame, card);
                    nextY[column] += card.height + 16;
                }
                bounds(frame, frame.x, frame.y, frame.width, Math.max(...nextY) - y);
                boardX += frame.width + 64;
            }

            const sectionBoard = Object.values(section).find(s => s.name === `Step 33 / Section Sampling / ${theme} / 224`);
            const sectionFrame = board('Section Sampling', boardX, 496, 460);
            for (const [index, mode] of sampling[1].entries()) {
                const original = child(sectionBoard, 'Section Along Curve / ' + mode, section);
                const x = boardX + 16 + index * 240;
                add(sectionFrame, caption(mode.toUpperCase(), x, y + 14, 224, sectionFrame.id));
                const card = cloneTree(original.id, sectionFrame.id, x - original.x, y + 48 - original.y);
                convert(card, [[...sampling, mode]]);
                add(sectionFrame, card);
            }
            if (theme === 'dark') {
                const triBoard = Object.values(tri).find(s => s.name === 'Step 04 / dark / 320 / expanded');
                const screen = childrenOf(triBoard, tri).findLast(s => s.name === 'Modifiers / dark / 320');
                const original = child(screen, 'Triangulate', tri);
                const frame = board('Triangulate Settings', boardX + 560, original.width + 32, original.height + 90);
                add(frame, caption('TRIANGULATE · SETTINGS OPEN', frame.x + 16, y + 14, original.width, frame.id));
                const card = cloneTree(original.id, frame.id, frame.x + 16 - original.x, y + 48 - original.y);
                convert(card, [contour]);
                add(frame, card);
                bounds(frame, frame.x, frame.y, frame.width, card.height + 64);
            }
        }
    }
    if(spotModeSelectorStep){
        const reference = Object.values(pages).find(page => page.name === '43 • Step 35 — Grid placement for spot labels');
        const modes = ['Points', 'Curves', 'Grid'];
        const childrenOf = (shape, shapes = objects) => shape.shapes.map(id => shapes[id]);
        const firstText = shape => childrenOf(shape).find(child => child.type === 'text');
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            for (const [widthIndex, width] of [400, 224].entries()) {
                const original = Object.values(reference.objects).find(shape => shape.name === `Step 35 / Spot Grid / ${theme} / ${width}`);
                const x = themeIndex * 1100, y = widthIndex * 870;
                const board = newFrame(`Step 36 / Spot Modes / ${theme} / ${width}`, x, y, width * 2 + 48, 820);
                board.fills = structuredClone(original.fills);
                board['applied-tokens'] = structuredClone(original['applied-tokens']);
                const children = [];
                for (const [column, name] of ['Spot Heights', 'Spot Slope'].entries()) {
                    const cardX = x + 16 + column * (width + 16);
                    children.push(caption(name.toUpperCase(), cardX, y + 16, width, board.id));
                    const sourceCard = childrenOf(original, reference.objects).find(shape => shape.name === `${name} / On a Grid`);
                    const sourceBody = childrenOf(sourceCard, reference.objects).find(shape => shape.name === 'Parameters');
                    const sourceRow = childrenOf(sourceBody, reference.objects).find(shape => shape.name === 'Sources property row');
                    const sourceEditor = childrenOf(sourceRow, reference.objects).find(shape => shape.name === 'Editor');
                    const buttonTemplate = childrenOf(sourceEditor, reference.objects)[0];
                    for (const [rowIndex, mode] of modes.entries()) {
                        const cardY = y + 52 + rowIndex * 246;
                        const card = cloneTree(sourceCard.id, board.id, cardX - sourceCard.x, cardY - sourceCard.y);
                        card.name = `${name} / ${mode}`;
                        const cardParts = childrenOf(card);
                        const body = cardParts.find(shape => shape.name === 'Parameters');
                        const accent = cardParts.find(shape => shape.name === 'Accent strip');
                        const rows = childrenOf(body);
                        const selector = rows.find(shape => shape.name === 'Sampling mode row');
                        selector.name = 'Placement mode selector';
                        selector.shapes = modes.map((option, index) => {
                            const segmentWidth = selector.width / modes.length;
                            const segmentX = selector.x + segmentWidth * index;
                            const segmentY = selector.y + 3;
                            const segment = cloneTree(buttonTemplate.id, selector.id, segmentX - buttonTemplate.x, segmentY - buttonTemplate.y);
                            segment.name = `${option} mode${option === mode ? ' (selected)' : ''}`;
                            bounds(segment, segmentX, segmentY, segmentWidth, 26);
                            const parts = childrenOf(segment);
                            const surface = parts.find(shape => shape.type === 'rect');
                            const title = parts.find(shape => shape.type === 'text');
                            bounds(surface, segmentX, segmentY, segmentWidth, 26);
                            bounds(title, segmentX + 2, segmentY + 4, segmentWidth - 4, 18);
                            setText(title, option);
                            if (option === mode) {
                                surface.fills = [{ 'fill-color': theme === 'dark' ? '#315878' : '#c5def2', 'fill-opacity': 1 }];
                                delete surface['applied-tokens'];
                            }
                            return segment.id;
                        });
                        const sources = rows.find(shape => shape.name === 'Sources property row');
                        const interval = rows.find(shape => shape.name === 'Number property row');
                        const units = rows.find(shape => shape.name === 'Slope units row');
                        const settings = rows.find(shape => shape.name === 'Section Annotation group');
                        if (mode !== 'Grid') setText(firstText(sources), mode);
                        if (mode === 'Curves') {
                            setText(firstText(interval), 'Interval');
                            setText(firstText(childrenOf(interval).find(shape => shape.name === 'Editor')), '10.000');
                        }
                        const placementRows = mode === 'Grid' ? [interval, sources] : mode === 'Curves' ? [sources, interval] : [sources];
                        const ordered = [selector, ...placementRows, ...(units ? [units] : []), settings];
                        let cursor = body.y + 6;
                        for (const property of ordered) {
                            if (property === settings) cursor += 6;
                            move(property, 0, cursor - property.y);
                            cursor += property.height;
                        }
                        body.shapes = ordered.map(property => property.id);
                        bounds(body, body.x, body.y, body.width, cursor + 6 - body.y);
                        bounds(card, card.x, card.y, card.width, cursor + 6 - card.y);
                        bounds(accent, accent.x, accent.y, accent.width, card.height);
                        children.push(card);
                    }
                }
                children.push(caption('POINTS, CURVES AND GRID IN EACH CARD', x + 16, y + 790, board.width - 32, board.id));
                board.shapes = children.map(shape => shape.id);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(spotGridPlacementStep){
        const reference = Object.values(pages).find(page => page.name === '42 • Step 34 — Spot Heights placement options');
        const slopeIcon = Object.values(allObjects).find(shape => shape.name === 'AnPointSlope' && shape.fills?.some(fill => fill['fill-image']));
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            for (const [widthIndex, width] of [400, 224].entries()) {
                const original = Object.values(reference.objects).find(shape => shape.name === `Step 34 / Spot Heights / ${theme} / ${width}`);
                const sourceCard = original.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'B / Along Curves');
                const sourceBody = sourceCard.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Parameters');
                const choiceTemplate = sourceBody.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Sampling mode row');
                const x = themeIndex * 1100, y = widthIndex * 460;
                const board = newFrame(`Step 35 / Spot Grid / ${theme} / ${width}`, x, y, width * 2 + 48, 370);
                board.fills = structuredClone(original.fills);
                board['applied-tokens'] = structuredClone(original['applied-tokens']);
                const children = [];
                for (const [column, name] of ['Spot Heights', 'Spot Slope'].entries()) {
                    const cardX = x + 16 + column * (width + 16), cardY = y + 52;
                    children.push(caption(name.toUpperCase(), cardX, y + 16, width, board.id));
                    const card = cloneTree(sourceCard.id, board.id, cardX - sourceCard.x, cardY - sourceCard.y);
                    card.name = name + ' / On a Grid';
                    const header = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Header');
                    const body = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Parameters');
                    const accent = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Accent strip');
                    if (column === 1) {
                        for (const shape of header.shapes.map(id => objects[id])) {
                            if (shape.type === 'text' && shape.name === 'Spot Heights') setText(shape, 'Spot Slope');
                            if (shape.fills?.some(fill => fill['fill-image'])) {
                                shape.fills = structuredClone(slopeIcon.fills);
                                shape.name = slopeIcon.name;
                            }
                        }
                        accent.fills = [{ 'fill-color': '#0288d1', 'fill-opacity': 1 }];
                    }
                    const rows = body.shapes.map(id => objects[id]);
                    const placement = rows.find(shape => shape.name === 'Sampling mode row');
                    const boundary = rows.find(shape => shape.name === 'Sources property row');
                    const spacing = rows.find(shape => shape.name === 'Number property row');
                    const settings = rows.find(shape => shape.name === 'Section Annotation group');
                    function label(row, text) {
                        setText(row.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), text);
                    }
                    function editorValue(row, text) {
                        const editor = row.shapes.map(id => objects[id]).find(shape => shape.name === 'Editor');
                        setText(editor.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), text);
                    }
                    editorValue(placement, 'On a Grid');
                    label(boundary, 'Boundaries');
                    label(spacing, 'Grid Spacing');
                    editorValue(spacing, '5.000');
                    const ordered = [placement, spacing, boundary];
                    if (column === 1) {
                        const units = cloneTree(choiceTemplate.id, body.id, placement.x - choiceTemplate.x, placement.y - choiceTemplate.y);
                        units.name = 'Slope units row';
                        label(units, 'Units');
                        editorValue(units, 'Percent');
                        ordered.push(units);
                    }
                    ordered.push(settings);
                    let cursor = body.y + 6;
                    for (const property of ordered) {
                        if (property === settings) cursor += 6;
                        move(property, 0, cursor - property.y);
                        cursor += property.height;
                    }
                    body.shapes = ordered.map(property => property.id);
                    bounds(body, body.x, body.y, body.width, cursor + 6 - body.y);
                    bounds(card, card.x, card.y, card.width, cursor + 6 - card.y);
                    bounds(accent, accent.x, accent.y, accent.width, card.height);
                    children.push(card);
                }
                children.push(caption('GRID COVERS THE WHOLE TERRAIN WHEN BOUNDARIES ARE EMPTY', x + 16, y + 330, board.width - 32, board.id));
                board.shapes = children.map(shape => shape.id);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(spotHeightPlacementStep){
        const reference = Object.values(pages).find(page => page.name === '41 • Step 33 — Section sampling follows detail size');
        const pointIcon = Object.values(allObjects).find(shape => shape.name === 'AnProjElevation' && shape.fills?.some(fill => fill['fill-image']));
        const curveIcon = Object.values(allObjects).find(shape => shape.name === 'AnCurveElevation' && shape.fills?.some(fill => fill['fill-image']));
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            for (const [widthIndex, width] of [400, 224].entries()) {
                const original = Object.values(reference.objects).find(shape => shape.name === `Step 33 / Section Sampling / ${theme} / ${width}`);
                const sourceCard = original.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Section Along Curve / Fixed Interval');
                const x = themeIndex * 1100, y = widthIndex * 610;
                const board = newFrame(`Step 34 / Spot Heights / ${theme} / ${width}`, x, y, width * 2 + 48, 530);
                board.fills = structuredClone(original.fills);
                board['applied-tokens'] = structuredClone(original['applied-tokens']);
                const children = [
                    caption('A · SEPARATE CARDS', x + 16, y + 16, width, board.id),
                    caption('B · ONE CARD WITH PLACEMENT', x + width + 32, y + 16, width, board.id)
                ];
                for (const [row, mode] of ['At Points', 'Along Curves'].entries()) {
                    const alongCurves = row === 1;
                    const cardY = y + 72 + row * 208;
                    children.push(caption(mode.toUpperCase(), x + 16, cardY - 22, width, board.id));
                    for (const column of [0, 1]) {
                        const combined = column === 1;
                        const cardX = x + 16 + column * (width + 16);
                        const card = cloneTree(sourceCard.id, board.id, cardX - sourceCard.x, cardY - sourceCard.y);
                        const name = combined ? 'Spot Heights' : `Spot Heights ${mode.toLowerCase()}`;
                        card.name = `${combined ? 'B' : 'A'} / ${mode}`;
                        const header = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Header');
                        const body = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Parameters');
                        for (const shape of header.shapes.map(id => objects[id])) {
                            if (shape.name === 'Section Along Curve') {
                                setText(shape, width === 224 ? 'Spot Hei…' : name);
                                shape.name = name;
                            } else if (shape.name === 'Section Along Curve type') {
                                setText(shape, name);
                            } else if (shape.fills?.some(fill => fill['fill-image'])) {
                                const icon = !combined && alongCurves ? curveIcon : pointIcon;
                                shape.fills = structuredClone(icon.fills);
                                shape.name = icon.name;
                            }
                        }
                        const accent = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Accent strip');
                        accent.fills = [{ 'fill-color': '#1565c0', 'fill-opacity': 1 }];
                        delete accent['applied-tokens'];
                        const sourceRows = body.shapes.map(id => objects[id]);
                        const source = sourceRows.find(shape => shape.name === 'Sources property row');
                        const placement = sourceRows.find(shape => shape.name === 'Sampling mode row');
                        const interval = sourceRows.find(shape => shape.shapes?.some(id => objects[id].name === 'Sample Interval'));
                        const settings = sourceRows.find(shape => shape.name === 'Section Annotation group');
                        setText(source.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), alongCurves ? 'Curves' : 'Points');
                        const placementParts = placement.shapes.map(id => objects[id]);
                        setText(placementParts.find(shape => shape.type === 'text'), 'Placement');
                        const placementEditor = placementParts.find(shape => shape.name === 'Editor');
                        setText(placementEditor.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), mode);
                        setText(interval.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), 'Interval');
                        const intervalEditor = interval.shapes.map(id => objects[id]).find(shape => shape.name === 'Editor');
                        setText(intervalEditor.shapes.map(id => objects[id]).find(shape => shape.name === '1.000'), '10.000');
                        setText(settings.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), '▸ Label Settings');
                        const rows = [...(combined ? [placement] : []), source, ...(alongCurves ? [interval] : []), settings];
                        let cursor = body.y + 6;
                        for (const property of rows) {
                            if (property === settings) cursor += 6;
                            move(property, 0, cursor - property.y);
                            cursor += property.height;
                        }
                        body.shapes = rows.map(property => property.id);
                        bounds(body, body.x, body.y, body.width, cursor + 6 - body.y);
                        bounds(card, card.x, card.y, card.width, cursor + 6 - card.y);
                        bounds(accent, accent.x, accent.y, accent.width, card.height);
                        children.push(card);
                    }
                }
                children.push(caption('BOTH OPTIONS USE THE SAME LABEL SETTINGS', x + 16, y + 500, board.width - 32, board.id));
                board.shapes = children.map(shape => shape.id);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(sectionAutoSamplingStep){
        const reference = Object.values(pages).find(page => page.name === '40 • Step 32 — Simplified Section Along Curve');
        for (const [themeIndex, theme] of ['dark', 'light'].entries()) {
            for (const [widthIndex, width] of [400, 224].entries()) {
                const original = Object.values(reference.objects).find(shape => shape.name === `Step 32 / Section Along Curve / ${theme} / ${width}`);
                const sourceCard = original.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Section Along Curve / Collapsed');
                const sourceBody = sourceCard.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Parameters');
                const choiceTemplate = sourceBody.shapes.map(id => reference.objects[id]).find(shape => shape.name === 'Comparison mode row');
                const x = themeIndex * 1100, y = widthIndex * 510;
                const board = newFrame(`Step 33 / Section Sampling / ${theme} / ${width}`, x, y, width * 2 + 48, 455);
                board.fills = structuredClone(original.fills);
                board['applied-tokens'] = structuredClone(original['applied-tokens']);
                const children = [];
                for (const [column, mode] of ['Auto', 'Fixed Interval'].entries()) {
                    const cardX = x + 16 + column * (width + 16), cardY = y + 52;
                    children.push(caption(mode.toUpperCase(), cardX, y + 16, width, board.id));
                    const card = cloneTree(sourceCard.id, board.id, cardX - sourceCard.x, cardY - sourceCard.y);
                    card.name = 'Section Along Curve / ' + mode;
                    const body = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Parameters');
                    const rows = body.shapes.map(id => objects[id]);
                    const sampleIndex = rows.findIndex(row => row.shapes?.some(id => objects[id].name === 'Sample Interval'));
                    const sample = rows[sampleIndex];
                    const sampling = cloneTree(choiceTemplate.id, body.id, sample.x - choiceTemplate.x, sample.y - choiceTemplate.y);
                    sampling.name = 'Sampling mode row';
                    const parts = sampling.shapes.map(id => objects[id]);
                    setText(parts.find(shape => shape.type === 'text'), 'Sampling');
                    const editor = parts.find(shape => shape.name === 'Editor');
                    setText(editor.shapes.map(id => objects[id]).find(shape => shape.type === 'text'), mode);
                    if (column === 0) {
                        rows[sampleIndex] = sampling;
                    } else {
                        for (const row of rows.slice(sampleIndex)) move(row, 0, sampling.height);
                        rows.splice(sampleIndex, 0, sampling);
                        bounds(body, body.x, body.y, body.width, body.height + sampling.height);
                        bounds(card, card.x, card.y, card.width, card.height + sampling.height);
                        const accent = card.shapes.map(id => objects[id]).find(shape => shape.name === 'Accent strip');
                        bounds(accent, accent.x, accent.y, accent.width, card.height);
                    }
                    body.shapes = rows.map(row => row.id);
                    children.push(card);
                }
                children.push(caption('AUTO FOLLOWS TERRAIN DETAIL SIZE · FIXED REVEALS SAMPLE INTERVAL', x + 16, y + 415, board.width - 32, board.id));
                board.shapes = children.map(shape => shape.id);
                objects[zero].shapes.push(board.id);
            }
        }
    }
    if(sectionAlongCurveStep){
        const reference=Object.values(pages).find(p=>p.name==='39 • Step 31 — Section Line and direct marks');
        const icon=Object.values(allObjects).find(s=>s.name==='AnLongSection'&&s.fills?.some(f=>f['fill-image']));
        for(const [ti,theme]of ['dark','light'].entries())for(const [wi,width]of [400,224].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 31 / Section Cut / '+theme+' / '+width),sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Section Cut / Expanded');
            const nativeBody=sourceCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters'),numericTemplate=nativeBody.shapes.map(i=>reference.objects[i]).find(r=>r.shapes?.some(i=>reference.objects[i].name==='Vertical Scale'));
            const x=ti*1300,y=wi*900,board=newFrame('Step 32 / Section Along Curve / '+theme+' / '+width,x,y,width*2+48,740);board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);const children=[];
            for(const column of [0,1]){
                const cx=x+16+column*(width+16),cy=y+52,expanded=column===1;children.push(caption(expanded?'SECTION ANNOTATION OPEN':'SECTION ANNOTATION COLLAPSED',cx,y+16,width,board.id));
                const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name='Section Along Curve / '+(expanded?'Expanded':'Collapsed');
                const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header'),body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');
                for(const s of header.shapes.map(i=>objects[i])){
                    if(s.name==='Section Cut'){setText(s,width===224?'Section…':'Section Along Curve');s.name='Section Along Curve';}
                    else if(s.name==='Section Cut type'){setText(s,'Section Along Curve');s.name='Section Along Curve type';}
                    else if(s.fills?.some(f=>f['fill-image'])){s.fills=structuredClone(icon.fills);s.name='AnLongSection';}
                }
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');accent.fills=[{'fill-color':'#5e35b1','fill-opacity':1}];delete accent['applied-tokens'];
                const rows=body.shapes.map(i=>objects[i]);
                function labelOf(r){return r.shapes?.map(i=>objects[i]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');}
                function rename(r,label){setText(r.shapes.map(i=>objects[i]).find(s=>s.type==='text'),label);}
                function value(r,text){const e=r.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');setText(e.shapes.map(i=>objects[i]).find(s=>s.type==='text'&&!['Unit','▴','▾'].includes(s.name)),text);}
                function checked(r,on){const e=r.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');for(const s of e.shapes.map(i=>objects[i]))if(s.name==='Checked')s.hidden=!on;}
                const source=rows.find(r=>labelOf(r)==='Section Line');rename(source,'Path');
                const sample=cloneTree(numericTemplate.id,body.id,body.x+8-numericTemplate.x,source.y+source.height-numericTemplate.y);rename(sample,'Sample Interval');value(sample,'1.000');
                for(const r of rows.slice(1))move(r,0,32);rows.splice(1,0,sample);
                const distances=rows.find(r=>labelOf(r)==='Distance Labels');checked(distances,true);
                const baseline=rows.find(r=>labelOf(r)==='Tick Marks');rename(baseline,'Baseline');checked(baseline,true);
                const distanceInterval=rows.find(r=>labelOf(r)==='Tick Interval');rename(distanceInterval,'Distance Interval');value(distanceInterval,'Auto');
                checked(rows.find(r=>labelOf(r)==='Grid Lines'),true);
                // Baseline establishes the drawing axes before their distance annotations.
                const di=rows.indexOf(distances),bi=rows.indexOf(baseline);[rows[di],rows[bi]]=[rows[bi],rows[di]];
                const groupIndex=rows.findIndex(s=>s.name==='Section Annotation group'),group=rows[groupIndex];setText(group.shapes.map(i=>objects[i]).find(s=>s.type==='text'),(expanded?'▾ ':'▸ ')+'Section Annotation');
                if(!expanded)rows.splice(groupIndex+1);
                let cursor=body.y+6;
                for(const [i,r]of rows.entries()){
                    if(i&&['Insertion point row','Comparison mode row','Section Annotation group'].includes(r.name))cursor+=6;
                    move(r,0,cursor-r.y);cursor+=r.height;
                }
                body.shapes=rows.map(s=>s.id);bounds(body,body.x,body.y,body.width,cursor+6-body.y);bounds(card,card.x,card.y,card.width,cursor+6-card.y);bounds(accent,accent.x,accent.y,accent.width,card.height);children.push(card);
            }
            children.push(caption('CURVE SAMPLING AND LABEL INTERVALS REMAIN SEPARATE · AUTOMATIC VERTICAL ARRANGEMENT',x+16,y+700,board.width-32,board.id));board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(sectionLineStep){
        const reference=Object.values(pages).find(p=>p.name==='38 • Step 30 — Simplified Section Cut');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 30','Step 31');
            const expanded=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Section Cut / Expanded'),expandedBody=expanded.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters');
            const marksTemplate=expandedBody.shapes.map(i=>reference.objects[i]).find(r=>r.shapes?.some(i=>reference.objects[i].type==='text'&&textOf(reference.objects[i].content)==='Section Marks'));
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame'&&s.name.startsWith('Section Cut / '))){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters'),rows=body.shapes.map(i=>objects[i]);
                for(const r of rows)for(const s of r.shapes?.map(i=>objects[i])??[])if(s.type==='text'&&textOf(s.content)==='Cut Lines')setText(s,'Section Line');
                const groupIndex=rows.findIndex(s=>s.name==='Plan Annotation group'),group=rows[groupIndex];if(!group)throw Error('Missing Plan Annotation group.');
                let marks=rows.find(r=>r.shapes?.some(i=>objects[i].type==='text'&&textOf(objects[i].content)==='Section Marks'));
                if(!marks)marks=cloneTree(marksTemplate.id,body.id,body.x+8-marksTemplate.x,group.y-marksTemplate.y);
                else move(marks,0,group.y-marks.y);
                const remaining=rows.filter(s=>s.id!==group.id&&s.id!==marks.id),next=remaining.findIndex(s=>s.name==='Section Annotation group');
                remaining.splice(next,0,marks);let cursor=marks.y+marks.height+6;
                for(const r of remaining.slice(next+1)){move(r,0,cursor-r.y);cursor+=r.height;}
                const end=cursor+6;body.shapes=remaining.map(s=>s.id);bounds(body,body.x,body.y,body.width,end-body.y);bounds(card,card.x,card.y,card.width,end-card.y);
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');bounds(accent,accent.x,accent.y,accent.width,card.height);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(sectionCutStep){
        const reference=Object.values(pages).find(p=>p.name==='36 • Step 28 — Plan and section annotation expanded');
        const comparisonReference=Object.values(pages).find(p=>p.name==='37 • Step 29 — Explicit cross-section comparison');
        const scene=JSON.parse(fs.readFileSync(path.resolve(here,'../output/scene.json'),'utf8')),atlas=scene.find(p=>p.name==='04 • Annotation card atlas');
        const cutIcon=Object.values(allObjects).find(s=>s.name==='AnTerrainSection'&&s.fills?.some(f=>f['fill-image']));
        for(const [ti,theme]of ['dark','light'].entries())for(const [wi,width]of [400,224].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 28 / Cross-Sections / '+theme+' / '+(width===400?304:224)),sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Cross-Sections / Annotation groups open');
            const sourceBody=sourceCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters'),sourceRows=sourceBody.shapes.map(i=>reference.objects[i]);
            const sourceLabel=r=>r.shapes?.map(i=>reference.objects[i]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');
            const sourceTemplate=sourceRows.find(s=>s.name==='Sources property row'),numericTemplate=sourceRows.find(s=>sourceLabel(s)==='Vertical Scale'),boolTemplate=sourceRows.find(s=>sourceLabel(s)==='Station Title'),textTemplate=sourceRows.find(s=>sourceLabel(s)==='Text Size'),groupTemplate=sourceRows.find(s=>s.name==='Section Annotation group'),insertTemplate=sourceRows.find(s=>s.name==='Insertion point row'),comparisonTemplate=sourceRows.find(s=>s.name==='Comparison mode row');
            const x=ti*1300,y=wi*1050,board=newFrame('Step 30 / Section Cut / '+theme+' / '+width,x,y,width*2+48,width===400?910:790);board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);const children=[];
            for(const column of [0,1]){
                const cx=x+16+column*(width+16),cy=y+52,expanded=column===1;children.push(caption(width===400&&!column?'CURRENT REFERENCE':expanded?'PROPOSED · EXPANDED':'PROPOSED · COLLAPSED',cx,y+16,width,board.id));
                if(width===400&&!column){
                    const old=atlas.boards.find(b=>b.name==='Section Cut • '+theme).children.find(c=>c.name==='Section Cut'),info=snapshot.get(old.id),before=cloneTree(old.id,board.id,cx-info.x,cy-info.y);children.push(before);continue;
                }
                const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name='Section Cut / '+(expanded?'Expanded':'Collapsed');const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header'),body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters'),dw=width-sourceCard.width;
                bounds(card,cx,cy,width,card.height);bounds(header,header.x,header.y,width-5,52);bounds(body,body.x,body.y,width-5,body.height);
                for(const s of header.shapes.map(i=>objects[i])){
                    if(s.name==='Card header')bounds(s,s.x,s.y,width-5,52);
                    else if(s.name==='Input')bounds(s,s.x,s.y,s.width+dw,s.height);
                    else if(s.name==='Cross-Sections'){setText(s,width===224?'Section…':'Section Cut');s.name='Section Cut';bounds(s,s.x,s.y,s.width+dw,s.height);}
                    else if(s.name==='Cross-Sections type'){setText(s,'Section Cut');s.name='Section Cut type';bounds(s,s.x,s.y,s.width+dw,s.height);}
                    else if(s.fills?.some(f=>f['fill-image'])){s.fills=structuredClone(cutIcon.fills);s.name='AnTerrainSection';}
                    else if(s.name.startsWith('Duplicate')||s.name.startsWith('Delete'))move(s,dw,0);
                }
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');accent.fills=[{'fill-color':'#7b1fa2','fill-opacity':1}];delete accent['applied-tokens'];const rows=[];let cursor=body.y+6;
                function row(template,label,value){
                    const r=cloneTree(template.id,body.id,body.x+8-template.x,cursor-template.y),oldw=r.width;bounds(r,r.x,r.y,width-21,r.height);
                    const parts=r.shapes.map(i=>objects[i]),title=parts.find(s=>s.type==='text'),editor=parts.find(s=>s.name==='Editor');if(title&&label)setText(title,label);
                    if(editor){
                        const grow=r.width-oldw;bounds(editor,editor.x,editor.y,editor.width+grow,editor.height);
                        if(template.id===sourceTemplate.id){
                            for(const [i,b]of editor.shapes.map(i=>objects[i]).entries()){const bw=(editor.width-4)/2,bx=editor.x+i*(bw+4);move(b,bx-b.x,0);bounds(b,bx,b.y,bw,b.height);for(const s of b.shapes.map(i=>objects[i]))bounds(s,s.x,s.y,s.type==='text'?bw-4:bw,s.height);}
                        }else for(const s of editor.shapes.map(i=>objects[i])){
                            if(s.name==='Input'||s.type==='text'&&!['Unit','▴','▾'].includes(s.name)){bounds(s,s.x,s.y,s.width+grow,s.height);if(s.type==='text'&&value!==undefined&&typeof value!=='boolean')setText(s,value);}
                            else if(s.name==='Checked'){if(typeof value==='boolean')s.hidden=!value;}
                            else if(s.name!=='Checkbox')move(s,grow,0);
                        }
                    }else if(template.id===insertTemplate.id){
                        const button=parts.find(s=>s.type==='frame');bounds(button,button.x,button.y,button.width+(r.width-oldw),button.height);for(const s of button.shapes.map(i=>objects[i]))bounds(s,s.x,s.y,s.width+(r.width-oldw),s.height);
                    }
                    rows.push(r);cursor+=r.height;return r;
                }
                function group(label,open){const r=cloneTree(groupTemplate.id,body.id,body.x+8-groupTemplate.x,cursor-groupTemplate.y);bounds(r,r.x,r.y,width-21,28);const t=r.shapes.map(i=>objects[i]).find(s=>s.type==='text');setText(t,(open?'▾ ':'▸ ')+label);bounds(t,t.x,t.y,r.width-4,t.height);r.name=label+' group';rows.push(r);cursor+=28;}
                row(sourceTemplate,'Cut Lines');row(numericTemplate,'Vertical Scale','1.000');cursor+=6;row(insertTemplate);cursor+=6;row(comparisonTemplate,'Comparison','Off');cursor+=6;
                group('Plan Annotation',expanded);if(expanded)row(boolTemplate,'Section Marks',true);cursor+=6;
                group('Section Annotation',expanded);if(expanded){row(boolTemplate,'Section Title',true);row(boolTemplate,'Distance Labels',false);row(boolTemplate,'Tick Marks',false);row(numericTemplate,'Tick Interval','0.000');row(boolTemplate,'Elevation Labels',true);row(boolTemplate,'Grid Lines',false);row(numericTemplate,'Elevation Interval','Auto');row(textTemplate);}
                body.shapes=rows.map(s=>s.id);bounds(body,body.x,body.y,width-5,cursor+6-body.y);bounds(card,cx,cy,width,cursor+6-cy);bounds(accent,cx,cy,5,card.height);children.push(card);
            }
            children.push(caption('SECTION CUT · DIRECT CUT CURVES · AUTOMATIC PLACEMENT · COMPARISON OPTIONAL',x+16,y+board.height-38,board.width-32,board.id));board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionComparisonStep){
        const reference=Object.values(pages).find(p=>p.name==='36 • Step 28 — Plan and section annotation expanded');
        for(const [ti,theme]of ['dark','light'].entries())for(const [wi,width]of [304,224].entries())for(const [pairIndex,modes]of [['Off','Original Terrain'],['Another Terrain','Rhino Geometry']].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 28 / Cross-Sections / '+theme+' / '+width),sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Cross-Sections / Annotation groups open');
            const nativeBody=sourceCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters'),nativeRows=nativeBody.shapes.map(i=>reference.objects[i]);
            const labelOf=r=>r.shapes?.map(i=>reference.objects[i]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');
            const choiceTemplate=nativeRows.find(s=>s.name==='Comparison mode row'),boolTemplate=nativeRows.find(s=>labelOf(s)==='Cut Lines'),sourcesTemplate=nativeRows.find(s=>s.name==='Sources property row');
            const x=ti*1800+pairIndex*850,y=wi*700,board=newFrame('Step 29 / Comparison '+(pairIndex?'references':'basic')+' / '+theme+' / '+width,x,y,width*2+48,590);
            board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);const children=[];
            for(const [column,mode]of modes.entries()){
                const cx=x+16+column*(width+16),cy=y+52;children.push(caption(mode.toUpperCase(),cx,y+16,width,board.id));
                const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name='Cross-Sections / '+mode;
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters'),allRows=body.shapes.map(i=>objects[i]),comparisonIndex=allRows.findIndex(s=>s.name==='Comparison mode row'),rows=allRows.slice(0,comparisonIndex+1),comparison=rows.at(-1);
                const editor=comparison.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');setText(editor.shapes.map(i=>objects[i]).find(s=>s.type==='text'),mode);
                let cursor=comparison.y+comparison.height;
                if(mode==='Another Terrain'){
                    const r=cloneTree(choiceTemplate.id,body.id,body.x+8-choiceTemplate.x,cursor-choiceTemplate.y),parts=r.shapes.map(i=>objects[i]);r.name='Comparison terrain picker';setText(parts.find(s=>s.type==='text'),'Terrain');
                    const e=parts.find(s=>s.name==='Editor');setText(e.shapes.map(i=>objects[i]).find(s=>s.type==='text'),'Existing Ground');rows.push(r);cursor+=32;
                }
                if(mode==='Rhino Geometry'){
                    const r=cloneTree(sourcesTemplate.id,body.id,body.x+8-sourcesTemplate.x,cursor-sourcesTemplate.y);r.name='Comparison geometry picker';setText(r.shapes.map(i=>objects[i]).find(s=>s.type==='text'),'Geometry');rows.push(r);cursor+=32;
                }
                if(mode!=='Off'){
                    const r=cloneTree(boolTemplate.id,body.id,body.x+8-boolTemplate.x,cursor-boolTemplate.y),parts=r.shapes.map(i=>objects[i]);r.name='Comparison shading toggle';setText(parts.find(s=>s.type==='text'),'Shade Cut/Fill');
                    const e=parts.find(s=>s.name==='Editor');for(const s of e.shapes.map(i=>objects[i]))if(s.name==='Checked')s.hidden=false;rows.push(r);cursor+=32;
                }
                cursor+=6;
                for(const name of ['Plan Annotation','Section Annotation','Layout Overrides']){
                    const r=allRows.find(s=>s.name===name+' group');move(r,0,cursor-r.y);setText(r.shapes.map(i=>objects[i]).find(s=>s.type==='text'),'▸ '+name);rows.push(r);cursor+=28;
                }
                body.shapes=rows.map(s=>s.id);bounds(body,body.x,body.y,body.width,cursor+6-body.y);bounds(card,card.x,card.y,card.width,cursor+6-card.y);
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');bounds(accent,accent.x,accent.y,accent.width,card.height);children.push(card);
            }
            children.push(caption('PROPOSED COMPARISON STATES · SHADING STARTS ON WHEN ENABLED · REFERENCES ILLUSTRATIVE',x+16,y+548,board.width-32,board.id));board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionExpandedAnnotationsStep){
        const reference=Object.values(pages).find(p=>p.name==='35 • Step 27 — Cross-section Paths input');
        const contourReference=Object.values(pages).find(p=>p.name==='31 • Step 23 — Contour settings on one property grid');
        const contentText=s=>s.shapes?.map(i=>objects[i]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 27','Step 28');bounds(board,board.x,board.y,board.width,840);
            const theme=original.name.includes('/ dark /')?'dark':'light',width=+original.name.split(' / ').at(-1);
            const contourBoard=Object.values(contourReference.objects).find(s=>s.name==='Step 23 / Contours / '+theme+' / '+width),contour=contourBoard.shapes.map(i=>contourReference.objects[i]).find(s=>s.name==='Contours / APPEARANCE OPEN'),contourBody=contour.shapes.map(i=>contourReference.objects[i]).find(s=>s.name==='Parameters');
            const booleanTemplate=contourBody.shapes.map(i=>contourReference.objects[i]).find(s=>s.shapes?.some(i=>contourReference.objects[i].name==='Separate Layers'));
            const textSizeTemplate=contourBody.shapes.map(i=>contourReference.objects[i]).find(s=>s.shapes?.some(i=>contourReference.objects[i].name==='Text Size'));
            for(const s of board.shapes.map(i=>objects[i]).filter(s=>s.type==='text')){
                const t=textOf(s.content);if(t==='AUTOMATIC PLACEMENT')setText(s,'ANNOTATION GROUPS OPEN');else if(t==='PICKED INSERTION POINT')setText(s,'PLUS LAYOUT OVERRIDES');else if(t.startsWith('PROPOSED DEFAULTS')){setText(s,'STATIC EXPANDED STATES · COMPARISON OFF · FULL-WIDTH GROUP HEADER CLICK TARGETS PLANNED');move(s,0,board.y+800-s.y);}
            }
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame')){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');if(!body)continue;
                const nativeCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name===card.name),nativeBody=nativeCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters');
                const numberTemplate=nativeBody.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Number property row');
                const nativeGroup=nativeBody.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Labels and Grid group');
                const openLayout=card.name.endsWith('/ Picked'),rows=body.shapes.map(i=>objects[i]).filter(s=>!['Labels and Grid group','Layout Overrides group'].includes(s.name));
                let cursor=rows.at(-1).y+rows.at(-1).height+6;
                function heading(label,open=true){
                    const r=cloneTree(nativeGroup.id,body.id,body.x+8-nativeGroup.x,cursor-nativeGroup.y);r.name=label+' group';
                    const title=r.shapes.map(i=>objects[i]).find(s=>s.type==='text');setText(title,(open?'▾ ':'▸ ')+label);
                    function style(v){if(v&&typeof v==='object'){if(v['font-weight']){v['font-weight']='700';v['font-variant-id']='bold';delete v['typography-ref-id'];delete v['typography-ref-file'];}for(const c of v.children??[])style(c);}}style(title.content);
                    rows.push(r);cursor+=28;
                }
                function bool(label,on){
                    const r=cloneTree(booleanTemplate.id,body.id,body.x+8-booleanTemplate.x,cursor-booleanTemplate.y),parts=r.shapes.map(i=>objects[i]);setText(parts.find(s=>s.type==='text'),label);
                    const editor=parts.find(s=>s.name==='Editor');for(const s of editor.shapes.map(i=>objects[i]))if(s.name==='Checked')s.hidden=!on;
                    rows.push(r);cursor+=32;
                }
                function number(label,value){
                    const r=cloneTree(numberTemplate.id,body.id,body.x+8-numberTemplate.x,cursor-numberTemplate.y),parts=r.shapes.map(i=>objects[i]);setText(parts.find(s=>s.type==='text'),label);
                    const editor=parts.find(s=>s.name==='Editor'),valueText=editor.shapes.map(i=>objects[i]).find(s=>s.type==='text'&&!['Unit','▴','▾'].includes(s.name));setText(valueText,value);rows.push(r);cursor+=32;
                }
                heading('Plan Annotation');bool('Cut Lines',true);bool('Station Labels',true);cursor+=6;
                heading('Section Annotation');bool('Station Title',true);bool('Distance Labels',true);bool('Elevation Labels',true);bool('Grid Lines',false);number('Elevation Interval','Auto');
                const textSize=cloneTree(textSizeTemplate.id,body.id,body.x+8-textSizeTemplate.x,cursor-textSizeTemplate.y);rows.push(textSize);cursor+=38;
                heading('Layout Overrides',openLayout);if(openLayout){number('Columns','Auto');number('Cell Width','Auto');number('Cell Height','Auto');}
                body.shapes=rows.map(s=>s.id);bounds(body,body.x,body.y,body.width,cursor+6-body.y);bounds(card,card.x,card.y,card.width,cursor+6-card.y);
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');bounds(accent,accent.x,accent.y,accent.width,card.height);
                card.name='Cross-Sections / '+(openLayout?'All annotation and layout groups open':'Annotation groups open');
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionPathsStep){
        const reference=Object.values(pages).find(p=>p.name==='34 • Step 26 — Insertion picked and clear pattern');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 26','Step 27');
            descendants(board,s=>{if(s.type==='text'&&textOf(s.content)==='Alignment')setText(s,'Paths');});
            objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionPickedPillStep){
        const reference=Object.values(pages).find(p=>p.name==='33 • Step 25 — Cross-section input edges aligned');
        const pathTemplate=Object.values(allObjects).find(s=>s.type==='path');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 25','Step 26');
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame')){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');if(!body)continue;
                const row=body.shapes.map(i=>objects[i]).find(s=>s.name==='Insertion point row'),picked=card.name.endsWith('/ Picked');
                const title=row.shapes.map(i=>objects[i]).find(s=>s.type==='text'&&textOf(s.content)==='Insertion');
                const nativeCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name===card.name),nativeBody=nativeCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters'),nativeRow=nativeBody.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Insertion point row');
                const buttonTemplate=nativeRow.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Pick insertion button'),x=row.x+90,y=row.y+2,w=row.width-90;
                const button=cloneTree(buttonTemplate.id,row.id,x-buttonTemplate.x,y-buttonTemplate.y),bw=w-(picked?22:0);button.name=picked?'Picked point — click to replace':'Pick point — automatic until picked';bounds(button,x,y,bw,22);
                for(const s of button.shapes.map(i=>objects[i])){bounds(s,s.x,s.y,s.type==='text'?bw-4:bw,s.height);if(s.type==='text')setText(s,picked?'Picked':'Pick Point');}
                row.shapes=[title.id,button.id];
                if(picked){
                    const clear=newFrame('Clear picked insertion — return to automatic',x+bw,y,22,22,row.id);clear.fills=[];clear.strokes=[];delete clear['applied-tokens'];const strokes=[];
                    for(const points of [[[clear.x+7,y+7],[clear.x+15,y+15]],[[clear.x+15,y+7],[clear.x+7,y+15]]]){
                        const s=cloneTree(pathTemplate.id,clear.id);s.name='Clear insertion glyph';bounds(s,clear.x+7,y+7,8,8);
                        const bytes=new Uint8Array(56),view=new DataView(bytes.buffer);points.forEach(([px,py],i)=>{view.setInt16(i*28,i?2:1,true);view.setFloat32(i*28+20,px,true);view.setFloat32(i*28+24,py,true);});s.content=bytes;s.fills=[];s.strokes=[{'stroke-color':original.name.includes('/ dark /')?'#e0e0e0':'#1e1e1e','stroke-opacity':1,'stroke-width':1.6,'stroke-style':'solid','stroke-alignment':'center'}];delete s['applied-tokens'];strokes.push(s.id);
                    }
                    clear.shapes=strokes;row.shapes.push(clear.id);
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionAlignedInputsStep){
        const reference=Object.values(pages).find(p=>p.name==='32 • Step 24 — Simple cross-sections and insertion point');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 24','Step 25');
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame')){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');if(!body)continue;
                for(const row of body.shapes.map(i=>objects[i]).filter(s=>s.name==='Number property row')){
                    const editor=row.shapes.map(i=>objects[i]).find(s=>s.name==='Editor'),parts=editor.shapes.map(i=>objects[i]),input=parts.find(s=>s.name==='Input');
                    const grow=editor.x+editor.width-input.x-input.width;
                    bounds(input,input.x,input.y,input.width+grow,input.height);
                    for(const s of parts.filter(s=>s.id!==input.id)){
                        if(s.name==='Unit'){s.hidden=true;continue;}
                        if(s.type==='text'&&!['▴','▾'].includes(s.name))bounds(s,s.x,s.y,s.width+grow,s.height);
                        else move(s,grow,0);
                    }
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(crossSectionSimpleStep){
        const reference=Object.values(pages).find(p=>p.name==='31 • Step 23 — Contour settings on one property grid');
        const modifierReference=Object.values(pages).find(p=>p.name==='21 • Step 13 — Fixed two-line collapsed headers');
        const crossIcon=Object.values(allObjects).find(s=>s.name==='AnCrossSection'&&s.fills?.some(f=>f['fill-image']));
        for(const [ti,theme]of ['dark','light'].entries())for(const [wi,width]of [304,224].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 23 / Contours / '+theme+' / '+width);
            const sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Contours / LABELS OFF');
            const modifiers=Object.values(modifierReference.objects).find(s=>s.name==='Step 13 / Modifiers / '+theme+' / 320');
            const tri=modifiers.shapes.map(i=>modifierReference.objects[i]).find(s=>s.name==='Triangulate');
            const triBody=tri.shapes.map(i=>modifierReference.objects[i]).find(s=>s.name==='Parameters');
            const sourcesTemplate=triBody.shapes.map(i=>modifierReference.objects[i]).find(s=>s.name==='Sources property row');
            const x=ti*1100,y=wi*570,board=newFrame('Step 24 / Cross-Sections / '+theme+' / '+width,x,y,width*2+48,520);
            board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);const children=[];
            for(const [column,label]of ['AUTOMATIC PLACEMENT','PICKED INSERTION POINT'].entries()){
                const cx=x+16+column*(width+16),cy=y+52;children.push(caption(label,cx,y+16,width,board.id));
                const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name='Cross-Sections / '+(column?'Picked':'Auto');
                const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header'),body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');
                for(const s of header.shapes.map(i=>objects[i])){
                    if(s.name==='Contours'){setText(s,width===224?'Cross-S…':'Cross-Sections');s.name='Cross-Sections';}
                    if(s.name==='Contours type'){setText(s,'Cross-Sections');s.name='Cross-Sections type';}
                    if(s.fills?.some(f=>f['fill-image'])){s.fills=structuredClone(crossIcon.fills);s.name='AnCrossSection';}
                }
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');accent.fills=[{'fill-color':'#8e24aa','fill-opacity':1}];delete accent['applied-tokens'];
                const nativeBody=sourceCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters'),numeric=nativeBody.shapes.map(i=>reference.objects[i]).find(r=>r.shapes?.some(i=>reference.objects[i].name==='Interval'));
                const onCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Contours / LABELS ON'),onBody=onCard.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Parameters');
                const choice=onBody.shapes.map(i=>reference.objects[i]).find(r=>r.shapes?.some(i=>reference.objects[i].name==='Label Decimals'));
                let cursor=body.y+6;const rows=[];
                const source=cloneTree(sourcesTemplate.id,body.id,body.x+8-sourcesTemplate.x,cursor-sourcesTemplate.y);
                bounds(source,source.x,cursor,width-21,32);const sourceParts=source.shapes.map(i=>objects[i]),sourceTitle=sourceParts.find(s=>s.type==='text'),sourceEditor=sourceParts.find(s=>s.name==='Editor');
                setText(sourceTitle,'Alignment');bounds(sourceTitle,source.x,cursor+5,90,18);move(sourceEditor,source.x+90-sourceEditor.x,0);bounds(sourceEditor,sourceEditor.x,sourceEditor.y,source.width-90,26);
                for(const [i,button]of sourceEditor.shapes.map(i=>objects[i]).entries()){
                    const bw=(sourceEditor.width-4)/2,bx=sourceEditor.x+i*(bw+4);move(button,bx-button.x,0);bounds(button,bx,button.y,bw,button.height);
                    for(const s of button.shapes.map(i=>objects[i]))bounds(s,s.x,s.y,s.type==='text'?bw-4:bw,s.height);
                }
                rows.push(source);cursor+=32;
                function number(label,value){const r=cloneTree(numeric.id,body.id,body.x+8-numeric.x,cursor-numeric.y),parts=r.shapes.map(i=>objects[i]);setText(parts.find(s=>s.type==='text'),label);const e=parts.find(s=>s.name==='Editor');const v=e.shapes.map(i=>objects[i]).find(s=>s.type==='text'&&!['▴','▾','Unit'].includes(s.name));setText(v,value);rows.push(r);cursor+=32;}
                number('Station Interval','10.000');number('Section Width','10.000');number('Vertical Scale','1.000');cursor+=6;
                const insertion=newFrame('Insertion point row',body.x+8,cursor,width-21,32,body.id);insertion.fills=[];delete insertion['applied-tokens'];
                const il=caption('Insertion',insertion.x,cursor+5,90,insertion.id),ew=insertion.width-90,buttonWidth=width===224?34:42,gap=4,statusWidth=ew-2*buttonWidth-2*gap;
                const status=caption(column?'Picked':'Auto',insertion.x+90,cursor+5,statusWidth,insertion.id);status.name=column?'Picked point — full coordinates in tooltip':'Auto — beside terrain';const parts=[il,status];
                const nativeSourceEditor=sourcesTemplate.shapes.map(i=>modifierReference.objects[i]).find(s=>s.name==='Editor'),actionTemplate=modifierReference.objects[nativeSourceEditor.shapes[0]];
                for(const [i,label]of ['Pick','Auto'].entries()){
                    const bx=insertion.x+90+statusWidth+gap+i*(buttonWidth+gap),button=cloneTree(actionTemplate.id,insertion.id,bx-actionTemplate.x,cursor+2-actionTemplate.y);button.name=label+' insertion button';bounds(button,bx,cursor+2,buttonWidth,22);
                    for(const s of button.shapes.map(i=>objects[i])){bounds(s,s.x,s.y,s.type==='text'?buttonWidth-4:buttonWidth,s.height);if(s.type==='text')setText(s,label);}parts.push(button);
                }
                insertion.shapes=parts.map(s=>s.id);rows.push(insertion);cursor+=38;
                const comp=cloneTree(choice.id,body.id,body.x+8-choice.x,cursor-choice.y);comp.name='Comparison mode row';const compParts=comp.shapes.map(i=>objects[i]);setText(compParts.find(s=>s.type==='text'),'Comparison');const ce=compParts.find(s=>s.name==='Editor');setText(ce.shapes.map(i=>objects[i]).find(s=>s.type==='text'),'Off');rows.push(comp);cursor+=38;
                for(const label of ['Labels and Grid','Layout Overrides']){const r=newFrame(label+' group',body.x+8,cursor,width-21,28,body.id);r.fills=[];delete r['applied-tokens'];const t=caption('▸ '+label,r.x+2,cursor+4,r.width-4,r.id);r.shapes=[t.id];rows.push(r);cursor+=28;}
                body.shapes=rows.map(s=>s.id);bounds(body,body.x,body.y,body.width,cursor+6-body.y);bounds(card,card.x,card.y,card.width,cursor+6-card.y);bounds(accent,accent.x,accent.y,accent.width,card.height);children.push(card);
            }
            children.push(caption('PROPOSED DEFAULTS · AUTOMATIC ARRANGEMENT · PICK/RESET INTERACTION NOT IMPLEMENTED',x+16,y+478,board.width-32,board.id));board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(contourFlushRowsStep){
        const reference=Object.values(pages).find(p=>p.name==='30 • Step 22 — Contour toggle in control column');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 22','Step 23');
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame')){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');if(!body)continue;
                for(const row of body.shapes.map(i=>objects[i])){
                    const indent=row.x-(body.x+8);if(indent<1)continue;
                    move(row,-indent,0);bounds(row,row.x,row.y,row.width+indent,row.height);
                    const parts=row.shapes.map(i=>objects[i]),title=parts.find(s=>s.type==='text'),editor=parts.find(s=>s.name==='Editor');
                    if(!editor)continue;
                    const change=row.width-90-editor.width;
                    move(editor,row.x+90-editor.x,0);bounds(editor,editor.x,editor.y,row.width-90,editor.height);
                    bounds(title,row.x,title.y,90,title.height);
                    if(textOf(title.content)==='Color'){
                        for(const s of editor.shapes.map(i=>objects[i])){
                            if(s.name==='Color swatch')bounds(s,editor.x,editor.y+3,18,18);
                            if(s.name==='Reset button')bounds(s,editor.x+26,editor.y,44,22);
                            if(s.name==='Reset')bounds(s,editor.x+28,editor.y+2,40,18);
                            if(s.name==='Hex value')bounds(s,editor.x+76,editor.y+4,editor.width-76,18);
                        }
                    }else for(const s of editor.shapes.map(i=>objects[i])){
                        if(s.name==='Input'||s.type==='text'&&!['▴','▾','Unit'].includes(s.name))bounds(s,s.x,s.y,s.width+change,s.height);
                        else if(!['Checkbox','Checked'].includes(s.name))move(s,change,0);
                    }
                }
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(contourAlignedToggleStep){
        const reference=Object.values(pages).find(p=>p.name==='29 • Step 21 — Contour labels reveal settings');
        for(const original of reference.objects[zero].shapes.map(i=>reference.objects[i])){
            const board=cloneTree(original.id,zero);board.name=original.name.replace('Step 21','Step 22');
            for(const card of board.shapes.map(i=>objects[i]).filter(s=>s.type==='frame')){
                const body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');if(!body)continue;
                const toggle=body.shapes.map(i=>objects[i]).find(r=>r.shapes?.some(j=>objects[j].type==='text'&&textOf(objects[j].content)==='Labels'));
                if(!toggle)throw Error('Missing Labels row.');
                const title=toggle.shapes.map(i=>objects[i]).find(s=>s.type==='text'),editor=toggle.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');
                bounds(title,toggle.x,toggle.y+4,90,20);move(editor,toggle.x+90-editor.x,0);
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(contourHierarchyStep){
        const reference=Object.values(pages).find(p=>p.name==='21 • Step 13 — Fixed two-line collapsed headers');
        const headerReference=Object.values(pages).find(p=>p.name==='28 • Step 20 — Header grouping side by side');
        const labelOf=r=>(r.shapes??[]).map(i=>objects[i]).filter(s=>s.type==='text').map(s=>textOf(s.content)).join('');
        for(const [ti,theme]of ['dark','light'].entries())for(const [wi,width]of [304,224].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 13 / Annotation / '+theme+' / 320');
            const sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Contours');
            const headerBoard=Object.values(headerReference.objects).find(s=>s.name==='Step 20 / header group comparison / '+theme);
            const headerCard=headerBoard.shapes.map(i=>headerReference.objects[i]).find(s=>s.name==='A current expanded narrow header');
            const sourceHeader=headerCard.shapes.map(i=>headerReference.objects[i]).find(s=>s.name==='Header');
            const x=ti*1400,y=wi*700,board=newFrame('Step 21 / Contours / '+theme+' / '+width,x,y,width*3+64,610);
            board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);
            const children=[];
            for(const [column,label]of ['LABELS OFF','LABELS ON','APPEARANCE OPEN'].entries()){
                const cx=x+16+column*(width+16),cy=y+52,on=column>0,appearance=column===2;
                children.push(caption(label,cx,y+16,width,board.id));
                const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name='Contours / '+label;
                const oldHeader=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header'),body=card.shapes.map(i=>objects[i]).find(s=>s.name==='Parameters');
                const contourImage=oldHeader.shapes.map(i=>objects[i]).find(s=>s.name==='AnContour');
                const header=cloneTree(sourceHeader.id,card.id,cx+5-sourceHeader.x,cy-sourceHeader.y),delta=width-224;
                for(const s of header.shapes.map(i=>objects[i])){
                    if(s.name==='Card header')bounds(s,s.x,s.y,width-5,52);
                    else if(s.name==='Input')bounds(s,s.x,s.y,s.width+delta,s.height);
                    else if(s.name==='Building Retaining Walls'){setText(s,'Contours');s.name='Contours';bounds(s,s.x,s.y,s.width+delta,s.height);}
                    else if(s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-size']==='11'){setText(s,'Contours');s.name='Contours type';bounds(s,s.x,s.y,s.width+delta,s.height);}
                    else if(s.fills?.some(f=>f['fill-image'])){s.fills=structuredClone(contourImage.fills);s.name='AnContour';}
                    else if(s.name.startsWith('Duplicate')||s.name.startsWith('Delete'))move(s,delta,0);
                }
                bounds(header,header.x,cy,width-5,52);card.shapes[card.shapes.indexOf(oldHeader.id)]=header.id;
                bounds(body,cx+5,cy+52,width-5,body.height);
                const rows=body.shapes.map(i=>objects[i]),byLabel=new Map(rows.map(r=>[labelOf(r),r]));
                let cursor=body.y+6;const arranged=[];
                function row(name,indent=0){
                    const r=byLabel.get(name);if(!r)throw Error('Missing contour row '+name);
                    const oldWidth=r.width;move(r,body.x+8+indent-r.x,cursor-r.y);bounds(r,r.x,cursor,width-21-indent,32);
                    const title=r.shapes.map(i=>objects[i]).find(s=>s.type==='text'),editor=r.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');
                    bounds(title,r.x,cursor+5,90,18);move(editor,r.x+90-editor.x,cursor+2-editor.y);
                    const dw=r.width-90-editor.width;bounds(editor,editor.x,editor.y,r.width-90,26);
                    for(const s of editor.shapes.map(i=>objects[i])){
                        if(s.name==='Input'||(s.type==='text'&&s.name!=='Unit'&&!['▴','▾','▼'].includes(s.name))){bounds(s,s.x,s.y,Math.max(12,s.width+dw),s.height);}
                        else if(!['Checkbox','Checked'].includes(s.name))move(s,dw,0);
                    }
                    if(name==='Color'){
                        bounds(title,r.x,cursor+5,70,18);bounds(editor,r.x+70,cursor+2,r.width-70,26);
                        for(const s of editor.shapes.map(i=>objects[i])){
                            if(s.name==='Color swatch')bounds(s,editor.x,editor.y+3,18,18);
                            if(s.name==='Reset button')bounds(s,editor.x+26,editor.y,44,22);
                            if(s.name==='Reset')bounds(s,editor.x+28,editor.y+2,40,18);
                            if(s.name==='Hex value')bounds(s,editor.x+76,editor.y+4,editor.width-76,18);
                        }
                    }
                    r['parent-id']=body.id;arranged.push(r);cursor+=32;return r;
                }
                function heading(label){
                    const h=newFrame(label+' group',body.x+8,cursor,width-21,28,body.id);h.fills=[];h.strokes=[];delete h['applied-tokens'];
                    const text=caption(label,h.x+2,cursor+4,h.width-4,h.id);h.shapes=[text.id];arranged.push(h);cursor+=28;return h;
                }
                row('Interval');row('Base Elevation');row('Major Every');cursor+=6;
                const toggle=byLabel.get('Label Contours'),toggleTitle=toggle.shapes.map(i=>objects[i]).find(s=>s.type==='text'),toggleEditor=toggle.shapes.map(i=>objects[i]).find(s=>s.name==='Editor');
                move(toggle,body.x+8-toggle.x,cursor-toggle.y);bounds(toggle,toggle.x,cursor,width-21,28);
                const check=toggleEditor.shapes.map(i=>objects[i]).find(s=>s.name==='Checkbox'),tick=toggleEditor.shapes.map(i=>objects[i]).find(s=>s.name==='Checked');
                const checkboxDx=toggle.x+2-check.x,checkboxDy=cursor+8-check.y;move(check,checkboxDx,checkboxDy);move(tick,checkboxDx,checkboxDy);
                tick.hidden=!on;
                toggleEditor.shapes=on?[check.id,tick.id]:[check.id];bounds(toggleEditor,toggle.x+2,cursor+8,12,12);
                setText(toggleTitle,'Labels');bounds(toggleTitle,toggle.x+22,cursor+4,toggle.width-22,20);toggleTitle.content['vertical-align']='center';
                toggle['parent-id']=body.id;arranged.push(toggle);cursor+=28;
                if(on){row('Label Interval',12);row('Label Every',12);row('Label Decimals',12);row('Text Size',12);}
                cursor+=6;heading((appearance?'▾ ':'▸ ')+'Appearance');
                if(appearance){row('Separate Layers',12);row('Color',12);}
                body.shapes=arranged.map(s=>s.id);bounds(body,body.x,body.y,width-5,cursor+6-body.y);bounds(card,cx,cy,width,cursor+6-cy);
                const accent=card.shapes.map(i=>objects[i]).find(s=>s.name==='Accent strip');bounds(accent,cx,cy,5,card.height);children.push(card);
            }
            children.push(caption('LABELS CHECKBOX ENABLES + REVEALS · VALUES RETAINED WHEN OFF · STATIC STATES',x+16,y+572,board.width-32,board.id));
            board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(groupingCompareStep){
        const reference=Object.values(pages).find(p=>p.name==='27 • Step 19 — Bare card type icon');
        for(const [ti,theme]of ['dark','light'].entries()){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 19 / bare type icon / '+theme),x=ti*1000;
            const board=newFrame('Step 20 / header group comparison / '+theme,x,0,848,470);board.fills=structuredClone(original.fills);board['applied-tokens']=structuredClone(original['applied-tokens']);
            const children=[caption('A · CURRENT GROUPING',x+16,16,400,board.id),caption('B · BLENDER-STYLE GROUPING',x+432,16,400,board.id)];
            for(const [row,kind]of ['collapsed wide','collapsed narrow','expanded wide','expanded narrow'].entries()){
                const cy=78+row*96;children.push(caption((kind.includes('expanded')?'EXPANDED':'COLLAPSED')+' · '+(kind.includes('narrow')?'224':'400')+' PX',x+16,cy-24,400,board.id));
                for(const column of [0,1]){
                    const sourceCard=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Icon only '+kind+' header'),cx=x+16+column*416;
                    const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name=(column?'B Blender-style ':'A current ')+kind+' header';
                    if(column){
                        const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header'),parts=header.shapes.map(i=>objects[i]);
                        const checkbox=parts.find(s=>s.name==='Checkbox'),tick=parts.find(s=>s.name==='Checked'),duplicate=parts.find(s=>s.name==='Duplicate button');
                        const dx=duplicate.x-26-checkbox.x;move(checkbox,dx,0);move(tick,dx,0);
                        for(const s of parts.filter(s=>s.name==='Icon plate'||s.fills?.some(f=>f['fill-image'])||s.name==='Input'||s.name==='Building Retaining Walls'))move(s,-26,0);
                    }
                    children.push(card);
                }
            }
            children.push(caption('SAME SIZES, NAME SPACE, ICONS AND ALIGNMENT · ONLY ICON/NAME AND ENABLE POSITION CHANGE',x+16,444,816,board.id));
            board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(bareTypeIconStep){
        const reference=Object.values(pages).find(p=>p.name==='26 • Step 18 — Borderless header actions');
        for(const theme of ['dark','light']){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 18 / borderless actions / '+theme);
            const board=cloneTree(original.id,zero);board.name='Step 19 / bare type icon / '+theme;
            for(const id of board.shapes){
                const card=objects[id];
                if(card.type==='text'){
                    const label=textOf(card.content);
                    if(label.startsWith('PREVIOUS ·'))setText(card,'PREVIOUS · TYPE ICON WITH BACKGROUND PLATE');
                    if(label.startsWith('BORDERLESS · COLLAPSED'))setText(card,'ICON ONLY · COLLAPSED HEADER');
                    if(label.startsWith('BORDERLESS · EXPANDED'))setText(card,'ICON ONLY · EXPANDED NAME FIELD');
                    if(label.startsWith('RESTING STATE'))setText(card,'SAME 10 PX TYPE GLYPH · NO BACKGROUND PLATE · SAME HEADER HEIGHT AND ALIGNMENT');
                    continue;
                }
                if(card.type!=='frame')continue;
                if(card.name.startsWith('Previous')){
                    const source=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Borderless collapsed'+(card.width===224?' narrow header':' wide header'));
                    const replacement=cloneTree(source.id,board.id,card.x-source.x,card.y-source.y);replacement.name=card.name;board.shapes[board.shapes.indexOf(id)]=replacement.id;continue;
                }
                card.name=card.name.replace('Borderless','Icon only');
                const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header');
                for(const plate of header.shapes.map(i=>objects[i]).filter(s=>s.name==='Icon plate')){plate.fills=[];plate.strokes=[];delete plate['applied-tokens'];}
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(borderlessActionsStep){
        const reference=Object.values(pages).find(p=>p.name==='25 • Step 17 — Smaller header controls');
        for(const theme of ['dark','light']){
            const original=Object.values(reference.objects).find(s=>s.name==='Step 17 / smaller controls / '+theme);
            const board=cloneTree(original.id,zero);board.name='Step 18 / borderless actions / '+theme;
            for(const id of board.shapes){
                const card=objects[id];
                if(card.type==='text'){
                    const label=textOf(card.content);
                    if(label.startsWith('PREVIOUS ·'))setText(card,'PREVIOUS · 18 PX ACTIONS WITH OUTLINES');
                    if(label.startsWith('SMALLER · 14'))setText(card,'BORDERLESS · COLLAPSED HEADER');
                    if(label.startsWith('SMALLER · EXPANDED'))setText(card,'BORDERLESS · EXPANDED NAME FIELD');
                    if(label.startsWith('X-HEIGHT ALIGNMENT'))setText(card,'RESTING STATE · SAME SIZES AND ALIGNMENT · HOVER/FOCUS FEEDBACK AT IMPLEMENTATION');
                    continue;
                }
                if(card.type!=='frame')continue;
                const previous=card.name.startsWith('Previous');
                // Use the smaller collapsed controls for the before row as well.
                if(previous){
                    const source=original.shapes.map(i=>reference.objects[i]).find(s=>s.name==='Smaller collapsed'+(card.width===224?' narrow header':' wide header'));
                    const replacement=cloneTree(source.id,board.id,card.x-source.x,card.y-source.y);replacement.name=card.name;
                    board.shapes[board.shapes.indexOf(id)]=replacement.id;
                    continue;
                }
                card.name=card.name.replace('Smaller','Borderless');
                const header=card.shapes.map(i=>objects[i]).find(s=>s.name==='Header');
                for(const button of header.shapes.map(i=>objects[i]).filter(s=>['Duplicate button','Delete button'].includes(s.name))){button.strokes=[];delete button['applied-tokens'];}
            }
            objects[zero].shapes.push(board.id);
        }
    }
    if(compactControlsStep){
        const reference=Object.values(pages).find(p=>p.name==='24 • Step 16 — X-height alignment and smaller badge');
        for(const [ti,theme]of ['dark','light'].entries()){
            const sourceBoard=Object.values(reference.objects).find(s=>s.name==='Step 16 / optical alignment / '+theme),x=ti*800;
            const board=newFrame('Step 17 / smaller controls / '+theme,x,0,680,382);board.fills=structuredClone(sourceBoard.fills);board['applied-tokens']=structuredClone(sourceBoard['applied-tokens']);
            const children=[caption('WIDE · 400 PX',x+16,16,400,board.id),caption('NARROW · 224 PX CARD',x+440,16,224,board.id)];
            for(const [row,label]of ['PREVIOUS · 16 PX BADGE / 22 PX ACTIONS','SMALLER · 14 PX BADGE / 18 PX ACTIONS','SMALLER · EXPANDED NAME FIELD'].entries()){
                const cy=76+row*96;children.push(caption(label,x+16,cy-24,648,board.id));
                for(const column of [0,1]){
                    const sourceName='X-height '+(row===2?'expanded':'collapsed')+(column?' narrow header':' wide header');
                    const sourceCard=sourceBoard.shapes.map(id=>reference.objects[id]).find(s=>s.name===sourceName),cx=x+(column?440:16);
                    const card=cloneTree(sourceCard.id,board.id,cx-sourceCard.x,cy-sourceCard.y);card.name=(row?'Smaller'+(row===2?' expanded':' collapsed'):'Previous')+(column?' narrow header':' wide header');
                    const header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header'),parts=header.shapes.map(id=>objects[id]);
                    if(row){
                        const plate=parts.find(s=>s.name==='Icon plate'),icon=parts.find(s=>s.fills?.some(f=>f['fill-image']));
                        bounds(plate,header.x+67,cy+13,14,14);bounds(icon,header.x+69,cy+15,10,10);
                        for(const name of ['Duplicate','Delete']){
                            const button=parts.find(s=>s.name===name+' button'),ox=button.x+11,oy=button.y+11;
                            bounds(button,ox-9,oy-9,18,18);
                            for(const glyph of parts.filter(s=>s.name===name)){
                                const factor=.8;
                                if(glyph.type==='path'&&glyph.content instanceof Uint8Array){const v=new DataView(glyph.content.buffer,glyph.content.byteOffset,glyph.content.byteLength);for(let o=0;o<glyph.content.byteLength;o+=28){const t=v.getInt16(o,true);for(const [a,b]of t===3?[[4,8],[12,16],[20,24]]:t===1||t===2?[[20,24]]:[]){v.setFloat32(o+a,ox+(v.getFloat32(o+a,true)-ox)*factor,true);v.setFloat32(o+b,oy+(v.getFloat32(o+b,true)-oy)*factor,true);}}}
                                bounds(glyph,ox+(glyph.x-ox)*factor,oy+(glyph.y-oy)*factor,glyph.width*factor,glyph.height*factor);
                                for(const stroke of glyph.strokes??[])if(stroke['stroke-width'])stroke['stroke-width']*=factor;
                            }
                        }
                    }
                    children.push(card);
                }
            }
            children.push(caption('X-HEIGHT ALIGNMENT PRESERVED · PROVISIONAL SIZES · RHINO MEASUREMENT PENDING',x+16,346,648,board.id));
            board.shapes=children.map(s=>s.id);objects[zero].shapes.push(board.id);
        }
    }
    if(xHeightStep){
        const reference=Object.values(pages).find(p=>p.name==='23 • Step 15 — Aligned B headers');
        const fullName='Building Retaining Walls';
        const calibrationPath=path.join(out,'xheight-calibration.json');
        const shifts=fs.existsSync(calibrationPath)?JSON.parse(fs.readFileSync(calibrationPath,'utf8')).shifts:{bold12:1,regular12:1,summary11:1};
        const probeSources=[];
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const board=Object.values(reference.objects).find(s=>s.name==='Step 15 / header alignment / '+theme);
            const sourceCards=board.shapes.map(id=>reference.objects[id]).filter(s=>s.type==='frame');
            const x=themeIndex*800,parent=newFrame('Step 16 / optical alignment / '+theme,x,0,680,382);
            parent.fills=structuredClone(board.fills);parent['applied-tokens']=structuredClone(board['applied-tokens']);
            const children=[caption('WIDE · 400 PX',x+16,16,400,parent.id),caption('NARROW · 224 PX CARD',x+440,16,224,parent.id)];
            for(const [row,label]of ['CURRENT · LINE-BOX CENTRED / 20 PX BADGE','X-HEIGHT · COLLAPSED / 16 PX BADGE','X-HEIGHT · EXPANDED / 16 PX BADGE'].entries()){
                const y=52+row*96;children.push(caption(label,x+16,y,648,parent.id));
                for(const [column,width]of [400,224].entries()){
                    const sourceName=(row===2?'Aligned expanded':'Aligned collapsed')+(column?' narrow header':' wide header');
                    const original=sourceCards.find(s=>s.name===sourceName),cx=x+(column?440:16),cy=y+24;
                    const card=cloneTree(original.id,parent.id,cx-original.x,cy-original.y),header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header'),parts=header.shapes.map(id=>objects[id]);
                    card.name=(row===0?'Line-box':row===1?'X-height collapsed':'X-height expanded')+(column?' narrow header':' wide header');
                    const title=parts.find(s=>s.type==='text'&&s.name===fullName),summary=parts.find(s=>s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-size']==='11');
                    if(row){
                        const plate=parts.find(s=>s.name==='Icon plate'),image=parts.find(s=>s.fills?.some(f=>f['fill-image']));
                        bounds(plate,header.x+66,cy+12,16,16);bounds(image,header.x+68,cy+14,12,12);
                        move(title,0,row===2?shifts.regular12:shifts.bold12);move(summary,0,shifts.summary11);
                    }
                    if(theme==='dark'&&!column){
                        const sourceHeader=original.shapes.map(id=>reference.objects[id]).find(s=>s.name==='Header');
                        if(row===0){probeSources.push({key:'bold12',label:'12 px bold name',id:sourceHeader.shapes.map(id=>reference.objects[id]).find(s=>s.type==='text'&&s.name===fullName).id,height:22});probeSources.push({key:'summary11',label:'11 px summary',id:sourceHeader.shapes.map(id=>reference.objects[id]).find(s=>s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-size']==='11').id,height:16});}
                        if(row===2)probeSources.push({key:'regular12',label:'12 px regular name',id:sourceHeader.shapes.map(id=>reference.objects[id]).find(s=>s.type==='text'&&s.name===fullName).id,height:26});
                    }
                    children.push(card);
                }
            }
            children.push(caption('X-HEIGHT ALIGNMENT · 16 PX BADGE / 12 PX GLYPH · SAME 52 PX HEIGHT',x+16,346,648,parent.id));
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
        const probes=newFrame('Step 16 / font probes',1600,0,600,232),probeChildren=[caption('NATIVE FONT X-HEIGHT PROBES',1616,16,220,probes.id),caption('LINE-BOX CENTRED',1860,16,140,probes.id),caption('X-HEIGHT TRIAL',2020,16,140,probes.id)],records=[];
        for(const [row,key]of ['bold12','regular12','summary11'].entries()){
            const source=probeSources.find(s=>s.key===key),top=40+row*60,target=top+20;
            probeChildren.push(caption(source.label,1616,top+12,210,probes.id));
            const ids=[];
            for(const column of [0,1]){
                const s=cloneTree(source.id,probes.id);setText(s,'xxxx');s.name=(column?'xheight-probe:':'linebox-probe:')+key;
                bounds(s,1600+(column?420:260),target-source.height/2+(column?shifts[key]:0),100,source.height);delete s['position-data'];probeChildren.push(s);ids.push(s.id);
            }
            records.push({key,target,beforeRegion:[260,top+2,360,top+38],afterRegion:[420,top+2,520,top+38],beforeId:ids[0],afterId:ids[1],appliedShift:shifts[key]});
        }
        probes.shapes=probeChildren.map(s=>s.id);objects[zero].shapes.push(probes.id);fs.writeFileSync(path.join(out,'font-probes.json'),JSON.stringify(records,null,2));
    }
    if(headerAlignStep){
        const reference=Object.values(pages).find(p=>p.name==='22 • Step 14 — Header control order comparison');
        const namesReference=Object.values(pages).find(p=>p.name==='13 • Step 05 — Custom card names comparison');
        const fullName='Building Retaining Walls',fullSummary='Retaining Wall · 2 objects · 1 layer';
        const circleTemplate=Object.values(allObjects).find(s=>s.type==='circle');
        const pathTemplate=Object.values(allObjects).find(s=>s.type==='path');
        const normalTypography=Object.entries(file.data.typographies).find(([,v])=>v['font-size']==='12'&&v['font-weight']==='400');
        const fit=(value,width,size)=>{const measure=s=>[...s].reduce((n,c)=>n+size*(/[ilI1'.,:;· ]/.test(c)?.28:/[MW@]/.test(c)?.86:/[A-Z]/.test(c)?.65:.52),0);if(measure(value)<=width)return value;let result=value;while(result&&measure(result+'…')>width)result=result.slice(0,-1);return result.trimEnd()+'…';};
        function centerText(s,x,y,w,h){bounds(s,x,y,w,h);delete s['position-data'];s.content['vertical-align']='center';}
        function path(name,points,color,parent,fill=false){
            const s=cloneTree(pathTemplate.id,parent);s.name=name;
            const xs=points.map(p=>p[0]),ys=points.map(p=>p[1]);bounds(s,Math.min(...xs),Math.min(...ys),Math.max(.01,Math.max(...xs)-Math.min(...xs)),Math.max(.01,Math.max(...ys)-Math.min(...ys)));
            const bytes=new Uint8Array(points.length*28),view=new DataView(bytes.buffer);points.forEach(([x,y],i)=>{view.setInt16(i*28,i?2:1,true);view.setFloat32(i*28+20,x,true);view.setFloat32(i*28+24,y,true);});s.content=bytes;
            s.fills=fill?[{'fill-color':color,'fill-opacity':1}]:[];s.strokes=fill?[]:[{...s.strokes[0],'stroke-color':color,'stroke-width':1.6}];delete s['applied-tokens'];return s;
        }
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const board=Object.values(reference.objects).find(s=>s.name==='Step 14 / header order / '+theme);
            const sourceCards=board.shapes.map(id=>reference.objects[id]).filter(s=>s.name===fullName);
            const nameBoard=Object.values(namesReference.objects).find(s=>s.name==='Step 05 / custom card names / '+theme);
            const boxed=nameBoard.shapes.map(id=>namesReference.objects[id]).find(s=>s.name===fullName);
            const boxedHeader=boxed.shapes.map(id=>namesReference.objects[id]).find(s=>s.name==='Header');
            const inputTemplate=boxedHeader.shapes.map(id=>namesReference.objects[id]).find(s=>s.name==='Input');
            const x=themeIndex*800,parent=newFrame('Step 15 / header alignment / '+theme,x,0,680,382);
            parent.fills=structuredClone(board.fills);parent['applied-tokens']=structuredClone(board['applied-tokens']);
            const children=[caption('WIDE · 400 PX',x+16,16,400,parent.id),caption('NARROW · 224 PX CARD',x+440,16,224,parent.id)];
            for(const [row,label]of ['B · BEFORE ALIGNMENT','B · ALIGNED COLLAPSED HEADER','B · ALIGNED EXPANDED NAME FIELD'].entries()){
                const y=52+row*96;children.push(caption(label,x+16,y,648,parent.id));
                for(const [column,width]of [400,224].entries()){
                    const sourceCard=sourceCards.filter(s=>s.width===width).at(-1),cx=x+(column?440:16),cy=y+24;
                    const card=cloneTree(sourceCard.id,parent.id,cx-sourceCard.x,cy-sourceCard.y);
                    card.name=(row===0?'Before':row===1?'Aligned collapsed':'Aligned expanded')+(column?' narrow header':' wide header');
                    const header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header'),parts=header.shapes.map(id=>objects[id]);
                    if(row){
                        const grip=parts.find(s=>s.type==='text'&&textOf(s.content)==='⠿'),arrow=parts.find(s=>s.type==='text'&&textOf(s.content)==='▶');
                        const checkbox=parts.find(s=>s.name==='Checkbox'),tick=parts.find(s=>s.name==='Checked');
                        const title=parts.find(s=>s.type==='text'&&s.name===fullName),summary=parts.find(s=>s.type==='text'&&s.name===fullSummary);
                        const muted=theme==='dark'?'#949494':'#6e6e6e',primary=theme==='dark'?'#e0e0e0':'#1e1e1e';
                        header.shapes=header.shapes.filter(id=>![grip.id,arrow.id,tick.id].includes(id));
                        for(const px of [10,14])for(const py of [14,19,24]){const dot=cloneTree(circleTemplate.id,header.id);bounds(dot,header.x+px,cy+py,2,2);dot.name='Drag grip dot';dot.fills=[{'fill-color':muted,'fill-opacity':1}];dot.strokes=[];delete dot['applied-tokens'];header.shapes.push(dot.id);}
                        header.shapes.push(path('Collapse arrow',[[header.x+28,cy+16],[header.x+34,cy+20],[header.x+28,cy+24],[header.x+28,cy+16]],primary,header.id,true).id);
                        bounds(checkbox,header.x+45,cy+14,12,12);
                        header.shapes.push(path('Checked',[[checkbox.x+2,checkbox.y+6],[checkbox.x+5,checkbox.y+9],[checkbox.x+10,checkbox.y+3]],primary,header.id).id);
                        if(row===2){
                            const input=cloneTree(inputTemplate.id,header.id);bounds(input,header.x+91,cy+7,width-157,26);header.shapes.splice(1,0,input.id);
                            function regular(node){if(node&&typeof node==='object'){if(node['font-weight']){node['font-weight']='400';node['font-variant-id']='regular';if(normalTypography)node['typography-ref-id']=normalTypography[0];}for(const child of node.children??[])regular(child);}}
                            regular(title.content);setText(title,fit(fullName,width-167,12));title.name=fullName;
                            centerText(title,header.x+96,cy+7,width-167,26);
                            setText(summary,'Retaining Wall');summary.name='Retaining Wall';centerText(summary,header.x+8,cy+34,header.width-16,16);
                        }else {centerText(title,header.x+91,cy+9,width-157,22);centerText(summary,header.x+8,cy+32,header.width-16,16);}
                    }
                    children.push(card);
                }
            }
            children.push(caption('B ORDER · SHARED CONTROL CENTRE · VECTOR GRIP AND ARROW · SAME 52 PX HEIGHT',x+16,346,648,parent.id));
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
    }
    if(headerOrderStep){
        const reference=Object.values(pages).find(p=>p.name==='13 • Step 05 — Custom card names comparison');
        const fullName='Building Retaining Walls',fullSummary='Retaining Wall · 2 objects · 1 layer';
        const size=s=>s.content?.children?.[0]?.children?.[0]?.['font-size'];
        const fit=(value,w,font)=>{
            const measure=text=>[...text].reduce((n,c)=>n+font*(/[ilI1'.,:;· ]/.test(c)?.28:/[MW@]/.test(c)?.86:/[A-Z]/.test(c)?.65:.52),0);
            if(measure(value)<=w)return value;let clipped=value;
            while(clipped&&measure(clipped+'…')>w)clipped=clipped.slice(0,-1);
            return clipped.trimEnd()+'…';
        };
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const referenceBoard=Object.values(reference.objects).find(s=>s.name==='Step 05 / custom card names / '+theme);
            const original=referenceBoard.shapes.map(id=>reference.objects[id]).filter(s=>s.name===fullName).at(-1);
            const x=themeIndex*800,parent=newFrame('Step 14 / header order / '+theme,x,0,680,382);
            parent.fills=structuredClone(referenceBoard.fills);parent['applied-tokens']=structuredClone(referenceBoard['applied-tokens']);
            const children=[caption('WIDE · 400 PX',x+16,16,400,parent.id),caption('NARROW · 224 PX CARD',x+440,16,224,parent.id)];
            for(const [row,variant]of ['CURRENT · grip / expand / type / enable','A · expand / enable / type ... grip at right','B · grip / expand / enable / type'].entries()){
                const y=52+row*96;children.push(caption(variant,x+16,y,648,parent.id));
                for(const [column,width]of [400,224].entries()){
                    const cx=x+(column?440:16),cy=y+24;
                    const card=cloneTree(original.id,parent.id,cx-original.x,cy-original.y),header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header');
                    bounds(card,cx,cy,width,52);bounds(header,cx+5,cy,width-5,52);
                    const accent=card.shapes.map(id=>objects[id]).find(s=>s.name==='Accent strip');bounds(accent,cx,cy,5,52);
                    const parts=header.shapes.map(id=>objects[id]),background=parts.find(s=>s.name==='Card header');bounds(background,header.x,cy,header.width,52);
                    function bundle(test,px,py){
                        const group=parts.filter(test);if(!group.length)throw Error('Missing header control bundle.');
                        const bx=Math.min(...group.map(s=>s.x)),by=Math.min(...group.map(s=>s.y));
                        for(const s of group)move(s,header.x+px-bx,cy+py-by);
                    }
                    const grip=s=>s.type==='text'&&textOf(s.content)==='⠿',arrow=s=>s.type==='text'&&textOf(s.content)==='▶';
                    const type=s=>s.name==='Icon plate'||s.fills?.some(f=>f['fill-image']),enable=s=>s.name==='Checkbox'||s.name==='Checked';
                    if(row===1){bundle(arrow,8,11);bundle(enable,30,13);bundle(type,50,10);bundle(grip,width-21,10);}
                    else {bundle(grip,7,10);bundle(arrow,24,11);bundle(enable,row===2?45:70,13);bundle(type,row===2?64:43,10);}
                    bundle(s=>s.name==='Duplicate button'||s.name==='Duplicate',width-(row===1?73:57),9);
                    bundle(s=>s.name==='Delete button'||s.name==='Delete',width-(row===1?47:31),9);
                    const title=parts.find(s=>s.type==='text'&&textOf(s.content)===fullName),summary=parts.find(s=>s.type==='text'&&size(s)==='11');
                    if(!title||!summary)throw Error('Missing name/summary in header-order example.');
                    const titleWidth=width-157;bounds(title,header.x+(row===1?78:91),cy+10,titleWidth,18);delete title['position-data'];
                    setText(title,fit(fullName,titleWidth,12));title.name=fullName;
                    bounds(summary,header.x+8,cy+32,header.width-16,16);delete summary['position-data'];setText(summary,fit(fullSummary,summary.width,11));summary.name=fullSummary;
                    children.push(card);
                }
            }
            children.push(caption('COLLAPSED HEADERS · SAME SIZE, NAME SPACE, SUMMARY AND ACTION BUTTONS',x+16,346,648,parent.id));
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
    }
    if(wordingStep){
        const reference=Object.values(pages).find(p=>p.name==='12 • Step 04 — Button alignment');
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const wrapper=Object.values(reference.objects).find(s=>s.name==='Step 04 / '+theme+' / 320');
            const panel=wrapper.shapes.map(id=>reference.objects[id]).findLast(s=>s.name==='Modifiers / '+theme+' / 320');
            const nodes=[];function collect(s){nodes.push(s);for(const id of s.shapes??[])collect(reference.objects[id]);}collect(panel);
            const textTemplate=nodes.find(s=>s.type==='text'&&textOf(s.content)==='Points');
            const inputTemplate=nodes.find(s=>s.name==='Input');
            const checkTemplate=nodes.find(s=>s.name==='Checkbox'),tickTemplate=nodes.find(s=>s.name==='Checked');
            if(!textTemplate||!inputTemplate||!checkTemplate||!tickTemplate)throw Error('Missing preserved wording templates.');
            const x=themeIndex*800,parent=newFrame('Step 10 / approved wording / '+theme,x,0,696,464);
            parent.fills=structuredClone(panel.fills);parent['applied-tokens']=structuredClone(panel['applied-tokens']);
            const children=[];
            function label(value,px,py,w){const s=cloneTree(textTemplate.id,parent.id);setText(s,value);bounds(s,px,py,w,22);delete s['position-data'];children.push(s);return s;}
            function field(value,px,py){
                const s=cloneTree(inputTemplate.id,parent.id);bounds(s,px,py,156,26);children.push(s);label(value,px+5,py+4,146);
            }
            for(const [column,approved]of [false,true].entries()){
                const cx=x+16+column*344;
                children.push(caption(approved?'APPROVED WORDING':'CURRENT WORDING',cx,16,320,parent.id));
                children.push(caption('OBJECT SOURCES',cx,52,320,parent.id));
                label(approved?'Sources':'Bindings',cx+4,82,128);label(approved?'3':'3 source refs',cx+144,82,156);
                if(approved)children.push(caption('Tooltip: 2 objects, 1 watched layer',cx+4,108,312,parent.id));
                children.push(caption('CONTOURS',cx,152,320,parent.id));
                const rows=[['Interval','Interval','1.000'],['Start Z','Base Elevation','0.000'],['Major Every Nth','Major Every','5'],['Split Major / Minor','Separate Layers',null],['Label Interval','Label Interval','0.000'],['Label Every Nth','Label Every','1']];
                for(const [i,[before,after,value]]of rows.entries()){
                    const py=182+i*36;label(approved?after:before,cx+4,py+4,136);
                    if(value===null){const dx=cx+144-checkTemplate.x,dy=py+6-checkTemplate.y;children.push(cloneTree(checkTemplate.id,parent.id,dx,dy),cloneTree(tickTemplate.id,parent.id,dx,dy));}
                    else field(value,cx+144,py);
                }
            }
            children.push(caption('WORDING ONLY · VALUES AND BEHAVIOUR UNCHANGED',x+16,426,664,parent.id));
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
    }
    if(tabIconStep){
        const names=['Modifiers','Objects','Zones','Analysis','Annotation'];
        const tips=['Edit the terrain build stack','Place and manage terrain objects','Define terrain regions','Measure and evaluate the terrain','Create terrain drawings and labels'];
        const resources=['TabModifiers','TabObjects','TabZones','TabAnalysis','TabAnnotation'];
        const pathTemplate=Object.values(source.objects).find(s=>s.type==='path');
        const rectTemplate=Object.values(source.objects).find(s=>s.type==='rect'&&s.name==='Add Modifier button');
        function newRect(name,x,y,w,h,color,parent){
            const s=cloneTree(rectTemplate.id,parent);s.name=name;bounds(s,x,y,w,h);s.fills=[{'fill-color':color,'fill-opacity':1}];s.strokes=[];delete s['applied-tokens'];return s;
        }
        function stroke(name,points,color,parent,lineWidth=1.5){
            const s=cloneTree(pathTemplate.id,parent);s.name=name;
            const xs=points.map(p=>p[0]),ys=points.map(p=>p[1]);bounds(s,Math.min(...xs),Math.min(...ys),Math.max(.01,Math.max(...xs)-Math.min(...xs)),Math.max(.01,Math.max(...ys)-Math.min(...ys)));
            const bytes=new Uint8Array(points.length*28),view=new DataView(bytes.buffer);
            points.forEach(([x,y],i)=>{view.setInt16(i*28,i?2:1,true);view.setFloat32(i*28+20,x,true);view.setFloat32(i*28+24,y,true);});
            s.content=bytes;s.fills=[];s.strokes[0]['stroke-color']=color;s.strokes[0]['stroke-width']=lineWidth;delete s['applied-tokens'];return s;
        }
        function glyph(index,x,y,color,parent,zonesTiles=false,annotationStyle='note'){
            const result=[],p=points=>result.push(stroke(names[index]+' icon',points.map(([px,py])=>[x+px,y+py]),color,parent));
            if(index===0)p([[13,1],[10,4],[12,6],[15,3],[15,6],[13,9],[10,10],[8,9],[4,14],[2,14],[1,13],[1,11],[6,7],[5,5],[6,2],[9,1],[13,1]]);
            if(index===1){p([[8,1],[14,4.5],[14,11.5],[8,15],[2,11.5],[2,4.5],[8,1]]);p([[2,4.5],[8,8],[14,4.5]]);p([[8,8],[8,15]]);}
            if(index===2){
                if(zonesTiles){result.push(newRect('Zones solid region',x+1,y+1,6,6,color,parent));p([[9,1],[15,1],[15,7],[9,7],[9,1]]);p([[5,9],[11,9],[11,15],[5,15],[5,9]]);}
                else {p([[1,2],[10,2],[10,10],[1,10],[1,2]]);p([[6,6],[14,6],[14,14],[6,14],[6,6]]);}
            }
            if(index===3){for(const [bx,by,h]of [[2,9,5],[6,5,9],[10,1,13]])result.push(newRect('Analysis icon bar',x+bx,y+by,2,h,color,parent));p([[1,15],[15,15]]);}
            if(index===4){
                if(annotationStyle==='pencil'){p([[2,11],[12,1],[15,4],[5,14],[1,15],[2,11]]);p([[10,3],[13,6]]);p([[2,11],[5,14]]);}
                else if(annotationStyle==='leader'){p([[1,10],[4,2],[7,10]]);p([[2,7],[6,7]]);p([[7,14],[11,10],[15,10]]);p([[7,11],[7,14],[10,14]]);}
                else {p([[2,2],[12,2],[14,4],[14,12],[9,12],[5,15],[5,12],[2,12],[2,2]]);p([[5,5],[11,5]]);p([[5,8],[10,8]]);}
            }
            return result;
        }
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const dark=theme==='dark',x=themeIndex*900,parent=newFrame('Step '+stepNumber+' / tab icons / '+theme,x,0,784,452);
            const canvas=dark?'#303030':'#f0f0f0',normal=dark?'#282828':'#e4e4e4',selected=dark?'#3a4e6c':'#dcecff',neutral=dark?'#c8c8c8':'#4f4f4f';
            const colors=dark?['#81a9db','#e4a36e','#b5ad75','#8dbb91','#a9a1d6']:['#456d9d','#925a30','#7d701f','#3b7547','#6b5b9d'];
            const earthColors=dark?['#c5a45c','#bf8265','#9fa96e','#7fa398','#c1b399']:['#8b6a24','#93583d','#667332','#477568','#756442'];
            parent.fills=[{'fill-color':canvas,'fill-opacity':1}];delete parent['applied-tokens'];
            const screen=Object.values(source.objects).find(s=>s.name==='Modifiers / '+theme+' / 480');
            const shell=screen.shapes.map(id=>source.objects[id]).find(s=>s.name==='Panel shell');
            const originalIcons=resources.map(name=>shell.shapes.map(id=>source.objects[id]).find(s=>s.name===name));
            const children=[caption('COMPACT · 240 PX',x+16,16,240,parent.id),caption('WIDE · 480 PX',x+288,16,480,parent.id)];
            const variants=annotationIconStep?['Previous note icon','A · Pencil / drawing','B · Label / leader']:earthTabStep?['Previous proposal','Earthy palette · revised Zones','Revised glyphs · neutral reference']:['Current icons','Single hue per category','Same glyphs · all neutral'];
            for(const [row,variant]of variants.entries()){
                const y=50+row*78;children.push(caption(variant,x+16,y,500,parent.id));
                for(const [column,width]of [240,480].entries()){
                    const bx=x+(column?288:16),by=y+25,full=width===480,gap=2,cell=(width-gap*4)/5;
                    const bar=newFrame(variant+' / '+width,bx,by,width,32,parent.id);bar.fills=[];delete bar['applied-tokens'];const content=[];
                    for(let i=0;i<5;i++){
                        const tx=bx+i*(cell+gap),hasToggle=i===2||i===3,mainWidth=cell-(hasToggle?14:0);
                        content.push(newRect(names[i]+' tab',tx,by,cell,32,i===3?selected:normal,bar.id));
                        const ix=full?tx+6:tx+(mainWidth-16)/2,iy=by+8;
                        if(row===0&&!earthTabStep){const image=cloneTree(originalIcons[i].id,bar.id);bounds(image,ix,iy,16,16);content.push(image);}
                        else content.push(...glyph(i,ix,iy,annotationIconStep?earthColors[i]:earthTabStep?(row===0?colors[i]:row===1?earthColors[i]:neutral):row===1?colors[i]:neutral,bar.id,annotationIconStep||earthTabStep&&row>0,annotationIconStep?['note','pencil','leader'][row]:'note'));
                        if(full){const label=caption(names[i],tx+27,by+9,mainWidth-29,bar.id);content.push(label);}
                        if(hasToggle){
                            const toggleName=i===2?'ZoneMeshes':'AnalysisVisible';
                            const originals=shell.shapes.map(id=>source.objects[id]).filter(s=>s.name===toggleName);
                            const minX=Math.min(...originals.map(s=>s.x)),minY=Math.min(...originals.map(s=>s.y));
                            for(const original of originals)content.push(cloneTree(original.id,bar.id,tx+cell-12-minX,by+11-minY));
                        }
                    }
                    bar.shapes=content.map(s=>s.id);children.push(bar);
                }
            }
            children.push(caption('TOOLTIPS · PLANNED TEXT, NOT LIVE HOVER BEHAVIOUR',x+16,302,752,parent.id));
            for(let i=0;i<5;i++)children.push(caption(names[i]+' — '+tips[i],x+16,330+i*20,752,parent.id));
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
    }
    if(nameExampleStep){
        const atlas=Object.values(pages).find(p=>p.name==='04 • Modifier card atlas');
        for(const [themeIndex,theme]of ['dark','light'].entries()){
            const wrapper=Object.values(atlas.objects).find(s=>s.name==='Retaining Wall • '+theme);
            if(!wrapper)throw Error('Missing Retaining Wall atlas board: '+theme);
            const wallTemplate=wrapper.shapes.map(id=>atlas.objects[id]).find(s=>s.name==='Retaining Wall');
            const wallHeader=wallTemplate.shapes.map(id=>atlas.objects[id]).find(s=>s.name==='Header');
            const wallIcon=wallHeader.shapes.map(id=>atlas.objects[id]).find(s=>s.fills?.some(f=>f['fill-image']));
            const wallAccent=wallTemplate.shapes.map(id=>atlas.objects[id]).find(s=>s.name==='Accent strip');
            const screen=Object.values(source.objects).find(s=>s.name==='Modifiers / '+theme+' / 320');
            const original=screen.shapes.map(id=>source.objects[id]).find(s=>s.name==='Grade Pad');
            const base=screen.shapes.map(id=>source.objects[id]).find(s=>s.name==='Triangulate');
            const baseHeader=base.shapes.map(id=>source.objects[id]).find(s=>s.name==='Header');
            const nameInput=baseHeader.shapes.map(id=>source.objects[id]).find(s=>s.name==='Input');
            const x=themeIndex*960,width=400,gap=24,padding=16,boardWidth=width*2+gap+padding*2;
            const parent=newFrame('Step 05 / custom card names / '+theme,x,0,boardWidth,302);
            parent.fills=structuredClone(wrapper.fills);parent['applied-tokens']=structuredClone(wrapper['applied-tokens']);
            const children=[caption('EDITABLE NAMES · CURRENT APPROACH',x+padding,16,width,parent.id),caption('PLAIN NAMES · COMPARISON ONLY',x+padding+width+gap,16,width,parent.id)];
            for(const [column,boxed]of [true,false].entries())for(const [index,name]of ['Terrain Walls','Building Retaining Walls','Kerbs'].entries()){
                const card=cloneTree(original.id,parent.id,x+padding+column*(width+gap)-original.x,56+index*72-original.y);
                card.name=name;const header=card.shapes.map(id=>objects[id]).find(s=>s.name==='Header');
                const accent=card.shapes.map(id=>objects[id]).find(s=>s.name==='Accent strip');
                card.shapes=[accent.id,header.id];bounds(card,card.x,card.y,400,header.height);bounds(accent,accent.x,accent.y,accent.width,header.height);accent.fills=structuredClone(wallAccent.fills);
                const input=cloneTree(nameInput.id,header.id);header.shapes.splice(1,0,input.id);
                // This is a header-only comparison, so discard the full card's flow layout.
                descendants(card,s=>{for(const key of Object.keys(s))if(key.startsWith('layout'))delete s[key];});
                const title=header.shapes.map(id=>objects[id]).find(s=>s.type==='text'&&textOf(s.content)==='Grade Pad');
                if(!title)throw Error('Missing editable name label.');setText(title,name);
                const subtitle=header.shapes.map(id=>objects[id]).find(s=>s.type==='text'&&s.y>title.y+18);
                if(!subtitle)throw Error('Missing card type subtitle.');setText(subtitle,'Retaining Wall');
                // Reconstruct the compact header from its source metrics rather than
                // retaining the expanded atlas instance's computed flex positions.
                bounds(header,card.x+5,card.y,card.width-5,52);
                for(const id of header.shapes){
                    const s=objects[id],hx=header.x,hy=header.y;
                    delete s['position-data'];
                    if(s.type==='path'&&['Duplicate','Delete'].includes(s.name))move(s,400-original.width,0);
                    if(s.name==='Card header')bounds(s,hx,hy,header.width,52);
                    else if(s.id===title.id)bounds(s,hx+96,hy+9,235,18);
                    else if(s.id===subtitle.id)bounds(s,hx+91,hy+32,295,18);
                    else if(s.name==='Input')bounds(s,hx+91,hy+5,243,26);
                    else if(s.name==='⠿')bounds(s,hx+7,hy+10,12,18);
                    else if(s.name==='▼'||s.name==='▶')bounds(s,hx+24,hy+11,14,18);
                    else if(s.name==='Icon plate'){bounds(s,hx+43,hy+10,20,20);s.fills=structuredClone(wallAccent.fills);}
                    else if(s.type==='rect'&&s.fills?.some(f=>f['fill-image'])){bounds(s,hx+46,hy+13,14,14);s.fills=structuredClone(wallIcon.fills);}
                    else if(s.name==='Checkbox')bounds(s,hx+70,hy+13,12,12);
                    else if(s.name==='Duplicate button')bounds(s,hx+343,hy+9,22,22);
                    else if(s.name==='Duplicate'&&s.type==='rect')bounds(s,hx+352.25,hy+14.75,7,7);
                    else if(s.name==='Delete button')bounds(s,hx+369,hy+9,22,22);
                }
                if(!boxed)header.shapes=header.shapes.filter(id=>objects[id].name!=='Input');
                children.push(card);
            }
            parent.shapes=children.map(s=>s.id);objects[zero].shapes.push(parent.id);
        }
    }
    function carryTabChoices(screen,theme){
        const names=['Modifiers','Objects','Zones','Analysis','Annotation'];
        const references=Object.values(pages).find(p=>p.name==='16 • Step 08 — Annotation icon alternatives');
        const root=Object.values(references.objects).find(s=>s.name==='Step 08 / tab icons / '+theme);
        const bar=root.shapes.map(id=>references.objects[id]).find(s=>s.name==='B · Label / leader / 480');
        const glyphs=bar.shapes.map(id=>references.objects[id]);
        const shell=screen.shapes.map(id=>objects[id]).find(s=>s.name==='Panel shell');
        const next=[];
        for(const id of shell.shapes){
            const image=objects[id],index=['TabModifiers','TabObjects','TabZones','TabAnalysis','TabAnnotation'].indexOf(image.name);
            if(index<0){next.push(id);continue;}
            const selected=glyphs.filter(s=>s.name===names[index]+' icon'||index===2&&s.name==='Zones solid region'||index===3&&s.name==='Analysis icon bar');
            if(!selected.length)throw Error('Missing approved tab glyph: '+names[index]);
            const dx=image.x-(bar.x+index*((480-8)/5+2)+6),dy=image.y-(bar.y+8);
            for(const original of selected)next.push(cloneTree(original.id,shell.id,dx,dy).id);
        }
        shell.shapes=next;
    }
    function softenBorders(screen,theme){
        const from=theme==='dark'?'#747474':'#a0a0a0',to=theme==='dark'?'#55595c':'#b7b9bd';let count=0;
        descendants(screen,s=>{
            if(s.type!=='rect'||!(s.name==='Input'||s.name==='Checkbox'||s.name.endsWith(' button')))return;
            for(const stroke of s.strokes??[])if(stroke['stroke-color']?.toLowerCase()===from){
                stroke['stroke-color']=to;delete stroke['stroke-color-ref-id'];delete stroke['stroke-color-ref-file'];
                if(s['applied-tokens'])delete s['applied-tokens']['stroke-color'];count++;
            }
        });
        if(!count)throw Error('No routine borders were softened.');
        console.log('Softened '+count+' borders in '+screen.name);
    }
    if(fullReviewStep){
        const wording={'Start Z':'Base Elevation','Major Every Nth':'Major Every','Split Major / Minor':'Separate Layers','Label Every Nth':'Label Every'};
        for(const [row,name]of ['Modifiers','Objects','Zones','Analysis','Annotation'].entries())for(const [column,[theme,width]]of [['dark',320],['dark',240],['dark',480],['light',320]].entries()){
            const original=Object.values(pages).flatMap(p=>Object.values(p.objects)).find(s=>s.name===name+' / '+theme+' / '+width&&snapshot.has(s.id));
            if(!original)throw Error('Missing original full-panel scene: '+name+'/'+theme+'/'+width);
            const info=snapshot.get(original.id),screen=cloneTree(original.id,zero,column*600-info.x,row*1700-info.y);
            if(name==='Modifiers'){updateTriangulate(screen,false,{settings:true,fixedSources:true,sourceHeading:false});bottomUp(screen);}
            carryTabChoices(screen,theme);centreButtonLabels(screen);
            descendants(screen,s=>{
                if(s.type!=='text')return;
                const value=textOf(s.content);
                if(wording[value])setText(s,wording[value]);
                else if(value==='Bindings')setText(s,'Sources');
                else if(/^\d+ source refs$/.test(value))setText(s,value.split(' ')[0]);
            });
            // Type captions use the existing second line on expanded cards only.
            // Collapsed summary/type placement stays open rather than adding height.
            for(const id of screen.shapes){
                const card=objects[id];if(card.name==='Panel shell'||!card.shapes)continue;
                const children=card.shapes.map(id=>objects[id]),header=children.find(s=>s.name==='Header');
                if(!header||!children.some(s=>s.name==='Parameters'))continue;
                const titleRows=header.shapes.map(id=>objects[id]).filter(s=>s.type==='text'&&s.y>=header.y+30&&s.height>=18);
                if(titleRows.length&&card.name!=='Triangulate'&&sourceInventory.cards.some(c=>c.name===card.name))setText(titleRows[0],card.name);
            }
            screen.name='Step 11 / '+name+' / '+theme+' / '+width;
            objects[zero].shapes.push(screen.id);
            console.log('Prepared '+screen.name);
        }
    }
    let x=0;
    for(const [width,theme,expanded]of anchoredImportStep||panelAlignmentStep||layerTransferStep||definitionTransferStep||panelWalkthroughStep||mapAnalysisStep||rampFeaturesStep||curvatureStep||gradientComplianceStep||resultsHeadingStep||waterflowStep||drainageStep||cutFillStep||earthworksCopyStep||earthworksStep||zoneSummaryStep||sectionContextStep||sectionHeadersStep||objectPlacementStep||scatterPlacementStep||scatterMixDisclosureStep||scatterCompactMixStep||scatterBlockFieldsStep||scatterControlSizesStep||scatterOrderStep||scatterLayoutStep||simplifyModesStep||displaceOriginStep||displacePlacementStep||displaceModesStep||smoothBoundariesStep||smoothStrengthStep||remeshModeFirstStep||remeshModesStep||numericDefaultsStep||inlineUnitsStep||gradePathStep||gradeLineStep||wallSlopeOverridesStep||retainingWallStep||gradeCalloutStep||legendStep||reportTableStep||modeDropdownAuditStep||spotModeSelectorStep||spotGridPlacementStep||spotHeightPlacementStep||sectionAutoSamplingStep||sectionAlongCurveStep||sectionLineStep||sectionCutStep||crossSectionComparisonStep||crossSectionExpandedAnnotationsStep||crossSectionPathsStep||crossSectionPickedPillStep||crossSectionAlignedInputsStep||crossSectionSimpleStep||contourFlushRowsStep||contourAlignedToggleStep||contourHierarchyStep||groupingCompareStep||bareTypeIconStep||borderlessActionsStep||compactControlsStep||xHeightStep||headerAlignStep||headerOrderStep||fixedHeaderStep||sourceSummaryStep||fullReviewStep||wordingStep||nameExampleStep||tabIconStep?[]:[[320,'dark',false],[240,'dark',false],[480,'dark',false],[320,'light',false],[320,'dark',true]]){
        const priorPage=softBorderStep?Object.values(pages).find(p=>p.name==='12 • Step 04 — Button alignment'):null;
        const priorBoard=priorPage?Object.values(priorPage.objects).find(s=>s.name===`Step 04 / ${theme} / ${width}${expanded?' / expanded':''}`):null;
        const original=softBorderStep?priorBoard?.shapes.map(id=>priorPage.objects[id]).findLast(s=>s.name===`Modifiers / ${theme} / ${width}`):Object.values(source.objects).find(s=>s.name===`Modifiers / ${theme} / ${width}`);
        if(!original)throw Error('Missing screen '+theme+'/'+width+'. Available: '+Object.values(source.objects).filter(s=>s.type==='frame'&&s['parent-id']===zero).map(s=>s.name).join(', '));
        const parent=newFrame(`Step ${stepNumber} / ${theme} / ${width}${expanded?' / expanded':''}`,x,0,width*2+56,original.height+72);
        parent.fills=structuredClone(original.fills);parent['applied-tokens']=structuredClone(original['applied-tokens']);
        const before=cloneTree(original.id,parent.id,x+16-original.x,48-original.y);
        before.name=original.name;
        if(settingsStep&&!softBorderStep)updateTriangulate(before,false,{settings:coreLayoutStep,fixedSources:buttonLayoutStep});
        if(buttonLayoutStep&&!softBorderStep)bottomUp(before);
        if(softBorderStep)carryTabChoices(before,theme);
        const after=cloneTree(original.id,parent.id,x+width+40-original.x,48-original.y);
        after.name=original.name;
        if(!softBorderStep)updateTriangulate(after,settingsStep?false:expanded,{settings:settingsStep,expandedSettings:expanded,fixedSources:coreLayoutStep,sourceHeading:!buttonLayoutStep});
        if(coreLayoutStep&&!softBorderStep)bottomUp(after);
        if(buttonLayoutStep&&!softBorderStep)centreButtonLabels(after);
        if(softBorderStep){carryTabChoices(after,theme);softenBorders(after,theme);}
        const labels=[caption(softBorderStep?'CURRENT BORDERS':settingsStep?'PREVIOUS STEP':'BEFORE',x+16,16,width,parent.id),caption(softBorderStep?'SOFTER BORDERS':expanded?(settingsStep?'AFTER · SETTINGS EXPANDED':'AFTER · SECTIONS EXPANDED'):'AFTER · DEFAULT',x+width+40,16,width,parent.id)];
        parent.shapes=[...labels.map(s=>s.id),before.id,after.id];
        bounds(parent,parent.x,0,parent.width,Math.max(before.height,after.height)+64);
        objects[zero].shapes.push(parent.id);x+=parent.width+80;
    }
    // Drop clones excluded from collapsed compositions, and repair frame ownership.
    const reachable=new Set();
    function visit(id,frame=zero){const s=objects[id];reachable.add(id);s['frame-id']=id===zero?zero:frame;for(const child of s.shapes??[])visit(child,s.type==='frame'?id:frame);}
    visit(zero);for(const id of Object.keys(objects))if(!reachable.has(id))delete objects[id];
    comparisonPage={id:pageId,name:pageName,objects};
    function encodeLike(value,template,key=''){
        if(transit.isBinary(template))return value;
        if(template?.tag&&Object.hasOwn(template,'rep'))return transit.tagged(template.tag,encodeLike(value,template.rep,key));
        if(transit.isUUID(template)||['id','parent-id','frame-id','page-id','component-id','component-file'].includes(key))return uuid(value);
        if(transit.isKeyword(template)||['constraints-h','constraints-v'].includes(key))return kw(value);
        if(transit.isSet(template))return transit.set(value.map(v=>typeof v==='string'?kw(v):v));
        if(Array.isArray(value))return value.map((v,i)=>encodeLike(v,template?.[i]??template?.[0],key==='shapes'?'id':''));
        if(value&&typeof value==='object'){
            return transit.map(Object.entries(value).flatMap(([k,v])=>[kw(k),encodeLike(v,transit.isMap(template)?template.get(kw(k)):undefined,k)]));
        }
        return value;
    }
    const encodedObjects=transit.map(Object.entries(objects).flatMap(([id,s])=>[uuid(id),encodeLike(s,templates.get(id))]));
    const encodedPage=map({id:uuid(pageId),name:pageName,objects:encodedObjects});
    fs.writeFileSync(path.join(out,'comparison-page.json'),JSON.stringify(comparisonPage));
    await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:file.revn,vern:file.vern,features:transit.set(features),changes:[map({type:kw('add-page'),page:encodedPage}),...componentChanges]});
    console.log('Added independent comparison page.');
}
let updatedRaw=await getFile(draft.fileId),updated=plain(updatedRaw),nativePage=updated.data['pages-index'][comparisonPage.id];
if(!nativePage)throw Error('Comparison page missing after update.');
if (scatterPlacementStep) {
    const index = nativePage.objects;
    const changes = Object.values(index).filter(shape => {
        if (shape.name !== 'Checked' || shape.hidden) return false;
        const row = index[index[shape['parent-id']]?.['parent-id']];
        return row?.shapes?.some(id => index[id].name === 'Exclusions');
    }).map(shape => map({
        type: kw('mod-obj'), 'page-id': uuid(nativePage.id), id: uuid(shape.id),
        operations: [map({type: kw('set'), attr: kw('hidden'), val: true})]
    }));
    if (changes.length) {
        await rpc('update-file', {
            id: uuid(draft.fileId), 'session-id': uuid(randomUUID()), revn: updated.revn,
            vern: updated.vern, features: transit.set(features), changes
        });
        updatedRaw = await getFile(draft.fileId);
        updated = plain(updatedRaw);
        nativePage = updated.data['pages-index'][comparisonPage.id];
    }
}
if(numericDefaultsStep){
    const components=Object.values(updated.data.components).filter(c=>c['main-instance-page']===nativePage.id&&!c.deleted);
    if(components.length!==2||components.some(c=>nativePage.objects[c['main-instance-id']]?.['component-id']!==c.id)){
        throw Error('Expected two reusable numeric field components on the defaults page.');
    }
    console.log('Verified two approved numeric field components.');
}
if(contourHierarchyStep){
    const patches=[];
    for(const s of Object.values(nativePage.objects).filter(s=>s.name==='Checked'&&s.hidden)){
        const parent=nativePage.objects[s['parent-id']];
        if(parent?.height===12&&parent.shapes?.includes(s.id))patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(s.id),operations:[map({type:kw('set'),attr:kw('hidden'),val:false})]}));
    }
    function setBounds(s,x,y,w,h){
        const rect={x,y,width:w,height:h,x1:x,y1:y,x2:x+w,y2:y+h};
        const attrs={x,y,width:w,height:h,selrect:transit.tagged('rect',map(rect)),points:[{x,y},{x:x+w,y},{x:x+w,y:y+h},{x,y:y+h}].map(p=>transit.tagged('point',map(p)))};
        patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(s.id),operations:Object.entries(attrs).map(([attr,val])=>map({type:kw('set'),attr:kw(attr),val}))}));
    }
    for(const row of Object.values(nativePage.objects).filter(s=>s.name==='Color property row'&&[191,271].includes(s.width))){
        const parts=row.shapes.map(i=>nativePage.objects[i]),title=parts.find(s=>s.type==='text'),editor=parts.find(s=>s.name==='Editor'),ex=row.x+70,ey=row.y+2,ew=row.width-70;
        setBounds(title,row.x,row.y+5,70,18);setBounds(editor,ex,ey,ew,26);
        for(const s of editor.shapes.map(i=>nativePage.objects[i])){
            if(s.name==='Color swatch')setBounds(s,ex,ey+3,18,18);
            if(s.name==='Reset button')setBounds(s,ex+26,ey,44,22);
            if(s.name==='Reset')setBounds(s,ex+28,ey+2,40,18);
            if(s.name==='Hex value')setBounds(s,ex+76,ey+4,ew-76,18);
        }
    }
    if(patches.length){await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:updated.revn,vern:updated.vern,features:transit.set(features),changes:patches});updatedRaw=await getFile(draft.fileId);updated=plain(updatedRaw);nativePage=updated.data['pages-index'][comparisonPage.id];}
}
if(xHeightStep&&fs.existsSync(path.join(out,'xheight-calibration.json'))){
    const shifts=JSON.parse(fs.readFileSync(path.join(out,'xheight-calibration.json'),'utf8')).shifts,patches=[];
    function placeText(s,y){
        const dy=y-s.y;if(Math.abs(dy)<0.001)return;
        const rect={...s.selrect,y:s.selrect.y+dy,y1:s.selrect.y1+dy,y2:s.selrect.y2+dy};
        patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(s.id),operations:[
            map({type:kw('set'),attr:kw('y'),val:y}),
            map({type:kw('set'),attr:kw('points'),val:s.points.map(p=>transit.tagged('point',map({x:p.x,y:p.y+dy})))}),
            map({type:kw('set'),attr:kw('selrect'),val:transit.tagged('rect',map(rect))})
        ]}));
    }
    for(const card of Object.values(nativePage.objects).filter(s=>s.type==='frame'&&/^X-height (collapsed|expanded) (wide|narrow) header$/.test(s.name))){
        const expanded=card.name.includes('expanded'),header=card.shapes.map(id=>nativePage.objects[id]).find(s=>s.name==='Header'),parts=header.shapes.map(id=>nativePage.objects[id]);
        const title=parts.find(s=>s.type==='text'&&s.name==='Building Retaining Walls'),summary=parts.find(s=>s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-size']==='11');
        placeText(title,card.y+(expanded?7:9)+shifts[expanded?'regular12':'bold12']);
        placeText(summary,card.y+(expanded?34:32)+shifts.summary11);
    }
    const records=JSON.parse(fs.readFileSync(path.join(out,'font-probes.json'),'utf8'));
    for(const p of records){const s=nativePage.objects[p.afterId];placeText(s,p.target-s.height/2+shifts[p.key]);p.appliedShift=shifts[p.key];}
    if(patches.length){await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:updated.revn,vern:updated.vern,features:transit.set(features),changes:patches});updatedRaw=await getFile(draft.fileId);updated=plain(updatedRaw);nativePage=updated.data['pages-index'][comparisonPage.id];}
    fs.writeFileSync(path.join(out,'font-probes.json'),JSON.stringify(records,null,2));
}
if(fullReviewStep){
    // Zones have user names, rather than modifier types. Preserve their subtitle.
    const patches=[],rawPage=updatedRaw.get(kw('data')).get(kw('pages-index')).get(uuid(nativePage.id));
    const contentText=node=>node?.text??(node?.children??[]).map(contentText).join('');
    function replaceText(value){
        if(transit.isMap(value)){const pairs=[];value.forEach((v,k)=>pairs.push(k,k.toString()===':text'&&v==='Paved'?'Later zones win ties':replaceText(v)));return transit.map(pairs);}
        if(Array.isArray(value))return value.map(replaceText);return value;
    }
    for(const screen of Object.values(nativePage.objects).filter(s=>s.name.startsWith('Step 11 / Zones / '))){
        const card=screen.shapes.map(id=>nativePage.objects[id]).find(s=>s.name==='Paved');
        const header=card.shapes.map(id=>nativePage.objects[id]).find(s=>s.name==='Header');
        for(const s of header.shapes.map(id=>nativePage.objects[id]).filter(s=>s.type==='text'&&contentText(s.content)==='Paved'&&s.y>=header.y+30&&s.height>=18)){
            const rawShape=rawPage.get(kw('objects')).get(uuid(s.id));
            patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(s.id),operations:[map({type:kw('set'),attr:kw('content'),val:replaceText((rawShape.rep??rawShape).get(kw('content')))})]}));
        }
    }
    if(patches.length){await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:updated.revn,vern:updated.vern,features:transit.set(features),changes:patches});updatedRaw=await getFile(draft.fileId);updated=plain(updatedRaw);nativePage=updated.data['pages-index'][comparisonPage.id];}
}
if(sourceSummaryStep){
    // A compact header's second title row is not a metadata row. Preserve the name.
    const reference=Object.values(pages).find(p=>p.name==='19 • Step 11 — Consolidated panel review');
    const rawReference=updatedRaw.get(kw('data')).get(kw('pages-index')).get(uuid(reference.id));
    const contentText=node=>node?.text??(node?.children??[]).map(contentText).join('');
    const isTitle=s=>s.type==='text'&&s.content?.children?.[0]?.children?.[0]?.['font-weight']==='700';
    const patches=[];
    for(const screen of Object.values(nativePage.objects).filter(s=>s.name.startsWith('Step 12 / ')&&s.name.endsWith('/ 240'))){
        const before=Object.values(reference.objects).find(s=>s.name===screen.name.replace('Step 12','Step 11'));
        for(const card of screen.shapes.map(id=>nativePage.objects[id]).filter(s=>s.name!=='Panel shell'&&s.shapes&&!s.shapes.some(id=>nativePage.objects[id].name==='Parameters'))){
            const header=card.shapes.map(id=>nativePage.objects[id]).find(s=>s.name==='Header');
            const oldCard=before.shapes.map(id=>reference.objects[id]).find(s=>s.name===card.name);
            const oldHeader=oldCard.shapes.map(id=>reference.objects[id]).find(s=>s.name==='Header');
            const title=header.shapes.map(id=>nativePage.objects[id]).find(isTitle),oldTitle=oldHeader.shapes.map(id=>reference.objects[id]).find(isTitle);
            if(!title||!oldTitle||contentText(title.content)===contentText(oldTitle.content))continue;
            const rawTitle=rawReference.get(kw('objects')).get(uuid(oldTitle.id));
            patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(title.id),operations:[map({type:kw('set'),attr:kw('content'),val:(rawTitle.rep??rawTitle).get(kw('content'))}),map({type:kw('set'),attr:kw('name'),val:oldTitle.name})]}));
        }
    }
    if(patches.length){await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:updated.revn,vern:updated.vern,features:transit.set(features),changes:patches});updatedRaw=await getFile(draft.fileId);updated=plain(updatedRaw);nativePage=updated.data['pages-index'][comparisonPage.id];console.log('Preserved '+patches.length+' compact title rows.');}
}
const issues=[];
if(headerOrderStep){
    const desired='Retaining Wall · 2 objects · 1 layer',patches=[];
    const rawPage=updatedRaw.get(kw('data')).get(kw('pages-index')).get(uuid(nativePage.id));
    const contentText=node=>node?.text??(node?.children??[]).map(contentText).join('');
    function restoreSummary(value){
        if(transit.isMap(value)){const pairs=[];value.forEach((v,k)=>pairs.push(k,k.toString()===':text'&&typeof v==='string'&&v.endsWith('…')?desired:restoreSummary(v)));return transit.map(pairs);}
        if(Array.isArray(value))return value.map(restoreSummary);return value;
    }
    for(const s of Object.values(nativePage.objects).filter(s=>s.type==='text'&&s.name===desired&&contentText(s.content).endsWith('…'))){
        const rawShape=rawPage.get(kw('objects')).get(uuid(s.id));
        patches.push(map({type:kw('mod-obj'),'page-id':uuid(nativePage.id),id:uuid(s.id),operations:[map({type:kw('set'),attr:kw('content'),val:restoreSummary((rawShape.rep??rawShape).get(kw('content')))})]}));
    }
    if(patches.length){await rpc('update-file',{id:uuid(draft.fileId),'session-id':uuid(randomUUID()),revn:updated.revn,vern:updated.vern,features:transit.set(features),changes:patches});updatedRaw=await getFile(draft.fileId);updated=plain(updatedRaw);nativePage=updated.data['pages-index'][comparisonPage.id];}
}
for(const s of Object.values(nativePage.objects)){
    if(s.id===zero)continue;const p=nativePage.objects[s['parent-id']];
    if(!p)issues.push('Missing parent: '+s.name);
    else if(p.id!==zero&&(s.x<p.x-2||s.y<p.y-2||s.x+s.width>p.x+p.width+2||s.y+s.height>p.y+p.height+2))issues.push('Outside parent: '+s.name+' '+JSON.stringify({x:s.x,y:s.y,w:s.width,h:s.height,parent:p.name,px:p.x,py:p.y,pw:p.width,ph:p.height}));
}
if(issues.length)throw Error(issues.slice(0,15).join('\n'));
const baselineAfter=plain(await rpc('get-file-stats',{id:uuid(draft.sourceFileId)}));
if(baselineBefore.revn!==baselineAfter.revn||baselineBefore['updated-at']!==baselineAfter['updated-at'])throw Error('Baseline changed during prototype work.');
const exports=[];
const exportFilter=process.argv.find(s=>s.startsWith('--export='))?.slice(9);
const cardExport=process.argv.find(s=>s.startsWith('--card='))?.slice(7),panelExport=process.argv.find(s=>s.startsWith('--panel='))?.slice(8);
const exportIds=cardExport?Object.values(nativePage.objects).filter(s=>s.type==='frame'&&s.name===cardExport&&nativePage.objects[s['parent-id']]?.name===panelExport&&nativePage.objects[s['parent-id']]?.['parent-id']===zero).map(s=>s.id):nativePage.objects[zero].shapes;
if(cardExport&&exportIds.length!==1)throw Error('Expected exactly one card in the selected panel.');
for(const id of exportIds){
    const b=nativePage.objects[id],name=b.name.replace(/[^\w-]+/g,'-');
    if(exportFilter&&!b.name.includes(exportFilter))continue;
    const resource=await request('/api/export',map({cmd:kw('export-shapes'),'profile-id':uuid(profile.id),wait:true,exports:[map({'file-id':uuid(draft.fileId),'page-id':uuid(nativePage.id),'object-id':uuid(id),type:kw('png'),suffix:'',scale:cardExport?2:1,name:b.name})]}));
    const value=plain(resource),uri=value.uri?.rep??value.uri;
    const target=new URL(uri,base);if(!['localhost','127.0.0.1'].includes(target.hostname))throw Error('Unexpected export host.');
    const response=await fetch(target,{headers:{Cookie:cookie}}),bytes=Buffer.from(await response.arrayBuffer());
    if(bytes[0]!==137||bytes[1]!==80)throw Error('Expected native PNG.');
    fs.writeFileSync(path.join(out,name+'.png'),bytes);exports.push({name:b.name,png:name+'.png',fileId:draft.fileId,pageId:nativePage.id,objectId:id});
    console.log('Rendered '+b.name);
}
if((exportFilter||cardExport)&&fs.existsSync(path.join(out,'exports.json'))){const ids=new Set(exports.map(e=>e.objectId));exports.push(...JSON.parse(fs.readFileSync(path.join(out,'exports.json'),'utf8')).filter(e=>e.fileId===draft.fileId&&!ids.has(e.objectId)));}
if(draft.currentStep&&draft.currentStep.pageId!==nativePage.id)draft.previousSteps=[...(draft.previousSteps??[]).filter(s=>s.pageId!==draft.currentStep.pageId),draft.currentStep];
draft.currentStep={name:anchoredImportStep?'Import anchored to terrain selector':panelAlignmentStep?'Full panel alignment':layerTransferStep?'Terrain layer assignments':definitionTransferStep?'Terrain definition transfer':panelWalkthroughStep?'Full panel walkthrough':mapAnalysisStep?'Slope Elevation and Aspect':rampFeaturesStep?'Color ramp features':curvatureStep?'Curvature and Landforms':gradientComplianceStep?'Gradient Compliance rules':resultsHeadingStep?'Results headings':waterflowStep?'Waterflow length limit':drainageStep?'Catchments and Ponding':cutFillStep?'Cut and Fill drawing outputs':earthworksCopyStep?'Selectable Earthworks results':earthworksStep?'Earthworks comparison modes':zoneSummaryStep?'Zone card and Last Build':sectionContextStep?'Section headers in the Objects panel':sectionHeadersStep?'Section header visibility':objectPlacementStep?'Plant and Orient actions':scatterPlacementStep?'Scatter placement modes':scatterMixDisclosureStep?'Collapsible Scatter block mix':scatterCompactMixStep?'Compact Scatter block mix':scatterBlockFieldsStep?'Scatter block fields on the shared grid':scatterControlSizesStep?'Scatter control sizes':scatterOrderStep?'Scatter where what and how':scatterLayoutStep?'Scatter systems and exclusions':simplifyModesStep?'Simplify target modes':displaceOriginStep?'Pick Displace origin':displacePlacementStep?'Displace image placement and coverage':displaceModesStep?'Displace noise and image':smoothBoundariesStep?'Smooth boundaries before settings':smoothStrengthStep?'Smooth strength first':remeshModeFirstStep?'Remesh mode first':remeshModesStep?'Remesh modes with per-option tooltips':numericDefaultsStep?'Approved numeric field components':inlineUnitsStep?'Units inside aligned fields':gradePathStep?'Grade Path variable width':gradeLineStep?'Grade Line left and right slopes':wallSlopeOverridesStep?'Retaining Wall slope overrides inherit when blank':retainingWallStep?'Retaining Wall top and toe slopes':gradeCalloutStep?'Grade Callout results and appearance':legendStep?'Legend follows terrain colouring':reportTableStep?'Report Table contents and appearance':modeDropdownAuditStep?'Mode buttons across MoleHill':spotModeSelectorStep?'Spot placement uses full-width mode buttons':spotGridPlacementStep?'On a Grid placement for Spot Heights and Spot Slope':spotHeightPlacementStep?'Spot Heights separate cards versus a Placement selector':sectionAutoSamplingStep?'Section Along Curve automatic and fixed sampling':sectionAlongCurveStep?'Section Along Curve follows the simplified annotation workflow':sectionLineStep?'Section Line input with directly visible section marks':sectionCutStep?'Section Cut follows the simplified annotation workflow':crossSectionComparisonStep?'Optional cross-section comparison with shading enabled':crossSectionExpandedAnnotationsStep?'Cross-section plan and section annotation fully expanded':crossSectionPathsStep?'Cross-section curve input labelled Paths':crossSectionPickedPillStep?'Insertion point uses picked pill and clear action':crossSectionAlignedInputsStep?'Cross-section controls aligned to shared right edge':crossSectionSimpleStep?'Simple cross-section workflow and insertion point':contourFlushRowsStep?'Contour settings aligned on one property grid':contourAlignedToggleStep?'Contour Labels toggle aligned with modifier property rows':contourHierarchyStep?'Contour hierarchy with Labels enable revealing settings':groupingCompareStep?'Current versus Blender-style header grouping':bareTypeIconStep?'Card type icon without background plate':borderlessActionsStep?'Borderless duplicate and delete actions':compactControlsStep?'Smaller type badge and header actions':xHeightStep?'X-height alignment and 16 px type badge':headerAlignStep?'B header order with shared vertical alignment':headerOrderStep?'Header control order alternatives':fixedHeaderStep?'Fixed two-line collapsed header trial':sourceSummaryStep?'Compact source counts and typed source summaries':fullReviewStep?'Consolidated full-panel review':wordingStep?'Approved source and contour wording':softBorderStep?'Softer routine borders — coloured strips preserved':annotationIconStep?'Annotation icon alternatives — pencil or label':earthTabStep?'Earthy tab colours and three-region Zones icon':tabIconStep?'Tab icon comparison — candidate colours':nameExampleStep?'Custom card names comparison — not a design change':buttonLayoutStep?'Centred button labels and direct core inputs':coreLayoutStep?'Fixed sources and bottom-to-top stack':settingsStep?'Triangulate settings':'Triangulate source groups',pageId:nativePage.id,revision:updated.revn,baselineUnchanged:true,renderedBoards:exports.length};
draft.workspaceUrl=base+'/#/workspace?team-id='+draft.teamId+'&file-id='+draft.fileId+'&page-id='+nativePage.id;
fs.writeFileSync(draftPath,JSON.stringify(draft,null,2));
fs.writeFileSync(path.join(out,'exports.json'),JSON.stringify(exports,null,2));
console.log(draft.workspaceUrl);
