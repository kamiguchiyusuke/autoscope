"""Plan immutable cached ranges and export only uncovered calendar intervals."""
import json
import os
from datetime import date, timedelta
from pathlib import Path


def plan(root, first, last, source, symbol, digits):
    candidates = []
    for stage in Path(root).glob('*/stage'):
        try:
            if stage.is_symlink() or stage.is_junction() or stage.parent.is_symlink() or stage.parent.is_junction():
                continue
            m = json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
            a, b = date.fromisoformat(m['start']), date.fromisoformat(m['end'])
            if a > b or m['source'] != source or m['symbol'] != symbol or m['digits'] != digits:
                continue
            # Planning needs only membership/type. Import validation still checks
            # every file's identity/change time (or its full contents).
            with os.scandir(stage) as entries:
                payloads = {e.name for e in entries if e.is_file(follow_symlinks=False)}
            if not m['days'] or not all(d[k] in payloads for d in m['days'] for k in ('ticks_file', 'bars_file')):
                continue
            candidates.append((a, b, stage))
        except (OSError, ValueError, KeyError, TypeError):
            continue
    result = []
    cursor = first
    while cursor <= last:
        covering = [x for x in candidates if x[0] <= cursor <= x[1]]
        if covering:
            finishing = [x for x in covering if x[1] >= last]
            a, b, stage = min(finishing, key=lambda x: ((x[1]-x[0]).days, str(x[2]))) if finishing else max(covering, key=lambda x: (x[1], x[0], str(x[2])))
            result.append((cursor, min(last, b), stage))
            cursor = b + timedelta(days=1)
        else:
            following = [a for a, _, _ in candidates if cursor < a <= last]
            stop = min(following)-timedelta(days=1) if following else last
            result.append((cursor, stop, None))
            cursor = stop + timedelta(days=1)
    return result
