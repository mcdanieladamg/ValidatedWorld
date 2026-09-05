using System.ComponentModel;
using ModelContextProtocol.Server;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;

namespace ValidatedWorld.Mcp;

[McpServerToolType]
internal sealed class McpTools(McpProjectService projects)
{
    [McpServerTool(UseStructuredContent = true), Description("Selects an existing local ValidatedWorld .vw.db project for this MCP session. Paths are interpreted by the host process and are never taken from graph text.")]
    public McpProjectSelectionResult SelectProject(
        [Description("Absolute or relative path to an existing .vw.db file.")] string path) => projects.Select(path);

    [McpServerTool(UseStructuredContent = true), Description("Initializes a new local ValidatedWorld project containing only its purpose node, then selects it for this MCP session. The destination is never overwritten.")]
    public McpProjectInitializationResult InitializeProject(
        [Description("Absolute or relative destination path ending in .vw.db.")] string path,
        [Description("Stable project identifier.")] string projectId,
        [Description("Human-readable project title.")] string title,
        [Description("Stable identifier for the purpose node.")] string purposeNodeId,
        [Description("Human-readable governing purpose text.")] string purposeText) =>
        projects.Initialize(path, projectId, title, purposeNodeId, purposeText);

    [McpServerTool(UseStructuredContent = true), Description("Returns the status and identity of the currently selected project, including its normalized path and state fingerprint.")]
    public McpProjectSelection ProjectStatus() => projects.Status();

    [McpServerTool(UseStructuredContent = true), Description("Reads one node from the selected project. The result is bounded and marked incomplete if the byte bound is exceeded.")]
    public object ReadNode([Description("Stable node identifier.")] string nodeId) =>
        McpProjectService.Read(McpProjectService.Node(projects.Queries().GetNode(new EntityId(nodeId))));

    [McpServerTool(UseStructuredContent = true), Description("Reads one edge from the selected project. The result is bounded and marked incomplete if the byte bound is exceeded.")]
    public object ReadEdge([Description("Stable edge identifier.")] string edgeId) =>
        McpProjectService.Read(McpProjectService.Edge(projects.Queries().GetEdge(new EntityId(edgeId))));

    [McpServerTool(UseStructuredContent = true), Description("Lists a bounded page of nodes from the selected project. Use nextCursor for the exact snapshot continuation.")]
    public object ListNodes(
        [Description("Maximum number of nodes to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().ListNodes(new QueryPageRequest(limit, cursor)), McpProjectService.Node);

    [McpServerTool(UseStructuredContent = true), Description("Lists a bounded page of edges from the selected project. Use nextCursor for the exact snapshot continuation.")]
    public object ListEdges(
        [Description("Maximum number of edges to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().ListEdges(new QueryPageRequest(limit, cursor)), McpProjectService.Edge);

    [McpServerTool(UseStructuredContent = true), Description("Performs bounded case-insensitive discovery across node and edge identifiers, text, metadata, tags, relationships, and rationales.")]
    public object Search(
        [Description("Non-empty search text.")] string text,
        [Description("Maximum number of hits to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().Search(text, new QueryPageRequest(limit, cursor)),
            hit => hit.Node is not null
                ? new McpSearchHit("node", hit.EntityId.Value, McpProjectService.Node(hit.Node), null)
                : new McpSearchHit("edge", hit.EntityId.Value, null, McpProjectService.Edge(hit.Edge!)));

    [McpServerTool(UseStructuredContent = true), Description("Performs bounded deterministic ranked lexical discovery with match explanations.")]
    public object RankedSearch(
        [Description("Non-empty ranked search text.")] string text,
        [Description("Maximum number of hits to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().SearchRanked(text, new QueryPageRequest(limit, cursor)),
            hit => new
            {
                entityKind = hit.EntityKind.ToString(),
                entityId = hit.EntityId.Value,
                node = hit.Node is null ? null : McpProjectService.Node(hit.Node),
                edge = hit.Edge is null ? null : McpProjectService.Edge(hit.Edge),
                hit.Score,
                matches = hit.Matches,
            });

    [McpServerTool(UseStructuredContent = true), Description("Finds entities carrying an exact case-sensitive tag.")]
    public object ReadTag(
        [Description("Exact case-sensitive tag.")] string tag,
        [Description("Maximum number of hits to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().SearchByTag(tag, new QueryPageRequest(limit, cursor)),
            hit => hit.Node is not null
                ? new McpSearchHit("node", hit.EntityId.Value, McpProjectService.Node(hit.Node), null)
                : new McpSearchHit("edge", hit.EntityId.Value, null, McpProjectService.Edge(hit.Edge!)));

    [McpServerTool(UseStructuredContent = true), Description("Reads one node's scope ancestor lineage and a bounded descendant page.")]
    public object ReadScope(
        [Description("Stable node identifier.")] string nodeId,
        [Description("Maximum descendant items to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact descendant continuation cursor.")] string? cursor = null,
        [Description("Positive traversal depth bound.")] int maxDepth = 10_000,
        [Description("Positive visited-node bound.")] int maxVisitedNodes = 100_000)
    {
        var result = projects.Queries().GetScope(
            new EntityId(nodeId),
            new QueryPageRequest(limit, cursor),
            new QueryTraversalOptions { MaxDepth = maxDepth, MaxVisitedNodes = maxVisitedNodes });
        return McpProjectService.Bound(new McpScopeResult(
            McpProjectService.Node(result.Node),
            result.Upstream.Select(McpProjectService.Node).ToArray(),
            McpProjectService.ProjectPage(result.Descendants, McpProjectService.Node),
            McpProjectService.Omissions(result.Omissions)));
    }

    [McpServerTool(UseStructuredContent = true), Description("Reads stored graph neighbors for one selected-project node.")]
    public object ReadNeighbors(
        [Description("Stable node identifier.")] string nodeId,
        [Description("Maximum number of entries to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().GetNeighbors(new EntityId(nodeId), new QueryPageRequest(limit, cursor)),
            entry => new { nodeId = entry.NodeId.Value, edge = McpProjectService.Edge(entry.Edge), entry.IsOutgoing });

    [McpServerTool(UseStructuredContent = true), Description("Reads expanded review dependencies for one selected-project node.")]
    public object ReadDependencies(
        [Description("Stable node identifier.")] string nodeId,
        [Description("Maximum number of entries to return, from 1 to 1000.")] int limit = 100,
        [Description("Exact continuation cursor returned by the preceding call.")] string? cursor = null) =>
        McpProjectService.ProjectPage(
            projects.Queries().GetDependencies(new EntityId(nodeId), new QueryPageRequest(limit, cursor)),
            entry => new { edgeId = entry.Arc.EdgeId.Value, from = entry.Arc.From.Value, to = entry.Arc.To.Value, entry.IsOutgoing });

    [McpServerTool(UseStructuredContent = true), Description("Finds a bounded directed review-dependency path between two selected-project nodes.")]
    public object ReadPath(
        [Description("Stable source node identifier.")] string sourceNodeId,
        [Description("Stable target node identifier.")] string targetNodeId,
        [Description("Positive traversal depth bound.")] int maxDepth = 10_000,
        [Description("Positive visited-node bound.")] int maxVisitedNodes = 100_000)
    {
        var result = projects.Queries().FindDependencyPath(
            new EntityId(sourceNodeId), new EntityId(targetNodeId),
            new QueryTraversalOptions { MaxDepth = maxDepth, MaxVisitedNodes = maxVisitedNodes });
        return McpProjectService.Bound(new McpPathResult(result.Found, result.Nodes.Select(id => id.Value).ToArray(),
            result.Edges.Select(id => id.Value).ToArray(), McpProjectService.Omissions(result.Omissions)));
    }

    [McpServerTool(UseStructuredContent = true), Description("Returns the combined scope-upstream context for selected-project node identifiers without sibling fan-out.")]
    public object ReadContext(
        [Description("Stable node identifiers whose scope lineage should be included.")] IReadOnlyList<string> nodeIds,
        [Description("Positive traversal depth bound.")] int maxDepth = 10_000,
        [Description("Positive visited-node bound.")] int maxVisitedNodes = 100_000)
    {
        var result = projects.Queries().GetContext(
            nodeIds.Select(id => new EntityId(id)),
            new QueryTraversalOptions { MaxDepth = maxDepth, MaxVisitedNodes = maxVisitedNodes });
        return McpProjectService.Bound(new McpContextResult(
            result.RequestedNodeIds.Select(id => id.Value).ToArray(),
            result.ContextNodes.Select(McpProjectService.Node).ToArray(),
            McpProjectService.Omissions(result.Omissions)));
    }

    [McpServerTool(UseStructuredContent = true), Description("Returns a bounded deterministic graph observability report for the selected project.")]
    public object ReadHealth([Description("Maximum items per report section, from 1 to 1000.")] int limit = 100)
    {
        var report = projects.Queries().GetGraphHealth(new GraphObservabilityOptions { MaxItems = limit });
        var result = new McpHealthResult(
            report.NodeCount,
            report.EdgeCount,
            report.SemanticReviewArcCount,
            report.ScopeCoverage,
            new McpReportSection<string>(
                report.UnreachableNodeIds.TotalCount,
                report.UnreachableNodeIds.Items.Select(id => id.Value).ToArray(),
                report.UnreachableNodeIds.OmittedCount),
            new McpReportSection<McpReviewFanOutHotspot>(
                report.ReviewFanOutHotspots.TotalCount,
                report.ReviewFanOutHotspots.Items.Select(item => new McpReviewFanOutHotspot(
                    item.NodeId.Value, item.OutgoingReviewArcCount, item.IncomingReviewArcCount)).ToArray(),
                report.ReviewFanOutHotspots.OmittedCount),
            new McpReportSection<McpIsolatedClaim>(
                report.SuspiciouslyIsolatedClaims.TotalCount,
                report.SuspiciouslyIsolatedClaims.Items.Select(item => new McpIsolatedClaim(
                    item.NodeId.Value, item.Kind)).ToArray(),
                report.SuspiciouslyIsolatedClaims.OmittedCount),
            new McpReportSection<McpMissingRationale>(
                report.MissingRationales.TotalCount,
                report.MissingRationales.Items.Select(item => new McpMissingRationale(
                    item.EdgeId.Value, item.Source.Value, item.Target.Value, item.Relationship)).ToArray(),
                report.MissingRationales.OmittedCount),
            new McpReportSection<McpTagUsage>(
                report.TagUsage.TotalCount,
                report.TagUsage.Items.Select(item => new McpTagUsage(
                    item.Tag, item.NodeCount, item.EdgeCount, item.TotalCount)).ToArray(),
                report.TagUsage.OmittedCount),
            report.UntaggedNodeCount,
            report.UntaggedEdgeCount,
            report.WasCancelled,
            McpProjectService.Omissions(report.Omissions));
        return McpProjectService.Bound(result);
    }

    [McpServerTool(UseStructuredContent = true), Description("Alias for read_health; returns the same bounded graph observability report.")]
    public object ReadReport(int limit = 100) => ReadHealth(limit);
}
