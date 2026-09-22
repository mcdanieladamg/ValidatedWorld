import unittest
from pathlib import Path
import sys
sys.path.insert(0, str(Path(__file__).parents[1]))

from eng.python_ci_config import aggregate, resolve, should_run


class CiConfigTests(unittest.TestCase):
    def test_default_and_false_run(self):
        self.assertEqual(resolve({}), {"windows": True, "linux": True, "macos": True})
        self.assertEqual(resolve({"VW_CI_SKIP_WINDOWS": "false"})["windows"], True)

    def test_true_and_all_skipped(self):
        self.assertEqual(resolve({"VW_CI_SKIP_WINDOWS": "true", "VW_CI_SKIP_LINUX": "true", "VW_CI_SKIP_MACOS": "true"}), {"windows": False, "linux": False, "macos": False})

    def test_invalid_nonempty_value_fails(self):
        with self.assertRaises(ValueError):
            should_run("maybe")
        for value in ("1", "0", "yes", "no"):
            with self.assertRaises(ValueError): should_run(value)

    def test_aggregate_requires_every_enabled_job_and_distinguishes_deliberate_exclusion(self):
        base = {"CONFIGURE_RESULT": "success", "RUN_WINDOWS": "true", "RUN_LINUX": "true", "RUN_MACOS": "false", "WINDOWS_RESULT": "success", "LINUX_RESULT": "success", "MACOS_RESULT": "skipped", "ALL_SKIPPED": "false"}
        self.assertTrue(aggregate(base)[0])
        self.assertFalse(aggregate({**base, "LINUX_RESULT": "failure"})[0])
        self.assertFalse(aggregate({**base, "LINUX_RESULT": "skipped"})[0])
        self.assertFalse(aggregate({**base, "CONFIGURE_RESULT": "failure"})[0])
        excluded = {"CONFIGURE_RESULT": "success", "RUN_WINDOWS": "false", "RUN_LINUX": "false", "RUN_MACOS": "false", "ALL_SKIPPED": "true"}
        ok, message = aggregate(excluded)
        self.assertTrue(ok); self.assertIn("excluded", message)
        self.assertFalse(aggregate({**excluded, "RUN_WINDOWS": "true"})[0])


if __name__ == "__main__":
    unittest.main()
