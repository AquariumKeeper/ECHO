"""Capture the C2 pack contract corpus from the checksum-pinned C0 release ZIP.

Optional reference-generation tool only; normal .NET verification reads the
committed JSON and does not need this script or the legacy executable.
Usage: python baseline/capture_pack_validation.py PATH/TO/RELEASE.zip
"""
import argparse
import hashlib
import json
import subprocess
import tempfile
import zipfile
from pathlib import Path


def capture(release_zip):
    here = Path(__file__).resolve().parent
    manifest = json.loads((here / 'manifest.json').read_text(encoding='utf-8'))
    if hashlib.sha256(release_zip.read_bytes()).hexdigest() != manifest['zip']['sha256']:
        raise ValueError('Release ZIP does not match the accepted C0 SHA-256')
    inputs = json.loads((here / 'pack-validation' / 'inputs.json').read_text(encoding='utf-8'))
    cases = []
    scratch = here.parent / 'artifacts' / 'reference-captures'
    scratch.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(prefix='c2-pack-contracts-', dir=scratch) as temporary:
        executable = Path(temporary) / 'planner.exe'
        with zipfile.ZipFile(release_zip) as archive:
            # Read one fixed archive member, never extract arbitrary archive paths.
            executable.write_bytes(archive.read('character-planner-windows-amd64/planner.exe'))
        version = subprocess.run([str(executable), '--json', 'version'], check=True,
                                 capture_output=True, text=True, encoding='utf-8')
        if json.loads(version.stdout)['result']['version'] != manifest['releaseVersion']:
            raise ValueError('Unexpected reference CLI version')
        for case in inputs['cases']:
            result = subprocess.run([str(executable), '--json', '--pack', '-', 'catalog', 'list', 'slots'],
                                    input=case['input'].encode('utf-8'), capture_output=True,
                                    cwd=temporary, timeout=10)
            if result.returncode not in (0, 3) or result.stderr:
                raise RuntimeError(f'{case["id"]}: unexpected CLI failure: {result.returncode} {result.stderr}')
            cases.append({**case, 'exitCode': result.returncode, 'response': json.loads(result.stdout)})
    output = {'version': 1, 'reference': {
        'commit': manifest['commit'], 'release': manifest['releaseVersion'],
        'zipSha256': manifest['zip']['sha256'],
        'inputsSha256': hashlib.sha256((here / 'pack-validation' / 'inputs.json').read_bytes()).hexdigest(),
        'args': ['--json', '--pack', '-', 'catalog', 'list', 'slots'],
        'inputEncoding': 'utf-8',
    }, 'cases': cases}
    destination = here / 'pack-validation' / 'legacy-v1.json'
    destination.write_text(json.dumps(output, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(f'Captured {len(cases)} cases from accepted release {manifest["releaseVersion"]}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('release_zip', type=Path)
    capture(parser.parse_args().release_zip)
