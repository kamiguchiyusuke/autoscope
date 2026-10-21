"""Local TDS -> MT5 bridge. No network service, third-party Python packages or trading."""
import argparse
import calendar
import csv
import hashlib
import json
import math
import os
import re
import shutil
import sqlite3
import struct
import subprocess
import sys
import time
import threading
import uuid
from contextlib import contextmanager, closing
from datetime import date, datetime, timedelta, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parent
FAST_EXE = ROOT.parent / 'AutoScope.exe'
if not FAST_EXE.exists(): FAST_EXE = ROOT.parent / 'ArmadaScopeFX.exe'
TDS_ROOT = Path(os.environ.get('LOCALAPPDATA', '.')) / 'Tick Data Suite'
TDS_EXE = Path(os.environ.get('ProgramFiles(x86)', 'C:/Program Files (x86)')) / 'eareview.net/Tick Data Suite/Tick Data Manager.exe'
COMMON = Path(os.environ.get('APPDATA', '.')) / 'MetaQuotes/Terminal/Common/Files/TDSBridge'
MOD = 2147483647
TICK = struct.Struct('<qdd')
BAR = struct.Struct('<qddddqiq')
_bridge_lock_owner = None


def save_json(path, value):
    path = Path(path)
    tmp = path.with_suffix(path.suffix + '.tmp')
    tmp.write_text(json.dumps(value, indent=2, ensure_ascii=False), encoding='utf-8')
    tmp.replace(path)


def date_range(start, end):
    a, b = date.fromisoformat(start), date.fromisoformat(end)
    if a > b:
        raise ValueError('開始日は終了日以前にしてください。')
    return a, b


def mark_work(path, kind, terminal=None):
    save_json(Path(path)/'.armada-work.json', dict(owner='Armada Scope FX', kind=kind,
              created=time.time(), pid=os.getpid(), terminal=terminal))


def process_alive(pid):
    if os.name != 'nt':
        try: os.kill(pid, 0); return True
        except ProcessLookupError: return False
        except PermissionError: return True
    import ctypes
    from ctypes import wintypes
    k = ctypes.WinDLL('kernel32', use_last_error=True)
    k.OpenProcess.argtypes = [wintypes.DWORD, wintypes.BOOL, wintypes.DWORD]
    k.OpenProcess.restype = wintypes.HANDLE
    k.WaitForSingleObject.argtypes = [wintypes.HANDLE, wintypes.DWORD]
    k.CloseHandle.argtypes = [wintypes.HANDLE]
    h = k.OpenProcess(0x100000, False, pid)
    if not h: return ctypes.get_last_error() != 87
    try: return k.WaitForSingleObject(h, 0) != 0
    finally: k.CloseHandle(h)


def remove_work(path, root):
    try:
        path, root = Path(path).absolute(), Path(root).absolute()
        if path == root or not path.is_relative_to(root): return False
        for p in (path, *path.parents):
            if p.is_symlink() or p.is_junction(): return False
        if not path.resolve().is_relative_to(root.resolve()): return False
        marker = json.loads((path/'.armada-work.json').read_text(encoding='utf-8'))
        if marker.get('owner') != 'Armada Scope FX' or marker.get('kind') not in ('export','import'): return False
        shutil.rmtree(path)
        return True
    except (OSError, ValueError): return False


def cleanup_stale_work(root, now=None):
    root = Path(root)
    if not root.exists(): return
    now = time.time() if now is None else now
    for path in root.iterdir():
        try:
            m = json.loads((path/'.armada-work.json').read_text(encoding='utf-8'))
            if now-m['created'] < 86400 or process_alive(m['pid']): continue
            if running_process('Tick Data Manager.exe') or running_process('terminal64.exe'): continue
            remove_work(path,root)
        except (OSError,ValueError,KeyError): continue


def safe_name(value):
    if not re.fullmatch(r'[A-Za-z0-9_.-]{1,48}', value) or value.startswith('.'):
        raise ValueError('銘柄・ソース名に使用できない文字があります。')
    return value


def inventory(root=TDS_ROOT):
    db = Path(root) / 'tds.db'
    with closing(sqlite3.connect(db.resolve().as_uri() + '?mode=ro', uri=True)) as c:
        c.row_factory = sqlite3.Row
        rows = c.execute('''SELECT p.name source,p.path provider_path,s.name symbol,s.path storage_path,
          s.digits, min(d.daytimestamp) first_stamp,max(d.daytimestamp) last_stamp,
          count(*) stored_days,sum(d.tickcount) ticks,sum(d.size) compressed_bytes
          FROM datainfo d JOIN symbols s ON s.id=d.symbolid JOIN provider p ON p.id=s.providerid
          WHERE d.tickcount>0 GROUP BY s.id ORDER BY p.name,s.name''').fetchall()
    result = []
    for row in rows:
        r = dict(row)
        for which in ('first', 'last'):
            r[which + '_day'] = datetime.fromtimestamp(r.pop(which + '_stamp'), timezone.utc).strftime('%Y-%m-%d')
        result.append(r)
    return result


def discover_terminals():
    base = Path(os.environ.get('APPDATA', '.')) / 'MetaQuotes/Terminal'
    found = []
    for origin in base.glob('*/origin.txt'):
        raw = origin.read_bytes()
        location = raw.decode('utf-16' if raw.startswith((b'\xff\xfe', b'\xfe\xff')) else 'utf-8-sig').strip()
        exe = Path(location) / 'terminal64.exe'
        editor = Path(location) / 'metaeditor64.exe'
        if exe.is_file() and editor.is_file() and (origin.parent / 'MQL5').is_dir():
            found.append(dict(name=Path(location).name, exe=str(exe), editor=str(editor), data=str(origin.parent)))
    return found


def export_arguments(exe, config, output, source, symbol, start, end):
    a, b = date_range(start, end)
    # Provider display names can include spaces; path delimiters cannot be accepted.
    if not re.fullmatch(r'[A-Za-z0-9 _.-]{1,48}', source):
        raise ValueError('不正なデータソース名です。')
    safe_name(symbol)
    return [str(exe), f'/export:{source}:{symbol}:{a:%Y.%m.%d}-{b:%Y.%m.%d}',
            f'/eformat:{config}', f'/output:{output}']


def running_process(name):
    from process_inventory import running_name
    return running_name(name)

def _export_ticks_once(source, symbol, start, end, run, log=print):
    if running_process('Tick Data Manager.exe'):
        raise RuntimeError('Tick Data Managerが起動中です。処理が終了してから通常終了し、再実行してください。')
    target = Path(run) / 'csv'
    target.mkdir(parents=True, exist_ok=False)
    a, b = date_range(start, end)
    # ~100 bytes/tick is a conservative estimate for CSV + staging + MT5 history.
    with closing(sqlite3.connect((TDS_ROOT / 'tds.db').as_uri() + '?mode=ro', uri=True)) as c:
        n = c.execute('''SELECT COALESCE(sum(d.tickcount),0) FROM datainfo d JOIN symbols s ON s.id=d.symbolid
         JOIN provider p ON p.id=s.providerid WHERE p.name=? AND s.path=? AND d.daytimestamp>=? AND d.daytimestamp<?''',
         (source, symbol, calendar.timegm(a.timetuple()), calendar.timegm((b+timedelta(days=1)).timetuple()))).fetchone()[0]
    if n == 0:
        raise ValueError('指定期間には保存済みティックがありません。')
    required = n * 180 + 512 * 1024**2
    if shutil.disk_usage(target).free < required:
        raise RuntimeError(f'空き容量不足です。目安として約{required/1024**3:.1f} GiB以上が必要です。')
    args = export_arguments(TDS_EXE, ROOT/'config/tds-utc.tcf', target, source, symbol, start, end)
    save_json(Path(run)/'export-command.json', args)
    log(f'TDS書き出し中: {symbol} {start} ～ {end} / DB記録 {n:,} ticks')
    proc = subprocess.Popen(args, creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    try:
        code = proc.wait(timeout=7200)
    except subprocess.TimeoutExpired as e:
        raise RuntimeError('書き出し待機が2時間を超えました。TDSの状態を確認してください。') from e
    files = list(target.glob('*.csv'))
    if code != 0 or len(files) != 1 or files[0].stat().st_size == 0:
        raise RuntimeError('TDSの書き出しに失敗しました。空のCSVは取り込みません。TDSログを確認してください。')
    with files[0].open('rb') as exported:
        exported.seek(-1, 2)
        if exported.read(1) != b'\n':
            raise RuntimeError('TDSのCSVが途中で切れています。未完了のデータは取り込みません。')
    return files[0], n



def export_ranges(start, end):
    a,b=date_range(start,end)
    while a<=b:
        stop=min(b,date(a.year,12,31))
        yield str(a),str(stop)
        a=stop+timedelta(days=1)


def export_ticks(source, symbol, start, end, run, log=print):
    ranges=list(export_ranges(start,end))
    if len(ranges)==1:return _export_ticks_once(source,symbol,start,end,run,log)
    run=Path(run);target=run/'csv';target.mkdir(parents=True,exist_ok=False)
    merged=target/'ticks.csv';total=0;header=None
    with merged.open('wb') as output:
        for first,last in ranges:
            part=run/('year-'+first);part.mkdir()
            try:csv,count=_export_ticks_once(source,symbol,first,last,part,log)
            except ValueError as error:
                if '保存済みティックがありません' in str(error):continue
                raise
            with csv.open('rb') as source_file:
                line=source_file.readline()
                if header is None:header=line;output.write(line)
                elif line!=header:raise ValueError('年別CSVのヘッダーが一致しません。')
                shutil.copyfileobj(source_file,output,8*1024*1024)
            total+=count
    if not total:raise ValueError('指定期間には保存済みティックがありません。')
    return merged,total


def tick_hash(h, msc, bid, ask, digits):
    factor = 10**digits
    return (h*31 + msc % MOD + round(bid*factor)*3 + round(ask*factor)*7) % MOD


def prepare_csv(path, start, end, output, digits=3, source='', symbol=''):
    a, b = date_range(start, end)
    if not 0 <= digits <= 8:
        raise ValueError('価格桁数は0～8です。')
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    if (output/'manifest.json').exists():
        raise ValueError('既存の取込データは上書きしません。新しい出力フォルダを指定してください。')
    lower = calendar.timegm(a.timetuple())*1000
    upper = calendar.timegm((b+timedelta(days=1)).timetuple())*1000
    m = dict(version=1, timezone='UTC', source=source, symbol=symbol, start=start, end=end, digits=digits,
             ticks=0, bars=0, first_msc=None, last_msc=None, tick_hash=0, days=[])
    tf = bf = None
    current_day = None
    current_bar = None
    day_info = None
    factor = 10**digits

    def flush_bar():
        nonlocal current_bar
        if current_bar:
            bf.write(BAR.pack(*current_bar))
            m['bars'] += 1
            day_info['bars'] += 1
            current_bar = None

    try:
        with Path(path).open('r', encoding='utf-8-sig', newline='') as f:
            sample = f.read(4096)
            f.seek(0)
            delimiter = '\t' if '\t' in sample.splitlines()[0] else ','
            reader = csv.reader(f, delimiter=delimiter)
            first = True
            for line_no, row in enumerate(reader, 1):
                if not row or not any(v.strip() for v in row):
                    continue
                if first and (row[0].strip().upper() in ('<DATE>', 'DATE', '日時', 'DATE AND TIME', 'DATETIME')
                              or (len(row)==3 and 'bid' in row[1].lower() and 'ask' in row[2].lower())):
                    first = False
                    continue
                first = False
                if len(row)==3 and re.fullmatch(r'\d{4}\.\d{2}\.\d{2} \d{2}:\d{2}:\d{2}\.\d{3}', row[0].strip()):
                    d, t = row[0].strip().split(' ')
                    row = [d, t, row[1], row[2]]
                if len(row) not in (4, 6, 7):
                    raise ValueError(f'CSV {line_no}行目: MT5の日付/時刻/Bid/Ask列が必要です。')
                try:
                    stamp = datetime.strptime(row[0].strip()+' '+row[1].strip(), '%Y.%m.%d %H:%M:%S.%f')
                    msc = calendar.timegm(stamp.timetuple())*1000 + stamp.microsecond//1000
                    bid, ask = float(row[2]), float(row[3])
                except (ValueError, IndexError) as e:
                    raise ValueError(f'CSV {line_no}行目: 日付または価格が不正です。') from e
                if stamp.microsecond % 1000 or not lower <= msc < upper:
                    raise ValueError(f'CSV {line_no}行目: 指定UTC期間外、またはミリ秒未満の時刻です。')
                if not all(math.isfinite(p) and 0 < p < 1e9 for p in (bid, ask)) or ask < bid:
                    raise ValueError(f'CSV {line_no}行目: 価格またはスプレッドが不正です。')
                if any(abs(p*factor-round(p*factor)) > 0.0001 for p in (bid, ask)):
                    raise ValueError(f'CSV {line_no}行目: 価格が指定桁数を超えています。')
                if m['last_msc'] is not None and msc < m['last_msc']:
                    raise ValueError(f'CSV {line_no}行目: 時刻が逆転しています。自動並べ替えはしません。')
                day = stamp.strftime('%Y%m%d')
                if day != current_day:
                    flush_bar()
                    if tf:
                        tf.close(); bf.close()
                    current_day = day
                    day_info = dict(day=day, day_start=msc//86400000*86400000,
                                    ticks_file=f'ticks-{day}.bin', bars_file=f'bars-{day}.bin', ticks=0, bars=0)
                    m['days'].append(day_info)
                    tf = (output/day_info['ticks_file']).open('wb')
                    bf = (output/day_info['bars_file']).open('wb')
                minute = msc//60000*60
                spread = round((ask-bid)*factor)
                if current_bar is None or current_bar[0] != minute:
                    flush_bar()
                    current_bar = [minute, bid, bid, bid, bid, 1, spread, 0]
                else:
                    current_bar[2] = max(current_bar[2], bid)
                    current_bar[3] = min(current_bar[3], bid)
                    current_bar[4] = bid
                    current_bar[5] += 1
                    current_bar[6] = spread
                tf.write(TICK.pack(msc, bid, ask))
                day_info['ticks'] += 1
                m['ticks'] += 1
                m['tick_hash'] = tick_hash(m['tick_hash'], msc, bid, ask, digits)
                if m['first_msc'] is None:
                    m['first_msc'] = msc
                m['last_msc'] = msc
            flush_bar()
        if not m['ticks']:
            raise ValueError('CSVにティックがありません。')
    except IndexError as e:
        raise ValueError('空のCSVです。') from e
    finally:
        if tf:
            tf.close(); bf.close()
    m['csv_sha256'] = file_sha256(path)
    for day in m['days']:
        day['ticks_sha256']=file_sha256(output/day['ticks_file'])
        day['bars_sha256']=file_sha256(output/day['bars_file'])
    save_json(output/'manifest.json', m)
    return m


def file_sha256(path):
    h = hashlib.sha256()
    with Path(path).open('rb') as f:
        for block in iter(lambda: f.read(1024*1024), b''):
            h.update(block)
    return h.hexdigest()


def warmup_day(source, symbol, start, root=TDS_ROOT):
    before=calendar.timegm(date.fromisoformat(start).timetuple())
    with closing(sqlite3.connect((Path(root)/'tds.db').resolve().as_uri()+'?mode=ro',uri=True)) as c:
        row=c.execute('''SELECT max(d.daytimestamp) FROM datainfo d JOIN symbols s ON s.id=d.symbolid
          JOIN provider p ON p.id=s.providerid WHERE p.name=? AND s.path=? AND d.daytimestamp<? AND d.tickcount>=100''',
          (source,symbol,before)).fetchone()[0]
    if row is None:
        raise ValueError('開始日以前の準備用データがありません。MT5の開始日ずれを防ぐため、もっと後の開始日を選んでください。')
    return datetime.fromtimestamp(row,timezone.utc).strftime('%Y-%m-%d')


def validate_stage(stage, source=None, symbol=None):
    stage=Path(stage).resolve()
    m=json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
    if m.get('version')!=1 or m.get('timezone')!='UTC' or not m.get('source') or not m.get('symbol'):
        raise ValueError('保存済みデータに銘柄・ソース情報がありません。CSVから再作成してください。')
    if (source is not None and source!=m['source']) or (symbol is not None and symbol!=m['symbol']):
        raise ValueError('保存済みデータの銘柄・ソースと、画面で選んだ銘柄が一致しません。')
    a,b=date_range(m['start'],m['end'])
    lower=calendar.timegm(a.timetuple())*1000
    upper=calendar.timegm((b+timedelta(days=1)).timetuple())*1000
    digits=m['digits']
    if not isinstance(digits,int) or not 0<=digits<=8:raise ValueError('不正な価格桁数です。')
    total=bars=hash_value=0
    first=last=None
    previous_day=-1
    for day in m['days']:
        if day['day_start']<=previous_day or day['day_start']%86400000:
            raise ValueError('保存日の並びが不正です。')
        previous_day=day['day_start']
        paths=[]
        for kind,width in (('ticks',24),('bars',60)):
            path=(stage/day[kind+'_file']).resolve()
            if path.parent!=stage or path.stat().st_size!=day[kind]*width or file_sha256(path)!=day[kind+'_sha256']:
                raise ValueError('保存済みデータの破損・変更を検出しました。')
            paths.append(path)
        day_ticks=day_bars=0
        bar=None
        with paths[0].open('rb') as tf, paths[1].open('rb') as bf:
            def check_bar():
                nonlocal day_bars,bars
                if bar is not None:
                    if bf.read(60)!=BAR.pack(*bar):raise ValueError('保存済みM1足とティックの内容が一致しません。')
                    day_bars+=1;bars+=1
            for raw in iter(lambda:tf.read(24),b''):
                if len(raw)!=24:raise ValueError('ティックファイルが途中で切れています。')
                msc,bid,ask=TICK.unpack(raw)
                if not lower<=msc<upper or msc//86400000*86400000!=day['day_start'] or (last is not None and msc<last):
                    raise ValueError('保存済みティックの期間・時刻順が不正です。')
                if not all(math.isfinite(p) and 0<p<1e9 for p in (bid,ask)) or ask<bid:
                    raise ValueError('保存済み価格が不正です。')
                factor=10**digits
                if any(abs(p*factor-round(p*factor))>.0001 for p in (bid,ask)):
                    raise ValueError('保存済み価格が桁数を超えています。')
                minute=msc//60000*60;spread=round((ask-bid)*factor)
                if bar is None or bar[0]!=minute:
                    check_bar();bar=[minute,bid,bid,bid,bid,1,spread,0]
                else:
                    bar[2]=max(bar[2],bid);bar[3]=min(bar[3],bid);bar[4]=bid;bar[5]+=1;bar[6]=spread
                hash_value=tick_hash(hash_value,msc,bid,ask,digits)
                if first is None:first=msc
                last=msc;total+=1;day_ticks+=1
            check_bar()
            if bf.read(1):raise ValueError('M1足が余分にあります。')
        if (day_ticks,day_bars)!=(day['ticks'],day['bars']):raise ValueError('保存日ごとの件数が一致しません。')
    if total<=0 or (total,bars,first,last,hash_value)!=(m['ticks'],m['bars'],m['first_msc'],m['last_msc'],m['tick_hash']):
        raise ValueError('保存済みデータとマニフェストが一致しません。')
    return m


def import_warmup(source, storage, start, digits, terminal, symbol, origin, run, log=print):
    day=warmup_day(source,storage,start)
    log(f'MT5準備用として直前の保存日 {day} を追加（テスト対象日には含めません）')
    warm=Path(run)/('warmup-'+uuid.uuid4().hex[:8])
    warm.mkdir(parents=True)
    csv,n=export_ticks(source,storage,day,day,warm,log)
    stage=warm/'stage'
    m=prepare_csv(csv,day,day,stage,digits,source,storage)
    if m['ticks']!=n:
        raise RuntimeError('準備用データのDB件数とCSV件数が一致しません。')
    if m['bars']<100:
        raise ValueError('準備用データのM1足が100本未満です。より長い履歴の取り込みが必要です。')
    import_stage(stage,terminal,symbol,origin,log)
    return stage


@contextmanager
def bridge_lock():
    global _bridge_lock_owner
    COMMON.mkdir(parents=True, exist_ok=True)
    lock = COMMON/'bridge.lock'
    try:
        f = lock.open('x', encoding='ascii')
    except FileExistsError as e:
        raise RuntimeError(f'別の連携処理が実行中です。異常終了後の場合は他の処理がないことを確認し、{lock} を削除してください。') from e
    try:
        f.write(str(os.getpid())); f.close()
        _bridge_lock_owner=(os.getpid(),threading.get_ident())
        yield
    finally:
        _bridge_lock_owner=None
        lock.unlink(missing_ok=True)


def compile_mql(terminal, log=print):
    data = Path(terminal['data'])
    for name, kind in [('TDSBridgeImport', 'Scripts'), ('TDSBridgeSmoke', 'Experts')]:
        src = ROOT/'mql5'/f'{name}.mq5'
        dst = data/'MQL5'/kind/'TDSBridge'/src.name
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)
        compile_log = ROOT/'build'/f'{name}.log'
        compile_log.parent.mkdir(exist_ok=True)
        compile_log.unlink(missing_ok=True)
        proc = subprocess.Popen([terminal['editor'], f'/compile:{dst}', f'/log:{compile_log}'],
                                creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        proc.wait(timeout=120)
        text = compile_log.read_text(encoding='utf-16', errors='replace') if compile_log.exists() else ''
        if not dst.with_suffix('.ex5').exists() or not re.search(r'0 errors, 0 warnings', text):
            raise RuntimeError(f'MQL5コンパイル失敗: {compile_log}\n{text[-1500:]}')
        shutil.copy2(dst.with_suffix('.ex5'), ROOT/'mql5'/f'{name}.ex5')
        log(f'{name}: 0 errors, 0 warnings')


def managed_immutable_stage(stage):
    # Only the caller holding our exclusive bridge lock can opt into sharing the
    # immutable, fully validated application cache with the read-only MQL importer.
    if _bridge_lock_owner!=(os.getpid(),threading.get_ident()):return False
    local=os.environ.get('LOCALAPPDATA')
    if not local:return False
    stage=Path(stage).absolute()
    cache=(Path(local)/'ArmadaScopeFX'/'TdsCache').absolute()
    if stage.name!='stage' or stage.parent.parent!=cache:return False
    return not any(p.is_symlink() or p.is_junction() for p in (stage,*stage.parents))


def write_job(stage, m, run_id, symbol, origin, immutable_cache=False):
    safe_name(symbol); safe_name(origin)
    if not re.fullmatch(r'[A-Z0-9]{3,12}_TDS_UTC',symbol) or len(symbol)>31:
        raise ValueError('カスタム銘柄名は通貨ペア名に_TDS_UTCを付けた31文字以内の名前にしてください。')
    common_run = COMMON/run_id
    common_run.mkdir(parents=True, exist_ok=False)
    mark_work(common_run, 'import')
    share=immutable_cache and managed_immutable_stage(stage)
    for day in m['days']:
        for key in ('ticks_file', 'bars_file'):
            source_file=Path(stage)/day[key];destination=common_run/day[key]
            if share and source_file.parent==Path(stage) and not source_file.is_symlink() and not source_file.is_junction():
                try:
                    os.link(source_file,destination)
                    continue
                except OSError:pass  # Different volume/filesystem: preserve the copy path.
            shutil.copy2(source_file,destination)
    lines = ['TDSBRIDGE1', symbol, origin, str(m['digits']), str(m['ticks']), str(m['bars']),
             str(m['first_msc']), str(m['last_msc']), str(m['tick_hash']), str(len(m['days']))]
    for d in m['days']:
        lines.extend([str(d['day_start']), d['ticks_file'], str(d['ticks']), d['bars_file'], str(d['bars'])])
    (common_run/'job.txt').write_text('\n'.join(lines)+'\n', encoding='ascii')
    return common_run


def running_terminal(exe):
    from process_inventory import running_path
    return running_path(exe)


def restore_profile(common_ini,encoding,previous_profile):
    if not previous_profile:return
    text=common_ini.read_text(encoding=encoding)
    text=re.sub(r'^ProfileLast=TDSBridge[ \t]*\r?$',lambda _: 'ProfileLast='+previous_profile,text,flags=re.M)
    common_ini.write_text(text,encoding=encoding)


def restore_max_bars(common_ini,encoding,previous):
    text=common_ini.read_text(encoding=encoding)
    text=re.sub(r'^MaxBars=100000000[ \t]*\r?$',lambda _: 'MaxBars='+previous if previous is not None else '',text,flags=re.M)
    common_ini.write_text(text,encoding=encoding)


def run_terminal(terminal, config, expected, timeout=300):
    if running_terminal(terminal['exe']):
        raise RuntimeError('MT5が起動中です。通常終了してから実行してください。既存MT5は強制終了しません。')
    # Use an empty bridge-owned chart profile so existing user EAs are not loaded.
    data = Path(terminal['data'])
    (data/'MQL5/Profiles/Charts/TDSBridge').mkdir(parents=True, exist_ok=True)
    config = Path(config)
    config_text = config.read_text(encoding='utf-16')
    config.write_text('[Charts]\nProfileLast=TDSBridge\nMaxBars=100000000\n\n'+config_text, encoding='utf-16')
    common_ini = data/'config/common.ini'
    recovery=data/'TDSBridge-profile-recovery.json'
    if recovery.exists():
        pending=json.loads(recovery.read_text(encoding='utf-8'))
        restore_profile(common_ini,pending['encoding'],pending['previous_profile'])
        if 'previous_max_bars' in pending:restore_max_bars(common_ini,pending['encoding'],pending['previous_max_bars'])
        recovery.unlink()
    raw = common_ini.read_bytes()
    encoding = 'utf-16' if raw.startswith((b'\xff\xfe',b'\xfe\xff')) else 'utf-8'
    previous = re.search(r'^ProfileLast=(.*)$', raw.decode(encoding), re.M)
    previous_profile = previous.group(1).strip() if previous else None
    previous_bars=re.search(r'^MaxBars=(.*)$',raw.decode(encoding),re.M)
    previous_max_bars=previous_bars.group(1).strip() if previous_bars else None
    save_json(recovery,dict(previous_profile=previous_profile,previous_max_bars=previous_max_bars,encoding=encoding))
    command=[terminal['exe']]
    if terminal.get('portable'):command.append('/portable')
    command.append(f'/config:{config}')
    proc = subprocess.Popen(command,
                            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
    try:
        deadline = time.monotonic()+timeout
        while time.monotonic()<deadline:
            if expected.exists():
                result = json.loads(expected.read_text(encoding='utf-8-sig'))
                try:proc.wait(timeout=60)
                except subprocess.TimeoutExpired as e:
                    raise RuntimeError('MT5は結果を書きましたが終了していません。通常終了してから再実行してください。') from e
                if not result.get('ok'):raise RuntimeError(f'MT5処理が失敗しました: {result}')
                return result
            if proc.poll() is not None:
                time.sleep(.5)
                if not expected.exists():raise RuntimeError('MT5が結果を書かずに終了しました。MT5ログを確認してください。')
            time.sleep(.4)
        raise RuntimeError(f'MT5の結果待ちがタイムアウトしました。通常終了してから再実行してください: {expected}')
    finally:
        if proc.poll() is not None:
            restore_profile(common_ini,encoding,previous_profile)
            restore_max_bars(common_ini,encoding,previous_max_bars)
            recovery.unlink(missing_ok=True)
        else:
            # Only this launched process is asked to exit normally; never force-kill or touch other MT5 instances.
            subprocess.run(['taskkill','/PID',str(proc.pid)],capture_output=True,
                           creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
            try:proc.wait(timeout=10)
            except subprocess.TimeoutExpired:
                save_json(Path(terminal['data'])/'TDSBridge-profile-recovery.json',dict(previous_profile=previous_profile,previous_max_bars=previous_max_bars,encoding=encoding))
            else:
                restore_profile(common_ini,encoding,previous_profile)
                restore_max_bars(common_ini,encoding,previous_max_bars)
                recovery.unlink(missing_ok=True)


def verification_registry(cache, terminal, symbol, origin):
    from verified_imports import VerifiedImports
    return VerifiedImports(cache, terminal, symbol, origin,
                           [Path(__file__), Path(__file__).with_name('verified_imports.py'),
                            Path(__file__).with_name('history_checkpoint.py'),
                            Path(__file__).with_name('process_inventory.py'),
                            *([Path(__file__).with_name('AutoScopeMetadata.dll')] if Path(__file__).with_name('AutoScopeMetadata.dll').is_file() else []),
                            ROOT/'mql5/TDSBridgeImport.mq5'])


def import_stage(stage, terminal, symbol, origin, log=print, source=None, source_symbol=None, immutable_cache=False, compile_if_needed=None):
    proof = None
    cache = Path(os.environ.get('LOCALAPPDATA', '.'))/'ArmadaScopeFX/TdsCache'
    allow_proof = (immutable_cache and _bridge_lock_owner == (os.getpid(), threading.get_ident())
                   and Path(stage).absolute().parent.parent == cache.absolute())
    def registry():
        return verification_registry(cache, terminal, symbol, origin)
    if allow_proof:
        try:
            if running_terminal(terminal['exe']):
                raise RuntimeError('MT5が起動中です。確認済み履歴を再利用できません。')
            proof = registry()
            reused = proof.reuse(stage, source, source_symbol)
            if reused:
                log(f'確認済み履歴を再利用: {symbol} / {reused[0]["ticks"]:,} ticks（変更なし・全件再読込を省略）')
                save_json(Path(stage)/'import-result.json', reused[1])
                return reused
        except (OSError, ValueError, KeyError, TypeError):
            # No proof, unknown filesystem or changed files: use the original
            # complete SHA/tick/M1/readback validation, never a partial result.
            proof = None
    if compile_if_needed is not None:
        compile_if_needed()
    lease = None
    if allow_proof:
        try:
            from verified_imports import SourceLease
            lease = SourceLease(stage)
        except (OSError, ValueError, KeyError, TypeError):
            pass
    try:
        m = validate_stage(stage, source=source, symbol=source_symbol)
        run_id = uuid.uuid4().hex
        job = COMMON/run_id
        presets = Path(terminal['data'])/'MQL5/Presets'
        presets.mkdir(parents=True, exist_ok=True)
        preset = presets/f'TDSBridge-{run_id}.set'
        config = Path(stage)/f'import-{run_id}.ini'
        try:
            write_job(stage, m, run_id, symbol, origin, immutable_cache=immutable_cache)
            preset.write_text(f'Job=TDSBridge\\{run_id}\\job.txt\n', encoding='utf-16')
            config.write_text(f'[StartUp]\nScript=TDSBridge\\TDSBridgeImport\nScriptParameters={preset.name}\n'
                              f'Expert=\nSymbol={origin}\nPeriod=M1\nShutdownTerminal=1\n\n[Tester]\nExpert=\n', encoding='utf-16')
            log(f'MT5へ取込中: {symbol} / {m["ticks"]:,} ticks')
            result = run_terminal(terminal, config.resolve(), job/'import-result.json', timeout=max(900,min(14400,120+m['ticks']//50000)))
            for key in ('ticks', 'bars', 'first_msc', 'last_msc', 'tick_hash'):
                if result.get(key) != m[key]:
                    raise RuntimeError(f'MT5照合失敗 {key}: expected={m[key]} got={result.get(key)}')
            save_json(Path(stage)/'import-result.json', result)
        finally:
            # If MT5 remains alive (or its state is unknown), retain its small preset
            # and config. They are intentionally not registered as arbitrary marker
            # paths; stale maintenance only reclaims the owned COMMON directory.
            if not running_terminal(terminal['exe']):
                remove_work(job, COMMON)
                for path in (preset, config):
                    try: path.unlink(missing_ok=True)
                    except OSError: pass
        if allow_proof and lease is not None and not running_terminal(terminal['exe']):
            try:
                # Link cleanup changes NTFS ChangeTime; record only after cleanup.
                lease.verify()
                (proof or registry()).remember(stage, m, result)
            except (OSError, ValueError, KeyError, TypeError) as e:
                log('確認済み履歴の記録を見送りました（次回は全件照合）: '+str(e))
        return m, result
    finally:
        if lease is not None:
            lease.close()


def smoke_test(stage, terminal, symbol, log=print):
    stage = Path(stage)
    m = validate_stage(stage)
    run_id = uuid.uuid4().hex
    common_run = COMMON/run_id
    common_run.mkdir(parents=True)
    presets = Path(terminal['data'])/'MQL5/Profiles/Tester'
    presets.mkdir(parents=True, exist_ok=True)
    setname = f'TDSBridgeSmoke-{run_id}.set'
    values = dict(ResultFile=f'TDSBridge\\{run_id}\\test-result.json', ExpectedTicks=m['ticks'],
                  ExpectedFirst=m['first_msc'], ExpectedLast=m['last_msc'], ExpectedHash=m['tick_hash'],
                  PriceDigits=m['digits'])
    (presets/setname).write_text('\n'.join(f'{k}={v}' for k,v in values.items())+'\n', encoding='utf-16')
    report = stage/'tester-report.htm'
    native_report = f'TDSBridge-{run_id}.htm'
    config = stage/f'tester-{run_id}.ini'
    until = date.fromisoformat(m['end'])+timedelta(days=1)
    config.write_text(f'[StartUp]\nExpert=\nScript=\n\n[Tester]\nExpert=TDSBridge\\TDSBridgeSmoke.ex5\nExpertParameters={setname}\n'
                      f'Symbol={symbol}\nPeriod=M1\nModel=4\nOptimization=0\n'
                      f'FromDate={m["start"].replace("-", ".")}\nToDate={until:%Y.%m.%d}\n'
                      f'Deposit=10000\nCurrency=USD\nLeverage=1:100\nUseLocal=1\nUseRemote=0\nUseCloud=0\n'
                      f'Visual=0\nReport={native_report}\nReplaceReport=1\nShutdownTerminal=1\n', encoding='utf-16')
    log('MT5実ティック・バックテスト中（発注しない検証用EA）')
    result = run_terminal(terminal, config.resolve(), common_run/'test-result.json', timeout=max(900,min(14400,120+m['ticks']//50000)))
    for key in ('ticks','first_msc','last_msc','tick_hash'):
        if result.get(key)!=m[key]:
            raise RuntimeError(f'テスター照合失敗 {key}: {result.get(key)} != {m[key]}')
    save_json(stage/'test-result.json', result)
    collect_report(terminal,native_report,stage)
    return result


def collect_report(terminal,native_report,stage):
    stage=Path(stage)
    for base in (Path(terminal['data']), Path(terminal['exe']).parent):
        native = base/native_report
        if native.exists():
            shutil.copy2(native, stage/'tester-report.htm')
            for extra in base.glob(Path(native_report).stem+'*.png'):
                shutil.copy2(extra,stage/extra.name)
            return stage/'tester-report.htm'
    raise RuntimeError(f'今回のMT5テスターレポートがありません: {native_report}')


# The native helper retains full record/hash/M1 validation for large histories.
_python_prepare_csv = prepare_csv
_python_validate_stage = validate_stage

def _fast(args):
    result=subprocess.run([str(FAST_EXE),'--tds-fast',*map(str,args)],capture_output=True,encoding='utf-8',creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0))
    if result.returncode!=0:raise ValueError(result.stderr.strip() or 'TDSデータ変換・照合に失敗しました。')
    return json.loads(result.stdout)

def prepare_csv(path,start,end,output,digits=3,source='',symbol=''):
    if not FAST_EXE.exists():return _python_prepare_csv(path,start,end,output,digits,source,symbol)
    return _fast(['prepare',path,start,end,output,digits,source,symbol])

def validate_stage(stage,source=None,symbol=None):
    if not FAST_EXE.exists():return _python_validate_stage(stage,source,symbol)
    m=_fast(['validate',stage])
    if (source is not None and source!=m['source']) or (symbol is not None and symbol!=m['symbol']):raise ValueError('保存済みデータの銘柄・ソースが一致しません。')
    return m


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command')
    sub.add_parser('inventory')
    sub.add_parser('terminals')
    sub.add_parser('compile')
    p = sub.add_parser('prepare')
    p.add_argument('csv'); p.add_argument('--start', required=True); p.add_argument('--end', required=True)
    p.add_argument('--output', required=True); p.add_argument('--digits', type=int, default=3)
    p.add_argument('--source',required=True);p.add_argument('--symbol',required=True)
    args = parser.parse_args()
    if args.command=='inventory':
        print(json.dumps(inventory(), ensure_ascii=False, indent=2))
    elif args.command=='terminals':
        print(json.dumps(discover_terminals(), ensure_ascii=False, indent=2))
    elif args.command=='compile':
        compile_mql(discover_terminals()[0])
    elif args.command=='prepare':
        print(json.dumps(prepare_csv(args.csv,args.start,args.end,args.output,args.digits,args.source,args.symbol),indent=2))
    else:
        from gui import launch
        launch()


if __name__=='__main__':
    main()
