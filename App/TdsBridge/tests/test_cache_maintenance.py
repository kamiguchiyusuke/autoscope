import importlib.util
import json
import os
import tempfile
import threading
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

spec=importlib.util.spec_from_file_location('cache_maintenance',Path(__file__).resolve().parents[1]/'cache_maintenance.py')
cache=importlib.util.module_from_spec(spec);spec.loader.exec_module(cache)

class CacheMaintenanceTests(unittest.TestCase):
    def setUp(self):
        self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup)
        self.local=Path(self.temp.name);self.root=self.local/'ArmadaScopeFX/TdsCache';self.root.mkdir(parents=True)
        self.environment=patch.dict(os.environ,{'LOCALAPPDATA':str(self.local)});self.environment.start();self.addCleanup(self.environment.stop)
        self.bridge=SimpleNamespace(_bridge_lock_owner=(os.getpid(),threading.get_ident()),running_process=lambda _:False)

    def stage(self,name,last=None):
        stage=self.root/(name+'-2026-01-01-2026-01-01')/'stage';stage.mkdir(parents=True)
        manifest=dict(start='2026-01-01',end='2026-01-01',ticks=1,bars=1,first_msc=1,last_msc=1,tick_hash=1,
                      days=[dict(ticks_file='ticks-20260101.bin',bars_file='bars-20260101.bin')])
        (stage/'manifest.json').write_text(json.dumps(manifest));(stage/'import-result.json').write_text(json.dumps(dict(ok=True,**manifest)))
        (stage/'ticks-20260101.bin').write_bytes(b't'*1024);(stage/'bars-20260101.bin').write_bytes(b'b'*60)
        if last is not None:(stage/cache.MARKER).write_text(json.dumps(dict(owner=cache.OWNER,last_used=last)))
        return stage

    def test_marks_only_success_and_keeps_oversized_current(self):
        current=self.stage('CURRENT');failed=self.stage('FAILED')
        (failed/'import-result.json').write_text('{"ok":false}')
        self.assertEqual(cache.maintain(self.root,[current,failed],self.bridge,now=100,max_bytes=0,max_age=0),[])
        self.assertEqual(json.loads((current/cache.MARKER).read_text())['last_used'],100)
        self.assertFalse((failed/cache.MARKER).exists());self.assertTrue(current.exists())

    def test_file_inventory_reuses_directory_metadata(self):
        stage=self.stage('CURRENT',0)
        with patch.object(Path,'is_file',side_effect=AssertionError('repeated per-file query')):
            details=cache.inspect(self.root,stage)
        self.assertEqual(len(details[0]),5)
        self.assertGreater(details[1],1084)

    def test_age_expiry_preserves_unknown_report_and_current(self):
        expired=self.stage('OLD',0);legacy=self.stage('LEGACY');report=self.stage('REPORT',0);current=self.stage('CURRENT',0)
        (report/'report.htm').write_text('user report')
        removed=cache.maintain(self.root,[current],self.bridge,now=cache.MAX_AGE+1)
        self.assertEqual(removed,[str(expired)]);self.assertFalse(expired.exists())
        self.assertTrue(legacy.exists());self.assertTrue((report/'report.htm').exists());self.assertTrue(current.exists())

    def test_budget_evicts_lru_until_under_limit(self):
        old=self.stage('OLD',10);new=self.stage('NEW',20);current=self.stage('CURRENT',30)
        budget=cache.inspect(self.root,new)[1]+cache.inspect(self.root,current)[1]+100
        removed=cache.maintain(self.root,[current],self.bridge,now=100,max_bytes=budget)
        self.assertEqual(removed,[str(old)]);self.assertTrue(new.exists());self.assertTrue(current.exists())

    def test_running_terminal_or_missing_lock_prevents_any_write(self):
        old=self.stage('OLD',0);current=self.stage('CURRENT')
        self.bridge.running_process=lambda _:True
        self.assertEqual(cache.maintain(self.root,[current],self.bridge,now=cache.MAX_AGE+1),[])
        self.assertTrue(old.exists());self.assertFalse((current/cache.MARKER).exists())
        self.bridge.running_process=lambda _:False;self.bridge._bridge_lock_owner=None
        self.assertEqual(cache.maintain(self.root,[current],self.bridge,now=cache.MAX_AGE+1),[])
        self.assertTrue(old.exists());self.assertFalse((current/cache.MARKER).exists())

    def test_reparse_or_extra_directory_is_preserved(self):
        old=self.stage('OLD',0)
        original=cache.linked
        for unsafe in (old,old.parent,old/'ticks-20260101.bin'):
            with patch.object(cache,'linked',side_effect=lambda p,*args: p==unsafe or original(p,*args)):
                self.assertEqual(cache.maintain(self.root,[],self.bridge,now=cache.MAX_AGE+1),[])
                self.assertTrue(old.exists())
        (old/'reports').mkdir()
        self.assertEqual(cache.maintain(self.root,[],self.bridge,now=cache.MAX_AGE+1),[])
        self.assertTrue(old.exists())

    def test_unknown_sibling_and_bad_owner_preserved(self):
        old=self.stage('OLD',0);other=self.stage('OTHER',0)
        (old.parent/'user-report.htm').write_text('keep')
        (other/cache.MARKER).write_text(json.dumps(dict(owner='someone else',last_used=0)))
        self.assertEqual(cache.maintain(self.root,[],self.bridge,now=cache.MAX_AGE+1),[])
        self.assertTrue(old.exists());self.assertTrue(other.exists())
        cache.maintain(self.root,[other],self.bridge,now=cache.MAX_AGE+1)
        self.assertEqual(json.loads((other/cache.MARKER).read_text())['owner'],'someone else')

    def test_terminal_starting_before_deletion_defers(self):
        old=self.stage('OLD',0);calls=iter([False,True]);self.bridge.running_process=lambda _:next(calls)
        self.assertEqual(cache.maintain(self.root,[],self.bridge,now=cache.MAX_AGE+1),[])
        self.assertTrue(old.exists())

if __name__=='__main__':unittest.main()
