"""Exploration of a drone photogrammetry mesh for docs/scan-to-terrain-plan-2026-10-18.md (B26).

Not product code: a measuring stick for the plan's figures. Usage:
    python -I tools/scan-explore.py <mesh.obj> <diffuse.jpg> <output folder>
Prints orientation, face classes, a first-cut ground filter, building components and roof facets, colour
classes and unexplained ground steps; writes hillshades, an orthophoto from the atlas, a class map and a
step map as PNGs. Needs numpy, scipy and Pillow.
"""
import sys, time
import numpy as np
from PIL import Image
from scipy import ndimage

OBJ, TEX, OUT = sys.argv[1], sys.argv[2], sys.argv[3]
t0 = time.time()

V, T, F, FT = [], [], [], []
with open(OBJ, encoding="utf-8", errors="ignore") as h:
    for line in h:
        if line.startswith("v "):
            V.append(line.split()[1:4])
        elif line.startswith("vt "):
            T.append(line.split()[1:3])
        elif line.startswith("f "):
            parts = line.split()[1:]
            idx = [p.split("/") for p in parts]
            for k in range(1, len(idx) - 1):
                tri = (idx[0], idx[k], idx[k + 1])
                F.append([int(t[0]) - 1 for t in tri])
                FT.append([int(t[1]) - 1 if len(t) > 1 and t[1] else -1 for t in tri])
V = np.array(V, float); T = np.array(T, float); F = np.array(F); FT = np.array(FT)
print(f"parsed {len(V)} v, {len(T)} vt, {len(F)} f in {time.time()-t0:.0f}s")

# Orientation: per-face normals, area-weighted up component per axis
a, b, c = V[F[:, 0]], V[F[:, 1]], V[F[:, 2]]
n = np.cross(b - a, c - a); area = 0.5 * np.linalg.norm(n, axis=1); nn = n / np.maximum(2 * area, 1e-12)[:, None]
for axis, name in enumerate("xyz"):
    print(f"area share facing +/-{name} (|n|>0.8): {area[np.abs(nn[:, axis]) > 0.8].sum() / area.sum():.2f}")
cz = nn[:, 2]
print(f"faces up (nz>0.5) {np.mean(cz>0.5):.2f}, walls (|nz|<0.5) {np.mean(np.abs(cz)<0.5):.2f}, down (nz<-0.5) {np.mean(cz<-0.5):.2f}")

# Per-face colour from the atlas at the UV centroid
img = Image.open(TEX).convert("RGB"); W, H = img.size; px = np.asarray(img)
print(f"texture {W}x{H}")
uv = T[FT].mean(axis=1)
ix = np.clip((uv[:, 0] * (W - 1)).astype(int), 0, W - 1); iy = np.clip(((1 - uv[:, 1]) * (H - 1)).astype(int), 0, H - 1)
col = px[iy, ix].astype(float)

# Rasters at 0.5 m: DSM = highest vertex, colour of the highest up-facing face per cell
cell = 0.5
x0, y0 = V[:, 0].min(), V[:, 1].min()
nx = int((V[:, 0].max() - x0) / cell) + 1; ny = int((V[:, 1].max() - y0) / cell) + 1
ci = ((V[:, 0] - x0) / cell).astype(int); cj = ((V[:, 1] - y0) / cell).astype(int)
dsm = np.full((ny, nx), -np.inf); np.maximum.at(dsm, (cj, ci), V[:, 2])
lowest = np.full((ny, nx), np.inf); np.minimum.at(lowest, (cj, ci), V[:, 2])
cen = (a + b + c) / 3
fi = ((cen[:, 0] - x0) / cell).astype(int); fj = ((cen[:, 1] - y0) / cell).astype(int)
up = cz > 0.3
rgb = np.zeros((ny, nx, 3)); zbest = np.full((ny, nx), -np.inf)
order = np.argsort(cen[up, 2])  # later (higher) overwrite lower
uidx = np.where(up)[0][order]
rgb[fj[uidx], fi[uidx]] = col[uidx]; zbest[fj[uidx], fi[uidx]] = cen[uidx, 2]
empty = ~np.isfinite(dsm)
print(f"grid {nx}x{ny} at {cell} m, empty cells {empty.mean():.3f}, vertical spread inside a cell (dsm-lowest) median {np.nanmedian(np.where(empty, np.nan, dsm-lowest)):.2f} m")
# fill empties by nearest
if empty.any():
    idx = ndimage.distance_transform_edt(empty, return_distances=False, return_indices=True)
    dsm = dsm[tuple(idx)]

# Progressive morphological filter (Zhang 2003) for ground
ground = np.ones_like(dsm, bool); surf = dsm.copy(); prev = 0.0
slope = 0.6  # allowed terrain slope (rise/run) for the threshold; Torshavn is steep
for w in [1, 2, 4, 8, 16, 24]:  # window in metres
    k = max(1, int(round(w / cell)))
    opened = ndimage.grey_opening(surf, size=(2 * k + 1, 2 * k + 1))
    dh = min(0.3 + slope * (w - prev), 4.0)
    ground &= (surf - opened) <= dh
    surf = opened; prev = w
nonground = ~ground
dtm = np.where(ground, dsm, np.nan)
# fill nonground by inverse-distance from nearest ground (nearest for exploration)
idx = ndimage.distance_transform_edt(nonground, return_distances=False, return_indices=True)
dtm_f = dsm[tuple(idx)]
dtm_s = ndimage.gaussian_filter(dtm_f, 2)
height = dsm - dtm_s

# Buildings: raised > 2 m, compact, >= 20 m2, with walls nearby
raised = (height > 2.0) & nonground
lab, nlab = ndimage.label(ndimage.binary_opening(raised, iterations=1))
sizes = ndimage.sum(np.ones_like(lab), lab, range(1, nlab + 1)) * cell * cell
heights = ndimage.maximum(height, lab, range(1, nlab + 1))
bmask = np.isin(lab, np.where(sizes >= 20)[0] + 1)
print(f"raised components {nlab}, >=20 m2: {(sizes>=20).sum()}, area {sizes[sizes>=20].sum():.0f} m2, median height {np.median(heights[sizes>=20]):.1f} m, sizes p10/p50/p90 {np.percentile(sizes[sizes>=20],[10,50,90]).round(0)}")

# Roof facets for the 5 largest buildings: cluster DSM normals by azimuth/slope
gy, gx = np.gradient(ndimage.gaussian_filter(dsm, 0.7), cell)
asp = np.degrees(np.arctan2(gy, gx)) % 360; slp = np.degrees(np.arctan(np.hypot(gx, gy)))
big = np.argsort(-sizes)[:8] + 1
for L in big:
    m = (lab == L)
    m = ndimage.binary_erosion(m, iterations=2)
    if m.sum() < 20: continue
    s = slp[m]; az = asp[m]
    flat = s < 8
    bins = np.round(az[~flat] / 45) % 8
    counts = np.bincount(bins.astype(int), minlength=8)
    facets = (counts > 0.08 * m.sum()).sum() + (flat.mean() > 0.15)
    print(f"  building {L}: {sizes[L-1]:.0f} m2, h {heights[L-1]:.1f} m, roof slope median {np.median(s):.0f} deg, flat share {flat.mean():.2f}, dominant facets ~{facets}, azimuth bins {counts.tolist()}")

# Colour classes on ground cells: green (excess green), grey (low saturation), other
R, G, B = rgb[..., 0], rgb[..., 1], rgb[..., 2]
exg = 2 * G - R - B
mx = rgb.max(axis=2); mn = rgb.min(axis=2); sat = (mx - mn) / np.maximum(mx, 1)
green = (exg > 25) & ~empty
grey = (sat < 0.15) & (mx > 60) & ~green & ~empty
print(f"ground cells: green {green[ground].mean():.2f}, grey {grey[ground].mean():.2f}; on buildings: green {green[bmask].mean():.2f}, grey {grey[bmask].mean():.2f}")

# Kerb/edge visibility on ground: max height step across 1 m, on a 0.25 m view of the DTM
lap = dtm_f - ndimage.gaussian_filter(dtm_f, 1.0)
step = ndimage.maximum_filter(dtm_f, 3) - ndimage.minimum_filter(dtm_f, 3)  # over 1.5 m
ok = ground & ~ndimage.binary_dilation(nonground, iterations=4)
gslope = np.degrees(np.arctan(np.hypot(*np.gradient(dtm_s, cell))))
print(f"ground slope median {np.median(gslope[ok]):.0f} deg, p90 {np.percentile(gslope[ok],90):.0f}")
# A kerb is a step that departs from the local plane: residual of step against what the slope explains
expected = np.tan(np.radians(gslope)) * 1.5
excess = step - expected
for t in [0.08, 0.12, 0.2, 0.4]:
    print(f"  ground cells with an unexplained step > {t} m: {(excess[ok] > t).mean():.3f}")

def save(name, arr, cmap=None, lo=None, hi=None):
    a = np.array(arr, float)
    lo = np.nanpercentile(a, 2) if lo is None else lo; hi = np.nanpercentile(a, 98) if hi is None else hi
    a = np.clip((a - lo) / max(hi - lo, 1e-9), 0, 1); a = np.nan_to_num(a)
    Image.fromarray((np.flipud(a) * 255).astype(np.uint8)).save(f"{OUT}/{name}.png")

ls = np.gradient(dsm, cell); shade = np.clip(0.5 - 0.5 * (ls[1] * 0.7 + ls[0] * 0.7) / np.sqrt(1 + ls[0]**2 + ls[1]**2), 0, 1)
save("dsm_hillshade", shade, lo=0, hi=1)
gs = np.gradient(dtm_s, cell); save("dtm_hillshade", np.clip(0.5 - 0.5 * (gs[1]*0.7+gs[0]*0.7)/np.sqrt(1+gs[0]**2+gs[1]**2),0,1), lo=0, hi=1)
Image.fromarray(np.flipud(np.clip(rgb, 0, 255)).astype(np.uint8)).save(f"{OUT}/ortho.png")
cls = np.zeros((ny, nx, 3), np.uint8); cls[green] = (60, 170, 60); cls[grey] = (150, 150, 150); cls[bmask] = (200, 60, 60)
Image.fromarray(np.flipud(cls)).save(f"{OUT}/classes.png")
save("kerb_excess", np.where(ok, excess, np.nan), lo=0, hi=0.4)
np.savez_compressed(f"{OUT}/rasters.npz", dsm=dsm, dtm=dtm_s, ground=ground, lab=lab, rgb=rgb.astype(np.uint8))
print(f"done in {time.time()-t0:.0f}s")
