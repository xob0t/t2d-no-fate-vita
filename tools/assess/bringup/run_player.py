"""Run a Windows test player on a desktop of its own (CreateDesktop), so it never takes the user's
screen, keyboard or mouse: a window on another desktop can't come in front of this one. Adapted from
butcher-vita's tools/downgrade/run_player.py.

The player runs windowed at a small size, with its log (-logFile -) on a file handle it inherits, and
Windows' per-app mute on its audio sessions for the whole run (mute_players.ps1). A player still
running after --timeout seconds is killed with whatever it started (exit code 124).

    python -I tools/assess/bringup/run_player.py <exe> <log> [--timeout s] [--size WxH] [-- player args...]

The probe build should also make cursor locking and clipping no-ops: Windows' cursor clip is global,
so a player on a hidden desktop can still confine the user's mouse.
"""

import argparse
import ctypes
import ctypes.wintypes as wt
import os
import subprocess
import sys
from pathlib import Path

kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
user32 = ctypes.WinDLL('user32', use_last_error=True)
DESKTOP = 't2d-probe-runs'
GENERIC_ALL = 0x10000000
GENERIC_WRITE = 0x40000000
FILE_SHARE_READ_WRITE = 0x3
CREATE_ALWAYS = 2
CREATE_NO_WINDOW = 0x08000000
STARTF_USESTDHANDLES = 0x100
EXTENDED_STARTUPINFO_PRESENT = 0x80000
PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x20002
WAIT_TIMEOUT = 0x102
TIMED_OUT = 124
INVALID_HANDLE = ctypes.c_void_p(-1).value


class StartupInfo(ctypes.Structure):
    _fields_ = [
        ('cb', wt.DWORD), ('lpReserved', wt.LPWSTR), ('lpDesktop', wt.LPWSTR), ('lpTitle', wt.LPWSTR),
        ('dwX', wt.DWORD), ('dwY', wt.DWORD), ('dwXSize', wt.DWORD), ('dwYSize', wt.DWORD),
        ('dwXCountChars', wt.DWORD), ('dwYCountChars', wt.DWORD), ('dwFillAttribute', wt.DWORD),
        ('dwFlags', wt.DWORD), ('wShowWindow', wt.WORD), ('cbReserved2', wt.WORD),
        ('lpReserved2', ctypes.c_void_p), ('hStdInput', wt.HANDLE), ('hStdOutput', wt.HANDLE),
        ('hStdError', wt.HANDLE),
    ]


class StartupInfoEx(ctypes.Structure):
    _fields_ = [('StartupInfo', StartupInfo), ('lpAttributeList', ctypes.c_void_p)]


class ProcessInformation(ctypes.Structure):
    _fields_ = [('hProcess', wt.HANDLE), ('hThread', wt.HANDLE), ('dwProcessId', wt.DWORD), ('dwThreadId', wt.DWORD)]


class SecurityAttributes(ctypes.Structure):
    _fields_ = [('nLength', wt.DWORD), ('lpSecurityDescriptor', ctypes.c_void_p), ('bInheritHandle', wt.BOOL)]


user32.CreateDesktopW.restype = wt.HANDLE
user32.CreateDesktopW.argtypes = [wt.LPCWSTR, wt.LPCWSTR, ctypes.c_void_p, wt.DWORD, wt.DWORD, ctypes.c_void_p]
kernel32.CreateFileW.restype = wt.HANDLE
kernel32.CreateFileW.argtypes = [wt.LPCWSTR, wt.DWORD, wt.DWORD, ctypes.POINTER(SecurityAttributes), wt.DWORD,
                                 wt.DWORD, wt.HANDLE]
kernel32.InitializeProcThreadAttributeList.argtypes = [ctypes.c_void_p, wt.DWORD, wt.DWORD,
                                                       ctypes.POINTER(ctypes.c_size_t)]
kernel32.UpdateProcThreadAttribute.argtypes = [ctypes.c_void_p, wt.DWORD, ctypes.c_size_t, ctypes.c_void_p,
                                               ctypes.c_size_t, ctypes.c_void_p, ctypes.c_void_p]
kernel32.DeleteProcThreadAttributeList.argtypes = [ctypes.c_void_p]
kernel32.CreateProcessW.argtypes = [wt.LPCWSTR, wt.LPWSTR, ctypes.c_void_p, ctypes.c_void_p, wt.BOOL, wt.DWORD,
                                    ctypes.c_void_p, wt.LPCWSTR, ctypes.POINTER(StartupInfoEx),
                                    ctypes.POINTER(ProcessInformation)]
kernel32.WaitForSingleObject.argtypes = [wt.HANDLE, wt.DWORD]
kernel32.GetExitCodeProcess.argtypes = [wt.HANDLE, ctypes.POINTER(wt.DWORD)]
kernel32.CloseHandle.argtypes = [wt.HANDLE]


def run(exe: Path, log: Path, arguments, timeout: float, width: int, height: int) -> int:
    desktop = user32.CreateDesktopW(DESKTOP, None, None, 0, GENERIC_ALL, None)
    if not desktop:
        raise OSError(ctypes.get_last_error(), 'CreateDesktop failed')
    log.parent.mkdir(parents=True, exist_ok=True)
    command = subprocess.list2cmdline([
        str(exe.resolve()), '-screen-fullscreen', '0', '-screen-width', str(width), '-screen-height', str(height),
        '-popupwindow', '-logFile', '-', *arguments,
    ])
    inherit = SecurityAttributes(ctypes.sizeof(SecurityAttributes), None, True)
    output = kernel32.CreateFileW(str(log.resolve()), GENERIC_WRITE, FILE_SHARE_READ_WRITE, ctypes.byref(inherit),
                                  CREATE_ALWAYS, 0, None)
    if output in (None, INVALID_HANDLE):
        raise OSError(ctypes.get_last_error(), f'CreateFile failed for {log}')
    size = ctypes.c_size_t()
    kernel32.InitializeProcThreadAttributeList(None, 1, 0, ctypes.byref(size))
    attributes = ctypes.create_string_buffer(size.value)
    handles = (wt.HANDLE * 1)(output)
    info = ProcessInformation()
    try:
        if not kernel32.InitializeProcThreadAttributeList(attributes, 1, 0, ctypes.byref(size)):
            raise OSError(ctypes.get_last_error(), 'InitializeProcThreadAttributeList failed')
        try:
            if not kernel32.UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles,
                                                      ctypes.sizeof(handles), None, None):
                raise OSError(ctypes.get_last_error(), 'UpdateProcThreadAttribute failed')
            startup = StartupInfoEx()
            startup.StartupInfo.cb = ctypes.sizeof(StartupInfoEx)
            startup.StartupInfo.lpDesktop = DESKTOP
            startup.StartupInfo.dwFlags = STARTF_USESTDHANDLES
            startup.StartupInfo.hStdOutput = startup.StartupInfo.hStdError = output
            startup.lpAttributeList = ctypes.cast(attributes, ctypes.c_void_p)
            if not kernel32.CreateProcessW(None, command, None, None, True,
                                           CREATE_NO_WINDOW | EXTENDED_STARTUPINFO_PRESENT, None, str(exe.parent),
                                           ctypes.byref(startup), ctypes.byref(info)):
                raise OSError(ctypes.get_last_error(), 'CreateProcess failed')
        finally:
            kernel32.DeleteProcThreadAttributeList(attributes)
    finally:
        kernel32.CloseHandle(output)
    muter = subprocess.Popen(
        ['powershell', '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File',
         str(Path(__file__).with_name('mute_players.ps1')), '-Watch', '-Name', exe.stem, '-Until', str(os.getpid())],
        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, creationflags=CREATE_NO_WINDOW)
    try:
        waited = kernel32.WaitForSingleObject(info.hProcess, int(timeout * 1000))
        if waited == WAIT_TIMEOUT:
            subprocess.run(['taskkill', '/T', '/F', '/PID', str(info.dwProcessId)], capture_output=True, check=False)
            return TIMED_OUT
        code = wt.DWORD()
        kernel32.GetExitCodeProcess(info.hProcess, ctypes.byref(code))
        return code.value
    finally:
        muter.kill()
        kernel32.CloseHandle(info.hThread)
        kernel32.CloseHandle(info.hProcess)


def main():
    argv = sys.argv[1:]
    extra = []
    if '--' in argv:
        i = argv.index('--')
        argv, extra = argv[:i], argv[i + 1:]
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument('exe', type=Path)
    ap.add_argument('log', type=Path)
    ap.add_argument('--timeout', type=float, default=60)
    ap.add_argument('--size', default='480x270')
    a = ap.parse_args(argv)
    w, h = (int(v) for v in a.size.split('x'))
    code = run(a.exe, a.log, extra, a.timeout, w, h)
    print(f'player exit {code}{" (timed out, killed)" if code == TIMED_OUT else ""}; log {a.log}')
    sys.exit(code)


if __name__ == '__main__':
    main()
