// Source-backed secondary surfaces. Example document contents are deliberately fictional.
export function buildDialogs(api){
    const {page,board,themes,group,rect,text,txt,wrap,row,button,field,checkbox,line,read}=api;
    for(const theme of ['dark','light']){
        const t=themes[theme],p=page('07 • Dialogs • '+theme);let nextX=0;
        const begin=(name,w)=>{const a=[rect('Title bar',0,0,w,32,t.HeaderBackground)];text(a,name,12,7,w-24,t,{bold:true});return {a,w,y:48,name};};
        const add=(d,r)=>{const v=row(r,16,d.y,d.w-32,t);d.a.push(...v.children);d.y+=v.h+4;};
        const note=(d,s)=>{d.y+=wrap(d.a,s,16,d.y,d.w-32,t)+8;};
        const actions=(d,labels)=>{let x=d.w-16-labels.reduce((n,s)=>n+Math.max(60,s.length*6.5+14)+6,0);for(const s of labels){const w=Math.max(60,s.length*6.5+14);d.a.push(...button(s,x,d.y,w,t));x+=w+6;}d.y+=30;};
        const finish=(d,min=0)=>{const b=board(p,d.name,nextX,0,d.w,Math.max(min,d.y+12),d.a,t.canvas);b.theme=theme;nextX+=d.w+64;return b;};
        const grid=(d,headers,rows,height=240)=>{
            const x=16,y=d.y,w=d.w-32,cw=w/headers.length;d.a.push(rect('Grid',x,y,w,height,t.InputBackground,t.border),rect('Grid header',x,y,w,26,t.HeaderBackground));
            headers.forEach((s,i)=>text(d.a,s,x+6+cw*i,y+5,cw-12,t,{bold:true,size:11}));
            rows.forEach((r,j)=>{if(j%2===0)d.a.push(rect('Alternate row',x,y+27+j*28,w,28,t.CardBackground));r.forEach((s,i)=>text(d.a,s,x+6+cw*i,y+32+j*28,cw-12,t,{size:11}));});d.y+=height+8;
        };

        let d=begin('MoleHill Layer Templates',780);
        note(d,'The layers MoleHill creates, how they look, and which output lands on each. Appearance is applied when a layer is first created; after that the layer is yours and your edits in Rhino’s Layers panel stick.');
        add(d,{type:'Choice',label:'Template:',value:'MoleHill'});actions(d,['New','Rename','Delete']);
        grid(d,['Layer','Print','Weight','Receives'],[['▾ MoleHill {terrain}','■','0.18',''],['   ▾ Output','■','0.18',''],['      Terrain','■','0.18','Terrain'],['      ▾ Annotation','■','0.13','Annotation'],['         Contours','■','0.13','Contours'],['         Sections','■','0.18','Sections']],224);
        actions(d,['+ Child','+ Sibling','Delete Layer']);
        for(const r of [{type:'ReadOnly',label:'Selected:',value:'MoleHill {terrain}::Output::Annotation'},{type:'Color',label:'Color:',value:'#808080',actionLabel:'Pick',hexText:'#808080'},{type:'Color',label:'Print color:',value:'#808080',actionLabel:'Pick',hexText:'#808080'},{type:'Number',label:'Plot weight:',value:'0.13'},{type:'Action',label:'Receives:',value:'Annotation'},{type:'Choice',label:'Annotation style:',value:'MoleHill'}])add(d,r);
        actions(d,['All roles listed']);actions(d,['Import…','Export…','Reset To Defaults']);actions(d,['Open Settings Folder','Save','Cancel']);finish(d,600);

        d=begin('Layer Receives • role checklist',600);note(d,'Tick everything that should be drawn on this layer. A role moves from its previous layer when selected here.');
        grid(d,['Selected','Output','Currently on'],[['☑','Annotation','Output::Annotation'],['☑','Contours','Output::Annotation'],['☐','Terrain','Output::Terrain'],['☐','Sections','Output::Sections']],240);actions(d,['OK','Cancel']);finish(d,420);

        d=begin('MoleHill Field Codes',820);note(d,'A rule says what a surveyed code means. Layer is relative to the survey’s own root; a blank layer follows the role’s input layer.');
        grid(d,['Code','Role','Layer','Closed','Note'],[['SPOT','Spots','','No','Spot height'],['BL','Breaklines','','No','Breakline'],['BDY','Boundary','','Yes','Site extent']],300);actions(d,['Add','Remove','Restore Defaults','Import…','Export…']);
        add(d,{type:'Rule',label:'Markers'});note(d,'How this office marks a run. Several spellings per box, separated by commas.');
        for(const [label,value]of [['Start','ST'],['End','END'],['Arc','ARC'],['Close','CLOSE'],['Continues a run','+'],['Unmatched codes go to','Unmatched']])add(d,{type:'Text',label,value});
        actions(d,['Save','Cancel']);finish(d,560);

        d=begin('Import Survey Points',760);text(d.a,'example-survey.csv',16,d.y,728,t,{bold:true});d.y+=28;
        // The original dialog keeps these controls in two compact horizontal rows.
        text(d.a,'Format',16,d.y+5,44,t);d.a.push(...field('PNEZD',66,d.y,166,t,{dropdown:true}));text(d.a,'Separator',246,d.y+5,60,t);d.a.push(...field('Comma',310,d.y,142,t,{dropdown:true}));text(d.a,'Header rows',466,d.y+5,80,t);d.a.push(...field('1',552,d.y,72,t,{stepper:true}));d.y+=36;
        text(d.a,'File units',16,d.y+5,64,t);d.a.push(...field('Metres',86,d.y,166,t,{dropdown:true}));text(d.a,'Vertical offset (m)',270,d.y+5,132,t);d.a.push(...field('0.000',408,d.y,120,t,{stepper:true}));d.y+=40;
        note(d,'The vertical offset reconciles a delivered datum with this document’s. Check that eastings are in X and northings in Y before importing.');
        grid(d,['Point','X','Y','Z','Code'],Array.from({length:7},(_,i)=>[String(i+1),(1000+i*3.5).toFixed(3),(2000+i*2.8).toFixed(3),(12+i*.1).toFixed(3),'SPOT']),260);note(d,'Example preview data · 7 valid rows');actions(d,['Import','Cancel']);finish(d,600);

        for(const annotation of [false,true]){
            d=begin(annotation?'Choose Annotation Block':'Add Blocks to Scatter',380);note(d,annotation?'Select the block drawn at each label.':'Select one or more blocks to scatter.');d.a.push(...field('Filter blocks…',16,d.y,348,t));d.y+=36;
            const start=d.y;d.a.push(rect('Block list',16,start,348,310,t.InputBackground,t.border));
            for(const [i,label]of (annotation?['Built-in spot height','Built-in slope arrow','Office label']:['Tree — example','Shrub — example','Grass — example']).entries()){
                if(i===0)d.a.push(rect('Selected block',17,start+1+i*44,346,44,t.ListSelectionBackground));
                d.a.push(rect('Thumbnail placeholder',24,start+5+i*44,34,34,t.PillBackground));text(d.a,label,70,start+14+i*44,280,t);
            }
            d.y+=322;actions(d,[annotation?'Use Block':'Add Selected','Cancel']);finish(d,480);
        }

        d=begin('Add Zones From Layers',420);note(d,'Pick layers to turn into zones. Each checked layer becomes one zone.');d.a.push(...field('Find a layer...',16,d.y,388,t));d.y+=38;
        for(const [i,label]of ['Landscape::Paved  (already a zone)','Landscape::Soft','Landscape::Planting','Landscape::Paths'].entries()){d.a.push(rect('Layer colour',16,d.y+5,12,12,['#999999','#43a047','#1e88e5','#e67e22'][i]),...checkbox(label,38,d.y,t,i<2));d.y+=32;}
        d.y=Math.max(d.y,385);actions(d,['Add Zones','Cancel']);finish(d,440);

        d=begin('Validate Terrain Inputs',500);note(d,'Selected terrain inputs: 12. Checked operations are applied in a fixed order. Surviving objects retain their Rhino settings and object IDs.');
        for(const label of ['Remove duplicate/coincident objects','Join near endpoints (same layer only)','Collapse short segments','Clean duplicate curve vertices']){d.a.push(...checkbox(label,16,d.y,t,true));d.y+=30;}
        add(d,{type:'Number',label:'Tolerance:',value:'0.001'});note(d,'Example preview: 12 objects selected · ready to validate');actions(d,['Preview','Cancel','Apply']);finish(d,310);

        d=begin('Slow Build Warning',464);note(d,'This terrain may take a while to rebuild. Continue with the requested build?');d.a.push(...checkbox('Don’t show this warning again for this terrain',16,d.y,t,false));d.y+=36;actions(d,['Cancel','Continue']);finish(d,174);

        for(const scan of [false,true]){
            d=begin(scan?'Scan to Terrain':'Terrain to Curves',660);note(d,scan?'Scan — example source mesh':'Proposed Terrain — example final mesh');note(d,'Adjust settings to update the preview. The views stay free to pan, zoom and rotate. Keep writes the layers; Cancel discards them.');
            if(scan){add(d,{type:'Bool',label:'Show source',checked:true});add(d,{type:'Rule',label:'Outputs'});for(const label of ['Walls and terrace edges','Paved and soft boundaries','Kerbs and paving edges','Building context','Ground check mesh'])add(d,{type:'Bool',label,checked:label!=='Ground check mesh'});}
            else add(d,{type:'Bool',label:'Preview reference mesh',checked:false});
            add(d,{type:'Rule',label:'Horizontal contours and spot heights'});add(d,{type:'Bool',label:'Contours and spots',checked:true});
            add(d,{type:'Number',label:scan?'Terrain accuracy · m':'Vertical tolerance · m',value:scan?'0.250':'0.100'});
            if(!scan)add(d,{type:'Number',label:'Plan tolerance · m',value:'0.250'});
            add(d,{type:'Number',label:'Contour interval · m',value:scan?'0.500':'0.250'});
            if(scan)add(d,{type:'Number',label:'Paved accuracy · m',value:'0.500'});
            else {add(d,{type:'Bool',label:'Preview creases',checked:true});add(d,{type:'Number',label:'Recovery inputs per hectare',value:'1000'});}
            if(scan)add(d,{type:'Action',label:'Advanced',value:'Expand…'});
            note(d,'Ready to recompute · example settings in metres');actions(d,['Recompute','Cancel','Keep']);finish(d,scan?790:460);
        }

        d=begin('Scan to Terrain • Advanced settings',660);
        const advanced=read('src/MoleHill.Rhino/UI/TerrainCurveExtractionPanel.Scan.cs'),settings=read('src/MoleHill.Rhino/Services/Commands/ScanToTerrainSettings.cs');
        for(const match of advanced.matchAll(/Section\(advanced, "([^"]+)"\)|Number\(\w+, "([^"]+)"[^\n]*nameof\(settings\.(\w+)\)/g)){
            if(match[1])add(d,{type:'Rule',label:match[1]});else{
                const label=match[2]+(match[2].endsWith('· ')?(match[0].includes('"²"')?'m²':'m'):''),key=match[3];
                const value=new RegExp('nameof\\('+key+'\\),\\s*([\\d.]+)').exec(settings)?.[1]??new RegExp(key+'\\s*\\{[^}]*\\}\\s*=\\s*([\\d.]+)').exec(settings)?.[1]??({BuildingArea:'20',SurfaceArea:'10'})[key];
                add(d,{type:'Number',label,value:value?Number(value).toFixed(3):'0.000'});
            }
        }
        add(d,{type:'Slope',label:'Ground slope allowance',value:'60%'});add(d,{type:'Bool',label:'Preview classification samples',checked:false});note(d,'Full scroll content · settings use document units in the running panel');actions(d,['Recompute','Cancel','Keep']);finish(d);

        d=begin('Inspect Curve',430);actions(d,['Pick curve','Close']);add(d,{type:'Rule',label:'ELEVATION PROFILE'});add(d,{type:'Choice',label:'Metric',value:'Grade'});
        const py=d.y;d.a.push(rect('Profile chart',16,py,398,130,t.InputBackground,t.border));
        for(let i=0;i<5;i++)d.a.push(line('Chart grid',[[24,py+18+i*22],[406,py+18+i*22]],t.RampDivider));
        d.a.push(line('Example terrain profile',[[24,py+100],[82,py+80],[142,py+90],[205,py+44],[268,py+36],[340,py+54],[406,py+22]],'#43a047',2));d.y+=144;note(d,'Example profile data · station along the selected curve');
        add(d,{type:'Rule',label:'CHECKS'});note(d,'Example checks: grade, radius and terrain clearance');actions(d,['▶ Rule limits']);add(d,{type:'Rule',label:'MEASUREMENTS'});add(d,{type:'ReadOnly',label:'Plan length',value:'100.00 m'});add(d,{type:'ReadOnly',label:'Elevation',value:'12.00 / 15.50 m'});add(d,{type:'Rule',label:'EVENTS'});note(d,'No profile events.');add(d,{type:'Rule',label:'DISPLAY'});
        for(const label of ['Ribbon','Labels','Events','Terrain']){d.a.push(...checkbox(label,16,d.y,t,true));d.y+=26;}add(d,{type:'Choice',label:'Label content',value:'Elevation'});actions(d,['Label']);finish(d,640);

        d=begin('Relax Route',300);note(d,'as your sketch');d.a.push(line('Example score history',[[16,d.y+24],[70,d.y+16],[120,d.y+21],[180,d.y+8],[284,d.y+5]],t.PrimaryText,1.5));d.y+=44;
        for(const r of [{type:'Slope',label:'Max grade',value:'8%'},{type:'Number',label:'Min radius',value:'0.000'},{type:'Number',label:'Corridor width',value:'4.000'}])add(d,r);
        add(d,{type:'Rule',label:'Balance'});const ty=d.y;d.a.push(line('Balance triangle',[[26,ty+130],[150,ty+10],[274,ty+130],[26,ty+130]],t.MutedText,1.5));text(d.a,'Distance',18,ty+134,70,t);text(d.a,'Grade',128,ty-5,60,t);text(d.a,'Earthworks',208,ty+134,76,t);d.y+=164;
        add(d,{type:'Slider',label:'Keep to sketch',value:'1.0'});add(d,{type:'Slider',label:'Plainness',value:'0.5'});actions(d,['Help me find a route']);actions(d,['Release pins','Back to sketch']);add(d,{type:'Action',label:'Layer',value:'Current layer'});note(d,'Example figures · route preview stays in the viewport');actions(d,['Keep','Cancel']);finish(d);
    }
}
