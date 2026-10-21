import os
import sys
import unittest
from pathlib import Path
from unittest.mock import patch
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
import process_inventory as probes


class ProcessInventoryTests(unittest.TestCase):
    def test_current_process_found_without_spawning_helpers(self):
        with patch('subprocess.run',side_effect=AssertionError('helper launched')):
            self.assertIn(os.getpid(),[pid for pid,name in probes.processes()])
            self.assertTrue(probes.running_path(sys.executable))
            self.assertTrue(probes.running_name(Path(sys.executable).name))
            self.assertFalse(probes.running_name('AutoScope-no-such-process-974e.exe'))

    def test_inaccessible_path_and_failed_snapshot_fail_closed(self):
        with patch.object(probes,'processes',side_effect=OSError('snapshot failed')):
            self.assertTrue(probes.running_name('terminal64.exe'))
            self.assertTrue(probes.running_path('C:/MT5/terminal64.exe'))
        with patch.object(probes,'processes',return_value=[(123,'terminal64.exe')]),patch.object(probes,'image_path',side_effect=OSError('denied')):
            self.assertTrue(probes.running_path('C:/MT5/terminal64.exe'))

    def test_exact_terminal_path_and_fresh_inventory(self):
        with patch.object(probes,'processes',side_effect=[[],[(123,'terminal64.exe')]]),patch.object(probes,'image_path',return_value='C:/MT5/terminal64.exe'):
            self.assertFalse(probes.running_path('C:/MT5/terminal64.exe'))
            self.assertTrue(probes.running_path('C:/MT5/terminal64.exe'))
        with patch.object(probes,'processes',return_value=[(123,'terminal64.exe')]),patch.object(probes,'image_path',return_value='C:/Other/terminal64.exe'):
            self.assertFalse(probes.running_path('C:/MT5/terminal64.exe'))

    def test_disappeared_process_is_not_running(self):
        with patch.object(probes,'processes',return_value=[(123,'terminal64.exe')]),patch.object(probes,'image_path',return_value=None):
            self.assertFalse(probes.running_path('C:/MT5/terminal64.exe'))

if __name__=='__main__':unittest.main()
