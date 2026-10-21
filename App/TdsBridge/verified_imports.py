"""Reuse a completed import only while its source and authoritative MT5 files are unchanged.

Windows ChangeTime is intentional: restoring LastWriteTime after a write must not
make a changed file look verified. Unknown filesystems/check failures fail closed.
Derived MT5 chart caches are excluded; authoritative HCC/TKC files are included.
"""
import ctypes
import hashlib
import json
import os
from pathlib import Path
from concurrent.futures import ThreadPoolExecutor
from functools import lru_cache

_volumes = {}


class SourceLease:
    """Keep validated source bytes read-only until the proof is committed.

    Delete sharing allows the bridge's temporary hardlinks to be removed.
    Replacing a source path is detected by its file identity before commit.
    """
    def __init__(self, stage):
        self.files = []
        from ctypes import wintypes
        kernel = ctypes.WinDLL('kernel32', use_last_error=True)
        create = kernel.CreateFileW
        create.argtypes = [wintypes.LPCWSTR, wintypes.DWORD, wintypes.DWORD,
                           ctypes.c_void_p, wintypes.DWORD, wintypes.DWORD, wintypes.HANDLE]
        create.restype = wintypes.HANDLE
        self.create = create
        self.close_handle = kernel.CloseHandle
        self.close_handle.argtypes = [wintypes.HANDLE]
        self.query = kernel.GetFileInformationByHandleEx
        self.query.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
        self.query.restype = wintypes.BOOL
        stage = plain(stage)
        m = json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
        names = ['manifest.json']
        for day in m['days']:
            names.extend(day[k] for k in ('ticks_file', 'bars_file'))
        try:
            for name in names:
                if Path(name).name != name or '/' in name or '\\' in name:
                    raise ValueError('Invalid stage filename')
                p = stage/name
                if getattr(p.lstat(), 'st_file_attributes', 0) & 0x400:
                    raise ValueError('Reparse point in stage')
                handle = create(str(p), 0x80000000, 1 | 4, None, 3, 0, None)
                if handle == ctypes.c_void_p(-1).value:
                    raise ctypes.WinError(ctypes.get_last_error())
                self.files.append((p, handle))
        except Exception:
            self.close()
            raise

    def verify(self):
        def identity(handle):
            # FILE_ID_INFO: volume serial + 128-bit file identity.
            info = ctypes.create_string_buffer(24)
            if not self.query(handle, 18, info, len(info)):
                raise ctypes.WinError(ctypes.get_last_error())
            return info.raw
        for p, handle in self.files:
            current = self.create(str(p), 0x80000000, 1 | 4, None, 3, 0, None)
            if current == ctypes.c_void_p(-1).value:
                raise ctypes.WinError(ctypes.get_last_error())
            try:
                if identity(handle) != identity(current):
                    raise ValueError('Source path changed during verification')
            finally:
                self.close_handle(current)

    def close(self):
        for _, handle in self.files:
            self.close_handle(handle)
        self.files.clear()


def plain(path):
    path = Path(path).absolute()
    if any(p.is_symlink() or p.is_junction() for p in (path, *path.parents)):
        raise ValueError('Reparse point is not a verified data path')
    return path


def digest(path):
    with plain(path).open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()


@lru_cache(maxsize=1)
def _metadata_api():
    from ctypes import wintypes
    kernel = ctypes.WinDLL('kernel32', use_last_error=True)
    class Basic(ctypes.Structure):
        _fields_ = [('creation', ctypes.c_longlong), ('access', ctypes.c_longlong),
                    ('write', ctypes.c_longlong), ('change', ctypes.c_longlong),
                    ('attributes', wintypes.DWORD)]
    query = kernel.GetFileInformationByHandleEx
    query.argtypes = [wintypes.HANDLE, ctypes.c_int, ctypes.c_void_p, wintypes.DWORD]
    query.restype = wintypes.BOOL
    create = kernel.CreateFileW
    create.argtypes = [wintypes.LPCWSTR,wintypes.DWORD,wintypes.DWORD,ctypes.c_void_p,
                       wintypes.DWORD,wintypes.DWORD,wintypes.HANDLE]
    create.restype = wintypes.HANDLE
    close = kernel.CloseHandle
    close.argtypes = [wintypes.HANDLE]
    return kernel, Basic, query, create, close


def fingerprint(path, checked_parent=False):
    path = Path(path).absolute() if checked_parent else plain(path)
    if os.name != 'nt':
        raise OSError('Verified reuse requires Windows change-time tracking')
    kernel, Basic, query, create, close = _metadata_api()
    anchor = path.anchor
    if anchor not in _volumes:
        fs = ctypes.create_unicode_buffer(32)
        if not kernel.GetVolumeInformationW(ctypes.c_wchar_p(anchor), None, 0, None, None, None, fs, len(fs)):
            raise ctypes.WinError(ctypes.get_last_error())
        _volumes[anchor] = fs.value
    if _volumes[anchor] not in ('NTFS', 'ReFS'):
        raise OSError('Filesystem does not provide reliable change-time tracking')
    # Read attributes only: opening the payload can trigger expensive data scans.
    handle = create(str(path),0x80,7,None,3,0x00200000,None)
    if handle == ctypes.c_void_p(-1).value:
        raise ctypes.WinError(ctypes.get_last_error())
    try:
        info = Basic()
        identity = ctypes.create_string_buffer(24)
        standard = ctypes.create_string_buffer(24)
        if (not query(handle,0,ctypes.byref(info),ctypes.sizeof(info)) or
                not query(handle,18,identity,24) or not query(handle,1,standard,24)):
            raise ctypes.WinError(ctypes.get_last_error())
        if not info.change or info.attributes & 0x400:
            raise OSError('Unreliable file identity')
        return [int.from_bytes(identity.raw[:8],'little'),
                int.from_bytes(identity.raw[8:24],'little'),
                int.from_bytes(standard.raw[8:16],'little'),info.write,info.change]
    finally:
        close(handle)

@lru_cache(maxsize=1)
def _native_metadata():
    library = Path(__file__).with_name('AutoScopeMetadata.dll')
    if os.name != 'nt' or not library.is_file():
        return None
    class Result(ctypes.Structure):
        _fields_ = [(name, ctypes.c_uint64) for name in ('volume', 'low', 'high', 'size', 'write', 'change')]
    dll = ctypes.WinDLL(str(plain(library)))
    call = dll.metadata
    call.argtypes = [ctypes.POINTER(ctypes.c_wchar_p), ctypes.c_uint,
                     ctypes.POINTER(Result), ctypes.c_uint]
    call.restype = ctypes.c_uint32
    return call, Result


def fingerprints(paths):
    """Fresh handle-based checks; callers validate each common parent path.

    Directory enumeration is deliberately not used: NTFS hardlink directory
    entries may contain stale metadata after a write through another name.
    """
    paths = [Path(p).absolute() for p in paths]
    if not paths:
        return []
    native = _native_metadata()
    if native is None:
        with ThreadPoolExecutor(max_workers=8) as workers:
            return list(workers.map(lambda p: fingerprint(p, checked_parent=True), paths))
    # Preserve the filesystem capability check on every involved volume.
    for anchor in {p.anchor for p in paths}:
        fingerprint(next(p for p in paths if p.anchor == anchor), checked_parent=True)
    call, Result = native
    names = (ctypes.c_wchar_p * len(paths))(*(str(p) for p in paths))
    values = (Result * len(paths))()
    error = call(names, len(paths), values, min(12, len(paths)))
    if error:
        raise ctypes.WinError(error)
    return [[v.volume, v.low | (v.high << 64), v.size, v.write, v.change] for v in values]


class VerifiedImports:
    def __init__(self, cache, terminal, symbol, origin, code_files):
        self.cache = plain(cache)
        self.terminal = terminal
        self.symbol = symbol
        self.origin = origin
        self.code_files = code_files
        key = json.dumps([str(plain(terminal['data'])), str(plain(terminal['exe'])),
                          terminal.get('portable', False), symbol, origin])
        self.path = self.cache/'verified-imports'/(hashlib.sha256(key.encode()).hexdigest()+'.json')
        self.entries = {}
        try:
            self.snapshot = self.target()
        except (OSError, ValueError):
            self.snapshot = None
        try:
            saved = json.loads(self.path.read_text(encoding='utf-8'))
            if isinstance(saved, dict) and self.snapshot is not None and saved.get('version') == 1 and saved.get('target') == self.snapshot and isinstance(saved.get('entries'), dict):
                self.entries = saved['entries']
        except (OSError, ValueError, KeyError, TypeError):
            pass

    def target(self):
        data = plain(self.terminal['data'])
        files = {}
        for kind, suffix in [('history', '.hcc'), ('ticks', '.tkc')]:
            folder = plain(data/'bases/Custom'/kind/self.symbol)
            matches = sorted(folder.glob('*'+suffix))
            if not matches:
                raise OSError('MT5 history is missing')
            files.update((str(p.relative_to(data)), value) for p, value in zip(matches, fingerprints(matches)))
            plain(folder)
        # Small metadata files use content hashes, so identical rewrites do not
        # invalidate a large history. A real specification/index change does.
        for p in [data/'bases/symbols.custom.dat',
                  data/'bases/Custom/ticks'/self.symbol/'ticks.dat']:
            if p.exists():
                files[str(p.relative_to(data))] = digest(p)
            elif p.name == 'symbols.custom.dat':
                raise OSError('Custom symbol metadata is missing')
        return dict(files=files, executable=fingerprint(self.terminal['exe']),
                    code=[digest(p) for p in self.code_files])

    def source(self, stage):
        stage = plain(stage)
        if stage.name != 'stage' or stage.parent.parent != self.cache:
            raise ValueError('Stage is outside the managed cache')
        m = json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
        files = {'manifest.json': digest(stage/'manifest.json')}
        names = []
        for day in m['days']:
            for key in ('ticks_file', 'bars_file'):
                name = day[key]
                if Path(name).name != name or '/' in name or '\\' in name:
                    raise ValueError('Invalid stage filename')
                names.append(name)
        files.update(zip(names, fingerprints([stage/name for name in names])))
        plain(stage)
        return m, files

    def reuse(self, stage, source=None, symbol=None):
        try:
            entry = self.entries[str(plain(stage))]
            m, files = self.source(stage)
            if ((source is not None and m['source'] != source) or
                    (symbol is not None and m['symbol'] != symbol) or
                    files != entry['files'] or m != entry['manifest'] or
                    self.target() != self.snapshot):
                return None
            result = dict(entry['result'], verified_reuse=True, written_ticks=0,
                          reused_ticks=m['ticks'], reused_bars=m['bars'])
            return m, result
        except (OSError, ValueError, KeyError, TypeError):
            return None

    def remember(self, stage, manifest, result):
        if result.get('ok') is not True or any(result.get(k) != manifest[k] for k in
                ('ticks', 'bars', 'first_msc', 'last_msc', 'tick_hash')):
            raise ValueError('Only a fully verified successful import can be remembered')
        m, files = self.source(stage)
        if m != manifest:
            raise ValueError('Manifest changed during import')
        # This object was created before the owned import. Previously valid
        # ranges remain valid when that import adds/replaces only its own dates.
        entries = {k: v for k, v in self.entries.items() if Path(k).is_dir()}
        entries[str(plain(stage))] = dict(files=files, manifest=m, result=result)
        saved = dict(version=1, target=self.target(), entries=entries)
        plain(self.path.parent).mkdir(parents=True, exist_ok=True)
        tmp = self.path.with_suffix('.tmp')
        tmp.write_text(json.dumps(saved, separators=(',', ':')), encoding='utf-8')
        tmp.replace(self.path)
