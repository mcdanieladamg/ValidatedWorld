import io
from pathlib import Path
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).parents[1]))
from eng import verify_skill_workflow as workflow


class SkillWorkflowHarnessTests(unittest.TestCase):
    def test_failure_preserves_original_error_and_developer_diagnostics(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / 'vw-workflow-examples-failure'; root.mkdir()
            diagnostics = io.StringIO()
            with patch.object(workflow.tempfile, 'mkdtemp', return_value=str(root)), patch.object(sys, 'stderr', diagnostics):
                with self.assertRaisesRegex(TimeoutError, 'original failure'):
                    with workflow.trial_directory():
                        (root / 'developer-diagnostic.txt').write_text('diagnostic fixture')
                        raise TimeoutError('original failure')
            self.assertTrue((root / 'developer-diagnostic.txt').exists())
            self.assertIn(str(root), diagnostics.getvalue())
            self.assertNotIn('Refusing unexpected trial cleanup', diagnostics.getvalue())

    def test_successful_trial_cleanup_and_unknown_file_refusal(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / 'vw-workflow-examples-success'; root.mkdir()
            with patch.object(workflow.tempfile, 'mkdtemp', return_value=str(root)), patch.object(workflow.tempfile, 'gettempdir', return_value=str(root.parent)):
                with workflow.trial_directory(): (root / 'docs-vw.html').write_text('fixture')
            self.assertFalse(root.exists())
            root.mkdir()
            with patch.object(workflow.tempfile, 'mkdtemp', return_value=str(root)), patch.object(workflow.tempfile, 'gettempdir', return_value=str(root.parent)):
                with self.assertRaisesRegex(AssertionError, 'Refusing unexpected'):
                    with workflow.trial_directory(): (root / 'unknown.txt').write_text('preserve')
            self.assertTrue((root / 'unknown.txt').exists())
