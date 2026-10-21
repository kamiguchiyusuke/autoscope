import sys,unittest,tempfile,os
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from verified_imports import fingerprints,fingerprint
class BulkFingerprintTests(unittest.TestCase):
 def test_native_exact_and_hardlink_change(self):
  with tempfile.TemporaryDirectory() as tmp:
   root=Path(tmp);files=[]
   for i in range(80):
    p=root/f'日本語{i}.bin';p.write_bytes(bytes([i])*23);files.append(p)
   before=fingerprints(files);self.assertEqual(before,[fingerprint(p) for p in files])
   alias=root/'alias';os.link(files[0],alias);s=alias.stat();alias.write_bytes(b'X'*23);os.utime(alias,ns=(s.st_atime_ns,s.st_mtime_ns))
   after=fingerprints(files);self.assertNotEqual(before[0],after[0]);self.assertEqual(after,[fingerprint(p) for p in files])
   files[1].unlink()
   with self.assertRaises(OSError):fingerprints(files)
 def test_no_payload_reads_and_fresh_per_call(self):
  with tempfile.TemporaryDirectory() as tmp:
   p=Path(tmp)/'file';p.write_bytes(b'data')
   with patch.object(Path,'open',side_effect=AssertionError('payload read')):a=fingerprints([p])
   p.write_bytes(b'changed');self.assertNotEqual(a,fingerprints([p]))
 def test_directories_rejected(self):
  with tempfile.TemporaryDirectory() as tmp:
   with self.assertRaises(OSError):fingerprints([Path(tmp)])
 def test_fallback_without_dll(self):
  import verified_imports
  with tempfile.TemporaryDirectory() as tmp:
   p=Path(tmp)/'file';p.write_bytes(b'data')
   with patch.object(verified_imports,'_native_metadata',return_value=None):self.assertEqual(fingerprints([p]),[fingerprint(p)])
