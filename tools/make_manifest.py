"""Build the demo's relative-path SHA-256/size manifest; no network or config."""
from pathlib import Path
import hashlib

ROOT = Path(__file__).resolve().parents[1]
EXCLUDED_DIRS = {'.git', 'bin', 'obj', '__pycache__'}

def files():
    return sorted(p for p in ROOT.rglob('*') if p.is_file()
                  and not any(part in EXCLUDED_DIRS for part in p.relative_to(ROOT).parts)
                  and p.name != 'runtime-files.tsv'
                  and not p.name.startswith('performance-report-'))

if __name__ == '__main__':
    entries = []
    for path in files():
        data = path.read_bytes()
        entries.append(f'{hashlib.sha256(data).hexdigest()}\t{len(data)}\t{path.relative_to(ROOT).as_posix()}\n')
    (ROOT / 'runtime-files.tsv').write_text(''.join(entries), encoding='utf-8', newline='\n')
    print(f'Manifest: {len(entries)} relative paths.')
