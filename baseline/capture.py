"""Capture the C0 legacy fixtures from the frozen Go CLI.

Usage: python baseline/capture.py PATH/TO/planner.exe

Run it with the `planner.exe` from the extracted go-final-0.1.0-beta.6 release
ZIP (baseline/manifest.json records its checksum); the session fixtures also launch the
`character-planner.exe` beside it, in a scratch folder (see legacy_desktop.py).
It reads each build's operations.json under baseline/fixtures/builds/ and
rewrites every other file under baseline/fixtures/. Each CLI call is recorded as
{"args", "stdin", "exitCode", "response"}; commands run from a scratch directory,
so recorded paths are relative. Library save times, session IDs, process IDs and
session timestamps are replaced by fixed values, so two captures give identical
files.
"""
import hashlib
import json
import re
import shutil
import subprocess
import sys
import tempfile
from pathlib import Path

from legacy_desktop import close, launch

ROOT = Path(__file__).resolve().parent / 'fixtures'
BUILDS = ROOT / 'builds'
EXPECTED_VERSION = '0.1.0-beta.6'
SAVED_AT = '2026-10-25T00:00:00Z'
SESSION_ID = '0' * 32
PLANNER = Path(sys.argv[1]).resolve()
WORK = Path(tempfile.mkdtemp(prefix='c0-capture-'))

# Assumptions used by the context runs: a hard target, the secondary weapon active.
ALT_CONTEXT = {'enemy_target': '2', 'enemy_difficulty': '6', 'weapon_slot': 'secondary_weapon'}
# A normal target at Heroic for the simulator, so kill-driven rules fire often.
SIM_CONTEXT = {'enemy_target': '2', 'enemy_difficulty': '4'}

SIMULATIONS = {
    'simulate-random': ['--iterations', '500', '--seed', '1'],
    'simulate-average': ['--average'],
    'simulate-incoming': ['--incoming', '--iterations', '500', '--seed', '1'],
    'simulate-incoming-average': ['--incoming', '--average'],
    'simulate-normal-target': ['--incoming', '--iterations', '500', '--seed', '1', '--context', 'sim-context.json'],
    'simulate-normal-target-average': ['--incoming', '--average', '--context', 'sim-context.json'],
}
EXTRA_SIMULATIONS = {
    'striker-rifle': {'simulate-swap-kill': ['--swap', 'kill', '--iterations', '500', '--seed', '7']},
    'foundry-shield': {'simulate-incoming-swap-empty': ['--incoming', '--swap', 'empty', '--iterations', '500', '--seed', '3']},
    'shotgun-close': {'simulate-distance-5': ['--distance', '5', '--average'],
                      'simulate-distance-30': ['--distance', '30', '--average']},
}
RANKS = {
    'striker-rifle': ('chest', 'equipped_weapon_practical_firing_dps'),
    'foundry-shield': ('chest', 'character_armor'),
}


def run(args, stdin=None):
    p = subprocess.run([str(PLANNER), '--json', *args], cwd=WORK, capture_output=True, text=True,
                       encoding='utf-8', input=stdin)
    return {'args': args, 'stdin': stdin, 'exitCode': p.returncode, 'response': json.loads(p.stdout)}


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    text = data if isinstance(data, str) else json.dumps(data, indent=2, ensure_ascii=False) + '\n'
    path.write_text(text, encoding='utf-8', newline='\n')


def record(path, args, stdin=None, expect=0):
    r = run(args, stdin)
    if r['exitCode'] != expect:
        raise SystemExit(f'{path.name}: exit {r["exitCode"]}, expected {expect}: {r["response"]}')
    write(path, r)
    return r['response']


def pin(directory, replacements):
    """Replace nondeterministic values in every JSON file under directory."""
    for f in directory.rglob('*.json'):
        text = f.read_text(encoding='utf-8')
        for pattern, value in replacements:
            text = re.sub(pattern, value, text)
        f.write_text(text, encoding='utf-8', newline='\n')


def sessions():
    """Drive a live desktop window through the session commands."""
    out = ROOT / 'session'
    proc, _, _ = launch(PLANNER.parent, WORK)
    try:
        sd = ['--session-dir', 'sessions']
        listed = record(out / 'list.json', ['session', 'list', *sd])
        session_id = next(s['id'] for s in listed['result'] if s.get('available'))
        rev = record(out / 'inspect-initial.json', ['session', 'inspect', *sd])['revision']
        rev = record(out / 'apply-build.json', ['session', 'apply', *sd, '--expected-revision', str(rev), '--operations', '-'],
                     stdin=(BUILDS / 'striker-rifle' / 'operations.json').read_text(encoding='utf-8'))['revision']
        record(out / 'apply-stale.json', ['session', 'apply', *sd, '--expected-revision', '0', '--operations', '-'],
               stdin=json.dumps([{'kind': 'notes', 'text': 'stale'}]), expect=6)
        rev = record(out / 'apply-notes.json', ['session', 'apply', *sd, '--expected-revision', str(rev), '--operations', '-'],
                     stdin=json.dumps([{'kind': 'notes', 'text': 'Edited through a session.'}]))['revision']
        rev = record(out / 'undo.json', ['session', 'undo', *sd, '--expected-revision', str(rev)])['revision']
        rev = record(out / 'redo.json', ['session', 'redo', *sd, '--expected-revision', str(rev)])['revision']
        rev = record(out / 'apply-context.json', ['session', 'apply', *sd, '--expected-revision', str(rev), '--operations', '-'],
                     stdin=json.dumps([{'kind': 'context', 'id': 'enemy_target', 'value': 2}]))['revision']
        record(out / 'resolve.json', ['session', 'resolve', *sd])
        # Known legacy bug: undoing an assumption edit makes the window record a
        # second, desktop-origin edit, which clears redo (see the feature ledger).
        rev = record(out / 'undo-context-known-bug.json', ['session', 'undo', *sd, '--expected-revision', str(rev)])['revision']
        record(out / 'history.json', ['session', 'history', *sd])
        record(out / 'inspect-wait-timeout.json', ['session', 'inspect', *sd, '--wait', '200ms', '--since-revision', str(rev)])
        record(out / 'inspect-unknown-session.json', ['session', 'inspect', *sd, '--session', 'f' * 32], expect=4)
    finally:
        close(proc)
    pin(out, [(session_id, SESSION_ID), (r'"pid": ?\d+', '"pid": 0'),
              (r'("[A-Za-z]*At": ?")[^"]+"', r'\g<1>' + SAVED_AT + '"')])


def main():
    version = run(['version'])['response']['result']['version']
    if version != EXPECTED_VERSION:
        raise SystemExit(f'planner is {version}, expected {EXPECTED_VERSION}')
    for child in ROOT.iterdir() if ROOT.exists() else ():
        if child.name != 'builds':
            shutil.rmtree(child) if child.is_dir() else child.unlink()
    for build in BUILDS.iterdir():
        for f in build.iterdir():
            if f.name != 'operations.json':
                shutil.rmtree(f) if f.is_dir() else f.unlink()

    cli = ROOT / 'cli'
    record(cli / 'version.json', ['version'])
    record(cli / 'describe.json', ['describe'])
    for kind in ('items', 'slots', 'attributes', 'mods', 'talents', 'skills', 'specializations', 'upgrades'):
        listing = record(cli / 'catalog' / f'list-{kind}.json', ['catalog', 'list', kind])
        first = listing['result'][0]['id']
        record(cli / 'catalog' / f'show-{kind}-{first}.json', ['catalog', 'show', kind, first])
    record(cli / 'catalog' / 'list-items-query-slot.json',
           ['catalog', 'list', 'items', '--query', 'striker', '--slot', 'chest'])
    record(cli / 'build-new.json', ['build', 'new', '--name', 'Empty'])
    write(WORK / 'context.json', ALT_CONTEXT)
    write(WORK / 'sim-context.json', SIM_CONTEXT)
    write(ROOT / 'contexts' / 'context.json', ALT_CONTEXT)
    write(ROOT / 'contexts' / 'sim-context.json', SIM_CONTEXT)

    names = sorted(b.name for b in BUILDS.iterdir() if (b / 'operations.json').exists())
    shares = {}
    for name in names:
        out = BUILDS / name
        doc = f'{name}.json'
        shutil.copy(out / 'operations.json', WORK / 'ops.json')
        run(['build', 'new', '--output', doc, '--force'])
        record(out / 'apply.json', ['build', 'apply', '--input', doc, '--operations', 'ops.json', '--output', doc, '--force'])
        shutil.copy(WORK / doc, out / 'document.json')
        record(out / 'validate.json', ['build', 'validate', '--input', doc])
        record(out / 'inspect.json', ['build', 'inspect', '--input', doc])
        record(out / 'export.json', ['build', 'export', '--input', doc])
        share = record(out / 'share.json', ['build', 'share', '--input', doc])['result']['code']
        shares[name] = share
        write(out / 'share.txt', share + '\n')
        record(out / 'import.json', ['build', 'import', '--share', share])
        record(out / 'resolve.json', ['build', 'resolve', '--input', doc])
        record(out / 'resolve-context.json', ['build', 'resolve', '--input', doc, '--context', 'context.json'])
        for label, args in {**SIMULATIONS, **EXTRA_SIMULATIONS.get(name, {})}.items():
            record(out / f'{label}.json', ['build', 'simulate', '--input', doc, *args])
        if name in RANKS:
            slot, stat = RANKS[name]
            record(out / 'rank.json', ['build', 'rank', '--input', doc, '--slot', slot, '--stat', stat])

    record(ROOT / 'compare' / 'striker-rifle-vs-shotgun-close.json',
           ['build', 'compare', '--left', 'striker-rifle.json', '--right', 'shotgun-close.json'])
    record(ROOT / 'compare' / 'striker-rifle-vs-shotgun-close-context.json',
           ['build', 'compare', '--left', 'striker-rifle.json', '--right', 'shotgun-close.json', '--context', 'context.json'])

    lib = ROOT / 'library'
    for name in names:
        record(lib / f'save-{name}.json', ['library', 'save', '--library', 'library.json', '--id', name, '--input', f'{name}.json'])
    record(lib / 'save-duplicate.json', ['library', 'save', '--library', 'library.json', '--id', names[0], '--input', f'{names[0]}.json'], expect=3)
    record(lib / 'save-replace.json', ['library', 'save', '--library', 'library.json', '--id', names[0], '--input', f'{names[0]}.json', '--replace'])
    record(lib / 'save-share.json', ['library', 'save', '--library', 'library.json', '--id', 'from-share', '--share', shares['striker-rifle']])
    record(lib / 'list.json', ['library', 'list', '--library', 'library.json'])
    record(lib / 'load.json', ['library', 'load', '--library', 'library.json', '--id', 'foundry-shield'])
    record(lib / 'delete.json', ['library', 'delete', '--library', 'library.json', '--id', 'from-share'])
    shutil.copy(WORK / 'library.json', lib / 'library.json')
    pin(lib, [(r'("savedAt": ?")[^"]+"', r'\g<1>' + SAVED_AT + '"')])

    err = ROOT / 'errors'
    record(err / 'unknown-command.json', ['frobnicate'], expect=2)
    record(err / 'unknown-catalog-id.json', ['catalog', 'show', 'items', 'primary_weapon_does_not_exist'], expect=2)
    record(err / 'unknown-catalog-kind.json', ['catalog', 'list', 'weapons'], expect=2)
    record(err / 'invalid-share-code.json', ['build', 'import', '--share', 'not-a-share-code'], expect=3)
    record(err / 'invalid-operation.json', ['build', 'apply', '--input', 'striker-rifle.json', '--operations', '-'],
           stdin=json.dumps([{'kind': 'equipment', 'slot': 'chest', 'id': 'primary_weapon_police_m4'}]), expect=3)
    record(err / 'invalid-context.json', ['build', 'resolve', '--input', 'striker-rifle.json', '--context', '-'],
           stdin=json.dumps({'not_an_assumption': 'true'}), expect=3)
    record(err / 'missing-input.json', ['build', 'resolve', '--input', 'missing.json'], expect=4)
    record(err / 'output-exists.json', ['build', 'new', '--output', 'striker-rifle.json'], expect=4)
    record(err / 'no-session.json', ['session', 'inspect', '--session-dir', 'no-sessions'], expect=4)

    sessions()

    files = sorted(p for p in ROOT.rglob('*') if p.is_file())
    write(ROOT / 'INDEX.txt', ''.join(
        f'{hashlib.sha256(p.read_bytes()).hexdigest()}  {p.relative_to(ROOT).as_posix()}\n' for p in files))
    shutil.rmtree(WORK, ignore_errors=True)
    print(f'{len(files)} files from planner {version}')


main()
