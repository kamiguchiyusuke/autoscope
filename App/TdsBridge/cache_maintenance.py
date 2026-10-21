"""Bounded retention for successfully imported, application-owned TDS stages."""
import json
import math
import os
import re
import stat
import threading
import time
from pathlib import Path

MARKER = '.armada-cache.json'
OWNER = 'Armada Scope FX TDS cache v1'
MAX_BYTES = 16 * 1024**3
MAX_AGE = 30 * 86400
FOLDER = re.compile(r'[A-Za-z0-9_.-]{1,48}-\d{4}-\d{2}-\d{2}-\d{4}-\d{2}-\d{2}\Z')
BINARY = re.compile(r'(ticks|bars)-\d{8}\.bin\Z')


def linked(path, info=None):
    if info is not None:
        return bool(getattr(info,'st_file_attributes',0)&0x400) or stat.S_ISLNK(info.st_mode)
    return path.is_symlink() or path.is_junction()


def inspect(root, stage):
    """Reject every unknown file, directory and reparse point before deletion."""
    if stage.name != 'stage' or stage.parent.parent != root or not FOLDER.fullmatch(stage.parent.name):
        return None
    if any(linked(p) for p in (stage, *stage.parents)):
        return None
    if set(stage.parent.iterdir()) != {stage}:
        return None
    # Windows directory enumeration already supplies size/type/reparse metadata.
    # Reuse that single fresh inventory; deletion below still rechecks each path.
    files=[];size=0
    with os.scandir(stage) as entries:
        for entry in entries:
            p=Path(entry.path);info=entry.stat(follow_symlinks=False)
            if linked(p,info) or not stat.S_ISREG(info.st_mode):return None
            files.append(p);size+=info.st_size
    manifest = json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
    receipt = json.loads((stage/'import-result.json').read_text(encoding='utf-8-sig'))
    if receipt.get('ok') is not True or any(receipt.get(k) != manifest[k] for k in ('ticks','bars','first_msc','last_msc','tick_hash')):
        return None
    if not stage.parent.name.endswith('-'+manifest['start']+'-'+manifest['end']):
        return None
    allowed = {'manifest.json', 'import-result.json'}
    for day in manifest['days']:
        for kind in ('ticks', 'bars'):
            name = day[kind+'_file']
            if not isinstance(name,str) or not BINARY.fullmatch(name) or not name.startswith(kind+'-'):
                return None
            allowed.add(name)
    actual = {p.name for p in files}
    if actual - {MARKER} != allowed:
        return None
    if MARKER in actual and json.loads((stage/MARKER).read_text(encoding='utf-8')).get('owner') != OWNER:
        return None
    return files, size


def maintain(root, current_stages, bridge, *, now=None, max_bytes=MAX_BYTES, max_age=MAX_AGE):
    """Call only after successful import. Maintenance errors never fail that import."""
    removed = []
    try:
        root = Path(root).absolute()
        local = os.environ.get('LOCALAPPDATA')
        if not local or root != (Path(local)/'ArmadaScopeFX/TdsCache').absolute():
            return removed
        if bridge._bridge_lock_owner != (os.getpid(), threading.get_ident()):
            return removed
        if any(linked(p) for p in (root, *root.parents)) or bridge.running_process('terminal64.exe'):
            return removed
        now = time.time() if now is None else now
        current = {Path(p).absolute() for p in current_stages}
        for stage in current:
            try:
                if inspect(root, stage) is not None:
                    # No arbitrary cleanup paths are stored in ownership metadata.
                    (stage/MARKER).write_text(json.dumps(dict(owner=OWNER,last_used=now)),encoding='utf-8')
            except (OSError, ValueError, KeyError, TypeError, AttributeError):
                pass
        entries=[]
        for folder in root.iterdir():
            stage=folder/'stage'
            try:
                details=inspect(root,stage)
                if details is None:continue
                marker=json.loads((stage/MARKER).read_text(encoding='utf-8'))
                last=marker['last_used']
                if marker.get('owner')!=OWNER or type(last) not in (int,float) or not math.isfinite(last):continue
                entries.append((last,stage,details[1]))
            except (OSError,ValueError,KeyError,TypeError,AttributeError):continue
        total=sum(size for _,_,size in entries)
        for last,stage,size in sorted(entries):
            if stage in current or (now-last<max_age and total<=max_bytes):continue
            try:
                # Recheck ownership structure and liveness immediately before mutation.
                if bridge.running_process('terminal64.exe'):break
                details=inspect(root,stage)
                if details is None:continue
                # Keep the ownership marker until the generated payload is gone.
                for file in sorted(details[0],key=lambda p:p.name==MARKER):
                    if linked(file):raise OSError('reparse point appeared')
                    file.unlink()
                stage.rmdir()
                stage.parent.rmdir()
                total-=size
                removed.append(str(stage))
            except (OSError,ValueError,KeyError,TypeError,AttributeError):continue
    except (OSError,ValueError,KeyError,TypeError,AttributeError):
        pass
    return removed
