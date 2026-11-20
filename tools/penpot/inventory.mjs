import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';

export const root = path.resolve(import.meta.dirname, '../..');
const registry = path.join(root, 'src/MoleHill.Rhino/Registry');
export const read = p => fs.readFileSync(path.join(root, p), 'utf8').replace(/^\uFEFF/, '');
const clean = s => s.replace(/\/\/[^\n]*/g, '').replace(/\/\*[\s\S]*?\*\//g, '');
const literal = s => /^\s*"((?:\\.|[^"\\])*)"/.exec(s)?.[1]?.replace(/\\"/g, '"');

// Split C# arguments without splitting tuple lists, lambdas, or quoted help text.
function argumentsAt(source, start) {
    let depth = 1, quote = false, escape = false, last = start, args = [];
    for (let i = start; i < source.length; i++) {
        const c = source[i];
        if (quote) { if (escape) escape = false; else if (c === '\\') escape = true; else if (c === '"') quote = false; continue; }
        if (c === '"') { quote = true; continue; }
        if ('([{'.includes(c)) depth++;
        if (')]}'.includes(c)) depth--;
        if (!depth) { args.push(source.slice(last, i).trim()); return { args, end: i }; }
        if (c === ',' && depth === 1) { args.push(source.slice(last, i).trim()); last = i + 1; }
    }
    throw new Error('Unclosed schema factory call');
}

export function rowsFrom(source, file) {
    const stripped = clean(source), rows = [];
    for (const m of stripped.matchAll(/(?:Modifier|Object|Analysis|Annotation)Param\.(\w+)\s*\(/g)) {
        const { args } = argumentsAt(stripped, m.index + m[0].length);
        if (m[1] === 'ColorRamp') {
            rows.push({ type: 'ColorRamp', key: 'colorRamp', label: 'Color ramp', source: file });
            continue;
        }
        const key = literal(args[0]), label = literal(args[1]);
        if (key === undefined || label === undefined) continue;
        const row = { type: m[1], key, label, source: file };
        row.conditional = /visibleWhen\s*:/.test(args.join(','));
        row.unit = /unit\s*:\s*ParameterUnit\.(\w+)/.exec(args.join(','))?.[1] ?? (/Slope/.test(m[1]) ? 'Slope' : 'None');
        row.decimalPlaces = +( /decimalPlaces\s*:\s*(\d+)/.exec(args.join(','))?.[1] ?? 3);
        row.minimum = /\bmin(?:Value)?\s*:\s*(-?[\d.]+)/.exec(args.join(','))?.[1];
        const optionArg = args[2] ?? '';
        let optionSource = optionArg;
        if (/^\w+$/.test(optionArg)) {
            const start = stripped.indexOf(optionArg + ' =');
            if (start >= 0) optionSource = stripped.slice(start, stripped.indexOf(';', start));
        }
        row.options = [...optionSource.matchAll(/\([^,\n]+,\s*"([^"]+)"\s*\)/g)].map(x => x[1]);
        row.help = args.slice(2).filter(a => /^"/.test(a)).map(a => [...a.matchAll(/"((?:\\.|[^"\\])*)"/g)].map(m => m[1]).join('')).join(' ');
        row.declaration = m[0] + args.join(', ') + ')';
        rows.push(row);
    }
    return rows.filter((r, i, all) => all.findIndex(s => s.key === r.key) === i);
}

export function inventory() {
    const cards = [];
    const common = rowsFrom(read('src/MoleHill.Rhino/Registry/ObjectParameterCatalog.cs'), 'src/MoleHill.Rhino/Registry/ObjectParameterCatalog.cs');
    const peel = rowsFrom(read('src/MoleHill.Rhino/Registry/GeometryInputParameterCatalog.cs'), 'src/MoleHill.Rhino/Registry/GeometryInputParameterCatalog.cs');
    for (const filename of fs.readdirSync(registry).filter(f => f.endsWith('.cs'))) {
        const file = 'src/MoleHill.Rhino/Registry/' + filename, source = read(file);
        const classes = [...source.matchAll(/internal sealed class (\w+)\s*:\s*(Modifier|Object|Analysis|Annotation)TypeDescriptor/g)];
        for (let i = 0; i < classes.length; i++) {
            const m = classes[i], body = source.slice(m.index, classes[i + 1]?.index ?? source.length);
            const property = name => new RegExp('(?:string\\??) ' + name + '\\s*=>\\s*"([^"]+)"').exec(body)?.[1];
            const name = property('DisplayName') ?? property('TypeLabel');
            if (!name) throw new Error('Missing descriptor name: ' + m[1]);
            let rows = rowsFrom(body, file);
            if (m[2] === 'Object' && m[1] !== 'ScatterObjectDescriptor') rows = [...common];
            if (body.includes('GeometryInputParameterCatalog.BoundaryPeel')) rows.push(...peel);
            if (body.includes('AnnotationParameterCatalog.SlopeUnitChoice')) rows.push({type:'Choice',key:'Unit',label:'Units',options:['Percent','Promille','Ratio','Degrees'],source:file});
            if (body.includes('AnnotationParameterCatalog.VerticalExaggeration')) rows.push({type:'Number',key:'VerticalExaggeration',label:'Vertical Exaggeration',source:file});
            if (body.includes('AnnotationParameterCatalog.SectionLabelling')) rows.push(...rowsFrom(source.slice(0, classes[0].index), file).filter(r => ['ShowElevationLabels','ShowSectionTitle','ShowPlanLabels'].includes(r.key)));
            if (body.includes('AnnotationParameterCatalog.BlockAttributeTail')) rows.push(...rowsFrom(source.slice(source.indexOf('public static IEnumerable<AnnotationParam> BlockAttributeTail'), classes[0].index), file));
            if (body.includes('ObjectParameterCatalog.CommonTransforms')) rows.push(...common.filter(r=>r.key.startsWith('random')||r.key==='zOffset'));
            const argb = /AccentArgb\s*=>[^;]*0xFF([A-Fa-f\d]{6})/.exec(body)?.[1];
            const definitionType=/DefinitionType\s*=>\s*typeof\((\w+)\)/.exec(body)?.[1];
            cards.push({className:m[1],definitionType,family:m[2],kind:property('Kind'),name,subtitle:property('Subtitle') ?? name,icon:property('IconName'),accent:argb ? '#' + argb : null,rows,source:file,sourceHash:crypto.createHash('sha256').update(source).digest('hex')});
        }
    }
    const modifierSource = read('src/MoleHill.Rhino/UI/MoleHillPanel.Modifiers.cs');
    const accents = Object.fromEntries([...modifierSource.matchAll(/"([\w-]+)"\s*=> Color.FromArgb\((\d+),\s*(\d+),\s*(\d+)\)/g)].map(m => [m[1], '#' + m.slice(2).map(n => (+n).toString(16).padStart(2, '0')).join('')]));
    const models=new Map();
    for(const filename of fs.readdirSync(path.join(root,'src/MoleHill.Rhino/Model')).filter(f=>f.endsWith('.cs'))){
        const source=clean(read('src/MoleHill.Rhino/Model/'+filename));
        for(const m of source.matchAll(/(?:public|internal)\s+(?:sealed\s+|abstract\s+)?class\s+(\w+)(?:\s*:\s*(\w+))?/g))models.set(m[1],{source,base:m[2]});
    }
    function modelValue(type,key,seen=new Set()){
        if(!type||seen.has(type))return undefined;seen.add(type);
        const model=models.get(type);if(!model)return undefined;
        const m=new RegExp('public\\s+(?:virtual\\s+)?(double\\??|int\\??|bool|string\\??)\\s+'+key+'\\s*\\{[^}]*\\}\\s*(?:=\\s*([^;]+);)?').exec(model.source);
        if(m){const expression=m[2]?.trim();if(expression==='true'||expression==='false')return expression==='true';if(expression==='null'||(!expression&&m[1].endsWith('?')))return null;if(expression&&/^[-+\d.eE]+[df]?$/i.test(expression))return Number(expression.replace(/[df]$/i,''));if(expression?.startsWith('"'))return literal(expression);if(!expression)return m[1]==='bool'?false:m[1].includes('string')?'':0;}
        return modelValue(model.base,key,seen);
    }
    for (const card of cards){
        card.accent ??= accents[card.kind] ?? '#787878';
        card.rows=card.rows.map(r=>{
            const property=/=>\s*(?:\(\(\w+\)\w+\)|\w+)\.(\w+)/.exec(r.declaration??'')?.[1]??r.key;
            const defaultValue=modelValue(card.definitionType,property);
            return {...r,...(defaultValue!==undefined?{defaultValue}:{})};
        });
    }
    return cards;
}

export function themeInventory() {
    const text = read('src/MoleHill.Rhino/UI/UiTheme.cs');
    return Object.fromEntries([...text.matchAll(/public static Color (\w+)\s*=> IsDark \? Color.FromArgb\(([^)]+)\)\s*:\s*Color.FromArgb\(([^)]+)\)/g)].map(m => {
        // Eto's four-argument overload is red, green, blue, alpha (unlike System.Drawing).
        const parts=s=>s.split(',').map(Number),hex=s=>'#'+parts(s).slice(0,3).map(x=>x.toString(16).padStart(2,'0')).join('');
        return [m[1], {dark:hex(m[2]),light:hex(m[3]),darkOpacity:(parts(m[2])[3]??255)/255,lightOpacity:(parts(m[3])[3]??255)/255}];
    }));
}
