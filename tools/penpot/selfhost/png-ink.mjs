// Read-only pixel measurements for native Penpot QA exports. No image editing.
import fs from 'node:fs';
import {inflateSync} from 'node:zlib';
export function readPng(filename){
    const bytes=fs.readFileSync(filename),width=bytes.readUInt32BE(16),height=bytes.readUInt32BE(20),depth=bytes[24],type=bytes[25];
    if(bytes.readUInt32BE(0)!==0x89504e47||depth!==8||![2,6].includes(type)||bytes[28]!==0)throw Error('Expected an 8-bit RGB/RGBA noninterlaced PNG.');
    const channels=type===6?4:3,chunks=[];
    for(let offset=8;offset<bytes.length;){const length=bytes.readUInt32BE(offset),kind=bytes.toString('ascii',offset+4,offset+8);if(kind==='IDAT')chunks.push(bytes.subarray(offset+8,offset+8+length));offset+=length+12;}
    const raw=inflateSync(Buffer.concat(chunks)),stride=width*channels,pixels=Buffer.alloc(height*stride);
    const paeth=(a,b,c)=>{const p=a+b-c,da=Math.abs(p-a),db=Math.abs(p-b),dc=Math.abs(p-c);return da<=db&&da<=dc?a:db<=dc?b:c;};
    for(let y=0;y<height;y++){
        const filter=raw[y*(stride+1)];if(filter>4)throw Error('Invalid PNG filter.');
        for(let x=0;x<stride;x++){
            const a=x>=channels?pixels[y*stride+x-channels]:0,b=y?pixels[(y-1)*stride+x]:0,c=y&&x>=channels?pixels[(y-1)*stride+x-channels]:0;
            const add=filter===1?a:filter===2?b:filter===3?Math.floor((a+b)/2):filter===4?paeth(a,b,c):0;
            pixels[y*stride+x]=(raw[y*(stride+1)+1+x]+add)&255;
        }
    }
    return {width,height,pixel:(x,y)=>[...pixels.subarray((y*width+x)*channels,(y*width+x)*channels+3)]};
}
export function inkBounds(image,[x0,y0,x1,y1],threshold=80){
    let minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity;
    for(let y=Math.max(0,y0);y<Math.min(y1,image.height);y++)for(let x=Math.max(0,x0);x<Math.min(x1,image.width);x++){
        if(Math.min(...image.pixel(x,y))<=threshold)continue;
        minX=Math.min(minX,x);minY=Math.min(minY,y);maxX=Math.max(maxX,x);maxY=Math.max(maxY,y);
    }
    return minX===Infinity?null:{minX,minY,maxX,maxY,centerX:(minX+maxX+1)/2,centerY:(minY+maxY+1)/2};
}
