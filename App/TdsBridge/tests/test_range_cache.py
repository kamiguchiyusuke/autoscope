import json
import sys
import tempfile
import unittest
from unittest.mock import patch
from datetime import date
from pathlib import Path
sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import range_cache


class RangeCacheTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)

    def stage(self, start, end, **changes):
        p = self.root/f'USDJPY-{start}-{end}'/'stage'
        p.mkdir(parents=True)
        (p/'ticks.bin').write_bytes(b'fixture')
        (p/'bars.bin').write_bytes(b'fixture')
        m = dict(start=start, end=end, source='Dukascopy', symbol='USDJPY', digits=3,
                 days=[dict(ticks_file='ticks.bin', bars_file='bars.bin')])
        m.update(changes)
        (p/'manifest.json').write_text(json.dumps(m))
        return p

    def plan(self, a, b):
        return range_cache.plan(self.root,date.fromisoformat(a),date.fromisoformat(b),'Dukascopy','USDJPY',3)

    def test_fifteen_year_history_only_exports_extra_day(self):
        p=self.stage('2011-10-10','2026-10-16')
        result=self.plan('2011-10-10','2026-10-17')
        self.assertEqual(result,[(date(2011,10,10),date(2026,10,16),p),(date(2026,10,17),date(2026,10,17),None)])

    def test_subset_and_same_range_reuse(self):
        p=self.stage('2024-01-01','2024-12-31')
        self.assertEqual(self.plan('2024-02-01','2024-03-01'),[(date(2024,2,1),date(2024,3,1),p)])

    def test_inventory_does_not_stat_each_payload(self):
        p=self.stage('2024-01-01','2024-12-31')
        with patch.object(Path,'is_file',side_effect=AssertionError('per-file stat')):
            self.assertEqual(self.plan('2024-02-01','2024-03-01')[0][2],p)

    def test_directory_is_not_payload(self):
        p=self.stage('2024-01-01','2024-12-31')
        (p/'ticks.bin').unlink();(p/'ticks.bin').mkdir()
        self.assertIsNone(self.plan('2024-02-01','2024-03-01')[0][2])

    def test_missing_prefix_middle_suffix(self):
        self.stage('2024-01-03','2024-01-05')
        self.stage('2024-01-08','2024-01-10')
        missing=[(a,b) for a,b,p in self.plan('2024-01-01','2024-01-12') if p is None]
        self.assertEqual(missing,[(date(2024,1,1),date(2024,1,2)),(date(2024,1,6),date(2024,1,7)),(date(2024,1,11),date(2024,1,12))])

    def test_smallest_complete_cache_is_preferred(self):
        self.stage('2011-01-01','2026-12-31')
        p=self.stage('2024-01-01','2024-01-31')
        self.assertEqual(self.plan('2024-01-02','2024-01-05')[0][2],p)

    def test_incompatible_or_missing_payload_not_reused(self):
        self.stage('2024-01-01','2024-01-04',digits=5)
        p=self.stage('2024-01-05','2024-01-08')
        (p/'ticks.bin').unlink()
        self.assertEqual(self.plan('2024-01-01','2024-01-08'),[(date(2024,1,1),date(2024,1,8),None)])

    def test_overlapping_ranges_do_not_export_covered_days(self):
        self.stage('2024-01-01','2024-01-06')
        self.stage('2024-01-04','2024-01-10')
        self.assertTrue(all(p is not None for _,_,p in self.plan('2024-01-01','2024-01-10')))


if __name__=='__main__':unittest.main()
