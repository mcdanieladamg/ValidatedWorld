import importlib.util
import io
import json
import os
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from pathlib import Path
from validated_world.scratch import decode_record


ROOT = Path(__file__).parents[1]
SKILL = ROOT / "skills" / "validated-world"
HELPER = SKILL / "scripts" / "ndjson_log.py"


class NdjsonLogTests(unittest.TestCase):
    def test_default_allocation_uses_project_cwd_despite_different_process_temp(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            project_folder = root / "project Ω"
            launch_temp = root / "launch-temp"
            project_folder.mkdir()
            launch_temp.mkdir()
            # Model differing command environments without assuming an OS sandbox bug.
            env = dict(os.environ, TMP=str(launch_temp), TEMP=str(launch_temp), TMPDIR=str(launch_temp))
            process = subprocess.Popen([sys.executable, "-X", "utf8", "-I", "-S", str(HELPER)],
                                       cwd=project_folder, env=env, stdin=subprocess.PIPE, stdout=subprocess.PIPE,
                                       stderr=subprocess.PIPE, text=True, encoding="utf-8")
            try:
                readiness = process.stderr.readline()
                self.assertTrue(readiness.startswith("NDJSON ready; responses: "), readiness)
                log = Path(readiness.removeprefix("NDJSON ready; responses: ").rstrip("\n"))
                self.assertEqual(log.parent, project_folder)
                process.stdin.write('{"version":1,"command":"host.help","payload":{}}\n')
                process.stdin.flush()
                # A separate interpreter must see the response before the host exits.
                reader_code = '''import json, subprocess, sys, time
for _ in range(200):
    result = subprocess.run([sys.executable, "-I", "-S", sys.argv[2], "--read", sys.argv[1]], capture_output=True, text=True)
    if result.returncode: raise RuntimeError(result.stderr)
    responses = json.loads(result.stdout)["responses"]
    if responses:
        print(json.dumps(responses[0]))
        break
    time.sleep(.02)
'''
                # Reader has a different temp environment from the host.
                reader = subprocess.run([sys.executable, "-I", "-S", "-c", reader_code,
                    str(log), str(HELPER)], cwd=project_folder, capture_output=True, text=True, encoding="utf-8", timeout=10)
                self.assertEqual(reader.returncode, 0, reader.stderr)
                self.assertEqual(json.loads(reader.stdout)["status"], "ok")
                self.assertIsNone(process.poll())
                process.stdin.write('{"version":1,"command":"host.exit","payload":{}}\n')
                process.stdin.flush()
                process.stdin.close()
                self.assertEqual(process.wait(timeout=10), 0)
                self.assertFalse(log.exists())
                self.assertEqual(list(project_folder.iterdir()), [])
                self.assertEqual(process.stderr.read(), '')
                self.assertEqual(list(launch_temp.iterdir()), [])
            finally:
                if process.poll() is None:
                    process.kill()
                    process.wait(timeout=10)
                for stream in (process.stdin, process.stdout, process.stderr):
                    stream.close()

    def test_missing_shared_root_does_not_create_it_or_start_host(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve() / "absent"
            result = subprocess.run([sys.executable, "-I", "-S", str(HELPER), "--temp-root", str(root)],
                                    capture_output=True, text=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(root.exists())
            self.assertIn("cannot create temporary response file", result.stderr)
            self.assertNotIn("NDJSON ready", result.stderr)

    def test_shared_root_override_is_explicit_and_leaves_cwd_untouched(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary).resolve()
            project_folder = root / "project"
            shared_root = root / "shared"
            project_folder.mkdir()
            shared_root.mkdir()
            result = subprocess.run([sys.executable, "-X", "utf8", "-I", "-S", str(HELPER),
                                     "--temp-root", str(shared_root)], cwd=project_folder,
                                    input='{"version":1,"command":"host.exit","payload":{}}\n',
                                    capture_output=True, text=True, encoding="utf-8", timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            log = Path(result.stderr.removeprefix("NDJSON ready; responses: ").rstrip("\n"))
            self.assertEqual(log.parent, shared_root)
            self.assertFalse(log.exists())
            self.assertEqual(list(shared_root.iterdir()), [])
            self.assertEqual(list(project_folder.iterdir()), [])

    def test_error_then_graceful_eof_removes_owned_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = subprocess.run([sys.executable, '-I', '-S', str(HELPER)], cwd=temporary,
                                    input='{"version":1,"command":"unknown","payload":{}}\n',
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertTrue(result.stderr.startswith('NDJSON ready; responses: '))
            self.assertEqual(list(Path(temporary).iterdir()), [])

    def test_requested_diagnostics_are_retained_after_clean_exit(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = subprocess.run([sys.executable, '-I', '-S', str(HELPER), '--keep-log'], cwd=temporary,
                                    input='{"version":1,"command":"host.exit","payload":{}}\n',
                                    capture_output=True, text=True, timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            lines = result.stderr.splitlines()
            log = Path(lines[0].removeprefix('NDJSON ready; responses: '))
            self.assertEqual(lines[1], f'Response log retained: {log}')
            self.assertEqual(decode_record(log.read_text(encoding='utf-8').splitlines(keepends=True)[-1])['command'], 'host.exit')

    def test_replaced_transport_and_abnormal_exit_preserve_data(self):
        from validated_world.scratch import ResponseTransport
        for abnormal in (False, True):
            with self.subTest(abnormal=abnormal), tempfile.TemporaryDirectory() as temporary:
                document = Path(temporary).resolve() / 'docs-vw.html'
                transport = ResponseTransport(document)
                if abnormal:
                    transport.close(clean=False)
                    self.assertTrue(transport.path.exists())
                else:
                    transport.path.unlink()
                    transport.path.write_bytes(b'preserve replacement')
                    with self.assertRaisesRegex(ValueError, 'replaced'):
                        transport.close()
                    self.assertEqual(transport.path.read_bytes(), b'preserve replacement')

    def test_default_allocation_failure_does_not_start_host(self):
        spec = importlib.util.spec_from_file_location('ndjson_log', HELPER)
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        diagnostics = io.StringIO()
        with patch.object(sys, 'argv', [str(HELPER)]), patch.object(sys, 'stderr', diagnostics), \
                patch('validated_world.scratch.ResponseTransport', side_effect=PermissionError('fixture root denied')), \
                patch.object(helper.runpy, 'run_path') as launch:
            with self.assertRaises(SystemExit) as raised: helper.main()
        self.assertEqual(raised.exception.code, 2)
        self.assertIn('fixture root denied', diagnostics.getvalue())
        self.assertNotIn('NDJSON ready', diagnostics.getvalue())
        self.assertEqual(launch.call_count, 1)  # Discovery only; host not launched.

    def test_explicit_log_with_missing_parent_reports_path_without_starting_host(self):
        with tempfile.TemporaryDirectory() as temporary:
            log = Path(temporary).resolve() / "absent" / "responses.jsonl"
            result = subprocess.run([sys.executable, "-I", "-S", str(HELPER), str(log)],
                                    capture_output=True, text=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse(log.exists())
            self.assertIn(str(log), result.stderr)
            self.assertNotIn("NDJSON ready", result.stderr)

    def test_relative_shared_root_is_rejected(self):
        result = subprocess.run([sys.executable, "-I", "-S", str(HELPER), "--temp-root", "."],
                                capture_output=True, text=True, timeout=10)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("temp-root must be an absolute path", result.stderr)

    def test_complete_reviewed_workflow_reads_responses_before_exit(self):
        spec = importlib.util.spec_from_file_location("verify_skill_workflow", ROOT / "eng" / "verify_skill_workflow.py")
        workflow = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(workflow)
        workflow.verify(SKILL, log_output=True, deny_hard_links=os.name == 'nt')

    def test_existing_response_log_is_preserved(self):
        with tempfile.TemporaryDirectory() as temporary:
            response_log = Path(temporary).resolve() / "responses.jsonl"
            response_log.write_bytes(b"human-owned log\n")
            result = subprocess.run([sys.executable, "-I", "-S", str(HELPER), str(response_log)],
                                    capture_output=True, text=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)
            self.assertEqual(response_log.read_bytes(), b"human-owned log\n")
            self.assertIn("cannot create response log", result.stderr)

    def test_relative_path_is_rejected_without_creating_a_log(self):
        with tempfile.TemporaryDirectory() as temporary:
            result = subprocess.run([sys.executable, "-I", "-S", str(HELPER), "responses.jsonl"],
                                    cwd=temporary, capture_output=True, text=True, timeout=10)
            self.assertNotEqual(result.returncode, 0)
            self.assertFalse((Path(temporary) / "responses.jsonl").exists())
            self.assertIn("must be an absolute path", result.stderr)

    def test_logged_protocol_preserves_unicode_and_structured_errors(self):
        with tempfile.TemporaryDirectory() as temporary:
            response_log = Path(temporary).resolve() / "responses Ω.jsonl"
            requests = [
                {"version": 1, "command": "unknown-Ω", "payload": {}},
                {"version": 1, "command": "host.help", "payload": {}},
                {"version": 1, "command": "host.exit", "payload": {}},
            ]
            result = subprocess.run([sys.executable, "-X", "utf8", "-I", "-S", str(HELPER), str(response_log)],
                                    input="\n".join(json.dumps(item) for item in requests) + "\n",
                                    capture_output=True, text=True, encoding="utf-8", timeout=10)
            self.assertEqual(result.returncode, 0, result.stderr)
            self.assertEqual(result.stdout, "")
            results = [json.loads(line) for line in response_log.read_text(encoding="utf-8").splitlines()]
            self.assertEqual([item["status"] for item in results], ["error", "ok", "ok"])
            self.assertIn("unknown-Ω", results[0]["payload"]["message"])
            self.assertTrue(response_log.read_bytes().endswith(b"\n"))


if __name__ == "__main__":
    unittest.main()
