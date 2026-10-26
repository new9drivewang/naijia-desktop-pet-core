"""Original geometric demo artwork. Standard library only; no source media."""
from pathlib import Path
import math
import struct
import zlib
import binascii

ROOT = Path(__file__).resolve().parents[1]
WIDTH, HEIGHT = 160, 200

def png(pixels):
    def chunk(kind, data):
        return struct.pack('>I', len(data)) + kind + data + struct.pack('>I', binascii.crc32(kind + data) & 0xffffffff)
    rows = b''.join(b'\0' + pixels[y * WIDTH * 4:(y + 1) * WIDTH * 4] for y in range(HEIGHT))
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', struct.pack('>IIBBBBB', WIDTH, HEIGHT, 8, 6, 0, 0, 0)) + chunk(b'IDAT', zlib.compress(rows, 9)) + chunk(b'IEND', b'')

def frame(phase=0, look=0, blink=0, tile=False):
    pixels = bytearray(WIDTH * HEIGHT * 4)
    bob = round(4 * math.sin(phase))
    for y in range(HEIGHT):
        for x in range(WIDTH):
            u, v = x / WIDTH, (y - bob) / HEIGHT
            color = (0, 0, 0, 0)
            # Plain rectangles/ellipses only; no likeness of an existing character.
            body = (.16 < u < .74 and .29 < v < .82) if tile else ((u - .44) / .29) ** 2 + ((v - .53) / .32) ** 2 < 1
            if body: color = (232, 164, 71, 255) if tile else (58, 170, 202, 255)
            if ((u - .365) / .15) ** 2 + ((v - .19) / .13) ** 2 < 1: color = (78, 93, 151, 255)
            if ((u - .44) / .15) ** 2 + ((v - .475) / .12) ** 2 < 1: color = (165, 226, 233, 255)
            if ((u - .365) / .035) ** 2 + ((v - .194) / (.018 + .008 * abs(math.sin(phase)))) ** 2 < 1: color = (255, 218, 84, 255)
            if abs(u - (.32 + look * .018)) < .018 and abs(v - .135) < max(.003, .013 * (1 - blink)): color = (245, 248, 251, 255)
            # Four square registration marks visibly identify engineering artwork.
            if .3 < u < .58 and .68 < v < .73 and int((u - .3) * 100) % 7 < 4: color = (255, 255, 255, 255)
            off = (y * WIDTH + x) * 4; pixels[off:off + 4] = bytes(color)
    return png(pixels)

def generate():
    files = {}
    files['assets/demo/idle.png'] = frame()
    for i in range(96): files[f'assets/demo/action/{i:03}.png'] = frame(2 * math.pi * i / 48)
    for i in range(11): files[f'assets/demo/look/{i:02}.png'] = frame(look=(i - 5) / 5)
    for i in range(7): files[f'assets/demo/blink/{i:02}.png'] = frame(blink=1 - abs(i - 3) / 3)
    files['characters/tile/idle.png'] = frame(tile=True)
    manifest = '''<character id="demo-shapes" name="Geometric placeholder (generated, not original character)" width="160" height="200" source="tools/generate_placeholders.py" license="PolyForm Noncommercial 1.0.0; original demo geometry only" action="几何占位动作" end="hold">
  <idle path="idle.png"/>
  <laugh directory="action" count="96" digits="3" fps="24"/>
  <look directory="look" count="11" digits="2" fps="30"/>
  <blinkLeft directory="blink" count="7" digits="2" fps="20"/>
  <blinkRight directory="blink" count="7" digits="2" fps="20"/>
</character>
'''
    files['assets/demo/character.xml'] = manifest.encode()
    files['characters/tile/character.xml'] = b'<character id="demo-tile" name="Geometric tile placeholder (generated)" width="160" height="200" source="tools/generate_placeholders.py" license="PolyForm Noncommercial 1.0.0; original demo geometry only"><idle path="idle.png"/></character>\n'
    return files

if __name__ == '__main__':
    files = generate()
    for relative, data in files.items():
        target = ROOT / relative; target.parent.mkdir(parents=True, exist_ok=True); target.write_bytes(data)
    print(f'Generated {len(files)} original geometric placeholder files.')
