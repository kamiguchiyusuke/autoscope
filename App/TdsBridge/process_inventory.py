"""Fresh, read-only Windows process probes without launching helper processes.

An inaccessible/failed query is treated as running: never authorize cleanup or
history replacement from an incomplete process inventory.
"""
import ctypes
import os
from functools import lru_cache
from pathlib import Path


@lru_cache(maxsize=1)
def api():
    from ctypes import wintypes as w
    class Entry(ctypes.Structure):
        _fields_=[('size',w.DWORD),('usage',w.DWORD),('pid',w.DWORD),
                  ('heap',ctypes.c_size_t),('module',w.DWORD),('threads',w.DWORD),
                  ('parent',w.DWORD),('priority',w.LONG),('flags',w.DWORD),
                  ('name',w.WCHAR*260)]
    k=ctypes.WinDLL('kernel32',use_last_error=True)
    k.CreateToolhelp32Snapshot.argtypes=[w.DWORD,w.DWORD]
    k.CreateToolhelp32Snapshot.restype=w.HANDLE
    for name in ('Process32FirstW','Process32NextW'):
        f=getattr(k,name);f.argtypes=[w.HANDLE,ctypes.POINTER(Entry)];f.restype=w.BOOL
    k.CloseHandle.argtypes=[w.HANDLE];k.CloseHandle.restype=w.BOOL
    k.OpenProcess.argtypes=[w.DWORD,w.BOOL,w.DWORD];k.OpenProcess.restype=w.HANDLE
    k.QueryFullProcessImageNameW.argtypes=[w.HANDLE,w.DWORD,w.LPWSTR,ctypes.POINTER(w.DWORD)]
    k.QueryFullProcessImageNameW.restype=w.BOOL
    return k,Entry


def processes():
    k,Entry=api()
    handle=k.CreateToolhelp32Snapshot(2,0)  # TH32CS_SNAPPROCESS
    if handle==ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        entry=Entry();entry.size=ctypes.sizeof(entry);rows=[]
        ok=k.Process32FirstW(handle,ctypes.byref(entry))
        while ok:
            rows.append((entry.pid,entry.name))
            ok=k.Process32NextW(handle,ctypes.byref(entry))
        error=ctypes.get_last_error()
        if error!=18:  # ERROR_NO_MORE_FILES is the only successful end.
            raise ctypes.WinError(error)
        return rows
    finally:
        k.CloseHandle(handle)


def image_path(pid):
    from ctypes import wintypes as w
    k,_=api()
    handle=k.OpenProcess(0x1000,False,pid)  # QUERY_LIMITED_INFORMATION only.
    if not handle:
        error=ctypes.get_last_error()
        if error==87:return None  # PID exited between snapshot and open.
        raise ctypes.WinError(error)
    try:
        buffer=ctypes.create_unicode_buffer(32768);size=w.DWORD(len(buffer))
        if not k.QueryFullProcessImageNameW(handle,0,buffer,ctypes.byref(size)):
            raise ctypes.WinError(ctypes.get_last_error())
        return buffer.value
    finally:
        k.CloseHandle(handle)


def running_name(name):
    if os.name!='nt':return False
    try:
        return any(image.casefold()==name.casefold() for _,image in processes())
    except OSError:
        return True


def running_path(exe):
    if os.name!='nt':return False
    try:
        want=os.path.normcase(os.path.abspath(exe))
        for pid,name in processes():
            if name.casefold()!=Path(exe).name.casefold():continue
            actual=image_path(pid)
            if actual and os.path.normcase(os.path.abspath(actual))==want:return True
        return False
    except OSError:
        return True
