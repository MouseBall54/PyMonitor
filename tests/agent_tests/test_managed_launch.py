import os
import sys
import tempfile
import unittest
from unittest import mock

from pyruntime_inspector_agent import managed_launch


class ManagedLaunchTests(unittest.TestCase):
    def test_restores_script_argv_path_and_main_module_execution(self):
        with tempfile.TemporaryDirectory() as directory:
            script = os.path.join(directory, "target.py")
            with open(script, "w", encoding="utf-8") as stream:
                stream.write("pass\n")

            original_argv = sys.argv
            original_path = list(sys.path)
            self.addCleanup(setattr, sys, "argv", original_argv)
            self.addCleanup(sys.path.__setitem__, slice(None), original_path)
            sys.argv = ["managed_launch", script, "one", "two words"]

            with mock.patch.object(managed_launch, "start_inspector") as start, mock.patch.object(managed_launch.runpy, "run_path") as run_path:
                managed_launch.main()

            start.assert_called_once_with()
            run_path.assert_called_once_with(os.path.abspath(script), run_name="__main__")
            self.assertEqual([os.path.abspath(script), "one", "two words"], sys.argv)
            self.assertEqual(directory, sys.path[0])

    def test_missing_script_returns_clear_system_exit(self):
        original_argv = sys.argv
        self.addCleanup(setattr, sys, "argv", original_argv)
        sys.argv = ["managed_launch", "missing.py"]
        with self.assertRaisesRegex(SystemExit, "Python script not found"):
            managed_launch.main()
