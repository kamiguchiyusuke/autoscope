"""Native/reference parity; set TDS_STAGE_TEST_COMMAND to a JSON argv prefix.
Example: ["dotnet", "/path/to/stage-tests.dll"]. No terminal is launched.
"""
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]

@unittest.skipUnless(os.environ.get('TDS_STAGE_TEST_COMMAND'), 'Native stage command not configured')
class NativeStageParityTests(unittest.TestCase):
    def test_native_matches_python_and_rejects_corruption(self):
        spec = importlib.util.spec_from_file_location('bridge_parity', ROOT / 'bridge.py')
        bridge = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(bridge)
        command = json.loads(os.environ['TDS_STAGE_TEST_COMMAND'])
        def native(*args):
            return subprocess.run(command + list(map(str, args)), capture_output=True, text=True, encoding="utf-8")
        with tempfile.TemporaryDirectory() as temp:
            root = Path(temp)
            for digits in range(9):
                with self.subTest(digits=digits):
                    csv = root / f'input{digits}.csv'
                    factor = 10 ** digits
                    rows = ['Datetime,Bid,Ask']
                    for stamp, delta in [('2020.12.31 23:59:00.000', 0),
                                         ('2020.12.31 23:59:00.000', 2),
                                         ('2020.12.31 23:59:59.999', 1),
                                         ('2021.01.01 00:00:00.000', 3)]:
                        rows.append(f'{stamp},{150+delta/factor:.{digits}f},{150+(delta+2)/factor:.{digits}f}')
                    csv.write_text('\n'.join(rows)+'\n', encoding='utf-8')
                    stage = root / f'native{digits}'
                    reference = root / f'python{digits}'
                    result = native('prepare', csv, '2020-12-31', '2021-01-01', stage, digits, 'Dukascopy', 'USDJPY')
                    self.assertEqual(result.returncode, 0, result.stderr)
                    expected = bridge._python_prepare_csv(csv, '2020-12-31', '2021-01-01', reference, digits, 'Dukascopy', 'USDJPY')
                    self.assertEqual(json.loads(result.stdout), expected)
                    for file in reference.glob('*.bin'):
                        self.assertEqual(file.read_bytes(), (stage/file.name).read_bytes())
                    result = native('validate', stage)
                    self.assertEqual(result.returncode, 0, result.stderr)
                    self.assertEqual(json.loads(result.stdout), bridge._python_validate_stage(stage))
                    sliced = native('slice', stage, '2021-01-01', '2021-01-01')
                    self.assertEqual(sliced.returncode, 0, sliced.stderr)
                    view = json.loads(sliced.stdout)
                    self.assertEqual(view['ticks'], 1)
                    self.assertEqual(view['bars'], 1)
                    self.assertEqual(len(view['days']), 1)
                    self.assertEqual(view['first_msc'], expected['last_msc'])
                    self.assertNotIn('csv_sha256', view)
                    self.assertEqual(native('slice', stage, '2020-12-30', '2021-01-01').returncode, 1)
                    # Integrity metadata is updated so this exercises M1 equality,
                    # not merely the SHA gate (including spread/real volume fields).
                    manifest = stage/'manifest.json'
                    meta = json.loads(manifest.read_text())
                    bar = stage/meta['days'][0]['bars_file']
                    data = bytearray(bar.read_bytes()); data[52] ^= 1; bar.write_bytes(data)
                    meta['days'][0]['bars_sha256'] = hashlib.sha256(data).hexdigest()
                    manifest.write_text(json.dumps(meta))
                    result = native('validate', stage)
                    self.assertEqual(result.returncode, 1)
                    self.assertIn('M1 mismatch', result.stderr)
                    with self.assertRaises(ValueError):
                        bridge._python_validate_stage(stage)

if __name__ == '__main__':
    unittest.main()

