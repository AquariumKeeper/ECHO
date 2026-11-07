"""Optionally capture C3 basic build resolution from the accepted C0 release.

Normal .NET verification consumes the committed response corpus; it never runs
this script, a Go toolchain or the legacy executable.
Usage: python baseline/capture_resolution.py PATH/TO/RELEASE.zip
"""
import argparse
import base64
import hashlib
import json
import shutil
import subprocess
import tempfile
import zipfile
import zlib
from pathlib import Path


def capture(release_zip, suite='resolution'):
    here = Path(__file__).resolve().parent
    manifest = json.loads((here / 'manifest.json').read_text(encoding='utf-8'))
    if hashlib.sha256(release_zip.read_bytes()).hexdigest() != manifest['zip']['sha256']:
        raise ValueError('Release ZIP does not match the accepted C0 SHA-256')
    input_path = here / suite / 'inputs.json'
    inputs = json.loads(input_path.read_text(encoding='utf-8'))
    pack_path = here.parent / 'data' / 'packs' / inputs['pack'] if suite == 'resolution' else here / suite / 'pack.json'
    pack_bytes = pack_path.read_bytes()
    scratch = (here.parent / 'artifacts' / 'reference-captures').resolve()
    scratch.mkdir(parents=True, exist_ok=True)
    temporary = Path(tempfile.mkdtemp(prefix='c3-resolution-', dir=scratch)).resolve()
    # Verify the absolute cleanup target before any recursive deletion.
    if temporary.parent != scratch or not temporary.name.startswith('c3-resolution-'):
        raise ValueError('Unexpected temporary directory')
    try:
        executable = temporary / 'planner.exe'
        with zipfile.ZipFile(release_zip) as archive:
            executable.write_bytes(archive.read('character-planner-windows-amd64/planner.exe'))

        def run(args, document=None):
            result = subprocess.run([str(executable), '--json', *args],
                                    input=None if document is None else json.dumps(document).encode('utf-8'),
                                    capture_output=True, cwd=temporary, timeout=15)
            if result.returncode not in (0, 3) or result.stderr:
                raise RuntimeError(f'Unexpected CLI failure: {result.returncode} {result.stderr!r}')
            return result.returncode, json.loads(result.stdout)

        if run(['version'])[1]['result']['version'] != manifest['releaseVersion']:
            raise ValueError('Unexpected reference CLI version')
        # Keep the recorded pack bytes intact and use an isolated pack file.
        local_pack = temporary / 'pack.json'
        local_pack.write_bytes(pack_bytes)
        prefix = ['--pack', str(local_pack)]
        cases = []
        verb = 'share' if suite == 'share-encoding' else 'resolve'
        for case in inputs['cases']:
            _, fresh = run([*prefix, 'build', 'new'])
            if 'document' not in fresh:
                raise RuntimeError(f'Cannot create reference build: {fresh}')
            document = fresh['document']
            document['build'].update(inputs.get('defaults', {}))
            document['build'].update({key: value for key, value in case.items() if key not in ('id', 'context')})
            context = {**inputs.get('defaultContext', {}), **case.get('context', {})}
            context_file = temporary / 'context.json'
            context_file.write_text(json.dumps(context), encoding='utf-8')
            code, response = run([*prefix, 'build', verb, '--input', '-', '--context', str(context_file)], document)
            if 'result' not in response and response.get('error', {}).get('code') != 'invalid-build':
                raise RuntimeError(f'{case["id"]}: unexpected resolution response: {response}')
            captured = {'id': case['id'], 'document': document, 'context': context, 'exitCode': code, 'response': response}
            if suite == 'share-encoding':
                encoded = response['result']['code']
                captured['payloadJson'] = zlib.decompress(base64.urlsafe_b64decode(encoded + '=' * (-len(encoded) % 4)), -15).decode('utf-8')
            cases.append(captured)
    finally:
        shutil.rmtree(temporary)
    output = {'version': 1, 'reference': {
        'commit': manifest['commit'], 'release': manifest['releaseVersion'], 'zipSha256': manifest['zip']['sha256'],
        'inputsSha256': hashlib.sha256(input_path.read_bytes()).hexdigest(),
        'packSha256': hashlib.sha256(pack_bytes).hexdigest(),
        'args': ['--json', '--pack', 'pack.json', 'build', verb, '--input', '-', '--context', 'context.json'],
        'inputEncoding': 'utf-8'
    }, 'cases': cases}
    (here / suite / 'legacy-v1.json').write_text(json.dumps(output, indent=2, ensure_ascii=False) + '\n', encoding='utf-8', newline='\n')
    print(f'Captured {len(cases)} cases from accepted release {manifest["releaseVersion"]}')


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('release_zip', type=Path)
    parser.add_argument('--suite', choices=('resolution', 'equipment-resolution', 'upgrade-resolution', 'prototype-resolution', 'talent-copy-resolution', 'specialization-resolution', 'active-skill-resolution', 'skill-mod-resolution', 'build-output-resolution', 'party-resolution', 'share-encoding'), default='resolution')
    args = parser.parse_args()
    capture(args.release_zip, args.suite)
