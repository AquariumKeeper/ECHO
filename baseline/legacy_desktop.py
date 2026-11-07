"""Launch and close the legacy Go desktop for baseline scripts (Windows only).

The window runs with APPDATA and its session registry inside a scratch folder,
so the user's own library and live sessions are never touched.
"""
import ctypes
import ctypes.wintypes as wt
import json
import os
import shutil
import subprocess
import time
from pathlib import Path

user32 = ctypes.windll.user32


def visible_window(pid):
    found = []

    @ctypes.WINFUNCTYPE(wt.BOOL, wt.HWND, wt.LPARAM)
    def each(hwnd, _):
        owner = wt.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(owner))
        if owner.value == pid and user32.IsWindowVisible(hwnd):
            found.append(hwnd)
        return True

    user32.EnumWindows(each, 0)
    return found[0] if found else None


def launch(release, work):
    """Start character-planner.exe; return (process, ms to window, ms to answering session)."""
    release, work = Path(release), Path(work)
    sessions = work / 'sessions'
    shutil.rmtree(sessions, ignore_errors=True)
    (work / 'appdata').mkdir(parents=True, exist_ok=True)
    env = dict(os.environ, APPDATA=str(work / 'appdata'), CHARACTER_PLANNER_SESSION_DIR=str(sessions))
    start = time.perf_counter()
    proc = subprocess.Popen([str(release / 'character-planner.exe')], cwd=release, env=env)
    window = session = None
    while (window is None or session is None) and time.perf_counter() - start < 60:
        if window is None and visible_window(proc.pid):
            window = (time.perf_counter() - start) * 1000
        if session is None:
            p = subprocess.run([str(release / 'planner.exe'), '--json', 'session', 'list', '--session-dir', str(sessions)],
                               capture_output=True, text=True, encoding='utf-8')
            if any(s.get('available') for s in json.loads(p.stdout)['result']):
                session = (time.perf_counter() - start) * 1000
        time.sleep(0.005)
    if session is None:
        close(proc)
        raise RuntimeError('the desktop session did not become available within 60 s')
    return proc, window, session


def close(proc):
    hwnd = visible_window(proc.pid)
    if hwnd:
        user32.PostMessageW(hwnd, 0x0010, 0, 0)  # WM_CLOSE
    try:
        proc.wait(10)
    except subprocess.TimeoutExpired:
        proc.kill()
        proc.wait()
