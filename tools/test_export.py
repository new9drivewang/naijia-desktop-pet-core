"""Data/integrity checks only. Does not test Windows UI, GDI or security."""
from pathlib import Path
import binascii
import hashlib
import re
import runpy
import struct
import unittest
import zlib

ROOT = Path(__file__).resolve().parents[1]

def load_manifest():
    entries = {}
    for line in (ROOT / 'runtime-files.tsv').read_text(encoding='utf-8').splitlines():
        digest, size, relative = line.split('\t')
        if not re.fullmatch('[0-9a-f]{64}', digest) or relative in entries:
            raise ValueError('Invalid or duplicate manifest entry')
        if Path(relative).is_absolute() or '..' in Path(relative).parts or '\\' in relative:
            raise ValueError('Manifest path escapes export')
        entries[relative] = (digest, int(size))
    return entries

class ExportTests(unittest.TestCase):
    def test_manifest_hashes_and_sizes(self):
        for relative, (digest, size) in load_manifest().items():
            data = (ROOT / relative).read_bytes()
            self.assertEqual(len(data), size, relative)
            self.assertEqual(hashlib.sha256(data).hexdigest(), digest, relative)

    def test_no_unlisted_payload(self):
        listed = set(load_manifest()) | {'runtime-files.tsv'}
        self.assertEqual(listed, {p.relative_to(ROOT).as_posix() for p in ROOT.rglob('*') if p.is_file()})

    def test_placeholder_reproducibility(self):
        generated = runpy.run_path(str(ROOT / 'tools/generate_placeholders.py'))['generate']()
        self.assertEqual(len(generated), 118)
        for relative, data in generated.items(): self.assertEqual((ROOT / relative).read_bytes(), data, relative)

    def test_all_png_chunks_and_full_rgba_data(self):
        paths = list(ROOT.rglob('*.png')); self.assertEqual(len(paths), 116)
        for path in paths:
            data = path.read_bytes(); self.assertEqual(data[:8], b'\x89PNG\r\n\x1a\n')
            position, image_data, ended = 8, b'', False
            while position < len(data):
                length = struct.unpack('>I', data[position:position+4])[0]
                kind = data[position+4:position+8]; payload = data[position+8:position+8+length]
                crc = struct.unpack('>I', data[position+8+length:position+12+length])[0]
                self.assertEqual(crc, binascii.crc32(kind+payload) & 0xffffffff)
                if kind == b'IHDR': self.assertEqual(struct.unpack('>IIBBBBB', payload), (160,200,8,6,0,0,0))
                if kind == b'IDAT': image_data += payload
                if kind == b'IEND': ended = True
                position += length + 12
            self.assertTrue(ended); self.assertEqual(position, len(data))
            raw = zlib.decompress(image_data); self.assertEqual(len(raw), 200*(160*4+1))
            self.assertTrue(all(raw[y*(160*4+1)] == 0 for y in range(200)))

    def test_action_changes_pixels(self):
        idle = (ROOT / 'assets/demo/idle.png').read_bytes()
        self.assertNotEqual(idle, (ROOT / 'assets/demo/action/006.png').read_bytes())

    def test_payload_exclusions(self):
        prohibited = {'.git', '.github', '.env', 'node_modules', 'bin', 'obj', '__pycache__'}
        for p in ROOT.rglob('*'):
            self.assertFalse(any(part in prohibited for part in p.relative_to(ROOT).parts), str(p))
            self.assertNotIn(p.suffix.lower(), {'.exe','.dll','.pdb','.wav','.mp3','.mp4','.gif','.nwp','.zip'})

    def test_offline_runtime_and_session_config(self):
        source = '\n'.join(p.read_text(encoding='utf-8') for p in (ROOT / 'src').glob('*.cs'))
        for token in ['HttpClient', 'WebClient', 'System.Net', 'Process.Start', 'Registry.', 'PetInfoPanel']:
            self.assertNotIn(token, source)
        settings = (ROOT / 'src/LocalSettings.cs').read_text()
        self.assertNotIn('System.IO', settings)
        self.assertNotIn('Environment.GetFolderPath', settings)

if __name__ == '__main__': unittest.main(verbosity=2)
