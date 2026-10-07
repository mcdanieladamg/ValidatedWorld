import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest
import zipfile


@unittest.skipUnless(os.name == "nt", "Windows local installer")
class LocalInstallTests(unittest.TestCase):
    def test_canonical_install_retires_only_verified_owned_marketplaces(self):
        with tempfile.TemporaryDirectory(prefix="vw-installer-") as temporary:
            trial = Path(temporary).resolve()
            checkout = trial / "checkout"
            (checkout / "eng").mkdir(parents=True)
            installer = checkout / "eng/Install-LocalPlugin.ps1"
            shutil.copyfile(Path(__file__).parents[1] / "eng/Install-LocalPlugin.ps1", installer)
            release = checkout / "artifacts/release/1.0.2"
            release.mkdir(parents=True)
            with zipfile.ZipFile(release / "validated-world-plugin-1.0.2.zip", "w") as archive:
                archive.writestr(".codex-plugin/plugin.json", json.dumps({"name": "validated-world", "version": "1.0.2"}))
                archive.writestr("skills/validated-world/SKILL.md", "# ValidatedWorld\n")

            # Earlier owned installations may have a descriptor in their ID.
            retired_name = "validated-world-retired"
            rows = []
            for name, root, plugin in (
                (retired_name + "-local", checkout / "artifacts/local-plugin/old", retired_name),
                ("unrelated-local", checkout / "artifacts/local-plugin/unrelated", "unrelated"),
                ("outside-local", trial / "outside", retired_name),
                ("escaping-local", checkout / "artifacts/local-plugin/../../escape", retired_name),
            ):
                root = root.resolve()
                record = root / ".agents/plugins/marketplace.json"
                record.parent.mkdir(parents=True)
                record.write_text(json.dumps({"name": name, "plugins": [{"name": plugin, "source": {"source": "local", "path": "./plugins/" + plugin}}]}), encoding="utf-8")
                rows.append([name, str(root)])
            (trial / "configured.json").write_text(json.dumps(rows), encoding="utf-8")
            fake = trial / "fake_codex.py"
            fake.write_text(
                "import json, pathlib, sys\n"
                "root=pathlib.Path(__file__).parent\n"
                "with (root/'calls.jsonl').open('a',encoding='utf-8') as log: log.write(json.dumps(sys.argv[1:])+'\\n')\n"
                "if sys.argv[1:]==['plugin','marketplace','list']:\n"
                " for name,path in json.loads((root/'configured.json').read_text(encoding='utf-8')): print(name+' '+path)\n",
                encoding="utf-8",
            )
            executable = trial / "codex.cmd"
            executable.write_text('@"' + sys.executable + '" "%~dp0fake_codex.py" %*\n', encoding="utf-8")
            result = subprocess.run(
                ["powershell.exe", "-NoProfile", "-NonInteractive", "-File", str(installer), "-Version", "1.0.2", "-CodexExecutable", str(executable)],
                capture_output=True, text=True,
            )
            self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
            marketplace = checkout / "artifacts/local-plugin/1.0.2/.agents/plugins/marketplace.json"
            metadata = json.loads(marketplace.read_text(encoding="utf-8-sig"))
            self.assertEqual(metadata["name"], "validated-world-local-1-0-2")
            self.assertEqual(metadata["interface"]["displayName"], "ValidatedWorld Local")
            self.assertEqual(metadata["plugins"][0]["name"], "validated-world")
            calls = [json.loads(line) for line in (trial / "calls.jsonl").read_text(encoding="utf-8").splitlines()]
            self.assertEqual(calls, [
                ["plugin", "marketplace", "list"],
                ["plugin", "marketplace", "add", str(checkout / "artifacts/local-plugin/1.0.2")],
                ["plugin", "add", "validated-world@validated-world-local-1-0-2"],
                ["plugin", "remove", retired_name + "@" + retired_name + "-local"],
                ["plugin", "marketplace", "remove", retired_name + "-local"],
            ])
            self.assertTrue((trial / "outside/.agents/plugins/marketplace.json").exists())
