import unittest,json,os
from unittest.mock import patch
import test_verified_imports as base
from history_checkpoint import create, finish

class HistoryCheckpointTests(unittest.TestCase):
 setUp=base.VerifiedImportsTests.setUp
 registry=base.VerifiedImportsTests.registry
 seed=base.VerifiedImportsTests.seed
 def test_restores_only_tester_changed_history_and_refreshes_proof(self):
  self.seed();reg=self.registry();token=create(reg)
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc';p.write_bytes(b'tester rewrite')
  result=finish(self.registry(),token)
  self.assertTrue(result['ok']);self.assertEqual(p.read_bytes(),b'history')
  self.assertIsNotNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
  self.assertFalse((self.cache/'checkpoints'/token).exists())
 def test_changed_ticks_are_never_rolled_back_or_blessed(self):
  self.seed();token=create(self.registry())
  p=self.data/'bases/Custom/ticks/USDJPY_TDS_UTC/202001.tkc';p.write_bytes(b'new ticks')
  self.assertFalse(finish(self.registry(),token)['ok']);self.assertEqual(p.read_bytes(),b'new ticks')
  self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_corrupted_snapshot_not_restored(self):
  self.seed();token=create(self.registry());folder=self.cache/'checkpoints'/token
  next((self.cache/'verified-snapshots').rglob('*.bin')).write_bytes(b'bad backup')
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc';p.write_bytes(b'tester rewrite')
  self.assertFalse(finish(self.registry(),token)['ok']);self.assertEqual(p.read_bytes(),b'tester rewrite')
 def test_added_history_is_preserved(self):
  self.seed();token=create(self.registry())
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2021.hcc';p.write_bytes(b'new history')
  self.assertFalse(finish(self.registry(),token)['ok']);self.assertTrue(p.exists())
 def test_no_proof_no_checkpoint(self):self.assertIsNone(create(self.registry()))
 def test_fresh_registry_still_rejects_change_during_capture(self):
  self.seed();reg=self.registry()
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc'
  from history_checkpoint import copy_snapshot
  def mutate(source,folder):
   saved=copy_snapshot(source,folder)
   p.write_bytes(b'changed during capture')
   return saved
  with patch('history_checkpoint.copy_snapshot',side_effect=mutate):
   with self.assertRaises(ValueError):create(reg,freshly_checked=True)
  self.assertEqual(list((self.cache/'checkpoints').iterdir()),[])
 def test_fresh_registry_checks_again_before_committing(self):
  self.seed();reg=self.registry()
  with patch.object(reg,'target',wraps=reg.target) as target:
   token=create(reg,freshly_checked=True)
   self.assertEqual(target.call_count,1)
  self.assertTrue(finish(self.registry(),token)['ok'])
 def test_second_checkpoint_reuses_snapshot_without_copying_data(self):
  self.seed();token=create(self.registry())
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc';p.write_bytes(b'tester rewrite')
  self.assertTrue(finish(self.registry(),token)['ok'])
  with patch('history_checkpoint.copy_snapshot',side_effect=AssertionError('unnecessary full copy')):
   token=create(self.registry())
  self.assertTrue(finish(self.registry(),token)['ok'])
 def test_modified_pool_with_restored_mtime_is_not_trusted(self):
  self.seed();token=create(self.registry());self.assertTrue(finish(self.registry(),token)['ok'])
  p=next((self.cache/'verified-snapshots').rglob('*.bin'));s=p.stat()
  p.write_bytes(b'HISTORY');os.utime(p,ns=(s.st_atime_ns,s.st_mtime_ns))
  token=create(self.registry())
  target=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc';target.write_bytes(b'tester rewrite')
  self.assertTrue(finish(self.registry(),token)['ok']);self.assertEqual(target.read_bytes(),b'history')
 def test_new_verified_history_reclaims_only_obsolete_snapshot(self):
  self.seed();token=create(self.registry());self.assertTrue(finish(self.registry(),token)['ok'])
  old=next((self.cache/'verified-snapshots').rglob('*.bin'))
  p=self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc';p.write_bytes(b'new verified history')
  self.seed();token=create(self.registry())
  self.assertFalse(old.exists());self.assertEqual(len(list((self.cache/'verified-snapshots').rglob('*.bin'))),1)
  p.write_bytes(b'tester rewrite')
  self.assertTrue(finish(self.registry(),token)['ok']);self.assertEqual(p.read_bytes(),b'new verified history')
 def test_checkpoint_cannot_mark_tick_archive_as_restorable(self):
  self.seed();token=create(self.registry());folder=self.cache/'checkpoints'/token
  path=folder/'checkpoint.json';state=json.loads(path.read_text())
  state['files']['bases\\Custom\\ticks\\USDJPY_TDS_UTC\\202001.tkc']=next(iter(state['files'].values()))
  path.write_text(json.dumps(state))
  p=self.data/'bases/Custom/ticks/USDJPY_TDS_UTC/202001.tkc';p.write_bytes(b'new ticks')
  self.assertFalse(finish(self.registry(),token)['ok']);self.assertEqual(p.read_bytes(),b'new ticks')
