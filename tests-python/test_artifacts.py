import base64
import hashlib
import os
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.artifacts import FileSystemArtifactChecker, check_artifacts
from validated_world.models import Attribute, Edge, Graph, GraphValue, Node


def anchor(identifier, path, digest, *, adapter=None, version=None):
    attributes = [
        Attribute("artifact.path", GraphValue.text(path)),
        Attribute("artifact.sha256", GraphValue.text(digest)),
    ]
    if adapter is not None:
        attributes.append(Attribute("artifact.adapter", GraphValue.symbol(adapter)))
    if version is not None:
        attributes.append(Attribute("artifact.adapter-version", GraphValue.integer(version)))
    return Node(identifier, "External artifact", "external-anchor", ("artifact",), tuple(attributes))


def graph(*anchors):
    purpose = Node("purpose", "Purpose", "purpose")
    return Graph("artifacts", "Artifacts", purpose.id, (purpose, *anchors), tuple(Edge(item.id + "-scope", item.id, purpose.id, "scope-parent") for item in anchors))


class ArtifactTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.project = self.root / "project.vw.db"

    def test_checker_distinguishes_match_drift_missing_and_bounded_samples(self):
        matched_bytes = b"matched-content"
        large_bytes = b"x" * (1024 * 1024 + 17)
        (self.root / "matched.bin").write_bytes(matched_bytes)
        (self.root / "large.bin").write_bytes(large_bytes)
        candidate = graph(
            anchor("matched", "matched.bin", hashlib.sha256(matched_bytes).hexdigest()),
            anchor("drifted", "large.bin", "0" * 64),
            anchor("missing", "missing.bin", "1" * 64),
        )
        report = check_artifacts(str(self.project), candidate, allowed_roots=[str(self.root)], max_sample_bytes=7)
        self.assertEqual((report["matchedCount"], report["driftedCount"], report["missingCount"]), (1, 1, 1))
        matched = next(item for item in report["items"] if item["nodeId"] == "matched")
        drifted = next(item for item in report["items"] if item["nodeId"] == "drifted")
        self.assertEqual(base64.b64decode(matched["contentSampleBase64"]), matched_bytes[:7])
        self.assertEqual(len(base64.b64decode(drifted["contentSampleBase64"])), 7)
        self.assertTrue(drifted["contentSampleTruncated"])
        self.assertEqual(drifted["actualSha256"], hashlib.sha256(large_bytes).hexdigest())

    def test_reads_are_denied_without_host_roots_and_parent_or_sibling_escapes(self):
        authorized = self.root / "allowed"
        sibling = self.root / "allowed-sibling"
        authorized.mkdir(); sibling.mkdir()
        secret = sibling / "secret.bin"; secret.write_bytes(b"secret")
        digest = hashlib.sha256(b"secret").hexdigest()
        candidate = graph(anchor("outside", str(secret), digest), anchor("parent", "../allowed-sibling/secret.bin", digest))
        no_authority = check_artifacts(str(authorized / "project.vw.db"), candidate)
        self.assertEqual(no_authority["unauthorizedCount"], 2)
        bounded = check_artifacts(str(authorized / "project.vw.db"), candidate, allowed_roots=[str(authorized)])
        self.assertEqual(bounded["unauthorizedCount"], 2)

    def test_relative_absolute_and_unicode_paths_are_authorized_by_exact_roots(self):
        folder = self.root / "allowed ünicode"; folder.mkdir()
        first = folder / "one.bin"; second = folder / "two.bin"
        first.write_bytes(b"one"); second.write_bytes(b"two")
        candidate = graph(
            anchor("relative", "one.bin", hashlib.sha256(b"one").hexdigest()),
            anchor("absolute", str(second), hashlib.sha256(b"two").hexdigest()),
        )
        report = check_artifacts(str(folder / "project.vw.db"), candidate, allowed_roots=[str(folder)])
        self.assertEqual(report["matchedCount"], 2)

    def test_link_resolving_outside_authority_is_rejected_when_supported(self):
        allowed = self.root / "allowed"; outside = self.root / "outside"
        allowed.mkdir(); outside.mkdir()
        target = outside / "secret.bin"; target.write_bytes(b"secret")
        link = allowed / "link.bin"
        try:
            os.symlink(target, link)
        except (OSError, NotImplementedError) as exc:
            self.skipTest(f"file symlinks are not available to this Windows account: {exc}")
        candidate = graph(anchor("link", "link.bin", hashlib.sha256(b"secret").hexdigest()))
        report = check_artifacts(str(allowed / "project.vw.db"), candidate, allowed_roots=[str(allowed)])
        self.assertEqual(report["items"][0]["status"], "unauthorized")

    def test_invalid_and_unsupported_anchors_are_reported_without_access(self):
        malformed = Node("malformed", "Malformed", "external-anchor", ("artifact",))
        candidate = graph(
            malformed,
            anchor("nul", "bad\0path", "0" * 64),
            anchor("adapter", "anything", "0" * 64, adapter="network"),
            anchor("version", "anything", "0" * 64, version=2),
        )
        report = check_artifacts(str(self.project), candidate, allowed_roots=[str(self.root)])
        statuses = {item["nodeId"]: item["status"] for item in report["items"]}
        self.assertEqual(statuses, {"adapter": "unsupportedAdapter", "malformed": "invalidAnchor", "nul": "invalidAnchor", "version": "unsupportedAdapter"})

    def test_scan_and_custom_host_adapter_are_bounded(self):
        candidates = graph(*(anchor(f"item-{index}", "unused", "0" * 64, adapter="memory") for index in range(4)))

        class MemoryChecker:
            adapter_id = "memory"
            contract_version = 1
            def check(self, request):
                value = request["anchor"]
                return {"nodeId": value["nodeId"], "path": value["path"], "resolvedPath": "memory://item", "adapterId": "memory", "adapterVersion": 1, "status": "matched", "message": "matched", "expectedSha256": value["sha256"], "actualSha256": value["sha256"], "contentSampleBase64": "", "contentSampleTruncated": False}

        report = check_artifacts(str(self.project), candidates, max_anchors=2, checkers=[MemoryChecker()])
        self.assertEqual((report["totalAnchorCount"], len(report["items"]), report["isComplete"]), (4, 2, False))
        self.assertEqual(report["matchedCount"], 2)
        with self.assertRaises(ValueError):
            check_artifacts(str(self.project), candidates, max_anchors=0)
        with self.assertRaises(ValueError):
            check_artifacts(str(self.project), candidates, checkers=[MemoryChecker(), MemoryChecker()])

    def test_anchor_metadata_and_checker_configuration_are_strict(self):
        invalid_digest = Node("digest", "Digest", "external-anchor", ("artifact",), (
            Attribute("artifact.path", GraphValue.text("x")),
            Attribute("artifact.sha256", GraphValue.text("not-a-digest")),
        ))
        invalid_adapter = Node("adapter", "Adapter", "external-anchor", ("artifact",), (
            Attribute("artifact.path", GraphValue.text("x")),
            Attribute("artifact.sha256", GraphValue.text("0" * 64)),
            Attribute("artifact.adapter", GraphValue.integer(1)),
        ))
        invalid_version = Node("version", "Version", "external-anchor", ("artifact",), (
            Attribute("artifact.path", GraphValue.text("x")),
            Attribute("artifact.sha256", GraphValue.text("0" * 64)),
            Attribute("artifact.adapter-version", GraphValue.integer(0)),
        ))
        report = check_artifacts(str(self.project), graph(invalid_digest, invalid_adapter, invalid_version))
        self.assertEqual(report["invalidAnchorCount"], 3)
        self.assertTrue(all(item["expectedSha256"] is None for item in report["items"]))

        class WrongVersion:
            adapter_id = "wrong"
            contract_version = 2
            def check(self, request):
                self.fail("must not run")

        with self.assertRaisesRegex(ValueError, "contract version"):
            check_artifacts(str(self.project), graph(), checkers=[WrongVersion()])
        with self.assertRaises(ValueError):
            check_artifacts(str(self.project), graph(), checkers=[])

    def test_allowed_root_validation_node_filter_and_non_file_are_safe(self):
        file_path = self.root / "one.bin"; file_path.write_bytes(b"one")
        candidates = graph(
            anchor("one", "one.bin", hashlib.sha256(b"one").hexdigest()),
            anchor("two", "two.bin", "0" * 64),
        )
        filtered = check_artifacts(str(self.project), candidates, node_id="one", allowed_roots=[str(self.root)])
        self.assertEqual((filtered["totalAnchorCount"], filtered["matchedCount"]), (1, 1))
        with self.assertRaisesRegex(ValueError, "allowed roots"):
            check_artifacts(str(self.project), candidates, allowed_roots=["bad\0root"])
        with self.assertRaises(FileNotFoundError):
            check_artifacts(str(self.project), candidates, allowed_roots=[str(self.root / "missing-root")])

        directory_anchor = graph(anchor("directory", ".", hashlib.sha256(b"").hexdigest()))
        result = check_artifacts(str(self.project), directory_anchor, allowed_roots=[str(self.root)])
        self.assertEqual(result["unreadableCount"], 1)

    def test_filesystem_checker_rejects_nontext_and_control_paths(self):
        checker = FileSystemArtifactChecker()
        base = {"nodeId": "n", "sha256": "0" * 64}
        for raw in (1, "bad\npath"):
            result = checker.check({"projectPath": str(self.project), "anchor": base | {"path": raw}, "allowedRoots": [], "maxSampleBytes": 1})
            with self.subTest(raw=raw):
                self.assertEqual(result["status"], "invalidAnchor")

    def test_caller_sized_sample_bound_does_not_preallocate_requested_capacity(self):
        content = b"tiny"
        (self.root / "tiny.bin").write_bytes(content)
        candidate = graph(anchor("tiny", "tiny.bin", hashlib.sha256(content).hexdigest()))
        report = check_artifacts(
            str(self.project), candidate, allowed_roots=[str(self.root)], max_sample_bytes=2**31 - 1
        )
        item = report["items"][0]
        self.assertEqual(base64.b64decode(item["contentSampleBase64"]), content)
        self.assertFalse(item["contentSampleTruncated"])


if __name__ == "__main__":
    unittest.main()
