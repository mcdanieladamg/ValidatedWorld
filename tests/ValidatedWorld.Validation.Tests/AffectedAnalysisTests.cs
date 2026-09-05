using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Validation.Tests;

public sealed class AffectedAnalysisTests
{
    [Fact]
    public void Node_change_follows_review_arcs_and_adds_only_scope_upstream_context()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("battery-assumption", "Battery lasts for the revised duty cycle", "assumption"))]);

        var analysis = new AffectedAnalyzer().Analyze(graph, proposal);

        Assert.True(analysis.IsComplete);
        Assert.Equal(
            new[] { "battery-assumption", "runtime-test" },
            analysis.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.Equal(
            new[] { "purpose", "scope-power" },
            analysis.ScopeContext.Select(entry => entry.NodeId.Value));
        Assert.DoesNotContain(analysis.AffectedNodes, node => node.NodeId == new EntityId("retention-policy"));
        var runtime = analysis.AffectedNodes.Single(node => node.NodeId == new EntityId("runtime-test"));
        Assert.Equal(new[] { "battery-assumption", "runtime-test" }, runtime.Explanation.Nodes.Select(id => id.Value));
        Assert.Equal(new[] { "battery-requires-test" }, runtime.Explanation.Edges.Select(id => id.Value));
    }

    [Fact]
    public void Edge_changes_are_displayed_and_use_old_and_new_review_directions()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var replacement = new GraphEdge(
            new EntityId("retention-informs-design"),
            new EntityId("retention-policy"),
            new EntityId("design-anchor"),
            "informs",
            ReviewDirection.SourceToTarget);
        var proposal = new GraphProjector().Project(graph, [GraphOperation.ReplaceEdge(replacement)]);

        var analysis = new AffectedAnalyzer().Analyze(graph, proposal);

        Assert.Single(analysis.EdgeChanges);
        Assert.Equal(new EntityId("retention-informs-design"), analysis.EdgeChanges[0].EdgeId);
        Assert.Contains(new EntityId("design-anchor"), analysis.SeedNodeIds);
        Assert.Contains(new EntityId("retention-policy"), analysis.SeedNodeIds);
        Assert.Contains(analysis.AffectedNodes, node => node.NodeId == new EntityId("design-anchor"));
        Assert.Contains(analysis.AffectedNodes, node => node.NodeId == new EntityId("retention-policy"));
        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("scope-power"));
        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("scope-privacy"));
    }

    [Fact]
    public void Review_direction_union_honors_source_target_both_and_none()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [
                GraphOperation.AddEdge(new GraphEdge(
                    new EntityId("battery-informs-retention"),
                    new EntityId("battery-assumption"),
                    new EntityId("retention-policy"),
                    "informs",
                    ReviewDirection.Both)),
                GraphOperation.AddEdge(new GraphEdge(
                    new EntityId("battery-ignores-retention"),
                    new EntityId("battery-assumption"),
                    new EntityId("retention-policy"),
                    "ignores",
                    ReviewDirection.None)),
            ]);

        var analysis = new AffectedAnalyzer().Analyze(
            graph,
            new GraphProjector().Project(
                graph,
                [GraphOperation.ReplaceNode(Node("battery-assumption", "Revised battery assumption", "assumption"))]));
        var edgeAnalysis = new AffectedAnalyzer().Analyze(graph, proposal);

        Assert.Contains(analysis.AffectedNodes, node => node.NodeId == new EntityId("runtime-test"));
        Assert.DoesNotContain(analysis.AffectedNodes, node => node.NodeId == new EntityId("retention-policy"));
        Assert.Contains(edgeAnalysis.EdgeChanges, change => change.EdgeId == new EntityId("battery-informs-retention"));
        Assert.Contains(edgeAnalysis.SeedNodeIds, id => id == new EntityId("battery-assumption"));
        Assert.Contains(edgeAnalysis.SeedNodeIds, id => id == new EntityId("retention-policy"));
        Assert.Contains(edgeAnalysis.AffectedNodes, node => node.NodeId == new EntityId("retention-policy"));
        Assert.DoesNotContain(edgeAnalysis.AffectedNodes, node => node.NodeId == new EntityId("scope-privacy"));
    }

    [Fact]
    public void Direct_scope_and_purpose_changes_expand_descendants_without_sibling_fan_out()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var scopeProposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("scope-power", "Revised power behavior", "scope"))]);
        var scopeAnalysis = new AffectedAnalyzer().Analyze(graph, scopeProposal);

        Assert.Equal(
            new[] { "battery-assumption", "design-anchor", "retention-policy", "runtime-test", "scope-power" },
            scopeAnalysis.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.DoesNotContain(scopeAnalysis.AffectedNodes, node => node.NodeId == new EntityId("scope-privacy"));
        Assert.Contains(scopeAnalysis.ScopeContext, entry => entry.NodeId == new EntityId("purpose"));

        var purposeProposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("purpose", "A revised project purpose"))]);
        var purposeAnalysis = new AffectedAnalyzer().Analyze(graph, purposeProposal);

        Assert.Equal(graph.Nodes.Count, purposeAnalysis.AffectedNodes.Count);
        Assert.Empty(purposeAnalysis.ScopeContext);
    }

    [Fact]
    public void Scope_parent_redirect_selects_child_subtree_and_both_parents_without_sibling_fan_out()
    {
        var baseline = ValidationGraphBuilder.CreateTechnicalProject();
        var detail = Node("battery-detail", "Battery chemistry detail", "fact");
        var graph = new ProjectGraph(
            baseline.ProjectId,
            baseline.Title,
            baseline.PurposeNodeId,
            baseline.Nodes.Concat([detail]),
            baseline.Edges.Concat([
                new GraphEdge(
                    new EntityId("battery-detail-parent"),
                    detail.Id,
                    new EntityId("battery-assumption"),
                    "scope-parent",
                    ReviewDirection.None),
            ]));
        var replacement = new GraphEdge(
            new EntityId("battery-scope-parent"),
            new EntityId("battery-assumption"),
            new EntityId("scope-privacy"),
            "scope-parent",
            ReviewDirection.None);

        var analysis = new AffectedAnalyzer().Analyze(
            graph,
            new GraphProjector().Project(graph, [GraphOperation.ReplaceEdge(replacement)]));

        Assert.True(analysis.IsComplete);
        Assert.Equal(
            new[] { "battery-assumption", "battery-detail", "runtime-test", "scope-power", "scope-privacy" },
            analysis.AffectedNodes.Select(node => node.NodeId.Value));
        Assert.All(analysis.AffectedNodes, node => Assert.False(node.IsDirectChange));
        Assert.Equal(new[] { "purpose" }, analysis.ScopeContext.Select(entry => entry.NodeId.Value));
        Assert.DoesNotContain(analysis.AffectedNodes, node => node.NodeId == new EntityId("retention-policy"));
        Assert.DoesNotContain(analysis.AffectedNodes, node => node.NodeId == new EntityId("design-anchor"));

        var oldParent = analysis.AffectedNodes.Single(node => node.NodeId == new EntityId("scope-power"));
        Assert.Equal(
            new[] { "battery-assumption", "scope-power" },
            oldParent.Explanation.Nodes.Select(id => id.Value));
        Assert.Equal(new[] { "battery-scope-parent" }, oldParent.Explanation.Edges.Select(id => id.Value));

        var newParent = analysis.AffectedNodes.Single(node => node.NodeId == new EntityId("scope-privacy"));
        Assert.Equal(
            new[] { "battery-assumption", "scope-privacy" },
            newParent.Explanation.Nodes.Select(id => id.Value));
        Assert.Equal(new[] { "battery-scope-parent" }, newParent.Explanation.Edges.Select(id => id.Value));
    }

    [Fact]
    public void Multiple_change_chains_keep_each_lineage_and_cycles_remain_bounded()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [
                GraphOperation.ReplaceNode(Node("battery-assumption", "Revised battery assumption", "assumption")),
                GraphOperation.ReplaceNode(Node("retention-policy", "Revised retention policy", "requirement")),
            ]);

        var analysis = new AffectedAnalyzer().Analyze(graph, proposal);

        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("scope-power"));
        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("scope-privacy"));
        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("purpose"));
        Assert.Contains(analysis.ScopeContext, entry => entry.NodeId == new EntityId("scope-privacy") &&
            entry.Lineages.Any(lineage => lineage.AffectedNodeId == new EntityId("retention-policy")));

        var cycle = new ProjectGraph(
            graph.ProjectId,
            graph.Title,
            graph.PurposeNodeId,
            graph.Nodes,
            graph.Edges.Concat([
                new GraphEdge(
                    new EntityId("cycle-link"),
                    new EntityId("runtime-test"),
                    new EntityId("battery-assumption"),
                    "cycles",
                    ReviewDirection.SourceToTarget),
            ]));
        var cycleProposal = new GraphProjector().Project(
            cycle,
            [GraphOperation.ReplaceNode(Node("battery-assumption", "Another battery assumption", "assumption"))]);
        var cycleAnalysis = new AffectedAnalyzer().Analyze(cycle, cycleProposal);

        Assert.True(cycleAnalysis.AffectedNodes.Count < 10);
        Assert.Equal(cycleAnalysis.AffectedNodes.Count, cycleAnalysis.AffectedNodes.Select(node => node.NodeId).Distinct().Count());
    }

    [Fact]
    public void Manual_review_requires_dispositions_and_context_coverage()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("battery-assumption", "Revised battery assumption", "assumption"))]);
        var session = new AffectedAnalyzer().Analyze(graph, proposal).CreateReviewSession();

        var pending = session.EvaluateReadiness();
        Assert.False(pending.IsReady);
        Assert.Contains(new EntityId("battery-assumption"), pending.PendingNodeIds);
        Assert.Contains(new EntityId("purpose"), pending.MissingContextNodeIds);

        session.SetDisposition(new EntityId("battery-assumption"), ReviewDispositionKind.Updated);
        session.SetDisposition(new EntityId("runtime-test"), ReviewDispositionKind.ReviewedNoChange);
        session.MarkContextPresented(new EntityId("purpose"));
        session.MarkContextPresented(new EntityId("scope-power"));

        var ready = session.EvaluateReadiness();
        Assert.True(ready.IsReady);
        Assert.Empty(ready.Blockers);
        Assert.Throws<ArgumentException>(() => session.SetDisposition(
            new EntityId("runtime-test"),
            ReviewDispositionKind.Updated));
        Assert.Throws<ArgumentException>(() => session.SetDisposition(
            new EntityId("runtime-test"),
            ReviewDispositionKind.NotApplicable));
    }

    [Fact]
    public void Refresh_invalidates_changed_node_content_and_context_evidence_only()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var firstProposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("battery-assumption", "First revised battery", "assumption"))]);
        var firstAnalysis = new AffectedAnalyzer().Analyze(graph, firstProposal);
        var session = firstAnalysis.CreateReviewSession();
        session.SetDisposition(new EntityId("battery-assumption"), ReviewDispositionKind.Updated);
        session.SetDisposition(new EntityId("runtime-test"), ReviewDispositionKind.ReviewedNoChange);
        session.MarkContextPresented(new EntityId("purpose"));
        session.MarkContextPresented(new EntityId("scope-power"));

        var secondProposal = new GraphProjector().Project(
            graph,
            [
                GraphOperation.ReplaceNode(Node("battery-assumption", "Second revised battery", "assumption")),
                GraphOperation.ReplaceNode(Node("scope-power", "Changed power context", "scope")),
            ]);
        var secondAnalysis = new AffectedAnalyzer().Analyze(graph, secondProposal);
        var refreshed = session.Refresh(secondAnalysis);

        Assert.Contains(new EntityId("battery-assumption"), refreshed.InvalidatedDispositionNodeIds);
        Assert.DoesNotContain(new EntityId("runtime-test"), refreshed.InvalidatedDispositionNodeIds);
        Assert.Contains(new EntityId("scope-power"), refreshed.InvalidatedContextNodeIds);
        Assert.Contains(new EntityId("purpose"), refreshed.InvalidatedContextNodeIds);
        Assert.Contains(new EntityId("battery-assumption"), session.PendingNodeIds);
        Assert.Contains(new EntityId("scope-power"), session.PendingNodeIds);
    }

    [Fact]
    public void Bounds_return_inconclusive_with_explicit_omissions()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var purposeProposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("purpose", "Revised purpose"))]);

        var depthLimited = new AffectedAnalyzer().Analyze(
            graph,
            purposeProposal,
            new AffectedAnalysisOptions { MaxTraversalDepth = 1 });
        Assert.True(depthLimited.IsInconclusive);
        Assert.Contains(depthLimited.Omissions, omission =>
            omission.Reason == AffectedOmissionReason.TraversalDepthLimit && omission.EdgeId is not null);

        var outputLimited = new AffectedAnalyzer().Analyze(
            graph,
            purposeProposal,
            new AffectedAnalysisOptions { MaxOutputItems = 1 });
        Assert.True(outputLimited.IsInconclusive);
        Assert.Contains(outputLimited.Omissions, omission => omission.Reason == AffectedOmissionReason.OutputLimit);
    }

    [Fact]
    public void Omission_metadata_is_grouped_and_detail_pages_are_fingerprint_bound()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("purpose", "A revised purpose"))]);

        var analysis = new AffectedAnalyzer().Analyze(
            graph,
            proposal,
            new AffectedAnalysisOptions { MaxOutputItems = 1 });

        Assert.NotEmpty(analysis.Omissions);
        Assert.All(analysis.Omissions, group =>
        {
            Assert.True(group.Count >= group.Sample.Count);
            Assert.InRange(group.Sample.Count, 0, AffectedAnalysis.OmissionSampleSize);
            Assert.False(string.IsNullOrWhiteSpace(group.DetailsFingerprint));
        });
        Assert.True(analysis.Omissions.Sum(group => group.Count) > analysis.Omissions.Count);

        var group = analysis.Omissions[0];
        var page = analysis.ReadOmissionDetails(group.DetailsFingerprint, 1);
        Assert.Equal(group.DetailsFingerprint, page.Fingerprint);
        Assert.Equal(group.Count, page.TotalCount);
        Assert.Single(page.Items);
        Assert.Throws<ArgumentException>(() => analysis.ReadOmissionDetails("wrong", 1));
        Assert.Throws<ArgumentException>(() => analysis.ReadOmissionDetails(
            group.DetailsFingerprint,
            1,
            Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("wrong:1"))));
    }

    [Fact]
    public void Public_api_smoke_completes_a_realistic_manual_review()
    {
        var graph = ValidationGraphBuilder.CreateTechnicalProject();
        var proposal = new GraphProjector().Project(
            graph,
            [GraphOperation.ReplaceNode(Node("battery-assumption", "Battery lasts for the revised duty cycle", "assumption"))]);
        var analysis = new AffectedAnalyzer().Analyze(graph, proposal);
        var review = analysis.CreateReviewSession();

        foreach (var affected in analysis.AffectedNodes)
        {
            review.SetDisposition(
                affected.NodeId,
                affected.IsDirectChange
                    ? ReviewDispositionKind.Updated
                    : ReviewDispositionKind.ReviewedNoChange);
        }

        foreach (var context in analysis.ScopeContext) review.MarkContextPresented(context.NodeId);

        var readiness = review.EvaluateReadiness();
        Assert.True(readiness.IsReady);
        Assert.True(analysis.ProposedValidation.IsValid);
        Assert.Equal(analysis.Operations, proposal.Operations);
    }

    private static GraphNode Node(string id, string text, string? kind = null) =>
        new(new EntityId(id), text, kind);
}
