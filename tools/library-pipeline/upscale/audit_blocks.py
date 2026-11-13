"""Block-level check of upscaled outputs against their sources: catches partial GPU garbage that the mean check misses."""
import json, os, sys, numpy as np
from PIL import Image
from concurrent.futures import ProcessPoolExecutor
REFS=r'C:\Users\Leshe\Pictures\refs'; UP=r'H:\MapLibrary\upscaled'
def one(r):
    try:
        s=Image.open(os.path.join(REFS,r['rel'])); s.load(); s=s.convert('RGBA')
        o=Image.open(os.path.join(UP,os.path.splitext(r['rel'])[0]+'.webp')); o.load()
        b=np.asarray(o.resize(s.size,Image.BOX),np.float32); a=np.asarray(s,np.float32)
        d=np.abs(b[...,:3]-a[...,:3]).mean(2); m=a[...,3]>200
        d=np.where(m,d,np.nan); h,w=d.shape; k=8
        H,W=h//k*k,w//k*k
        if H==0 or W==0: return r['rel'],0.0,0.0,0.0
        bl=d[:H,:W].reshape(H//k,k,W//k,k).swapaxes(1,2).reshape(H//k,W//k,-1)
        cnt=np.sum(~np.isnan(bl),2); mean=np.where(cnt>=16,np.nanmean(np.where(cnt[...,None]>0,bl,0),2),np.nan)
        mean=mean[~np.isnan(mean)]
        if mean.size==0: return r['rel'],0.0,0.0,0.0
        return r['rel'],float(mean.max()),float((mean>40).mean()),float(np.percentile(mean,99))
    except Exception as e: return r['rel'],-1,-1,str(e)[:100]
if __name__=='__main__':
    sel=sys.argv[1]; out=sys.argv[2]
    rows={}
    for l in open(os.path.join(UP,'manifest.jsonl'),encoding='utf-8-sig'):
        try: x=json.loads(l)
        except: continue
        if x.get('ok'): rows[x['rel']]=x
    rows=list(rows.values())
    if sel=='suspect': rows=[x for x in rows if x.get('colourD',0)>10 or x.get('completedAt','')>'2026-11-12T18:46']
    elif sel=='sample': import random; rows=random.Random(1).sample(rows,200)
    with ProcessPoolExecutor(int(sys.argv[3]) if len(sys.argv)>3 else 3) as ex, open(out,'w',encoding='utf-8') as f:
        for res in ex.map(one,rows,chunksize=4): f.write(json.dumps(res,ensure_ascii=False)+'\n')
