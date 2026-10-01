"""Render the geometric SVG mark into pixel-aligned Windows ICO frames.

Requires Pillow. Native-size rectangles preserve the two-pixel harbor walls
in the 16px tray icon, rather than shrinking a large bitmap indiscriminately.
"""
from pathlib import Path
import argparse
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parents[1]
SIZES = (16, 20, 24, 32, 40, 48, 64, 128, 256)

def render(size):
    svg = ET.parse(ROOT / 'windows/MyProxy/Assets/app.svg').getroot()
    tile, harbor, sun = list(svg)[1:]
    paper, ink = tile.attrib['fill'], harbor.attrib['fill']
    image = Image.new('RGBA', (size, size))
    draw = ImageDraw.Draw(image)
    def pixel(value): return round(int(value) * size / 256)
    def rect(x, y, width, height, fill, outline=None, stroke=1):
        draw.rectangle((pixel(x), pixel(y), pixel(int(x)+int(width))-1,
                        pixel(int(y)+int(height))-1), fill=fill, outline=outline, width=stroke)
    rect(tile.attrib['x'], tile.attrib['y'], tile.attrib['width'], tile.attrib['height'],
         paper, ink, max(1, pixel(tile.attrib['stroke-width'])))
    assert harbor.attrib['d'] == 'M56 72H88V168H168V72H200V200H56Z'
    rect(56, 72, 32, 128, ink)
    rect(168, 72, 32, 128, ink)
    rect(88, 168, 80, 32, ink)
    rect(sun.attrib['x'], sun.attrib['y'], sun.attrib['width'], sun.attrib['height'], ink)
    return image

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--preview-dir', type=Path)
    args = parser.parse_args()
    frames = [render(size) for size in SIZES]
    target = ROOT / 'windows/MyProxy/Assets/app.ico'
    frames[-1].save(target, format='ICO', sizes=[(n,n) for n in SIZES], append_images=frames[:-1])
    with Image.open(target) as icon:
        assert icon.ico.sizes() == {(n,n) for n in SIZES}
    if args.preview_dir:
        args.preview_dir.mkdir(parents=True, exist_ok=True)
        for size, image in zip(SIZES, frames): image.save(args.preview_dir / f'icon-{size}.png')
        preview = Image.new('RGB', (800, 280), '#eae6dc')
        draw = ImageDraw.Draw(preview)
        for i, size in enumerate([16, 24, 32, 48, 64, 128]):
            x = 24 + i * 128
            draw.rectangle((x, 40, x+112, 200), fill='#faf8f1' if i%2==0 else '#25231f')
            icon = render(size)
            if size<48: icon=icon.resize((size*3,size*3), Image.Resampling.NEAREST)
            if size==128: icon=icon.resize((96,96), Image.Resampling.LANCZOS)
            preview.paste(icon, (x+(112-icon.width)//2, 80), icon)
            draw.text((x+36,220), f'{size}px', fill='#25231f')
        preview.save(args.preview_dir / 'icon-preview.png')
    print('WINDOWS_ICON_OK sizes=' + ','.join(map(str,SIZES)))

if __name__ == '__main__': main()
