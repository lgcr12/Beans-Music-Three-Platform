"""Reproducible, local feathered mesh plates. Requires only bundled Pillow/numpy.

The original stays intact. Displacement tapers to zero at each ROI boundary and
around anchors; each plate contains the scenery under the hair/cloth too, so no
transparent holes are exposed. Two extreme poses are blended on the compositor.
"""
from pathlib import Path
import json
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'Assets/Player/Scenes'
SOURCES = {
    'summer': ROOT / 'Assets/Player/Backgrounds/anime-player-youth-v2.png',
    'spring': ROOT / 'artifacts/anime-scenes/spring-original.png',
}
# Coordinates refer to normalized 2048 x 1152 source; face/neck never included.
REGIONS = {
    'summer': {'hair': (205, 350, 451, 628), 'cloth': (365, 781, 635, 1058), 'leaves': (0, 0, 354, 194)},
    'spring': {'hair': (75, 372, 335, 793), 'cloth': (265, 955, 600, 1152), 'leaves': (0, 0, 590, 181)},
}

def make_plate(source, bounds, direction, horizontal, vertical):
    x0, y0, x1, y1 = bounds
    # Include the immutable border in the crop; bicubic samples never wrap.
    crop = source.crop(bounds)
    w, h = crop.size
    mesh = []
    def sample(x, y):
        taper = np.sin(np.pi * min(1, max(0, x / w))) ** 2 * np.sin(np.pi * min(1, max(0, y / h))) ** 2
        return (x + direction * horizontal * taper, y + direction * vertical * taper)
    for y in range(0, h, 12):
        for x in range(0, w, 12):
            r, b = min(w, x+12), min(h, y+12)
            mesh.append(((x,y,r,b), (*sample(x,y), *sample(x,b), *sample(r,b), *sample(r,y))))
    warped = crop.transform(crop.size, Image.Transform.MESH, mesh, Image.Resampling.BICUBIC)
    yy, xx = np.mgrid[:h, :w]
    alpha = np.clip(np.minimum.reduce([xx, yy, w-1-xx, h-1-yy])/16, 0, 1)*255
    warped.putalpha(Image.fromarray(alpha.astype('uint8')))
    plate = Image.new('RGBA', source.size)
    plate.paste(warped, (x0,y0))
    return plate

def main():
    for scene, src in SOURCES.items():
        dest = OUT / scene
        dest.mkdir(parents=True, exist_ok=True)
        source = Image.open(src).convert('RGBA').resize((2048,1152), Image.Resampling.LANCZOS)
        source.save(dest/'background.png')
        for layer, bounds in REGIONS[scene].items():
            for name, sign in [('minus',-1),('plus',1)]:
                # Summer has a readable breeze; spring keeps gentle local motion.
                amplitude = ({'hair': 4.5, 'cloth': 3.5, 'leaves': 6.0} if scene == 'summer'
                             else {'hair': 2.5, 'cloth': 2.0, 'leaves': 3.0})[layer]
                make_plate(source,bounds,sign,amplitude,1.4 if scene == 'summer' else .8).save(dest/f'{layer}-{name}.png')
        water = Image.new('RGBA', source.size)
        draw = ImageDraw.Draw(water)
        rng = np.random.default_rng(42)
        # Confine to central river, away from right-hand lyric and queue columns.
        for i in range(65):
            x = int(rng.integers(1080,1510)); y = int(rng.integers(690,740) if scene=='spring' else rng.integers(673,725))
            draw.line((x,y,x+int(rng.integers(2,18)),y),fill=(255,245,225,int(rng.integers(35,125))),width=1)
        water.filter(ImageFilter.GaussianBlur(.6)).save(dest/'water.png')
        cloud = Image.new('RGBA', source.size)
        draw = ImageDraw.Draw(cloud)
        for x,y,rx,ry in [(1010,400,170,11),(1230,440,120,8),(1410,365,75,8)]:
            draw.ellipse((x-rx,y-ry,x+rx,y+ry),fill=(255,252,251,20))
        cloud.filter(ImageFilter.GaussianBlur(18)).save(dest/'cloud.png')
        # Review original and both extremes at full resolution without modifying originals.
        crops=[]
        for layer,bounds in REGIONS[scene].items():
            for pose in ['original','minus','plus']:
                img=source.copy()
                if pose!='original': img=Image.alpha_composite(img,Image.open(dest/f'{layer}-{pose}.png'))
                crops.append(img.crop(bounds).resize((280,320)))
        contact=Image.new('RGB',(840,960),'#e5eafa')
        for i,img in enumerate(crops): contact.paste(img,((i%3)*280,(i//3)*320))
        contact.save(ROOT/f'artifacts/anime-scenes/{scene}-extremes.jpg')
    (OUT/'manifest.json').write_text(json.dumps({'size':[2048,1152],'regions':REGIONS},indent=2),encoding='utf-8')
    print('Prepared summer/spring scene assets and extreme-pose contact sheets.')

if __name__ == '__main__': main()
