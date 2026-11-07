"""Optional portable-document capture from the checksum-pinned C0 release.
Normal .NET verification uses committed snapshots only.
"""
import argparse
import hashlib
import json
import shutil
import subprocess
import tempfile
import zipfile
from pathlib import Path


def capture(release_zip):
    here = Path(__file__).resolve().parent
    manifest = json.loads((here / 'manifest.json').read_text(encoding='utf-8'))
    if hashlib.sha256(release_zip.read_bytes()).hexdigest() != manifest['zip']['sha256']:
        raise ValueError('Release ZIP does not match the accepted C0 SHA-256')
    input_path = here / 'document-json' / 'inputs.json'
    inputs = json.loads(input_path.read_text(encoding='utf-8'))
    pack_bytes = (here.parent / 'data' / 'packs' / 'ashen-vale.json').read_bytes()
    scratch = (here.parent / 'artifacts' / 'reference-captures').resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix='c3-documents-', dir=scratch)).resolve()
    if temporary.parent != scratch or not temporary.name.startswith('c3-documents-'):
        raise ValueError('Unexpected temporary cleanup target')
    try:
        executable = temporary / 'planner.exe'
        with zipfile.ZipFile(release_zip) as archive:
            executable.write_bytes(archive.read('character-planner-windows-amd64/planner.exe'))
        version = subprocess.run([str(executable), '--json', 'version'], capture_output=True, check=True, cwd=temporary)
        if json.loads(version.stdout)['result']['version'] != manifest['releaseVersion']:
            raise ValueError('Unexpected reference CLI version')
        (temporary / 'pack.json').write_bytes(pack_bytes)
        cases = []
        for case in inputs['cases']:
            result = subprocess.run([str(executable), '--json', '--pack', 'pack.json', 'build', 'export', '--input', '-'],
                                    input=case['input'].encode('utf-8'), capture_output=True, cwd=temporary, timeout=15)
            if result.returncode not in (0, 3) or result.stderr:
                raise RuntimeError(f'Unexpected CLI failure: {result.returncode} {result.stderr!r}')
            response = json.loads(result.stdout)
            error = response.get('error', {})
            accepted = 'document' in response or error.get('message') == 'build or context failed validation'
            if error.get('code') not in (None, 'invalid-build'):
                raise RuntimeError(f'{case["id"]}: unexpected response: {response}')
            cases.append({**case, 'decodeAccepted': accepted, 'exitCode': result.returncode, 'response': response})
    finally:
        shutil.rmtree(temporary)
    output = {'version': 1, 'reference': {
        'commit': manifest['commit'], 'release': manifest['releaseVersion'], 'zipSha256': manifest['zip']['sha256'],
        'inputsSha256': hashlib.sha256(input_path.read_bytes()).hexdigest(), 'packSha256': hashlib.sha256(pack_bytes).hexdigest(),
        'args': ['--json', '--pack', 'pack.json', 'build', 'export', '--input', '-'], 'inputEncoding': 'utf-8'
    }, 'cases': cases}
    (here / 'document-json' / 'legacy-v1.json').write_text(json.dumps(output, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(f'Captured {len(cases)} document contracts')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('release_zip', type=Path)
    capture(parser.parse_args().release_zip)
