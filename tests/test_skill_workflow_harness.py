import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
from eng import verify_skill_workflow as workflow


class SkillWorkflowHarnessTests(unittest.TestCase):
    def test_timeout_keeps_original_error_and_interrupted_workspace(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / "vw-workflow-examples-failure"
            root.mkdir()
            log = root / ".docs-vw.tmp.html"
            log.write_bytes(b"preserve interrupted state")

            class Host:
                stdin = io.StringIO()
                stdout = io.StringIO()
                stderr = io.StringIO(f"NDJSON ready; responses: {log}\n")

                def poll(self):
                    return 0 if self.stdin.closed else None

                def wait(self, timeout):
                    if timeout != 30:
                        raise AssertionError("Host shutdown should allow thirty seconds")
                    if not self.stdin.closed:
                        raise AssertionError("Host should receive graceful EOF first")
                    return 0

            diagnostics = io.StringIO()
            with patch.object(workflow.tempfile, "mkdtemp", return_value=str(root)), \
                    patch.object(workflow.subprocess, "Popen", return_value=Host()), \
                    patch.object(workflow.subprocess, "run", return_value=type('Read', (), {'returncode': 0, 'stdout': '{"responses": [], "nextOffset": 0}', 'stderr': ''})()), \
                    patch.object(workflow.time, "monotonic", side_effect=[0, 11, 31]), \
                    patch.object(workflow.time, "sleep") as pause, \
                    patch.object(sys, "stderr", diagnostics):
                with self.assertRaisesRegex(AssertionError, "timed out during host.help"):
                    workflow.verify(Path(__file__).parents[1] / "skills/validated-world", log_output=True)
            pause.assert_called_once_with(.02)  # Eleven seconds is still within the response budget.
            self.assertEqual(log.read_bytes(), b"preserve interrupted state")
            self.assertTrue(log.exists())
            self.assertIn(str(root), diagnostics.getvalue())
            self.assertNotIn("Refusing unexpected trial cleanup", diagnostics.getvalue())

    def test_successful_cleanup_recognizes_the_actual_document_lock_name(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / "vw-workflow-examples-lock"
            root.mkdir()
            with patch.object(workflow.tempfile, "mkdtemp", return_value=str(root)), \
                    patch.object(workflow.tempfile, "gettempdir", return_value=str(root.parent)):
                with workflow.trial_directory():
                    (root / ".docs-vw.html.vw-lock").write_bytes(b"0")
            self.assertFalse(root.exists())
