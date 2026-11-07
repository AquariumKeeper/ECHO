"""Optionally verify C#-produced share codes with the checksum-pinned legacy reader.
Usage: python baseline/check_share_encoding.py RELEASE.zip ENCODINGS.json
ENCODINGS.json contains id/code/legacyCode rows produced by a C# probe.
Normal .NET verification uses committed payload and import snapshots only.
"""
import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path


def check(release_zip, encodings):
    here = Path(__file__).resolve().parent
    manifest = json.loads((here / 'manifest.json').read_text(encoding='utf-8'))
    if hashlib.sha256(release_zip.read_bytes()).hexdigest() != manifest['zip']['sha256']:
        raise ValueError('Unexpected release ZIP SHA-256')
    inputs = json.loads(encodings.read_text(encoding='utf-8'))
    scratch = (here.parent / 'artifacts' / 'reference-captures').resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix='c3-share-import-', dir=scratch)).resolve()
    if temporary.parent != scratch or not temporary.name.startswith('c3-share-import-'):
        raise ValueError('Unexpected temporary cleanup target')
    try:
        executable = temporary / 'planner.exe'
        with zipfile.ZipFile(release_zip) as archive:
            executable.write_bytes(archive.read('character-planner-windows-amd64/planner.exe'))
        version = subprocess.run([str(executable), '--json', 'version'], capture_output=True, check=True, cwd=temporary)
        if json.loads(version.stdout)['result']['version'] != manifest['releaseVersion']:
            raise ValueError('Unexpected reference CLI version')
        (temporary / 'share.json').write_bytes((here / 'share-encoding' / 'pack.json').read_bytes())
        (temporary / 'division.json').write_bytes((here.parent / 'data' / 'packs' / 'division-2.json').read_bytes())
        cases = []
        for row in inputs['cases']:
            responses = []
            for code in (row['legacyCode'], row['code']):
                result = subprocess.run([str(executable), '--json', '--pack', 'division.json' if row['id'].startswith('division/') else 'share.json',
                                         'build', 'import', '--share', code], capture_output=True, cwd=temporary, timeout=15)
                if result.returncode != 0 or result.stderr:
                    raise RuntimeError(f'{row["id"]}: import failed: {result.stdout!r} {result.stderr!r}')
                responses.append(json.loads(result.stdout))
            if responses[0] != responses[1]:
                raise ValueError(f'{row["id"]}: C# and legacy codes import differently')
            cases.append({**row, 'response': responses[1]})
    finally:
        shutil.rmtree(temporary)
    output = {'version': 1, 'reference': {
        'commit': manifest['commit'], 'release': manifest['releaseVersion'], 'zipSha256': manifest['zip']['sha256'],
        'encodingsSha256': hashlib.sha256(encodings.read_bytes()).hexdigest(), 'csharpAssemblySha256': inputs['assemblySha256'],
        'sharePackSha256': hashlib.sha256((here / 'share-encoding' / 'pack.json').read_bytes()).hexdigest(),
        'divisionPackSha256': hashlib.sha256((here.parent / 'data' / 'packs' / 'division-2.json').read_bytes()).hexdigest(),
        'args': ['--json', '--pack', 'PACK.json', 'build', 'import', '--share', 'CODE']
    }, 'cases': cases}
    (here / 'share-encoding' / 'csharp-import-v1.json').write_text(json.dumps(output, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(f'Legacy reader imported all {len(cases)} C# codes identically')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('release_zip', type=Path)
    parser.add_argument('encodings', type=Path)
    args = parser.parse_args()
    check(args.release_zip, args.encodings)
