import json,sys,os,hashlib,calendar,shutil,uuid
from pathlib import Path
from datetime import date,timedelta
import bridge
import cache_maintenance
import range_cache
import time
import history_checkpoint

def run(request):
    q=json.loads(Path(request).read_text(encoding='utf-8-sig'))
    origin=q['symbol']; bridge.safe_name(origin)
    rows=[r for r in bridge.inventory() if r['source']=='Dukascopy' and (origin.upper()==r['symbol'].upper() or origin.upper().startswith(r['symbol'].upper()+'.'))]
    if len(rows)!=1: raise ValueError('TDSのDukascopy銘柄と一意に対応しません: '+origin)
    r=rows[0]; target=r['symbol']+'_TDS_UTC'
    terminal=dict(exe=q['terminal'],data=q['data'],portable=q['portable'],editor=str(Path(q['terminal']).parent/'metaeditor64.exe'))
    start=date.fromisoformat(q['from']); end=date.fromisoformat(q['to'])
    # Import enough preparation history for 100 bars of the selected timeframe.
    warm={'M1':7,'M5':7,'M15':7,'M30':7,'H1':14,'H4':45,'D1':200,'W1':900,'MN':3650}.get(q['period'],14)
    first=max(start-timedelta(days=warm),date.fromisoformat(r['first_day']))
    last=min(end-timedelta(days=1),date.fromisoformat(r['last_day']))
    if first>last or start>last:raise ValueError('指定期間にTDSデータがありません。')
    if start<date.fromisoformat(r['first_day']) or end-timedelta(days=3)>date.fromisoformat(r['last_day']):
        raise ValueError('指定期間がTDS保存期間を超えています: '+r['first_day']+' ～ '+r['last_day'])
    cache=Path(os.environ['LOCALAPPDATA'])/'ArmadaScopeFX'/'TdsCache';cache.mkdir(parents=True,exist_ok=True)
    # Keep bridge build outputs out of the installed application directory.
    bridge.ROOT=cache/'engine';bridge.ROOT.mkdir(exist_ok=True)
    source=Path(__file__).parent
    for directory in ('mql5','config'):shutil.copytree(source/directory,bridge.ROOT/directory,dirs_exist_ok=True)
    if bridge.running_terminal(terminal['exe']):raise ValueError('対象MT5を閉じてください。')
    with bridge.bridge_lock():
        bridge.cleanup_stale_work(bridge.COMMON)
        compiled=False
        def compile_if_needed():
            nonlocal compiled
            if not compiled:
                bridge.compile_mql(terminal)
                compiled=True
        receipts=[];used_stages=[];started=time.monotonic()
        segments=range_cache.plan(cache,first,last,r['source'],r['storage_path'],r['digits'])
        reused_days=sum((b-a).days+1 for a,b,stage in segments if stage is not None)
        added_days=sum((b-a).days+1 for a,b,stage in segments if stage is None)
        print(f'TDS期間再利用: {reused_days}日 / 追加書出: {added_days}日',flush=True)
        for cursor,stop,cached_stage in segments:
            key=f'{r["symbol"]}-{cursor}-{stop}'
            folder=cache/key;stage=cached_stage or folder/'stage'
            if cached_stage is not None:folder=stage.parent
            bridge.cleanup_stale_work(folder)
            if (stage/'manifest.json').exists():
                print('TDSキャッシュを照合します: '+key,flush=True)
            else:
                folder.mkdir(exist_ok=True)
                attempt=folder/('attempt-'+uuid.uuid4().hex);attempt.mkdir()
                bridge.mark_work(attempt,'export')
                try:
                    try:csv,n=bridge.export_ticks(r['source'],r['storage_path'],str(cursor),str(stop),attempt)
                    except ValueError as e:
                        if '保存済みティックがありません' in str(e):continue
                        raise
                    staging=attempt/'stage'
                    m=bridge.prepare_csv(csv,str(cursor),str(stop),staging,digits=r['digits'],source=r['source'],symbol=r['storage_path'])
                    if m['ticks']!=n:raise ValueError('TDS DBとCSVのティック件数が一致しません。')
                    staging.rename(stage)
                finally:
                    if not bridge.running_process('Tick Data Manager.exe'):bridge.remove_work(attempt,folder)
            # Restrict a larger cached stage to the requested UTC days. The native
            # helper verifies every selected tick/bar and SHA before making a view.
            cached_manifest=json.loads((stage/'manifest.json').read_text(encoding='utf-8'))
            if cached_stage is not None and (cached_manifest['start']!=str(cursor) or cached_manifest['end']!=str(stop)):
                lower=calendar.timegm(cursor.timetuple())*1000
                upper=calendar.timegm((stop+timedelta(days=1)).timetuple())*1000
                if not any(lower<=day['day_start']<upper for day in cached_manifest['days']):
                    used_stages.append(stage)
                    continue
                view_folder=cache/key;view_folder.mkdir(exist_ok=True)
                view_stage=view_folder/'stage'
                if not (view_stage/'manifest.json').exists():
                    view_work=view_folder/('attempt-'+uuid.uuid4().hex);view_work.mkdir();bridge.mark_work(view_work,'export')
                    try:
                        view_manifest=bridge._fast(['slice',stage,str(cursor),str(stop)])
                        view_payload=view_work/'stage';view_payload.mkdir()
                        for day in view_manifest['days']:
                            for field in ('ticks_file','bars_file'):
                                original=stage/day[field];destination=view_payload/day[field]
                                try:os.link(original,destination)
                                except OSError:shutil.copy2(original,destination)
                        bridge.save_json(view_payload/'manifest.json',view_manifest)
                        view_payload.rename(view_stage)
                    finally:bridge.remove_work(view_work,view_folder)
                used_stages.append(stage)
                stage=view_stage
            m,result=bridge.import_stage(stage,terminal,target,origin,source=r['source'],source_symbol=r['storage_path'],immutable_cache=True,compile_if_needed=compile_if_needed)
            receipts.append(dict(start=m['start'],ticks=m['ticks'],first_msc=m['first_msc'],last_msc=m['last_msc'],tick_hash=m['tick_hash'],verified_reuse=result.get('verified_reuse',False)))
            used_stages.append(stage)
        if not receipts:raise ValueError('取り込めるデータがありません。')
        checkpoint=None
        try:
            checkpoint=history_checkpoint.create(bridge.verification_registry(cache,terminal,target,origin),freshly_checked=True)
            if checkpoint:print('検証済みの復元データを準備しました（変更がない部分は再コピーしません）。',flush=True)
        except (OSError,ValueError,KeyError,TypeError) as e:
            print('履歴の一時保存を見送りました。次回変更時は全件照合します: '+str(e),flush=True)
        bridge.save_json(q['output'],dict(ok=True,symbol=target,origin=origin,source='Dukascopy / TDS',timezone='UTC',chunks=receipts,reused_days=reused_days,export_days=added_days,checkpoint=checkpoint,elapsed_seconds=time.monotonic()-started))
        cache_maintenance.maintain(cache,used_stages,bridge)
    print('TDS履歴準備・検証完了: '+target,flush=True)

def finish(request):
    q=json.loads(Path(request).read_text(encoding='utf-8-sig'))
    receipt=Path(q['output'])
    if not receipt.exists():return
    result=json.loads(receipt.read_text(encoding='utf-8-sig'))
    token=result.get('checkpoint')
    if not token:return
    terminal=dict(exe=q['terminal'],data=q['data'],portable=q['portable'])
    if bridge.running_terminal(terminal['exe']):raise ValueError('MT5起動中の履歴は復元しません。')
    cache=Path(os.environ['LOCALAPPDATA'])/'ArmadaScopeFX/TdsCache'
    bridge.ROOT=cache/'engine'
    with bridge.bridge_lock():
        proof=bridge.verification_registry(cache,terminal,result['symbol'],result['origin'])
        restored=history_checkpoint.finish(proof,token)
        bridge.save_json(receipt.with_name('tds-finish.json'),restored)
    if restored['ok']:
        print(f'検証済み履歴を維持しました（復元 {restored["restored_files"]}ファイル・実行用の一時記録を削除）。',flush=True)
    else:print('履歴の変更を検出したため、次回は全件照合します: '+restored['reason'],flush=True)

if __name__=='__main__':
    try:
        if sys.argv[1]=='--worker':
            from worker_protocol import serve
            import verified_imports
            def code_identity():
                root=Path(__file__).parent
                return [(p.name,hashlib.sha256(p.read_bytes()).hexdigest())
                        for p in sorted(root.iterdir()) if p.suffix in ('.py','.dll')]
            initial_code=code_identity()
            def handle(op,request):
                if code_identity()!=initial_code:
                    raise ValueError('TDS連携が更新されました。連続テストを再開してください。')
                verified_imports._volumes.clear()
                (run if op=='prepare' else finish)(request)
            serve(sys.stdin,sys.stdout,handle)
        elif sys.argv[1]=='--finish':finish(sys.argv[2])
        else:run(sys.argv[1])
    except Exception as e:print(str(e),file=sys.stderr);sys.exit(1)
