import json
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
    def test_development_version_overrides_crlf_source_metadata_in_both_archives(self):
        source = Path(__file__).parents[1]
        with tempfile.TemporaryDirectory(prefix="vw-build-test-") as temporary:
            root = Path(temporary).resolve()
            (root / "eng").mkdir()
            shutil.copyfile(source / "eng/Build-Package.ps1", root / "eng/Build-Package.ps1")
            for folder in ("src", "skills", "packaging/plugin"):
                shutil.copytree(source / folder, root / folder, ignore=shutil.ignore_patterns("__pycache__", "*.pyc", "*.egg-info"))
            (root / "docs").mkdir()
            for guide in ("document_format", "privacy", "terms", "support"):
                shutil.copyfile(source / "docs" / (guide + ".md"), root / "docs" / (guide + ".md"))
            shutil.copyfile(source / "LICENSE", root / "LICENSE")
            for relative in ("pyproject.toml", "src/validated_world/__init__.py"):
                text = (source / relative).read_text(encoding="utf-8")
                (root / relative).write_bytes(text.replace("\n", "\r\n").encode("utf-8"))
            result = subprocess.run(
                [SHELL, "-NoProfile", "-NonInteractive", "-File", str(root / "eng/Build-Package.ps1"), "-Version", "0.3.0-dev"],
                capture_output=True, text=True, timeout=30,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            for kind, prefix in (("skill", ""), ("plugin", "skills/validated-world/")):
                with zipfile.ZipFile(root / f"artifacts/release/0.3.0-dev/validated-world-{kind}-0.3.0-dev.zip") as archive:
                    metadata = tomllib.loads(archive.read(prefix + "pyproject.toml").decode("utf-8"))
                    self.assertEqual(metadata["project"]["version"], "0.3.0.dev0")
                    init = archive.read(prefix + "src/validated_world/__init__.py").decode("utf-8")
                    self.assertIn('__version__ = "0.3.0.dev0"\r\n', init)
                    if kind == "plugin":
                        self.assertEqual(json.loads(archive.read("plugin.json"))["version"], "0.3.0-dev")
