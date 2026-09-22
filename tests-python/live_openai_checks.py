"""Explicitly opted-in paid OpenAI acceptance checks.

This module is intentionally outside unittest discovery. GitHub Actions invokes
it only for trusted manual/main runs after the human enables the corresponding
feature-specific live-test setting.
"""

from pathlib import Path
import sys
import tempfile

import pytest

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.application import Application, sample_graph
from validated_world.authoring import AuthoringConversation, AuthoringToolHost
from validated_world.config import ReviewConfig, load_authoring_config, load_review_config
from validated_world.models import EntityKind, Node, Operation, OperationKind
from validated_world.storage import ProjectStore


pytestmark = pytest.mark.live


def _reviewed_replace(path, node, config):
    application = Application(review_config_loader=lambda: config)
    session = application.begin(str(path), "technical-project", "live-acceptance", "Exercise the exact semantic review gate")
    session = application.apply(session.reference(), (Operation(OperationKind.REPLACE, EntityKind.NODE, node.id, node=node),))
    session = application.review(session.reference(), [{"nodeId": item["nodeId"], "kind": "updated" if item["isDirectChange"] else "reviewedNoChange"} for item in session.affected_nodes], [item["nodeId"] for item in session.scope_context])
    session.preview(max(1, len(session.review_items())))
    return application.write(session.reference())


def test_live_semantic_review_allows_control_and_blocks_a_purpose_contradiction_sequentially():
    config = load_review_config()
    if not config.live_tests: pytest.skip("VW_AIREVIEW__LIVETESTS=true is required")
    assert config.effectively_enabled, "Live review requires an enabled feature and configured API key"
    with tempfile.TemporaryDirectory() as temporary:
        allow_path = Path(temporary) / "allow.vw.db"; block_path = Path(temporary) / "block.vw.db"
        ProjectStore().initialize(allow_path, sample_graph()); ProjectStore().initialize(block_path, sample_graph())
        allowed = _reviewed_replace(allow_path, Node("battery-assumption", "The battery lasts for the target duty cycle.", "assumption"), config)
        assert allowed["status"] == "written", allowed
        assert allowed["semanticReview"]["decision"] == "allow", allowed
        blocked = _reviewed_replace(block_path, Node("purpose", "The sensor requires a continuous public-cloud connection and uploads every reading off-device."), config)
        assert blocked["status"] == "semanticReviewBlocked", blocked
        assert blocked["semanticReview"]["decision"] == "block", blocked


def test_live_authoring_handles_minimal_new_and_existing_projects_sequentially():
    config = load_authoring_config()
    if not config["liveTests"]: pytest.skip("VW_AIAUTHORING__LIVETESTS=true is required")
    assert config["enabled"] and config["configured"], "Live authoring requires an enabled feature and configured API key"
    with tempfile.TemporaryDirectory() as temporary:
        new_path = Path(temporary) / "new-lore.vw.db"
        new_conversation = AuthoringConversation(
            AuthoringToolHost(Application(), str(new_path)), max_tool_calls=config["maxToolCallsPerTurn"]
        )
        created = new_conversation.turn(
            "Create a new project with stable ID tiny-lore, title Tiny Lore, purpose ID purpose, "
            "and purpose text 'Keep a tiny coherent lore graph.' Do not begin another change."
        )
        assert not created["warnings"], created
        new_project = ProjectStore().load(new_path)
        assert new_project.graph.project_id == "tiny-lore"
        assert len(new_project.graph.nodes) == 1
        assert len(new_project.graph.edges) == 0

        existing_path = Path(temporary) / "technical.vw.db"
        ProjectStore().initialize(existing_path, sample_graph())
        original = ProjectStore().load(existing_path).graph
        review_calls = []
        review_config = ReviewConfig(True, "openai", "offline-allow", 1, False, "offline-key", None, None, None)

        def allow_reviewer(request, _configuration):
            review_calls.append(request)
            return {"status": "complete", "decision": "allow", "summary": "Offline acceptance gate allowed the exact proposal.", "concerns": []}

        application = Application(review_config_loader=lambda: review_config, semantic_reviewer=allow_reviewer)
        existing_conversation = AuthoringConversation(
            AuthoringToolHost(application, str(existing_path)), max_tool_calls=config["maxToolCallsPerTurn"]
        )
        updated_result = existing_conversation.turn(
            "Search first, then add exactly one note node with stable ID power-maintenance-note, "
            "text 'Inspect the power enclosure before maintenance.', kind note, no tags or attributes. "
            "Add exactly one scope-parent edge with stable ID power-maintenance-note-parent from that "
            "node to scope-power, review direction none, and no rationale, tags, or attributes. Make no "
            "other changes. Preview and write the change."
        )
        assert not updated_result["warnings"], updated_result
        updated = ProjectStore().load(existing_path).graph
        assert next(node for node in updated.nodes if node.id == "power-maintenance-note").text == "Inspect the power enclosure before maintenance."
        edge = next(edge for edge in updated.edges if edge.id == "power-maintenance-note-parent")
        assert (edge.source, edge.target, edge.relationship) == ("power-maintenance-note", "scope-power", "scope-parent")
        assert len(updated.nodes) == len(original.nodes) + 1
        assert len(updated.edges) == len(original.edges) + 1
        assert set(original.nodes).issubset(updated.nodes)
        assert set(original.edges).issubset(updated.edges)
        assert len(review_calls) == 1
