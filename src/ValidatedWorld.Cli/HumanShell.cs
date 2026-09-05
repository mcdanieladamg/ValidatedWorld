using System.Globalization;
using System.Text;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Cli;

internal sealed class HumanShell(
    ProjectApplication application,
    string path,
    TextReader input,
    TextWriter output,
    TextWriter error,
    CancellationToken cancellationToken,
    bool showPrompt)
{
    private StoredProject _project = null!;
    private ChangeSessionSnapshot? _session;
    private EntityId? _selectedNode;
    private EntityId? _selectedEdge;

    public async Task<int> RunAsync()
    {
        _project = application.Load(path);
        _selectedNode = _project.Graph.PurposeNodeId;
        await output.WriteLineAsync(
            $"Opened {_project.Graph.ProjectId.Value} — {_project.Graph.Title} ({_project.Graph.Nodes.Count} nodes, " +
            $"{_project.Graph.Edges.Count} edges). Selected root {_selectedNode.Value.Value}. Type 'help' for commands.");

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (showPrompt)
            {
                await output.WriteAsync("vw> ");
                await output.FlushAsync(cancellationToken);
            }

            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await WriteExitWarnings();
                return CliRunner.SuccessExitCode;
            }

            try
            {
                var tokens = ShellCommandLine.Tokenize(line);
                if (tokens.Count == 0) continue;
                if (await Dispatch(tokens))
                {
                    await WriteExitWarnings();
                    return CliRunner.SuccessExitCode;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (IOException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var (code, message, _) = CliRunner.Error(exception);
                await error.WriteLineAsync($"error[{code}]: {message}");
            }
        }
    }

    private async Task<bool> Dispatch(IReadOnlyList<string> tokens)
    {
        var command = tokens[0].ToLowerInvariant();
        switch (command)
        {
            case "help":
                await Help(tokens.Skip(1).ToArray());
                return false;
            case "status":
                NoArguments(tokens, 1);
                await Status();
                return false;
            case "pwd":
                NoArguments(tokens, 1);
                await PrintWorkingNode();
                return false;
            case "dir" or "ls":
                await Directory(Flags(tokens, 1));
                return false;
            case "cd":
                await ChangeDirectory(tokens);
                return false;
            case "root":
                NoArguments(tokens, 1);
                await SelectRoot();
                return false;
            case "search":
                await Search(Flags(tokens, 1));
                return false;
            case "begin":
                await Begin(Flags(tokens, 1));
                return false;
            case "changes":
                NoArguments(tokens, 1);
                await Changes();
                return false;
            case "affected":
                NoArguments(tokens, 1);
                await Affected();
                return false;
            case "review":
                await Review(Flags(tokens, 1));
                return false;
            case "context":
                await Context(tokens);
                return false;
            case "health" or "report":
                await Health(Flags(tokens, 1));
                return false;
            case "validate":
                NoArguments(tokens, 1);
                await Validate();
                return false;
            case "commit":
                await Commit(Flags(tokens, 1));
                return false;
            case "discard":
                NoArguments(tokens, 1);
                await Discard();
                return false;
            case "node":
                await NodeCommand(tokens);
                return false;
            case "edge":
                await EdgeCommand(tokens);
                return false;
            case "ai":
                await Ai(tokens);
                return false;
            case "exit" or "quit":
                NoArguments(tokens, 1);
                return true;
            default:
                throw new ArgumentException($"Unknown shell command '{tokens[0]}'. Type 'help' for commands.");
        }
    }

    private async Task Help(IReadOnlyList<string> arguments)
    {
        if (arguments.Count > 1) throw new ArgumentException("Use 'help' or 'help <topic>'.");
        var topic = arguments.Count == 0 ? null : arguments[0].ToLowerInvariant();
        if (topic is null)
        {
            await output.WriteLineAsync("Navigation: pwd | dir|ls [--limit N] [--depth N] [--upstream N] [--scope-only]");
            await output.WriteLineAsync("            cd ID | cd .. | cd / | root | search --text TEXT [--limit N]");
            await output.WriteLineAsync("            node ... | edge ...");
            await output.WriteLineAsync("Change: begin --author NAME --intent TEXT | changes | affected | review ... | context mark ...");
            await output.WriteLineAsync("Finish: validate | commit [--bypass-ai-review] | discard | exit");
            await output.WriteLineAsync("Other: health [--limit N] | ai status | help navigation | help node | help edge | help review");
            return;
        }

        switch (topic)
        {
            case "navigation":
                await output.WriteLineAsync("pwd");
                await output.WriteLineAsync("dir|ls [--limit N] [--depth N] [--upstream N] [--scope-only]");
                await output.WriteLineAsync("cd ID | cd --id ID | cd .. | cd / | root");
                await output.WriteLineAsync("search --text TEXT [--limit N]");
                await output.WriteLineAsync("The purpose root is selected when the shell opens. Navigation uses the current proposal.");
                break;
            case "node":
                await output.WriteLineAsync("node list [--limit N]");
                await output.WriteLineAsync("node select --id ID | node show [--id ID]");
                await output.WriteLineAsync("node add --id ID --text TEXT --parent ID [--kind KIND] [--scope-edge-id ID]");
                await output.WriteLineAsync("node set [--id ID] (--text TEXT | --kind KIND | --clear-kind)");
                await output.WriteLineAsync("node move [--id ID] --parent ID");
                await output.WriteLineAsync("node tag-add|tag-remove [--id ID] --tag TAG");
                await output.WriteLineAsync("node attribute-set [--id ID] --name NAME --type TYPE --value VALUE");
                await output.WriteLineAsync("node attribute-remove [--id ID] --name NAME | node remove [--id ID]");
                break;
            case "edge":
                await output.WriteLineAsync("edge list [--limit N]");
                await output.WriteLineAsync("edge select --id ID | edge show [--id ID]");
                await output.WriteLineAsync("edge add --id ID --source ID --target ID --relationship LABEL --direction DIRECTION");
                await output.WriteLineAsync("edge set [--id ID] (--source ID | --target ID | --relationship LABEL | --direction DIRECTION | --rationale TEXT | --clear-rationale)");
                await output.WriteLineAsync("edge tag-add|tag-remove [--id ID] --tag TAG");
                await output.WriteLineAsync("edge attribute-set [--id ID] --name NAME --type TYPE --value VALUE");
                await output.WriteLineAsync("edge attribute-remove [--id ID] --name NAME | edge remove [--id ID]");
                break;
            case "review":
                await output.WriteLineAsync("affected");
                await output.WriteLineAsync("review [--id ID] --as updated|reviewed-no-change|not-applicable|pending [--rationale TEXT]");
                await output.WriteLineAsync("context mark --id ID");
                break;
            case "health" or "report":
                await output.WriteLineAsync("health|report [--limit N]");
                await output.WriteLineAsync("Shows bounded deterministic graph-quality diagnostics.");
                break;
            default:
                throw new ArgumentException($"Unknown help topic '{topic}'.");
        }
    }

    private async Task Status()
    {
        var graph = CurrentGraph;
        await output.WriteLineAsync(
            $"Project {_project.Graph.ProjectId.Value}: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges.");
        await output.WriteLineAsync(_session is null
            ? "No active change."
            : $"Active change: {_session.Operations.Operations.Count} operations; " +
              $"{_session.Readiness.PendingNodeIds.Count} pending reviews; ready={_session.Readiness.IsReady.ToString().ToLowerInvariant()}.");
        if (_selectedNode is { } node) await output.WriteLineAsync($"Selected node: {node.Value}");
        if (_selectedEdge is { } edge) await output.WriteLineAsync($"Selected edge: {edge.Value}");
    }

    private async Task Health(ShellFlags flags)
    {
        flags.Allow("limit");
        var limit = flags.PositiveInt("limit", 20, QueryPageRequest.MaximumLimit);
        var report = application.Queries(path).GetGraphObservability(new GraphObservabilityOptions
        {
            MaxItems = limit,
            CancellationToken = cancellationToken,
        });
        var scope = report.ScopeCoverage;
        await output.WriteLineAsync(
            $"Graph health: {report.NodeCount} nodes, {report.EdgeCount} edges, " +
            $"{report.SemanticReviewArcCount} review arcs.");
        await output.WriteLineAsync(
            $"Scope coverage: {scope.NodesReachingPurpose}/{scope.TotalNodeCount} nodes " +
            $"({scope.CoveragePercent.ToString("0.##", CultureInfo.InvariantCulture)}%); " +
            $"{scope.NodesWithExactlyOneScopeParent} have exactly one scope parent.");
        await output.WriteLineAsync(
            $"Unreachable nodes: {report.UnreachableNodeIds.TotalCount}; " +
            $"fan-out sources: {report.ReviewFanOutHotspots.TotalCount}; " +
            $"isolated claims: {report.SuspiciouslyIsolatedClaims.TotalCount}; " +
            $"missing rationales: {report.MissingRationales.TotalCount}.");
        await output.WriteLineAsync(
            $"Tags: {report.TagUsage.TotalCount} distinct; " +
            $"untagged nodes={report.UntaggedNodeCount}, edges={report.UntaggedEdgeCount}.");
        if (report.UnreachableNodeIds.Items.Count > 0)
            await output.WriteLineAsync($"  unreachable: {string.Join(", ", report.UnreachableNodeIds.Items)}");
        if (report.SuspiciouslyIsolatedClaims.Items.Count > 0)
            await output.WriteLineAsync($"  isolated: {string.Join(", ", report.SuspiciouslyIsolatedClaims.Items.Select(item => item.NodeId))}");
        if (report.MissingRationales.Items.Count > 0)
            await output.WriteLineAsync($"  missing rationale: {string.Join(", ", report.MissingRationales.Items.Select(item => item.EdgeId))}");
        if (report.ReviewFanOutHotspots.Items.Count > 0)
            await output.WriteLineAsync($"  fan-out: {string.Join(", ", report.ReviewFanOutHotspots.Items.Select(item => $"{item.NodeId}={item.OutgoingReviewArcCount}"))}");
        if (report.TagUsage.Items.Count > 0)
            await output.WriteLineAsync($"  tags: {string.Join(", ", report.TagUsage.Items.Select(item => $"{item.Tag}={item.TotalCount}"))}");
    }

    private async Task PrintWorkingNode()
    {
        var selected = FindNode(NodeId(null));
        await output.WriteLineAsync(ScopePath(selected.Id));
    }

    private async Task Directory(ShellFlags flags)
    {
        flags.Allow("limit", "depth", "upstream", "scope-only");
        var limit = flags.PositiveInt("limit", 20, QueryPageRequest.MaximumLimit);
        var depth = flags.NonNegativeInt("depth", 1, QueryPageRequest.MaximumLimit);
        var upstream = flags.NonNegativeInt("upstream", 1, QueryPageRequest.MaximumLimit);
        var scopeOnly = flags.Boolean("scope-only");
        var graph = CurrentGraph;
        var index = new GraphIndex(graph);
        var selected = FindNode(NodeId(null));
        var entries = new List<DirectoryEntry>();

        var ancestor = selected;
        var seenAncestors = new HashSet<EntityId> { selected.Id };
        for (var level = 1; level <= upstream; level++)
        {
            var parentEdges = index.GetScopeParentEdges(ancestor.Id);
            if (parentEdges.Count == 0) break;
            if (parentEdges.Count != 1)
                throw new InvalidOperationException($"Node '{ancestor.Id.Value}' has an ambiguous scope parent.");
            var parentEdge = parentEdges[0];
            if (!seenAncestors.Add(parentEdge.Target))
                throw new InvalidOperationException("The current scope tree contains a cycle.");
            ancestor = FindNode(parentEdge.Target);
            entries.Add(new DirectoryEntry($"[..{level}]", ancestor, null, null));
        }

        var visitedDescendants = new HashSet<EntityId> { selected.Id };
        var frontier = new Queue<(GraphNode Node, int Depth)>();
        frontier.Enqueue((selected, 0));
        while (frontier.TryDequeue(out var current))
        {
            if (current.Depth >= depth) continue;
            foreach (var childId in index.GetScopeChildren(current.Node.Id))
            {
                var child = FindNode(childId);
                if (!visitedDescendants.Add(child.Id))
                    throw new InvalidOperationException("The current scope tree contains a cycle or duplicate child.");
                entries.Add(new DirectoryEntry($"[scope +{current.Depth + 1}]", child, null, null));
                frontier.Enqueue((child, current.Depth + 1));
            }
        }

        if (!scopeOnly)
        {
            foreach (var edge in index.GetEdgesFrom(selected.Id)
                         .Concat(index.GetEdgesTo(selected.Id))
                         .Where(edge => !IsScopeParent(edge))
                         .DistinctBy(edge => edge.Id)
                         .OrderBy(edge => edge.Id.Value, StringComparer.Ordinal))
            {
                var outgoing = edge.Source == selected.Id;
                var neighbor = FindNode(outgoing ? edge.Target : edge.Source);
                entries.Add(new DirectoryEntry(
                    outgoing ? "[out]" : "[in]",
                    neighbor,
                    edge,
                    outgoing ? "to" : "from"));
            }
        }

        await output.WriteLineAsync($"[.] {selected.Id.Value} — {selected.Text}");
        foreach (var entry in entries.Take(limit))
        {
            if (entry.Edge is null)
            {
                await output.WriteLineAsync($"{entry.Label} {entry.Node.Id.Value} — {entry.Node.Text}");
                continue;
            }

            await output.WriteLineAsync(
                $"{entry.Label} {entry.Node.Id.Value} {entry.Preposition} {entry.Edge.Id.Value} " +
                $"[{entry.Edge.Relationship}/{DirectionName(entry.Edge.ReviewDirection)}] — {entry.Node.Text}");
        }
        if (entries.Count > limit)
            await output.WriteLineAsync($"... {entries.Count - limit} more connections omitted; raise --limit.");
    }

    private async Task ChangeDirectory(IReadOnlyList<string> tokens)
    {
        EntityId target;
        if (tokens.Count == 2 && tokens[1] == "/")
        {
            target = CurrentGraph.PurposeNodeId;
        }
        else if (tokens.Count == 2 && tokens[1] == "..")
        {
            var selected = FindNode(NodeId(null));
            var parent = ScopeParent(CurrentGraph, selected.Id);
            target = parent?.Target ?? selected.Id;
        }
        else if (tokens.Count == 2 && !tokens[1].StartsWith("--", StringComparison.Ordinal))
        {
            target = new EntityId(tokens[1]);
        }
        else
        {
            var flags = Flags(tokens, 1);
            flags.Allow("id");
            target = new EntityId(flags.Required("id"));
        }

        var node = FindNode(target);
        _selectedNode = node.Id;
        await output.WriteLineAsync(ScopePath(node.Id));
    }

    private async Task SelectRoot()
    {
        var root = FindNode(CurrentGraph.PurposeNodeId);
        _selectedNode = root.Id;
        await output.WriteLineAsync(ScopePath(root.Id));
    }

    private async Task Search(ShellFlags flags)
    {
        flags.Allow("text", "limit");
        var text = flags.Required("text");
        var limit = flags.PositiveInt("limit", 20, QueryPageRequest.MaximumLimit);
        var hits = CurrentGraph.Nodes
            .Where(node => Contains(node.Id.Value, text) || Contains(node.Text, text) || Contains(node.Kind, text) ||
                           node.Tags.Any(tag => Contains(tag, text)))
            .Select(node => $"node {node.Id.Value} — {node.Text}")
            .Concat(CurrentGraph.Edges
                .Where(edge => Contains(edge.Id.Value, text) || Contains(edge.Relationship, text) ||
                               Contains(edge.Rationale, text) || edge.Tags.Any(tag => Contains(tag, text)))
                .Select(edge => $"edge {edge.Id.Value} — {edge.Source.Value} -[{edge.Relationship}]-> {edge.Target.Value}"))
            .Order(StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
        foreach (var hit in hits) await output.WriteLineAsync(hit);
        if (hits.Length == 0) await output.WriteLineAsync("No matches.");
    }

    private async Task Begin(ShellFlags flags)
    {
        flags.Allow("author", "intent");
        if (_session is not null) throw new ArgumentException("A change is already active; commit or discard it first.");
        _session = application.BeginChange(
            _project.Path,
            _project.Graph.ProjectId,
            flags.Required("author"),
            flags.Required("intent"));
        await output.WriteLineAsync($"Change begun. Session {_session.Reference.SessionId}.");
    }

    private async Task Changes()
    {
        var session = RequireSession();
        if (session.Operations.Operations.Count == 0)
        {
            await output.WriteLineAsync("No pending operations.");
            return;
        }
        foreach (var operation in session.Operations.Operations)
            await output.WriteLineAsync(
                $"{operation.Kind.ToString().ToLowerInvariant()} {operation.EntityKind.ToString().ToLowerInvariant()} {operation.EntityId.Value}");
    }

    private async Task Affected()
    {
        var session = RequireSession();
        foreach (var node in session.Affected.AffectedNodes)
        {
            var disposition = session.Dispositions.First(item => item.NodeId == node.NodeId);
            await output.WriteLineAsync(
                $"{node.NodeId.Value}: {(node.IsDirectChange ? "direct" : "affected")}, distance={node.Distance}, " +
                $"review={ReviewName(disposition.Kind)}");
        }
        await output.WriteLineAsync("Context required: " +
            (session.Affected.ScopeContext.Count == 0
                ? "none"
                : string.Join(", ", session.Affected.ScopeContext.Select(item => item.NodeId.Value))));
        if (session.Affected.Omissions.Count > 0)
            await output.WriteLineAsync($"Warning: {session.Affected.Omissions.Count} affected-analysis omissions.");
    }

    private async Task Review(ShellFlags flags)
    {
        flags.Allow("id", "as", "rationale");
        var session = RequireSession();
        var id = NodeId(flags.Optional("id"));
        var kind = ParseDisposition(flags.Required("as"));
        _session = application.ReviewChange(
            session.Reference,
            new ChangeReviewUpdate(
                [new ReviewDisposition(id, kind, flags.Optional("rationale"))],
                []));
        await output.WriteLineAsync($"Review for {id.Value}: {ReviewName(kind)}.");
    }

    private async Task Context(IReadOnlyList<string> tokens)
    {
        RequireSubcommand(tokens, "context", out var subcommand, out var flags);
        if (subcommand != "mark") throw new ArgumentException("Use 'context mark --id ID'.");
        flags.Allow("id");
        var session = RequireSession();
        var id = new EntityId(flags.Required("id"));
        _session = application.ReviewChange(
            session.Reference,
            new ChangeReviewUpdate([], [id]));
        await output.WriteLineAsync($"Context marked presented: {id.Value}.");
    }

    private async Task Validate()
    {
        _session = application.ValidateChange(RequireSession().Reference);
        await WriteReadiness(_session);
    }

    private async Task Commit(ShellFlags flags)
    {
        flags.Allow("bypass-ai-review");
        var bypass = flags.Boolean("bypass-ai-review");
        var result = await application.WriteChangeAsync(
            RequireSession().Reference,
            new ChangeWriteOptions(bypass),
            cancellationToken);
        await output.WriteLineAsync($"Commit {result.Status.ToString().ToLowerInvariant()}: {result.Message}");
        if (result.SemanticReview is { } semantic)
        {
            await output.WriteLineAsync(
                $"AI review: {semantic.Status.ToString().ToLowerInvariant()}" +
                (semantic.Decision is null ? string.Empty : $"/{semantic.Decision.ToString()!.ToLowerInvariant()}") +
                $" — {semantic.Summary}");
            foreach (var concern in semantic.Concerns)
                await output.WriteLineAsync(
                    $"  [{concern.Code}] {concern.Message} ({string.Join(", ", concern.Citations.Select(id => id.Value))})");
        }
        if (result.Status == ChangeWriteStatus.Written)
        {
            _project = result.Project!;
            _session = null;
            RepairSelections();
        }
    }

    private async Task Discard()
    {
        var result = application.DiscardChange(RequireSession().Reference);
        _session = null;
        RepairSelections();
        await output.WriteLineAsync($"Discarded session {result.SessionId}.");
    }

    private async Task NodeCommand(IReadOnlyList<string> tokens)
    {
        RequireSubcommand(tokens, "node", out var subcommand, out var flags);
        switch (subcommand)
        {
            case "list": await NodeList(flags); break;
            case "select": await NodeSelect(flags); break;
            case "show": await NodeShow(flags); break;
            case "add": await NodeAdd(flags); break;
            case "set": await NodeSet(flags); break;
            case "move": await NodeMove(flags); break;
            case "remove": await NodeRemove(flags); break;
            case "tag-add": await NodeTag(flags, add: true); break;
            case "tag-remove": await NodeTag(flags, add: false); break;
            case "attribute-set": await NodeAttributeSet(flags); break;
            case "attribute-remove": await NodeAttributeRemove(flags); break;
            default: throw new ArgumentException($"Unknown node command '{subcommand}'. Type 'help node'.");
        }
    }

    private async Task NodeList(ShellFlags flags)
    {
        flags.Allow("limit");
        var limit = flags.PositiveInt("limit", 20, QueryPageRequest.MaximumLimit);
        foreach (var node in CurrentGraph.Nodes.Take(limit))
            await output.WriteLineAsync($"{node.Id.Value} — {node.Text}");
    }

    private async Task NodeSelect(ShellFlags flags)
    {
        flags.Allow("id");
        var node = FindNode(new EntityId(flags.Required("id")));
        _selectedNode = node.Id;
        await WriteNode(node);
    }

    private async Task NodeShow(ShellFlags flags)
    {
        flags.Allow("id");
        var node = FindNode(NodeId(flags.Optional("id")));
        await WriteNode(node);
    }

    private async Task NodeAdd(ShellFlags flags)
    {
        flags.Allow("id", "text", "kind", "parent", "scope-edge-id");
        RequireSession();
        var id = new EntityId(flags.Required("id"));
        EnsureEntityMissing(id);
        var parent = FindNode(new EntityId(flags.Required("parent")));
        var edgeId = new EntityId(flags.Optional("scope-edge-id") ?? $"{id.Value}-scope-parent");
        EnsureEntityMissing(edgeId);
        var node = new GraphNode(id, flags.Required("text"), flags.Optional("kind"));
        var scope = new GraphEdge(
            edgeId, id, parent.Id, "scope-parent", ReviewDirection.None);
        await Patch([GraphOperation.AddNode(node), GraphOperation.AddEdge(scope)]);
        _selectedNode = id;
    }

    private async Task NodeSet(ShellFlags flags)
    {
        flags.Allow("id", "text", "kind", "clear-kind");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        flags.ExactlyOne("text", "kind", "clear-kind");
        var replacement = new GraphNode(
            node.Id,
            flags.Optional("text") ?? node.Text,
            flags.Has("clear-kind") ? null : flags.Optional("kind") ?? node.Kind,
            node.Tags,
            Attributes(node.Attributes));
        await Patch([GraphOperation.ReplaceNode(replacement)]);
        _selectedNode = node.Id;
    }

    private async Task NodeMove(ShellFlags flags)
    {
        flags.Allow("id", "parent");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        var parent = FindNode(new EntityId(flags.Required("parent")));
        var parents = CurrentGraph.Edges.Where(edge =>
            edge.Source == node.Id && StringComparer.Ordinal.Equals(edge.Relationship, "scope-parent")).ToArray();
        if (parents.Length != 1)
            throw new ArgumentException($"Node '{node.Id.Value}' does not have exactly one current scope-parent edge.");
        var edge = parents[0];
        var replacement = new GraphEdge(
            edge.Id, edge.Source, parent.Id, edge.Relationship, edge.ReviewDirection,
            edge.Rationale, edge.Tags, Attributes(edge.Attributes));
        await Patch([GraphOperation.ReplaceEdge(replacement)]);
        _selectedNode = node.Id;
    }

    private async Task NodeRemove(ShellFlags flags)
    {
        flags.Allow("id");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        if (node.Id == CurrentGraph.PurposeNodeId) throw new ArgumentException("The purpose node cannot be removed.");
        var parentId = ScopeParent(CurrentGraph, node.Id)?.Target ?? CurrentGraph.PurposeNodeId;
        var incident = CurrentGraph.Edges.Where(edge => edge.Source == node.Id || edge.Target == node.Id).ToArray();
        var operations = incident.Select(edge => GraphOperation.RemoveEdge(edge.Id))
            .Append(GraphOperation.RemoveNode(node.Id));
        await Patch(operations);
        _selectedNode = parentId;
        await output.WriteLineAsync($"Removal also included {incident.Length} incident edges.");
        await output.WriteLineAsync($"Selected parent {parentId.Value}.");
    }

    private async Task NodeTag(ShellFlags flags, bool add)
    {
        flags.Allow("id", "tag");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        var tag = flags.Required("tag");
        var tags = add
            ? node.Tags.Append(tag)
            : node.Tags.Where(value => !StringComparer.Ordinal.Equals(value, tag));
        var replacement = new GraphNode(node.Id, node.Text, node.Kind, tags, Attributes(node.Attributes));
        await Patch([GraphOperation.ReplaceNode(replacement)]);
    }

    private async Task NodeAttributeSet(ShellFlags flags)
    {
        flags.Allow("id", "name", "type", "value");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        var attributes = AttributeMap(node.Attributes);
        attributes[flags.Required("name")] = ParseValue(flags.Required("type"), flags.Required("value"));
        var replacement = new GraphNode(node.Id, node.Text, node.Kind, node.Tags, attributes);
        await Patch([GraphOperation.ReplaceNode(replacement)]);
    }

    private async Task NodeAttributeRemove(ShellFlags flags)
    {
        flags.Allow("id", "name");
        RequireSession();
        var node = FindNode(NodeId(flags.Optional("id")));
        var attributes = AttributeMap(node.Attributes);
        attributes.Remove(flags.Required("name"));
        var replacement = new GraphNode(node.Id, node.Text, node.Kind, node.Tags, attributes);
        await Patch([GraphOperation.ReplaceNode(replacement)]);
    }

    private async Task EdgeCommand(IReadOnlyList<string> tokens)
    {
        RequireSubcommand(tokens, "edge", out var subcommand, out var flags);
        switch (subcommand)
        {
            case "list": await EdgeList(flags); break;
            case "select": await EdgeSelect(flags); break;
            case "show": await EdgeShow(flags); break;
            case "add": await EdgeAdd(flags); break;
            case "set": await EdgeSet(flags); break;
            case "remove": await EdgeRemove(flags); break;
            case "tag-add": await EdgeTag(flags, add: true); break;
            case "tag-remove": await EdgeTag(flags, add: false); break;
            case "attribute-set": await EdgeAttributeSet(flags); break;
            case "attribute-remove": await EdgeAttributeRemove(flags); break;
            default: throw new ArgumentException($"Unknown edge command '{subcommand}'. Type 'help edge'.");
        }
    }

    private async Task EdgeList(ShellFlags flags)
    {
        flags.Allow("limit");
        var limit = flags.PositiveInt("limit", 20, QueryPageRequest.MaximumLimit);
        foreach (var edge in CurrentGraph.Edges.Take(limit))
            await output.WriteLineAsync(
                $"{edge.Id.Value} — {edge.Source.Value} -[{edge.Relationship}/{DirectionName(edge.ReviewDirection)}]-> {edge.Target.Value}");
    }

    private async Task EdgeSelect(ShellFlags flags)
    {
        flags.Allow("id");
        var edge = FindEdge(new EntityId(flags.Required("id")));
        _selectedEdge = edge.Id;
        await WriteEdge(edge);
    }

    private async Task EdgeShow(ShellFlags flags)
    {
        flags.Allow("id");
        await WriteEdge(FindEdge(EdgeId(flags.Optional("id"))));
    }

    private async Task EdgeAdd(ShellFlags flags)
    {
        flags.Allow("id", "source", "target", "relationship", "direction", "rationale");
        RequireSession();
        var id = new EntityId(flags.Required("id"));
        EnsureEntityMissing(id);
        var source = FindNode(new EntityId(flags.Required("source"))).Id;
        var target = FindNode(new EntityId(flags.Required("target"))).Id;
        var edge = new GraphEdge(
            id, source, target, flags.Required("relationship"),
            ParseDirection(flags.Required("direction")), flags.Optional("rationale"));
        await Patch([GraphOperation.AddEdge(edge)]);
        _selectedEdge = id;
    }

    private async Task EdgeSet(ShellFlags flags)
    {
        flags.Allow("id", "source", "target", "relationship", "direction", "rationale", "clear-rationale");
        RequireSession();
        var edge = FindEdge(EdgeId(flags.Optional("id")));
        flags.ExactlyOne("source", "target", "relationship", "direction", "rationale", "clear-rationale");
        var source = flags.Optional("source") is { } sourceId ? FindNode(new EntityId(sourceId)).Id : edge.Source;
        var target = flags.Optional("target") is { } targetId ? FindNode(new EntityId(targetId)).Id : edge.Target;
        var replacement = new GraphEdge(
            edge.Id,
            source,
            target,
            flags.Optional("relationship") ?? edge.Relationship,
            flags.Optional("direction") is { } direction ? ParseDirection(direction) : edge.ReviewDirection,
            flags.Has("clear-rationale") ? null : flags.Optional("rationale") ?? edge.Rationale,
            edge.Tags,
            Attributes(edge.Attributes));
        await Patch([GraphOperation.ReplaceEdge(replacement)]);
        _selectedEdge = edge.Id;
    }

    private async Task EdgeRemove(ShellFlags flags)
    {
        flags.Allow("id");
        RequireSession();
        var edge = FindEdge(EdgeId(flags.Optional("id")));
        await Patch([GraphOperation.RemoveEdge(edge.Id)]);
        _selectedEdge = null;
    }

    private async Task EdgeTag(ShellFlags flags, bool add)
    {
        flags.Allow("id", "tag");
        RequireSession();
        var edge = FindEdge(EdgeId(flags.Optional("id")));
        var tag = flags.Required("tag");
        var tags = add
            ? edge.Tags.Append(tag)
            : edge.Tags.Where(value => !StringComparer.Ordinal.Equals(value, tag));
        var replacement = new GraphEdge(
            edge.Id, edge.Source, edge.Target, edge.Relationship, edge.ReviewDirection,
            edge.Rationale, tags, Attributes(edge.Attributes));
        await Patch([GraphOperation.ReplaceEdge(replacement)]);
    }

    private async Task EdgeAttributeSet(ShellFlags flags)
    {
        flags.Allow("id", "name", "type", "value");
        RequireSession();
        var edge = FindEdge(EdgeId(flags.Optional("id")));
        var attributes = AttributeMap(edge.Attributes);
        attributes[flags.Required("name")] = ParseValue(flags.Required("type"), flags.Required("value"));
        var replacement = new GraphEdge(
            edge.Id, edge.Source, edge.Target, edge.Relationship, edge.ReviewDirection,
            edge.Rationale, edge.Tags, attributes);
        await Patch([GraphOperation.ReplaceEdge(replacement)]);
    }

    private async Task EdgeAttributeRemove(ShellFlags flags)
    {
        flags.Allow("id", "name");
        RequireSession();
        var edge = FindEdge(EdgeId(flags.Optional("id")));
        var attributes = AttributeMap(edge.Attributes);
        attributes.Remove(flags.Required("name"));
        var replacement = new GraphEdge(
            edge.Id, edge.Source, edge.Target, edge.Relationship, edge.ReviewDirection,
            edge.Rationale, edge.Tags, attributes);
        await Patch([GraphOperation.ReplaceEdge(replacement)]);
    }

    private async Task Ai(IReadOnlyList<string> tokens)
    {
        if (tokens.Count != 2 || !StringComparer.OrdinalIgnoreCase.Equals(tokens[1], "status"))
            throw new ArgumentException("Use 'ai status'.");
        var status = application.SemanticReviewAvailability;
        await output.WriteLineAsync(
            $"AI review enabled={status.Enabled.ToString().ToLowerInvariant()}, " +
            $"configured={status.Configured.ToString().ToLowerInvariant()}, provider={status.Provider}, model={status.Model}.");
        await output.WriteLineAsync(status.Message);
    }

    private async Task Patch(IEnumerable<GraphOperation> operations)
    {
        var session = RequireSession();
        _session = application.PatchChange(session.Reference, new GraphOperationBatch(operations));
        await output.WriteLineAsync(
            $"Pending operations: {_session.Operations.Operations.Count}. " +
            $"Affected nodes: {_session.Affected.AffectedNodes.Count}. " +
            $"Pending reviews: {_session.Readiness.PendingNodeIds.Count}.");
    }

    private async Task WriteReadiness(ChangeSessionSnapshot session)
    {
        await output.WriteLineAsync(session.Readiness.IsReady ? "Ready to commit." : "Not ready to commit.");
        foreach (var blocker in session.Readiness.Blockers) await output.WriteLineAsync($"- {blocker}");
    }

    private async Task WriteNode(GraphNode node)
    {
        await output.WriteLineAsync($"Node {node.Id.Value}");
        await output.WriteLineAsync($"  text: {node.Text}");
        await output.WriteLineAsync($"  kind: {node.Kind ?? "(none)"}");
        await output.WriteLineAsync($"  tags: {(node.Tags.Count == 0 ? "(none)" : string.Join(", ", node.Tags))}");
        foreach (var attribute in node.Attributes)
            await output.WriteLineAsync($"  attribute {attribute.Name} ({attribute.Value.Kind.ToString().ToLowerInvariant()}): {attribute.Value}");
    }

    private async Task WriteEdge(GraphEdge edge)
    {
        await output.WriteLineAsync($"Edge {edge.Id.Value}");
        await output.WriteLineAsync($"  source: {edge.Source.Value}");
        await output.WriteLineAsync($"  target: {edge.Target.Value}");
        await output.WriteLineAsync($"  relationship: {edge.Relationship}");
        await output.WriteLineAsync($"  direction: {DirectionName(edge.ReviewDirection)}");
        await output.WriteLineAsync($"  rationale: {edge.Rationale ?? "(none)"}");
        await output.WriteLineAsync($"  tags: {(edge.Tags.Count == 0 ? "(none)" : string.Join(", ", edge.Tags))}");
        foreach (var attribute in edge.Attributes)
            await output.WriteLineAsync($"  attribute {attribute.Name} ({attribute.Value.Kind.ToString().ToLowerInvariant()}): {attribute.Value}");
    }

    private ChangeSessionSnapshot RequireSession() => _session ??
        throw new ArgumentException("No change is active. Use 'begin --author NAME --intent TEXT'.");

    private ProjectGraph CurrentGraph => _session?.ProposedGraph ?? _project.Graph;

    private string ScopePath(EntityId nodeId)
    {
        var graph = CurrentGraph;
        var path = new List<string>();
        var current = FindNode(nodeId);
        var seen = new HashSet<EntityId>();
        while (true)
        {
            if (!seen.Add(current.Id)) throw new InvalidOperationException("The current scope tree contains a cycle.");
            path.Add(current.Id.Value);
            var parent = ScopeParent(graph, current.Id);
            if (parent is null) break;
            current = FindNode(parent.Target);
        }
        path.Reverse();
        return "/" + string.Join('/', path);
    }

    private static GraphEdge? ScopeParent(ProjectGraph graph, EntityId nodeId) =>
        graph.Edges.SingleOrDefault(edge => IsScopeParent(edge) && edge.Source == nodeId);

    private static bool IsScopeParent(GraphEdge edge) =>
        StringComparer.Ordinal.Equals(edge.Relationship, "scope-parent");

    private GraphNode FindNode(EntityId id) => CurrentGraph.Nodes.FirstOrDefault(node => node.Id == id) ??
        throw new ProjectQueryException(ProjectQueryErrorCode.NodeNotFound, $"Node '{id.Value}' does not exist.");

    private GraphEdge FindEdge(EntityId id) => CurrentGraph.Edges.FirstOrDefault(edge => edge.Id == id) ??
        throw new ProjectQueryException(ProjectQueryErrorCode.EdgeNotFound, $"Edge '{id.Value}' does not exist.");

    private void EnsureEntityMissing(EntityId id)
    {
        if (CurrentGraph.Nodes.Any(node => node.Id == id) || CurrentGraph.Edges.Any(edge => edge.Id == id))
            throw new ArgumentException($"Entity '{id.Value}' already exists.");
    }

    private void RepairSelections()
    {
        if (_selectedNode is null || !CurrentGraph.Nodes.Any(node => node.Id == _selectedNode.Value))
            _selectedNode = CurrentGraph.PurposeNodeId;
        if (_selectedEdge is { } edge && !CurrentGraph.Edges.Any(candidate => candidate.Id == edge))
            _selectedEdge = null;
    }

    private EntityId NodeId(string? supplied) => supplied is not null
        ? new EntityId(supplied)
        : _selectedNode ?? throw new ArgumentException("Select a node first or provide --id ID.");

    private EntityId EdgeId(string? supplied) => supplied is not null
        ? new EntityId(supplied)
        : _selectedEdge ?? throw new ArgumentException("Select an edge first or provide --id ID.");

    private async Task WriteExitWarnings()
    {
        foreach (var warning in application.GetExitWarnings())
            await error.WriteLineAsync(
                $"warning[session-loss]: project={warning.ProjectId.Value} session={warning.SessionId} " +
                $"operations={warning.OperationCount} pending={warning.PendingReviewCount} {warning.Message}");
    }

    private static ShellFlags Flags(IReadOnlyList<string> tokens, int start) =>
        ShellFlags.Parse(tokens.Skip(start));

    private static void RequireSubcommand(
        IReadOnlyList<string> tokens,
        string command,
        out string subcommand,
        out ShellFlags flags)
    {
        if (tokens.Count < 2) throw new ArgumentException($"'{command}' requires a subcommand.");
        subcommand = tokens[1].ToLowerInvariant();
        flags = Flags(tokens, 2);
    }

    private static void NoArguments(IReadOnlyList<string> tokens, int expectedCount)
    {
        if (tokens.Count != expectedCount) throw new ArgumentException($"'{tokens[0]}' accepts no arguments.");
    }

    private static bool Contains(string? value, string text) =>
        value?.Contains(text, StringComparison.OrdinalIgnoreCase) == true;

    private static ReviewDispositionKind ParseDisposition(string value) => value.ToLowerInvariant() switch
    {
        "updated" => ReviewDispositionKind.Updated,
        "reviewed-no-change" or "reviewednochange" => ReviewDispositionKind.ReviewedNoChange,
        "not-applicable" or "notapplicable" => ReviewDispositionKind.NotApplicable,
        "pending" => ReviewDispositionKind.Pending,
        _ => throw new ArgumentException("Review disposition must be updated, reviewed-no-change, not-applicable, or pending."),
    };

    private static string ReviewName(ReviewDispositionKind kind) => kind switch
    {
        ReviewDispositionKind.ReviewedNoChange => "reviewed-no-change",
        ReviewDispositionKind.NotApplicable => "not-applicable",
        _ => kind.ToString().ToLowerInvariant(),
    };

    private static ReviewDirection ParseDirection(string value) => value.ToLowerInvariant() switch
    {
        "none" => ReviewDirection.None,
        "source-to-target" or "sourcetotarget" => ReviewDirection.SourceToTarget,
        "target-to-source" or "targettosource" => ReviewDirection.TargetToSource,
        "both" => ReviewDirection.Both,
        _ => throw new ArgumentException("Direction must be none, source-to-target, target-to-source, or both."),
    };

    private static string DirectionName(ReviewDirection value) => value switch
    {
        ReviewDirection.SourceToTarget => "source-to-target",
        ReviewDirection.TargetToSource => "target-to-source",
        _ => value.ToString().ToLowerInvariant(),
    };

    private static GraphValue ParseValue(string type, string value) => type.ToLowerInvariant() switch
    {
        "text" => GraphValue.FromText(value),
        "integer" => GraphValue.FromInteger(long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture)),
        "decimal" => GraphValue.FromDecimal(value),
        "boolean" => GraphValue.FromBoolean(bool.Parse(value)),
        "symbol" => GraphValue.FromSymbol(value),
        "instant" => GraphValue.FromInstant(DateTimeOffset.ParseExact(
            value, "O", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)),
        _ => throw new ArgumentException("Attribute type must be text, integer, decimal, boolean, symbol, or instant."),
    };

    private static IEnumerable<KeyValuePair<string, GraphValue>> Attributes(
        IEnumerable<GraphAttribute> attributes) => attributes.Select(attribute =>
            new KeyValuePair<string, GraphValue>(attribute.Name, attribute.Value));

    private static Dictionary<string, GraphValue> AttributeMap(IEnumerable<GraphAttribute> attributes) =>
        attributes.ToDictionary(attribute => attribute.Name, attribute => attribute.Value, StringComparer.Ordinal);

    private sealed record DirectoryEntry(
        string Label,
        GraphNode Node,
        GraphEdge? Edge,
        string? Preposition);
}

internal static class ShellCommandLine
{
    public static IReadOnlyList<string> Tokenize(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var tokens = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var escaping = false;
        var tokenStarted = false;
        foreach (var character in line)
        {
            if (escaping)
            {
                if (character is not ('\\' or '\'' or '"')) current.Append('\\');
                current.Append(character);
                escaping = false;
                tokenStarted = true;
                continue;
            }
            if (character == '\\' && quote is not null)
            {
                escaping = true;
                continue;
            }
            if (quote is not null)
            {
                if (character == quote) quote = null;
                else current.Append(character);
                tokenStarted = true;
                continue;
            }
            if (character is '\'' or '"')
            {
                quote = character;
                tokenStarted = true;
                continue;
            }
            if (char.IsWhiteSpace(character))
            {
                if (!tokenStarted) continue;
                tokens.Add(current.ToString());
                current.Clear();
                tokenStarted = false;
                continue;
            }
            current.Append(character);
            tokenStarted = true;
        }
        if (escaping) current.Append('\\');
        if (quote is not null) throw new ArgumentException("The command contains an unterminated quoted value.");
        if (tokenStarted) tokens.Add(current.ToString());
        return tokens;
    }
}

internal sealed class ShellFlags
{
    private readonly Dictionary<string, string?> _values;

    private ShellFlags(Dictionary<string, string?> values) => _values = values;

    public static ShellFlags Parse(IEnumerable<string> tokens)
    {
        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        var items = tokens.ToArray();
        for (var index = 0; index < items.Length; index++)
        {
            var token = items[index];
            if (!token.StartsWith("--", StringComparison.Ordinal) || token.Length == 2)
                throw new ArgumentException($"Expected a --flag but found '{token}'.");
            var name = token[2..].ToLowerInvariant();
            if (values.ContainsKey(name)) throw new ArgumentException($"Flag '--{name}' was supplied more than once.");
            string? value = null;
            if (index + 1 < items.Length && !items[index + 1].StartsWith("--", StringComparison.Ordinal))
                value = items[++index];
            values.Add(name, value);
        }
        return new ShellFlags(values);
    }

    public void Allow(params string[] names)
    {
        var allowed = names.ToHashSet(StringComparer.Ordinal);
        var unknown = _values.Keys.FirstOrDefault(name => !allowed.Contains(name));
        if (unknown is not null) throw new ArgumentException($"Unknown flag '--{unknown}'.");
    }

    public bool Has(string name) => _values.ContainsKey(name);

    public string Required(string name) => _values.TryGetValue(name, out var value) && value is not null
        ? value
        : throw new ArgumentException($"Flag '--{name}' requires a value.");

    public string? Optional(string name)
    {
        if (!_values.TryGetValue(name, out var value)) return null;
        return value ?? throw new ArgumentException($"Flag '--{name}' requires a value.");
    }

    public bool Boolean(string name)
    {
        if (!_values.TryGetValue(name, out var value)) return false;
        if (value is not null) throw new ArgumentException($"Flag '--{name}' does not take a value.");
        return true;
    }

    public int PositiveInt(string name, int fallback, int maximum)
    {
        var value = Optional(name);
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 1 || parsed > maximum)
            throw new ArgumentException($"Flag '--{name}' must be between 1 and {maximum}.");
        return parsed;
    }

    public int NonNegativeInt(string name, int fallback, int maximum)
    {
        var value = Optional(name);
        if (value is null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
            parsed < 0 || parsed > maximum)
            throw new ArgumentException($"Flag '--{name}' must be between 0 and {maximum}.");
        return parsed;
    }

    public void ExactlyOne(params string[] names)
    {
        if (names.Count(Has) != 1)
            throw new ArgumentException("Supply exactly one of " + string.Join(", ", names.Select(name => $"--{name}")) + ".");
        foreach (var name in names.Where(Has))
        {
            if (_values[name] is null && !name.StartsWith("clear-", StringComparison.Ordinal))
                throw new ArgumentException($"Flag '--{name}' requires a value.");
            if (_values[name] is not null && name.StartsWith("clear-", StringComparison.Ordinal))
                throw new ArgumentException($"Flag '--{name}' does not take a value.");
        }
    }
}
