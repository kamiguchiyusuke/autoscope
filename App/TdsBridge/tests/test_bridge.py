import importlib.util
import json
import struct
import tempfile
import sqlite3
import unittest
import subprocess
import sys
from types import SimpleNamespace
from unittest.mock import patch
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


class PipelineTests(unittest.TestCase):
    def setUp(self):
        self.assertTrue((ROOT / 'bridge.py').is_file(), 'Data pipeline is not implemented')
        spec = importlib.util.spec_from_file_location('bridge', ROOT / 'bridge.py')
        self.bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(self.bridge)
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def prepare(self, rows, **kw):
        p = self.root / 'input.csv'
        p.write_text('<DATE>\t<TIME>\t<BID>\t<ASK>\t<LAST>\t<VOLUME>\n' + rows, encoding='utf-8')
        return self.bridge.prepare_csv(p, '2026-10-15', '2026-10-15', self.root / 'stage', digits=3, **kw)

    def test_preserves_same_millisecond_ticks_and_derives_bid_m1(self):
        m = self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n'
                         '2026.10.15\t00:00:01.123\t150.005\t150.007\t\t\n'
                         '2026.10.15\t00:01:00.000\t149.999\t150.002\t\t\n')
        self.assertEqual(m['ticks'], 3)
        self.assertEqual(m['bars'], 2)
        self.assertEqual(m['first_msc'], 1792022401123)
        self.assertEqual(m['last_msc'], 1792022460000)
        tick_bytes = (self.root / 'stage' / m['days'][0]['ticks_file']).read_bytes()
        self.assertEqual(len(tick_bytes), 72)
        self.assertEqual(struct.unpack_from('<qdd', tick_bytes), (1792022401123, 150.001, 150.003))
        bars = (self.root / 'stage' / m['days'][0]['bars_file']).read_bytes()
        self.assertEqual(struct.unpack_from('<qddddqiq', bars),
                         (1792022400, 150.001, 150.005, 150.001, 150.005, 2, 2, 0))

    def test_rejects_invalid_prices_and_reversed_spread(self):
        for bid, ask in [('nan', '150'), ('0', '150'), ('150', '149'), ('150', 'inf')]:
            with self.subTest(bid=bid, ask=ask), self.assertRaises(ValueError):
                self.prepare(f'2026.10.15\t00:00:01.000\t{bid}\t{ask}\t\t\n')

    def test_rejects_time_reversal(self):
        with self.assertRaises(ValueError):
            self.prepare('2026.10.15\t00:01:00.000\t150\t151\t\t\n'
                         '2026.10.15\t00:00:00.000\t150\t151\t\t\n')

    def test_rejects_outside_requested_range_and_empty_file(self):
        for data in ['', '2026.10.16\t00:00:00.000\t150\t151\t\t\n']:
            with self.subTest(data=data), self.assertRaises(ValueError):
                self.prepare(data)

    def test_rejects_incorrect_columns_and_fractional_precision(self):
        for data in ['2026.10.15\t00:00:00.000\t150\n',
                     '2026.10.15\t00:00:00.000\t150.0001\t151\t\t\n']:
            with self.subTest(data=data), self.assertRaises(ValueError):
                self.prepare(data)

    def test_rejects_date_reversal_and_unsafe_symbol(self):
        for symbol, start, end in [('USDJPY', '2026-10-16', '2026-10-15'),
                                   ('../USDJPY', '2026-10-15', '2026-10-15')]:
            with self.subTest(symbol=symbol), self.assertRaises(ValueError):
                self.bridge.export_arguments(Path('TDM.exe'), Path('format.tcf'),
                                             Path('out'), 'Dukascopy', symbol, start, end)

    def test_export_uses_exact_symbol_range_and_argument_list(self):
        args = self.bridge.export_arguments(Path('C:/program files/TDM.exe'), Path('C:/a b/f.tcf'),
                                            Path('C:/a b/out'), 'Dukascopy', 'USDJPY',
                                            '2026-10-15', '2026-10-15')
        self.assertEqual(args[1:], ['/export:Dukascopy:USDJPY:2026.10.15-2026.10.15',
                                    '/eformat:' + str(Path('C:/a b/f.tcf')),
                                    '/output:' + str(Path('C:/a b/out'))])

    def test_failed_validation_does_not_leave_success_manifest(self):
        with self.assertRaises(ValueError):
            self.prepare('2026.10.15\t00:00:00.000\t150\t151\t\t\n'
                         'broken\n')
        self.assertFalse((self.root / 'stage' / 'manifest.json').exists())

    def test_reads_actual_tds_mt5_preset_with_japanese_header(self):
        p = self.root/'actual.csv'
        p.write_text('日時,売値(bid),買値(ask)\n2026.10.15 00:00:00.087,157.979,157.985\n', encoding='utf-8')
        m = self.bridge.prepare_csv(p, '2026-10-15', '2026-10-15', self.root/'stage', digits=3)
        self.assertEqual((m['ticks'],m['first_msc']), (1,1792022400087))

    def test_warmup_uses_latest_real_saved_day_strictly_before_test(self):
        with sqlite3.connect(self.root/'tds.db') as c:
            c.executescript('CREATE TABLE provider(id,name); CREATE TABLE symbols(id,providerid,path); CREATE TABLE datainfo(symbolid,daytimestamp,tickcount);'
                            "INSERT INTO provider VALUES(1,'Dukascopy'); INSERT INTO symbols VALUES(1,1,'USDJPY');"
                            'INSERT INTO datainfo VALUES(1,1791936000,100),(1,1792022400,200),(1,1791849600,0);')
        c.close()
        self.assertEqual(self.bridge.warmup_day('Dukascopy','USDJPY','2026-10-15',self.root),'2026-10-14')
        with self.assertRaises(ValueError):
            self.bridge.warmup_day('Dukascopy','USDJPY','2026-10-14',self.root)

    def test_saved_stage_rejects_wrong_instrument_before_import(self):
        m=self.prepare('2026.10.15\t00:00:00.000\t150\t151\t\t\n',source='Dukascopy',symbol='USDJPY')
        with self.assertRaises(ValueError):
            self.bridge.validate_stage(self.root/'stage',source='Dukascopy',symbol='EURUSD')

    def test_saved_stage_rejects_corruption_before_import(self):
        m=self.prepare('2026.10.15\t00:00:00.000\t150\t151\t\t\n',source='Dukascopy',symbol='USDJPY')
        f=self.root/'stage'/m['days'][0]['bars_file']
        content=bytearray(f.read_bytes()); struct.pack_into('<d',content,8,149.5);f.write_bytes(content)
        with self.assertRaises(ValueError): self.bridge.validate_stage(self.root/'stage')
        # Even updating the file hash must not hide M1/tick inconsistency.
        manifest=json.loads((self.root/'stage/manifest.json').read_text())
        manifest['days'][0]['bars_sha256']=self.bridge.file_sha256(f)
        (self.root/'stage/manifest.json').write_text(json.dumps(manifest))
        with self.assertRaises(ValueError): self.bridge.validate_stage(self.root/'stage')

    def test_current_missing_report_cannot_reuse_previous_report(self):
        stage=self.root/'stage';stage.mkdir();(stage/'tester-report.htm').write_text('old report')
        with self.assertRaises(RuntimeError):
            self.bridge.collect_report({'data':str(self.root),'exe':str(self.root/'terminal64.exe')},'new-unique.htm',stage)

    def test_restores_profile_on_early_terminal_failure(self):
        data=self.root/'terminal';(data/'config').mkdir(parents=True)
        common=data/'config/common.ini';common.write_text('[Charts]\nProfileLast=MyWorkspace\n',encoding='utf-16')
        config=self.root/'run.ini';config.write_text('[StartUp]\n',encoding='utf-16')
        class Exited:
            pid=123
            def poll(self): return 1
            def wait(self,timeout=None): return 1
        with patch.object(self.bridge,'running_terminal',return_value=False), patch.object(self.bridge.subprocess,'Popen',return_value=Exited()):
            with self.assertRaises(RuntimeError):
                self.bridge.run_terminal({'exe':str(data/'terminal64.exe'),'data':str(data)},config,self.root/'missing.json',1)
        self.assertIn('ProfileLast=MyWorkspace',common.read_text(encoding='utf-16'))

    def test_deferred_profile_recovery_runs_when_terminal_is_stopped(self):
        data=self.root/'terminal';(data/'config').mkdir(parents=True)
        common=data/'config/common.ini';common.write_text('[Charts]\nProfileLast=TDSBridge\n',encoding='utf-16')
        (data/'TDSBridge-profile-recovery.json').write_text(json.dumps({'previous_profile':'MyWorkspace','encoding':'utf-16'}))
        config=self.root/'run.ini';config.write_text('[StartUp]\n',encoding='utf-16')
        class Exited:
            pid=123
            def poll(self):return 1
            def wait(self,timeout=None):return 1
        with patch.object(self.bridge,'running_terminal',return_value=False),patch.object(self.bridge.subprocess,'Popen',return_value=Exited()):
            with self.assertRaises(RuntimeError):
                self.bridge.run_terminal({'exe':str(data/'terminal64.exe'),'data':str(data)},config,self.root/'missing.json',1)
        self.assertIn('ProfileLast=MyWorkspace',common.read_text(encoding='utf-16'))
        self.assertFalse((data/'TDSBridge-profile-recovery.json').exists())



    def test_year_chunks_cover_leap_year_without_gaps(self):
        self.assertEqual(list(self.bridge.export_ranges('2019-12-30','2021-01-02')),[('2019-12-30','2019-12-31'),('2020-01-01','2020-12-31'),('2021-01-01','2021-01-02')])

    def test_year_exports_merge_one_header_and_preserve_rows(self):
        calls=[]
        def export(source,symbol,start,end,run,log):
            calls.append((start,end));p=Path(run)/'part.csv';p.write_bytes(b'header\n'+start.encode()+b'\n');return p,1
        with patch.object(self.bridge,'_export_ticks_once',side_effect=export):
            csv,count=self.bridge.export_ticks('Dukascopy','USDJPY','2019-12-31','2021-01-01',self.root/'run')
        self.assertEqual(count,3)
        self.assertEqual(csv.read_bytes(),b'header\n2019-12-31\n2020-01-01\n2021-01-01\n')
        self.assertEqual(len(calls),3)



    def test_zero_exit_with_truncated_csv_is_rejected(self):
        db=sqlite3.connect(self.root/'tds.db')
        db.executescript("CREATE TABLE provider(id INTEGER,name TEXT); CREATE TABLE symbols(id INTEGER,providerid INTEGER,path TEXT); CREATE TABLE datainfo(symbolid INTEGER,daytimestamp INTEGER,tickcount INTEGER); INSERT INTO provider VALUES(1,'Dukascopy'); INSERT INTO symbols VALUES(1,1,'USDJPY'); INSERT INTO datainfo VALUES(1,1792022400,1);")
        db.commit();db.close()
        def launch(args,**kwargs):
            target=Path(next(a[8:] for a in args if a.startswith('/output:')))
            (target/'partial.csv').write_bytes(b'date,bid,ask\n2026.10.15 00:00:00.001,150,15')
            class Exited:
                def wait(self,timeout=None):return 0
            return Exited()
        with patch.object(self.bridge,'TDS_ROOT',self.root),patch.object(self.bridge,'running_process',return_value=False),patch.object(self.bridge.subprocess,'Popen',side_effect=launch):
            with self.assertRaisesRegex(RuntimeError,'途中で切れ'):
                self.bridge.export_ticks('Dukascopy','USDJPY','2026-10-15','2026-10-15',self.root/'run',lambda _:None)



    def test_import_expands_history_limit_and_restores_it_after_failure(self):
        data=self.root/'terminal';(data/'config').mkdir(parents=True)
        common=data/'config/common.ini';common.write_text('[Charts]\nProfileLast=Original\nMaxBars=100000\n',encoding='utf-16')
        config=self.root/'run.ini';config.write_text('[StartUp]\n',encoding='utf-16')
        def launch(*args,**kwargs):
            self.assertIn('MaxBars=100000000',config.read_text(encoding='utf-16'))
            common.write_text('[Charts]\nProfileLast=TDSBridge\nMaxBars=100000000\n',encoding='utf-16')
            class Exited:
                pid=123
                def poll(self):return 1
                def wait(self,timeout=None):return 1
            return Exited()
        with patch.object(self.bridge,'running_terminal',return_value=False),patch.object(self.bridge.subprocess,'Popen',side_effect=launch):
            with self.assertRaises(RuntimeError):self.bridge.run_terminal({'exe':str(data/'terminal64.exe'),'data':str(data)},config,self.root/'absent.json',1)
        self.assertIn('MaxBars=100000\n',common.read_text(encoding='utf-16'))
        self.assertIn('ProfileLast=Original',common.read_text(encoding='utf-16'))
        self.assertFalse((data/'TDSBridge-profile-recovery.json').exists())

    def test_history_limit_restore_does_not_overwrite_other_changes(self):
        common=self.root/'common.ini';common.write_text('[Charts]\nMaxBars=50000\n',encoding='utf-16')
        self.bridge.restore_max_bars(common,'utf-16','100000')
        self.assertIn('MaxBars=50000',common.read_text(encoding='utf-16'))



    def test_quoted_csv_matches_python_reference(self):
        csv=self.root/'quoted.csv';csv.write_text('"Datetime","Bid","Ask"\n"2026.10.15 00:00:01.123","150.001","150.003"\n"2026.10.15 00:00:01.123","150.005","150.007"\n',encoding='utf-8')
        actual=self.bridge.prepare_csv(csv,'2026-10-15','2026-10-15',self.root/'native',3,'Dukascopy','USDJPY')
        expected=self.bridge._python_prepare_csv(csv,'2026-10-15','2026-10-15',self.root/'reference',3,'Dukascopy','USDJPY')
        self.assertEqual(actual,expected)

    def test_owned_work_cleanup_preserves_unknown_and_outside_paths(self):
        root=self.root/'owned';root.mkdir()
        unknown=root/'user-data';unknown.mkdir();(unknown/'report.htm').write_text('keep')
        self.assertFalse(self.bridge.remove_work(unknown,root))
        work=root/'attempt';work.mkdir();self.bridge.mark_work(work,'export')
        (work/'large.bin').write_bytes(b'x'*1024)
        self.assertFalse(self.bridge.remove_work(root,root))
        self.assertFalse(self.bridge.remove_work(work,self.root/'elsewhere'))
        self.assertTrue(self.bridge.remove_work(work,root))
        self.assertTrue((unknown/'report.htm').exists())

    def test_process_probe_failures_defer_cleanup(self):
        import process_inventory
        with patch.object(process_inventory,'processes',side_effect=OSError('probe unavailable')):
            self.assertTrue(self.bridge.running_process('terminal64.exe'))
            self.assertTrue(self.bridge.running_terminal('C:/MT5/terminal64.exe'))

    def test_terminal_probe_requires_success_and_accessible_paths(self):
        import process_inventory
        with patch.object(process_inventory,'processes',return_value=[]):
            self.assertFalse(self.bridge.running_terminal('C:/MT5/terminal64.exe'))
            self.assertFalse(self.bridge.running_process('terminal64.exe'))
        with patch.object(process_inventory,'processes',return_value=[(1,'terminal64.exe')]),patch.object(process_inventory,'image_path',side_effect=OSError('inaccessible')):
            self.assertTrue(self.bridge.running_terminal('C:/MT5/terminal64.exe'))

    def test_cached_stage_still_sweeps_abandoned_attempts(self):
        with patch.dict(sys.modules,{'bridge':self.bridge,'cache_maintenance':SimpleNamespace(maintain=lambda *a,**k:None)}):
            spec=importlib.util.spec_from_file_location('scope_bridge',ROOT/'scope_bridge.py')
            scope=importlib.util.module_from_spec(spec);spec.loader.exec_module(scope)
        local=self.root/'local';folder=local/'ArmadaScopeFX/TdsCache/USDJPY-2026-10-01-2026-10-15'
        stage=folder/'stage';stage.mkdir(parents=True);(stage/'manifest.json').write_text('{}')
        request=self.root/'request.json';request.write_text(json.dumps(dict(symbol='USDJPY',terminal='fake-terminal',data=str(self.root/'mt5'),portable=True,
            **{'from':'2026-10-08','to':'2026-10-16','period':'M1','output':str(self.root/'output.json')})))
        inventory=[dict(source='Dukascopy',symbol='USDJPY',storage_path='USDJPY',digits=3,first_day='2026-10-01',last_day='2026-10-15')]
        manifest=dict(start='2026-10-01',ticks=1,first_msc=1,last_msc=1,tick_hash=1)
        with patch.dict(self.bridge.os.environ,{'LOCALAPPDATA':str(local)}),patch.object(self.bridge,'COMMON',self.root/'common'),\
             patch.object(self.bridge,'inventory',return_value=inventory),patch.object(self.bridge,'running_terminal',return_value=False),\
             patch.object(self.bridge,'compile_mql'),patch.object(self.bridge,'cleanup_stale_work') as cleanup,\
             patch.object(self.bridge,'import_stage',return_value=(manifest,{'ok':True})),patch.object(self.bridge,'export_ticks') as export:
            scope.run(request)
        cleanup.assert_any_call(folder);export.assert_not_called()
        self.assertTrue((stage/'manifest.json').exists())

    def test_cleanup_defers_active_or_recent_work(self):
        root=self.root/'owned';root.mkdir()
        a=root/'active';a.mkdir();self.bridge.mark_work(a,'export')
        b=root/'abandoned';b.mkdir();self.bridge.mark_work(b,'export')
        for p in (a,b):
            meta=json.loads((p/'.armada-work.json').read_text());meta.update(created=0,pid=11 if p==a else 12)
            (p/'.armada-work.json').write_text(json.dumps(meta))
        with patch.object(self.bridge,'process_alive',side_effect=lambda pid:pid==11),patch.object(self.bridge,'running_process',return_value=False):
            self.bridge.cleanup_stale_work(root,now=100000)
        self.assertTrue(a.exists());self.assertFalse(b.exists())

    def test_import_cleans_duplicate_files_and_preserves_receipt(self):
        m=self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n',source='Dukascopy',symbol='USDJPY')
        common=self.root/'common';common.mkdir();terminal={'data':str(self.root/'mt5'),'exe':'fake-terminal'}
        result={k:m[k] for k in ('ticks','bars','first_msc','last_msc','tick_hash')};result['ok']=True
        with patch.object(self.bridge,'COMMON',common),patch.object(self.bridge,'run_terminal',return_value=result),patch.object(self.bridge,'running_terminal',return_value=False):
            self.bridge.import_stage(self.root/'stage',terminal,'USDJPY_TDS_UTC','USDJPY',lambda _:None,source='Dukascopy',source_symbol='USDJPY')
        self.assertEqual(list(common.iterdir()),[])
        self.assertEqual(json.loads((self.root/'stage/import-result.json').read_text()),result)
        self.assertFalse(list((self.root/'mt5/MQL5/Presets').glob('TDSBridge-*.set')))

    def test_failed_import_cleans_duplicate_unless_terminal_still_running(self):
        self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n',source='Dukascopy',symbol='USDJPY')
        common=self.root/'common';common.mkdir();terminal={'data':str(self.root/'mt5'),'exe':'fake-terminal'}
        with patch.object(self.bridge,'COMMON',common),patch.object(self.bridge,'run_terminal',side_effect=RuntimeError('failed')),patch.object(self.bridge,'running_terminal',return_value=False):
            with self.assertRaises(RuntimeError):self.bridge.import_stage(self.root/'stage',terminal,'USDJPY_TDS_UTC','USDJPY',lambda _:None)
        self.assertFalse(list(common.iterdir()))
        with patch.object(self.bridge,'COMMON',common),patch.object(self.bridge,'run_terminal',side_effect=RuntimeError('busy')),patch.object(self.bridge,'running_terminal',return_value=True):
            with self.assertRaises(RuntimeError):self.bridge.import_stage(self.root/'stage',terminal,'USDJPY_TDS_UTC','USDJPY',lambda _:None)
        self.assertEqual(len(list(common.iterdir())),1)

    def test_managed_cache_links_under_lock_and_cleanup_preserves_stage(self):
        m=self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n',source='Dukascopy',symbol='USDJPY')
        local=self.root/'local';stage=local/'ArmadaScopeFX/TdsCache/USDJPY-2026-10-15-2026-10-15/stage'
        stage.parent.mkdir(parents=True);(self.root/'stage').rename(stage)
        common=self.root/'common';terminal={'data':str(self.root/'mt5'),'exe':'fake-terminal'}
        result={k:m[k] for k in ('ticks','bars','first_msc','last_msc','tick_hash')};result['ok']=True
        originals={day[k]:(stage/day[k]).read_bytes() for day in m['days'] for k in ('ticks_file','bars_file')}
        def imported(terminal,config,expected,**kw):
            for name in originals:
                self.assertTrue((stage/name).samefile(expected.parent/name))
                self.assertEqual((stage/name).stat().st_nlink,2)
            return result
        with patch.dict(self.bridge.os.environ,{'LOCALAPPDATA':str(local)}),patch.object(self.bridge,'COMMON',common),\
             patch.object(self.bridge,'running_terminal',return_value=False),patch.object(self.bridge,'run_terminal',side_effect=imported):
            with self.bridge.bridge_lock():
                self.bridge.import_stage(stage,terminal,'USDJPY_TDS_UTC','USDJPY',lambda _:None,immutable_cache=True)
        self.assertFalse(list(common.iterdir()))
        for name,data in originals.items():
            self.assertEqual((stage/name).read_bytes(),data)
            self.assertEqual((stage/name).stat().st_nlink,1)
        self.bridge.validate_stage(stage)

    def test_managed_cache_link_failure_falls_back_to_copy(self):
        m=self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n')
        local=self.root/'local';stage=local/'ArmadaScopeFX/TdsCache/test/stage'
        stage.parent.mkdir(parents=True);(self.root/'stage').rename(stage)
        with patch.dict(self.bridge.os.environ,{'LOCALAPPDATA':str(local)}),patch.object(self.bridge,'COMMON',self.root/'common'),\
             patch.object(self.bridge.os,'link',side_effect=OSError('cross volume')) as link:
            with self.bridge.bridge_lock():
                job=self.bridge.write_job(stage,m,'fallback','USDJPY_TDS_UTC','USDJPY',immutable_cache=True)
            self.assertEqual(link.call_count,2)
            for day in m['days']:
                for key in ('ticks_file','bars_file'):
                    self.assertFalse((stage/day[key]).samefile(job/day[key]))
                    self.assertEqual((stage/day[key]).read_bytes(),(job/day[key]).read_bytes())

    def test_arbitrary_stage_or_missing_lock_never_links(self):
        m=self.prepare('2026.10.15\t00:00:01.123\t150.001\t150.003\t\t\n')
        local=self.root/'local';managed=local/'ArmadaScopeFX/TdsCache/test/stage'
        managed.parent.mkdir(parents=True)
        with patch.dict(self.bridge.os.environ,{'LOCALAPPDATA':str(local)}),patch.object(self.bridge,'COMMON',self.root/'common'),\
             patch.object(self.bridge.os,'link') as link:
            with self.bridge.bridge_lock():
                self.bridge.write_job(self.root/'stage',m,'arbitrary','USDJPY_TDS_UTC','USDJPY',immutable_cache=True)
            (self.root/'stage').rename(managed)
            self.bridge.write_job(managed,m,'unlocked','USDJPY_TDS_UTC','USDJPY',immutable_cache=True)
            with self.bridge.bridge_lock():
                self.bridge.write_job(managed,m,'not-opted-in','USDJPY_TDS_UTC','USDJPY')
            link.assert_not_called()


if __name__ == '__main__':
    unittest.main()
