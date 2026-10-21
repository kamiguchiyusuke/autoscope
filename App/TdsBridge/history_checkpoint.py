"""Restore verified custom-symbol bar/index files after an owned tester run.

No MT5 binary format is guessed or partially trusted. Exact verified bytes are
saved before the test and restored afterwards. Changed tick archives, symbol
specifications, builds or file sets abort restoration and invalidate reuse.
"""
import hashlib
import json
import re
import shutil
import time
import uuid
from pathlib import Path
from verified_imports import plain, digest, fingerprint

OWNER = 'Auto Scope verified history checkpoint v1'
POOL_OWNER = 'Auto Scope reusable verified snapshots v1'


def write_json(path, value):
    tmp = path.with_suffix('.tmp')
    tmp.write_text(json.dumps(value, separators=(',', ':')), encoding='utf-8')
    tmp.replace(path)


def pool(proof):
    folder = plain(proof.cache/'verified-snapshots'/proof.path.stem)
    folder.mkdir(parents=True, exist_ok=True)
    index = folder/'index.json'
    try:
        saved = json.loads(index.read_text(encoding='utf-8'))
        if saved.get('owner') == POOL_OWNER and isinstance(saved.get('files'), dict):
            return folder, saved
    except (OSError, ValueError, AttributeError):
        pass
    return folder, dict(owner=POOL_OWNER, files={})


def copy_snapshot(source, folder):
    temporary = folder/(uuid.uuid4().hex+'.tmp')
    h = hashlib.sha256()
    try:
        with source.open('rb') as reader, temporary.open('xb') as writer:
            while chunk := reader.read(4*1024**2):
                writer.write(chunk)
                h.update(chunk)
        sha = h.hexdigest()
        destination = folder/(sha+'.bin')
        temporary.replace(destination)
        return dict(name=destination.name, sha256=sha, fingerprint=fingerprint(destination))
    finally:
        temporary.unlink(missing_ok=True)


def prune_pool(folder, index):
    """Only obsolete, owned snapshot blobs; never touch terminal/source data."""
    wanted = {e['name'] for e in index['files'].values()}
    for p in folder.iterdir():
        if (re.fullmatch('[0-9a-f]{64}\\.bin', p.name) and p.name not in wanted
                and not p.is_symlink() and not p.is_junction() and p.is_file()):
            p.unlink()


def cleanup(folder):
    try:
        folder = plain(folder)
        m = json.loads((folder/'checkpoint.json').read_text(encoding='utf-8'))
        if m.get('owner') != OWNER or not re.fullmatch('[0-9a-f]{32}', folder.name):
            return
        files = list(folder.iterdir())
        if any(p.is_symlink() or p.is_junction() or not p.is_file() or
               (p.name != 'checkpoint.json' and not re.fullmatch('[0-9a-f]{64}\\.bin', p.name)) for p in files):
            return
        for p in files:
            p.unlink()
        folder.rmdir()
    except (OSError, ValueError):
        pass


def create(proof, *, freshly_checked=False):
    # A just-created registry already compared fresh target metadata with the
    # saved proof. In that case avoid an immediate duplicate scan. The mandatory
    # post-capture target + proof checks below still cover the entire operation.
    if not proof.entries or (not freshly_checked and proof.target() != proof.snapshot):
        return None
    data = plain(proof.terminal['data'])
    files = {k: v for k, v in proof.snapshot['files'].items()
             if k.lower().endswith('.hcc') or k.lower().endswith('ticks.dat')}
    parent = plain(proof.cache/'checkpoints')
    parent.mkdir(exist_ok=True)
    # Only expired, explicitly owned temporary snapshots are reclaimed.
    for old in parent.iterdir():
        if old.is_dir() and time.time()-old.stat().st_mtime > 86400:
            cleanup(old)
    token = uuid.uuid4().hex
    folder = parent/token
    folder.mkdir()
    state = dict(owner=OWNER, created=time.time(), proof=proof.path.name,
                 proof_sha256=digest(proof.path), target=proof.snapshot, files={}, pooled=True)
    (folder/'checkpoint.json').write_text(json.dumps(state), encoding='utf-8')
    try:
        storage, index = pool(proof)
        selected = {}
        for relative in files:
            src = plain(data/relative)
            entry = index['files'].get(relative)
            try:
                reusable = (entry is not None and entry['source'] == files[relative] and
                            re.fullmatch('[0-9a-f]{64}\\.bin',entry['name']) and
                            fingerprint(storage/entry['name']) == entry['fingerprint'])
            except (OSError, ValueError, KeyError, TypeError):
                reusable = False
            if not reusable:
                if shutil.disk_usage(proof.cache).free < src.stat().st_size + 512*1024**2:
                    raise OSError('Insufficient space for a new verified snapshot')
                entry = dict(copy_snapshot(src,storage), source=files[relative])
            selected[relative] = entry
            state['files'][relative] = entry
        if proof.target() != proof.snapshot or digest(proof.path) != state['proof_sha256']:
            raise ValueError('History changed while capturing checkpoint')
        index['files'] = selected
        write_json(storage/'index.json',index)
        prune_pool(storage,index)
        (folder/'checkpoint.json').write_text(json.dumps(state, separators=(',', ':')), encoding='utf-8')
        return token
    except Exception:
        cleanup(folder)
        raise


def finish(proof, token):
    if not isinstance(token, str) or not re.fullmatch('[0-9a-f]{32}', token):
        return dict(ok=False, reason='Invalid checkpoint')
    folder = plain(proof.cache/'checkpoints'/token)
    restored = 0
    try:
        state = json.loads((folder/'checkpoint.json').read_text(encoding='utf-8'))
        if (state.get('owner') != OWNER or state['proof'] != proof.path.name or
                digest(proof.path) != state['proof_sha256']):
            raise ValueError('Verification context changed')
        saved = state['target']
        expected = {k for k in saved['files']
                    if k.lower().endswith('.hcc') or k.lower().endswith('ticks.dat')}
        if set(state['files']) != expected:
            raise ValueError('Invalid checkpoint file set')
        storage = plain(proof.cache/'verified-snapshots'/proof.path.stem) if state.get('pooled') else folder
        current = proof.target()
        if (current['code'] != saved['code'] or current['executable'] != saved['executable'] or
                set(current['files']) != set(saved['files'])):
            raise ValueError('Build or history file set changed')
        for key, value in saved['files'].items():
            if key not in state['files'] and current['files'][key] != value:
                raise ValueError('Tick archive or symbol specification changed')
        changed = [k for k in state['files'] if current['files'][k] != saved['files'][k]]
        # Validate every snapshot before making the first replacement.
        for key in changed:
            entry = state['files'][key]
            if not re.fullmatch('[0-9a-f]{64}\\.bin', entry['name']):
                raise ValueError('Invalid snapshot name')
            if digest(storage/entry['name']) != entry['sha256']:
                raise ValueError('Snapshot integrity mismatch')
        data = plain(proof.terminal['data'])
        for key in changed:
            destination = plain(data/key)
            # Keys must be precisely the authoritative paths collected by target().
            if str(destination.relative_to(data)) != key:
                raise ValueError('Invalid history destination')
            entry = state['files'][key]
            if digest(destination) != entry['sha256']:
                temporary = destination.with_name(destination.name+'.autoscope-'+token)
                try:
                    shutil.copyfile(storage/entry['name'], temporary)
                    if digest(temporary) != entry['sha256']:
                        raise ValueError('Restoration copy mismatch')
                    temporary.replace(destination)
                finally:
                    temporary.unlink(missing_ok=True)
                restored += 1
        updated = proof.target()
        if any(updated['files'][k] != v for k, v in saved['files'].items() if k not in state['files']):
            raise ValueError('Tick archive changed during restoration')
        ledger = json.loads(proof.path.read_text(encoding='utf-8'))
        ledger['target'] = updated
        temporary = proof.path.with_suffix('.tmp')
        temporary.write_text(json.dumps(ledger, separators=(',', ':')), encoding='utf-8')
        temporary.replace(proof.path)
        if state.get('pooled'):
            storage,index = pool(proof)
            for key,entry in state['files'].items():
                cached = index['files'].get(key)
                if cached == entry and fingerprint(storage/entry['name']) == entry['fingerprint']:
                    cached['source'] = updated['files'][key]
            write_json(storage/'index.json',index)
        return dict(ok=True, restored_files=restored)
    except (OSError, ValueError, KeyError, TypeError) as e:
        return dict(ok=False, reason=str(e))
    finally:
        cleanup(folder)
