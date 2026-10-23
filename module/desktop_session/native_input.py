"""子会话本地 SendInput；不通过 RDP 发送相对移动。"""
import ctypes
import os
from ctypes import wintypes

from utils.windows_session import process_session_id


class MouseInput(ctypes.Structure):
    _fields_ = [('dx', wintypes.LONG), ('dy', wintypes.LONG), ('mouseData', wintypes.DWORD),
                ('dwFlags', wintypes.DWORD), ('time', wintypes.DWORD), ('dwExtraInfo', ctypes.c_size_t)]


class InputUnion(ctypes.Union):
    _fields_ = [('mi', MouseInput)]


class Input(ctypes.Structure):
    _fields_ = [('type', wintypes.DWORD), ('data', InputUnion)]


class GameMouse:
    def __init__(self, process_name):
        self.process_name = process_name
        self.user32 = ctypes.WinDLL('user32', use_last_error=True)
        self.kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
        self.user32.GetForegroundWindow.restype = wintypes.HWND
        self.user32.GetWindowThreadProcessId.argtypes = (wintypes.HWND, ctypes.POINTER(wintypes.DWORD))
        self.user32.GetWindowThreadProcessId.restype = wintypes.DWORD
        self.user32.SendInput.argtypes = (wintypes.UINT, ctypes.POINTER(Input), ctypes.c_int)
        self.user32.SendInput.restype = wintypes.UINT
        self.user32.GetAsyncKeyState.argtypes = (ctypes.c_int,)
        self.user32.GetAsyncKeyState.restype = ctypes.c_short
        self.kernel32.OpenProcess.argtypes = (wintypes.DWORD, wintypes.BOOL, wintypes.DWORD)
        self.kernel32.OpenProcess.restype = wintypes.HANDLE
        self.kernel32.QueryFullProcessImageNameW.argtypes = (wintypes.HANDLE, wintypes.DWORD, wintypes.LPWSTR, ctypes.POINTER(wintypes.DWORD))
        self.kernel32.QueryFullProcessImageNameW.restype = wintypes.BOOL
        self.kernel32.CloseHandle.argtypes = (wintypes.HANDLE,)
        self.kernel32.CloseHandle.restype = wintypes.BOOL

    def is_active(self):
        # RDP can deliver Alt before the host's next polling tick.
        if self.user32.GetAsyncKeyState(0x12) & 0x8000:
            return False
        hwnd = self.user32.GetForegroundWindow()
        if not hwnd:
            return False
        pid = wintypes.DWORD()
        if not self.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid)):
            return False
        handle = self.kernel32.OpenProcess(0x1000, False, pid.value)
        if not handle:
            return False
        try:
            buffer = ctypes.create_unicode_buffer(32768)
            length = wintypes.DWORD(len(buffer))
            if not self.kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(length)):
                return False
            return os.path.basename(buffer.value).casefold() == self.process_name().casefold()
        finally:
            self.kernel32.CloseHandle(handle)

    def send(self, samples):
        if not samples:
            return True
        if not self.is_active():
            return False
        # MOUSEEVENTF_MOVE | MOUSEEVENTF_MOVE_NOCOALESCE; never ABSOLUTE.
        inputs = (Input * len(samples))(*(Input(0, InputUnion(MouseInput(dx, dy, 0, 0x2001, 0, 0))) for dx, dy in samples))
        return self.user32.SendInput(len(inputs), inputs, ctypes.sizeof(Input)) == len(inputs)


def verify_pipe_server(handle, expected_pid):
    kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)
    function = kernel32.GetNamedPipeServerProcessId
    function.argtypes = (wintypes.HANDLE, ctypes.POINTER(wintypes.ULONG))
    function.restype = wintypes.BOOL
    pid = wintypes.ULONG()
    return bool(function(handle, ctypes.byref(pid)) and pid.value == expected_pid and
                process_session_id(pid.value) != process_session_id())
