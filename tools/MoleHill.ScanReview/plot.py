import json, pathlib, sys, numpy as np
from PIL import Image,ImageDraw,ImageFont
folder=pathlib.Path(sys.argv[1]);d=json.loads((folder/'terrain.json').read_text());photo=Image.open(folder/'orthophoto.ppm').convert('RGB')
def pts(c):return np.array(c['PointsXyz']).reshape(-1,3)
def metric(c):
 p=pts(c);length=np.linalg.norm(np.diff(p[:,:2],axis=0),axis=1).sum();pieces=c['Pieces'];short=0
 for k in pieces:
  a=np.array(k['ControlPoints']).reshape(-1,3);short+=bool(np.linalg.norm(a[-1,:2]-a[0,:2])<1)
 return dict(length=float(length),controls=c['ControlPointCount'],pieces=len(pieces),short=short)
rows=[dict(index=i,**metric(w['Top']),height=w['MeanHeight']) for i,w in enumerate(d['Walls'])]
summary={'Walls':rows}
for name,cs in [('wall rails',[w['Top'] for w in d['Walls']]+[w['Toe'] for w in d['Walls']]),('road edges',[w['Bottom'] for w in d['Roads']]),('area boundaries',[w['Curve'] for w in d['Areas']])]:
 ms=[metric(c) for c in cs];summary[name]={'count':len(ms),'controls':sum(x['controls'] for x in ms),'median controls':float(np.median([x['controls'] for x in ms])),'maximum controls':max(x['controls'] for x in ms),'pieces':sum(x['pieces'] for x in ms),'sub-metre pieces':sum(x['short'] for x in ms)}
print(json.dumps(summary,indent=2));(folder/'quality.json').write_text(json.dumps(summary,indent=2))
font=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',20);small=ImageFont.truetype('C:/Windows/Fonts/arial.ttf',16)
def render(bounds,width,height,controls=False,wall=None):
 x0,y0,x1,y1=bounds;scale=min(width/(x1-x0),height/(y1-y0));ww=int((x1-x0)*scale);hh=int((y1-y0)*scale)
 def xy(p):return ((p[0]-x0)*scale,(y1-p[1])*scale)
 box=((x0-d['OriginX'])/d['CellSize'],d['Rows']-1-(y1-d['OriginY'])/d['CellSize'],(x1-d['OriginX'])/d['CellSize'],d['Rows']-1-(y0-d['OriginY'])/d['CellSize'])
 im=photo.transform((ww,hh),Image.Transform.EXTENT,box,Image.Resampling.BILINEAR);draw=ImageDraw.Draw(im)
 def curve(c,col,lw=2):
  p=[xy(p) for p in pts(c)];draw.line(p,fill=col,width=lw)
  if controls:
   for piece in c['Pieces']:
    for p in np.array(piece['ControlPoints']).reshape(-1,3):
     x,y=xy(p);draw.ellipse((x-2,y-2,x+2,y+2),fill=col)
 if wall is None:
  for a in d['Areas']:curve(a['Curve'],'#a1c879',1)
  for r in d['Roads']:curve(r['Bottom'],'#00eaff' if r['IsKerb'] else '#e150ff',2)
  for i,w in enumerate(d['Walls']):
   curve(w['Top'],'#ff3300');curve(w['Toe'],'#00eaff');p=pts(w['Top']);draw.text(xy(p[len(p)//2]),str(i),fill='white',font=small,stroke_width=2,stroke_fill='black')
 else:
  curve(wall['Top'],'#ff3300');curve(wall['Toe'],'#00eaff')
 return im
bounds=[d['OriginX']-.5*d['CellSize'],d['OriginY']-.5*d['CellSize'],d['OriginX']+(d['Columns']-.5)*d['CellSize'],d['OriginY']+(d['Rows']-.5)*d['CellSize']]
im=render(bounds,1800,1700);canvas=Image.new('RGB',(im.width,im.height+70),'#f7f5ef');canvas.paste(im,(0,70));draw=ImageDraw.Draw(canvas);draw.text((20,10),'Gronalid terrain outputs | wall top: red | toe: cyan | colour edge: magenta',font=font,fill='black');draw.text((20,38),'Terrain diagnostic. World XY, metres. Numbers identify wall outputs.',font=small,fill='black');canvas.save(folder/'overview.png')
canvas=Image.new('RGB',(1800,1140),'#f7f5ef');draw=ImageDraw.Draw(canvas)
for j,m in enumerate(sorted(rows,key=lambda x:x['controls'],reverse=True)[:6]):
 w=d['Walls'][m['index']];a=np.concatenate([pts(w['Top']),pts(w['Toe'])]);lo=a[:,:2].min(axis=0)-1;hi=a[:,:2].max(axis=0)+1;cx,cy=(lo+hi)/2;half=max(hi-lo)/2;bounds=[cx-half,cy-half,cx+half,cy+half];im=render(bounds,580,510,True,w);x=(j%3)*600+10;y=(j//3)*570+50;canvas.paste(im,(x,y));draw.text((x,y-40),f"Wall {m['index']}: {m['length']:.1f} m, {m['controls']} top controls",font=font,fill='black');draw.text((x,y-17),f"{m['pieces']} pieces; dots show controls",font=small,fill='black')
canvas.save(folder/'wall-details.png')
