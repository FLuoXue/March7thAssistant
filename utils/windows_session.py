"""Windows 会话标识。模块导入不会创建桌面或改变系统设置。"""
import os
import sys


def process_session_id(pid=None):
    if sys.platform != 'win32':
        return 0
    import ctypes
    from ctypes import wintypes
    session = wintypes.DWORD()
    function = ctypes.WinDLL('kernel32', use_last_error=True).ProcessIdToSessionId
    function.argtypes = (wintypes.DWORD, ctypes.POINTER(wintypes.DWORD))
    function.restype = wintypes.BOOL
    if not function(os.getpid() if pid is None else pid, ctypes.byref(session)):
        raise ctypes.WinError(ctypes.get_last_error())
    return session.value


def instance_scope():
    if sys.platform != 'win32':
        return ''
    import win32api
    import win32con
    import win32security
    token = win32security.OpenProcessToken(win32api.GetCurrentProcess(), win32con.TOKEN_QUERY)
    try:
        sid = win32security.GetTokenInformation(token, win32security.TokenUser)[0]
    finally:
        token.Close()
    return f'_{win32security.ConvertSidToStringSid(sid)}_{process_session_id()}'
