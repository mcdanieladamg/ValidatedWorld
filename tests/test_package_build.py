import json
import hashlib
from pathlib import Path
import shutil
import subprocess
import tempfile
import tomllib
import unittest
import zipfile


SHELL = shutil.which("pwsh") or shutil.which("powershell.exe")


@unittest.skipUnless(SHELL, "Package builder requires PowerShell")
class PackageBuildTests(unittest.TestCase):
    def prepare(self, root):
        source = Path(__file__).parents[1]
        (root / "eng").mkdir()
        for script in ("Build-Package.ps1", "Test-Package.ps1"):
            shutil.copyfile(source / "eng" / script, root / "eng" / script)
        for folder in ("src", "skills", "packaging/plugin"):
            shutil.copytree(source / folder, root / folder, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.egg-info"))
        (root / "docs").mkdir()
        for guide in ("document_format", "privacy", "terms", "support"):
            shutil.copyfile(source / "docs" / (guide + ".md"), root / "docs" / (guide + ".md"))
        shutil.copyfile(source / "LICENSE", root / "LICENSE")
        for relative in ("pyproject.toml", "src/validated_world/__init__.py"):
            text = (source / relative).read_text(encoding="utf-8")
            (root / relative).write_bytes(text.replace("\n", "\r\n").encode("utf-8"))

    def build(self, root, version):
        return subprocess.run(
            [SHELL, "-NoProfile", "-NonInteractive", "-File", str(root / "eng/Build-Package.ps1"), "-Version", version],
            capture_output=True, text=True, timeout=30,
        )

    def test_development_version_overrides_crlf_source_metadata_in_both_archives(self):
        with tempfile.TemporaryDirectory(prefix="vw-build-test-") as temporary:
            root = Path(temporary).resolve()
            self.prepare(root)
            result = self.build(root, "0.3.0-dev")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            release = root / "artifacts/release/0.3.0-dev"
            self.assertIn("Development build", (release / "RELEASE_NOTES-0.3.0-dev.md").read_text())
            for kind, prefix in (("skill", ""), ("plugin", "skills/validated-world/")):
                with zipfile.ZipFile(root / f"artifacts/release/0.3.0-dev/validated-world-{kind}-0.3.0-dev.zip") as archive:
                    metadata = tomllib.loads(archive.read(prefix + "pyproject.toml").decode("utf-8"))
                    self.assertEqual(metadata["project"]["version"], "0.3.0.dev0")
                    init = archive.read(prefix + "src/validated_world/__init__.py").decode("utf-8")
                    self.assertIn('__version__ = "0.3.0.dev0"\r\n', init)
                    if kind == "plugin":
                        self.assertEqual(json.loads(archive.read("plugin.json"))["version"], "0.3.0-dev")

    def test_stable_notes_required_and_checksums_reject_tampering_before_extraction(self):
        with tempfile.TemporaryDirectory(prefix="vw-build-test-") as temporary:
            root = Path(temporary).resolve()
            self.prepare(root)
            result = self.build(root, "1.0.3")
            self.assertNotEqual(result.returncode, 0)
            self.assertIn("Missing tracked release notes", result.stdout + result.stderr)
            release = root / "artifacts/release/1.0.3"
            self.assertFalse(release.exists())
            (root / "docs/releases").mkdir()
            notes = "# ValidatedWorld 1.0.3\n\nA verified release.\n"
            (root / "docs/releases/1.0.3.md").write_text(notes, encoding="utf-8")
            result = self.build(root, "1.0.3")
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            self.assertEqual((release / "RELEASE_NOTES-1.0.3.md").read_text(), notes)
            entries = dict(line.split("  ")[::-1] for line in (release / "SHA256SUMS.txt").read_text().splitlines())
            self.assertEqual(set(entries), {"validated-world-skill-1.0.3.zip", "validated-world-plugin-1.0.3.zip", "RELEASE_NOTES-1.0.3.md"})
            for name, expected in entries.items():
                self.assertEqual(hashlib.sha256((release / name).read_bytes()).hexdigest(), expected)
            for name in entries:
                original = (release / name).read_bytes()
                (release / name).write_bytes(original + b"tampered")
                result = subprocess.run(
                    [SHELL, "-NoProfile", "-NonInteractive", "-File", str(root / "eng/Test-Package.ps1"), "-PackagesDirectory", str(release)],
                    capture_output=True, text=True, timeout=30,
                )
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("Release checksum mismatch", result.stdout + result.stderr)
                (release / name).write_bytes(original)
