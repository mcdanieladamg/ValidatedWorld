import json
from pathlib import Path
import shutil
import sys
import tempfile
import tomllib
import unittest

sys.path.insert(0, str(Path(__file__).parents[1]))
from eng.verify_plugin_package import verify


class PackageMetadataTests(unittest.TestCase):
    def test_release_source_versions_agree(self):
        from validated_world import __version__
        source = Path(__file__).parents[1]
        metadata = tomllib.loads((source / "pyproject.toml").read_text(encoding="utf-8"))
        self.assertEqual(metadata["project"]["version"], __version__)
        for relative in ("plugin.json", ".codex-plugin/plugin.json"):
            manifest = json.loads((source / "packaging/plugin" / relative).read_text(encoding="utf-8"))
            self.assertEqual(manifest["version"], __version__)
            self.assertEqual(manifest["name"], metadata["project"]["name"])

    def setUp(self):
        self.trial = tempfile.TemporaryDirectory(prefix="vw-listing-test-")
        self.addCleanup(self.trial.cleanup)
        self.root = Path(self.trial.name) / "plugin"
        source = Path(__file__).parents[1]
        shutil.copytree(source / "packaging/plugin", self.root)
        for directory in (self.root, self.root / "skills/validated-world"):
            (directory / "docs").mkdir()
            for guide in ("privacy", "terms", "support"):
                shutil.copyfile(source / "docs" / f"{guide}.md", directory / "docs" / f"{guide}.md")

    def change_interface(self, field, value, *, both=True):
        portable_path = self.root / "plugin.json"
        portable = json.loads(portable_path.read_text(encoding="utf-8"))
        portable["extensions"]["com.openai"]["interface"][field] = value
        portable_path.write_text(json.dumps(portable), encoding="utf-8")
        if both:
            overlay_path = self.root / ".codex-plugin/plugin.json"
            overlay = json.loads(overlay_path.read_text(encoding="utf-8"))
            overlay["interface"][field] = value
            overlay_path.write_text(json.dumps(overlay), encoding="utf-8")

    def test_complete_listing_passes_and_submission_text_limit_is_enforced(self):
        verify(self.root)
        self.change_interface("shortDescription", "x" * 31)
        with self.assertRaisesRegex(ValueError, "shortDescription"):
            verify(self.root)

    def test_compatibility_overlay_cannot_silently_advertise_different_behavior(self):
        self.change_interface("capabilities", ["Unreviewed writes"], both=False)
        with self.assertRaisesRegex(ValueError, "disagree"):
            verify(self.root)

    def test_package_identity_is_portable_and_matches_the_overlay(self):
        portable_path = self.root / "plugin.json"
        original = json.loads(portable_path.read_text(encoding="utf-8"))
        for invalid_name in ("ValidatedWorld", "validated--world", "validated_world", "", "x" * 65):
            with self.subTest(name=invalid_name):
                portable_path.write_text(json.dumps({**original, "name": invalid_name}), encoding="utf-8")
                with self.assertRaisesRegex(ValueError, "Plugin name"):
                    verify(self.root)
        portable_path.write_text(json.dumps(original), encoding="utf-8")
        overlay_path = self.root / ".codex-plugin/plugin.json"
        overlay = json.loads(overlay_path.read_text(encoding="utf-8"))
        overlay_path.write_text(json.dumps({**overlay, "name": "different-plugin"}), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "identities disagree"):
            verify(self.root)

    def test_missing_and_parent_traversing_assets_are_rejected(self):
        for path in ("./assets/missing.svg", "./../outside.svg"):
            with self.subTest(path=path):
                self.change_interface("logo", path)
                with self.assertRaises(ValueError):
                    verify(self.root)

    def test_active_svg_and_missing_isolated_skill_policy_are_rejected(self):
        icon = self.root / "assets/icon.svg"
        original = icon.read_text(encoding="utf-8")
        icon.write_text(original.replace("</svg>", "<script>alert(1)</script></svg>"), encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "SVG element"):
            verify(self.root)
        icon.write_text(original, encoding="utf-8")
        (self.root / "skills/validated-world/docs/privacy.md").unlink()
        with self.assertRaisesRegex(ValueError, "Missing bundled policy"):
            verify(self.root)


if __name__ == "__main__":
    unittest.main()
