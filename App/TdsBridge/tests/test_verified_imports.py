import sys,unittest,tempfile,json,os,time
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from verified_imports import VerifiedImports, fingerprint, SourceLease

class VerifiedImportsTests(unittest.TestCase):
 def setUp(self):
  self.temp=tempfile.TemporaryDirectory();self.addCleanup(self.temp.cleanup);self.root=Path(self.temp.name)
  self.cache=self.root/'cache';self.stage=self.cache/'USDJPY-test/stage';self.stage.mkdir(parents=True)
  self.data=self.root/'terminal';self.exe=self.root/'terminal64.exe';self.exe.write_bytes(b'exe')
  self.code=self.root/'import.mq5';self.code.write_text('version1')
  for folder,name in [('history','2020.hcc'),('ticks','202001.tkc')]:
   p=self.data/'bases/Custom'/folder/'USDJPY_TDS_UTC';p.mkdir(parents=True);(p/name).write_bytes(b'history')
  (self.data/'bases/symbols.custom.dat').write_bytes(b'spec')
  self.terminal=dict(data=str(self.data),exe=str(self.exe),portable=False)
  self.manifest=dict(source='Dukascopy',symbol='USDJPY',ticks=1,bars=1,first_msc=1,last_msc=1,tick_hash=1,days=[dict(ticks_file='ticks.bin',bars_file='bars.bin')])
  (self.stage/'manifest.json').write_text(json.dumps(self.manifest));(self.stage/'ticks.bin').write_bytes(b'ticks');(self.stage/'bars.bin').write_bytes(b'bars')
  self.result=dict(ok=True,**{k:self.manifest[k] for k in ('ticks','bars','first_msc','last_msc','tick_hash')})
 def registry(self):return VerifiedImports(self.cache,self.terminal,'USDJPY_TDS_UTC','USDJPY.raw',[self.code])
 def seed(self):self.registry().remember(self.stage,self.manifest,self.result)
 def test_unproven_not_reused(self):self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_fingerprint_does_not_open_data_stream(self):
  p=self.stage/'ticks.bin'
  with patch.object(Path,'open',side_effect=AssertionError('payload opened')):
   value=fingerprint(p)
  self.assertEqual(value[2],5)
 def test_fingerprint_reuses_api_bindings_but_reads_fresh_metadata(self):
  import ctypes
  p=self.stage/'ticks.bin';before=fingerprint(p,checked_parent=True)
  p.write_bytes(b'updated payload')
  with patch.object(ctypes,'WinDLL',side_effect=AssertionError('API bindings rebuilt')):
   after=fingerprint(p,checked_parent=True)
  self.assertNotEqual(before,after)
  self.assertEqual(after[2],15)
 def test_target_checks_parent_paths_per_folder_not_per_archive(self):
  import verified_imports
  folder=self.data/'bases/Custom/ticks/USDJPY_TDS_UTC'
  for n in range(40):(folder/f'{n}.tkc').write_bytes(b'ticks')
  with patch.object(verified_imports,'plain',wraps=verified_imports.plain) as checked:
   self.registry().target()
  self.assertLess(checked.call_count,35)
 def test_hardlink_write_with_restored_mtime_invalidates(self):
  self.seed();p=self.stage/'ticks.bin';link=self.root/'alias.bin';os.link(p,link);s=p.stat()
  link.write_bytes(b'TICKS');os.utime(link,ns=(s.st_atime_ns,s.st_mtime_ns))
  self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_unchanged_reused(self):
  self.seed();self.assertEqual(self.registry().reuse(self.stage,'Dukascopy','USDJPY')[0],self.manifest)
 def test_source_mutation_with_restored_mtime_invalidates(self):
  self.seed();p=self.stage/'ticks.bin';s=p.stat();p.write_bytes(b'TICKS');os.utime(p,ns=(s.st_atime_ns,s.st_mtime_ns))
  self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_history_mutation_with_restored_mtime_invalidates(self):
  self.seed();p=self.data/'bases/Custom/ticks/USDJPY_TDS_UTC/202001.tkc';s=p.stat();p.write_bytes(b'HISTORY');os.utime(p,ns=(s.st_atime_ns,s.st_mtime_ns))
  self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_missing_history_invalidates(self):
  self.seed();(self.data/'bases/Custom/history/USDJPY_TDS_UTC/2020.hcc').unlink();self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_metadata_changes_invalidate(self):
  self.seed();(self.data/'bases/symbols.custom.dat').write_bytes(b'changed');self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_code_changes_invalidate(self):
  self.seed();self.code.write_text('version2');self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_build_changes_invalidate(self):
  self.seed();self.exe.write_bytes(b'new exe');self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_wrong_source_rejected(self):
  self.seed();self.assertIsNone(self.registry().reuse(self.stage,'Other','USDJPY'))
 def test_own_verified_addition_preserves_prior_range(self):
  self.seed();reg=self.registry();p=self.data/'bases/Custom/ticks/USDJPY_TDS_UTC/202001.tkc';p.write_bytes(b'extended')
  other=self.cache/'second/stage';other.mkdir(parents=True)
  for f in self.stage.iterdir():(other/f.name).write_bytes(f.read_bytes())
  reg.remember(other,self.manifest,self.result)
  self.assertIsNotNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
  self.assertIsNotNone(self.registry().reuse(other,'Dukascopy','USDJPY'))
 def test_failed_import_never_recorded(self):
  with self.assertRaises(ValueError):self.registry().remember(self.stage,self.manifest,dict(self.result,ok=False))
  self.assertIsNone(self.registry().reuse(self.stage,'Dukascopy','USDJPY'))
 def test_source_lease_blocks_writes_until_proof_committed(self):
  lease=SourceLease(self.stage)
  try:
   with self.assertRaises(OSError):(self.stage/'ticks.bin').write_bytes(b'changed')
   lease.verify()
  finally:lease.close()
  (self.stage/'ticks.bin').write_bytes(b'changed')
 def test_source_lease_detects_replaced_paths(self):
  lease=SourceLease(self.stage)
  try:
   p=self.stage/'ticks.bin';p.unlink();p.write_bytes(b'ticks')
   with self.assertRaises(ValueError):lease.verify()
  finally:lease.close()
 def test_untrusted_paths_rejected(self):
  self.manifest['days'][0]['ticks_file']='../ticks.bin';(self.stage/'manifest.json').write_text(json.dumps(self.manifest))
  with self.assertRaises(ValueError):self.registry().remember(self.stage,self.manifest,self.result)
 def test_pipeline_reuse_skips_full_read_and_mt5_launch(self):
  import bridge
  local=self.root/'local';managed=local/'ArmadaScopeFX/TdsCache/range/stage'
  managed.parent.mkdir(parents=True);self.stage.rename(managed);self.stage=managed
  with patch.dict(os.environ,{'LOCALAPPDATA':str(local)}),patch.object(bridge,'COMMON',self.root/'common'),patch.object(bridge,'running_terminal',return_value=False),patch.object(bridge,'write_job'),patch.object(bridge,'validate_stage',return_value=self.manifest) as validate,patch.object(bridge,'run_terminal',return_value=self.result) as launch:
   with bridge.bridge_lock():
    bridge.import_stage(managed,self.terminal,'USDJPY_TDS_UTC','USDJPY.raw',lambda _:None,source='Dukascopy',source_symbol='USDJPY',immutable_cache=True)
    validate.reset_mock();launch.reset_mock()
    m,result=bridge.import_stage(managed,self.terminal,'USDJPY_TDS_UTC','USDJPY.raw',lambda _:None,source='Dukascopy',source_symbol='USDJPY',immutable_cache=True)
    validate.assert_not_called();launch.assert_not_called();self.assertTrue(result['verified_reuse'])
    (managed/'ticks.bin').write_bytes(b'changed')
    validate.side_effect=ValueError('changed data rejected')
    with self.assertRaisesRegex(ValueError,'changed data rejected'):
     bridge.import_stage(managed,self.terminal,'USDJPY_TDS_UTC','USDJPY.raw',lambda _:None,source='Dukascopy',source_symbol='USDJPY',immutable_cache=True)
    validate.assert_called_once()

if __name__=='__main__':unittest.main()
