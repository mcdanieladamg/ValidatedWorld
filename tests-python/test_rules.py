import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parents[1] / "src-python"))

from validated_world.models import Attribute, Edge, Graph, GraphValue, Node
from validated_world.rules import evaluate_rules
from validated_world.storage import ProjectStore


def rule(identifier, message, expression, *, version=1):
    return Node(
        identifier,
        message,
        "validation-rule",
        ("rule:active",),
        (
            Attribute("rule:version", GraphValue.integer(version)),
            Attribute("rule:expression", GraphValue.text(expression)),
        ),
    )


def view(identifier, name, expression, *, version=1):
    return Node(
        identifier,
        name,
        "validation-view",
        (),
        (
            Attribute("view:version", GraphValue.integer(version)),
            Attribute("view:name", GraphValue.text(name)),
            Attribute("view:expression", GraphValue.text(expression)),
        ),
    )


def graph(*children):
    purpose = Node("purpose", "Purpose", "purpose")
    return Graph(
        "rules",
        "Rules",
        purpose.id,
        (purpose, *children),
        tuple(Edge(item.id + "-scope", item.id, purpose.id, "scope-parent") for item in children),
    )


class RuleTests(unittest.TestCase):
    def test_complete_graph_evaluation_bounds_offender_samples(self):
        candidate = graph(
            Node("one", "One", "claim", ("required",)),
            Node("two", "Two", "claim"),
            Node("three", "Three", "claim"),
            rule(
                "must-tag",
                "Every claim needs required.",
                '{"all":{"set":{"nodes":{"kind":"claim"}},"condition":{"hasTag":"required"}}}',
            ),
        )
        result = evaluate_rules(candidate, max_sample=1)
        self.assertEqual(result.status, "invalid")
        self.assertEqual(result.diagnostics[0].offender_ids, ("three",))
        self.assertEqual(result.diagnostics[0].omitted_count, 1)

    def test_views_compose_and_invalid_definitions_are_inconclusive(self):
        valid = graph(
            view("view-claims", "claims", '{"nodes":{"kind":"claim"}}'),
            rule("one-claim", "Exactly one claim.", '{"count":{"set":{"view":"claims"},"compare":"eq","value":1}}'),
            Node("claim", "Claim", "claim"),
        )
        self.assertTrue(evaluate_rules(valid).is_valid)

        cases = (
            graph(rule("unknown", "Bad", '{"sql":"select 1"}')),
            graph(rule("unknown-field", "Bad", '{"count":{"set":{"nodes":{}},"compare":"eq","value":1,"extra":true}}')),
            graph(rule("duplicate-json", "Bad", '{"count":{"set":{"nodes":{}},"set":{"nodes":{}},"compare":"eq","value":1}}')),
            graph(view("a", "a", '{"view":"b"}'), view("b", "b", '{"view":"a"}')),
            graph(view("a", "same", '{"nodes":{}}'), view("b", "same", '{"nodes":{}}')),
            graph(rule("version", "Bad", '{"exists":{"nodes":{}}}', version=2)),
        )
        for candidate in cases:
            with self.subTest(candidate=[item.id for item in candidate.nodes]):
                self.assertEqual(evaluate_rules(candidate).status, "inconclusive")

    def test_attribute_selectors_preserve_scalar_types(self):
        candidate = graph(
            Node("ready", "Ready", "claim", (), (Attribute("priority", GraphValue.integer(2)),)),
            Node("text", "Text", "claim", (), (Attribute("priority", GraphValue.text("2")),)),
            rule(
                "integer-priority",
                "Exactly one claim has integer priority two.",
                '{"count":{"set":{"nodes":{"kind":"claim","attributes":[{"name":"priority","kind":"integer","value":2}]}},"compare":"eq","value":1}}',
            ),
        )
        self.assertTrue(evaluate_rules(candidate).is_valid)

    def test_explicit_work_budget_is_inconclusive_without_a_hidden_default_cap(self):
        expression = '{"exists":{"nodes":{}}}'
        for _ in range(80):
            expression = '{"not":' + expression + "}"
        candidate = graph(*(rule(f"rule-{index}", "Some nodes exist", expression) for index in range(129)))
        self.assertTrue(evaluate_rules(candidate).is_valid)
        self.assertEqual(evaluate_rules(candidate, max_work=1).status, "inconclusive")

    def test_tracked_blueprint_rules_remain_valid(self):
        project = ProjectStore().load(Path(__file__).parents[1] / "ValidatedWorld.Blueprint.vw.db")
        self.assertTrue(evaluate_rules(project.graph).is_valid)


if __name__ == "__main__":
    unittest.main()
