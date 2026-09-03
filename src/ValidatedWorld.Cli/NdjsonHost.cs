using System.Text.Json;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Cli;

internal sealed class NdjsonHost(
    ProjectApplication application,
    TextReader input,
    TextWriter output,
    TextWriter error,
    CancellationToken cancellationToken)
{
    private static readonly string[] Commands =
    [
        "host.help", "host.exit",
        "project.init", "project.open", "project.status", "project.verify", "project.backup", "project.export-sql",
        "project.diff",
        "sample.list", "sample.create",
        "read.node", "read.edge", "read.nodes", "read.edges", "read.search", "read.tag", "read.scope",
        "read.neighbors", "read.dependencies", "read.path", "read.context",
        "change.begin", "change.show", "change.focus", "change.apply", "change.patch", "change.expand",
        "change.affected", "change.review", "change.validate", "change.write", "change.discard",
        "ai.status",
    ];

    public async Task<int> RunAsync()
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                await WriteExitWarnings();
                return CliRunner.SuccessExitCode;
            }

            var command = "unknown";
            try
            {
                var request = Protocol.Deserialize<ProtocolRequest>(line);
                command = Required(request.Command, "command");
                if (request.Payload.ValueKind != JsonValueKind.Object)
                    throw new JsonException("The command payload must be a JSON object.");

                var (payload, shouldExit) = await DispatchAsync(command, request.Payload);
                await WriteResult(command, "ok", payload);
                if (shouldExit)
                {
                    await WriteExitWarnings();
                    return CliRunner.SuccessExitCode;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                var (code, message, _) = CliRunner.Error(exception);
                await WriteResult(command, "error", new ErrorDto(code, message));
            }
        }
    }

    private async Task<(object Payload, bool ShouldExit)> DispatchAsync(string command, JsonElement payload)
    {
        if (command == "change.write") return (await ChangeWrite(payload), false);
        return Dispatch(command, payload);
    }

    private (object Payload, bool ShouldExit) Dispatch(string command, JsonElement payload) => command switch
    {
        "host.help" => HostHelp(payload),
        "host.exit" => HostExit(payload),
        "project.init" => (ProjectInit(payload), false),
        "project.open" => (ProjectOpen(payload), false),
        "project.status" => (ProjectStatus(payload), false),
        "project.verify" => (ProjectVerify(payload), false),
        "project.backup" => (ProjectBackup(payload), false),
        "project.export-sql" => (ProjectExportSql(payload), false),
        "project.diff" => (ProjectDiff(payload), false),
        "sample.list" => (SampleList(payload), false),
        "sample.create" => (SampleCreate(payload), false),
        "read.node" => (ReadNode(payload), false),
        "read.edge" => (ReadEdge(payload), false),
        "read.nodes" => (ReadNodes(payload), false),
        "read.edges" => (ReadEdges(payload), false),
        "read.search" => (ReadSearch(payload), false),
        "read.tag" => (ReadTag(payload), false),
        "read.scope" => (ReadScope(payload), false),
        "read.neighbors" => (ReadNeighbors(payload), false),
        "read.dependencies" => (ReadDependencies(payload), false),
        "read.path" => (ReadPath(payload), false),
        "read.context" => (ReadContext(payload), false),
        "change.begin" => (ChangeBegin(payload), false),
        "change.show" => (ChangeShow(payload), false),
        "change.focus" => (ChangeFocus(payload), false),
        "change.apply" => (ChangeApply(payload), false),
        "change.patch" => (ChangePatch(payload), false),
        "change.expand" => (ChangeExpand(payload), false),
        "change.affected" => (ChangeAffected(payload), false),
        "change.review" => (ChangeReview(payload), false),
        "change.validate" => (ChangeValidate(payload), false),
        "change.discard" => (ChangeDiscard(payload), false),
        "ai.status" => (AiStatus(payload), false),
        _ => throw new ArgumentException($"Unknown NDJSON command '{command}'.", nameof(command)),
    };

    private static (object, bool) HostHelp(JsonElement payload)
    {
        _ = CliJson.Payload<EmptyRequest>(payload);
        return (new
        {
            protocolVersion = Protocol.CurrentVersion,
            framing = "One request and one result JSON object per line. Unknown fields are rejected.",
            requestShape = new { version = 1, command = "change.begin", payload = new { } },
            resultShape = new { version = 1, command = "change.begin", status = "ok|error", payload = new { } },
            note = "Sessions exist only for this process. Use the exact reference returned by each change response.",
            commands = Commands,
            payloads = new
            {
                project = "init {path,graph}; open|status|verify|export-sql {path}; " +
                    "backup {sourcePath,destinationPath}; " +
                    "diff {basePath,targetPath,limit?,cursor?}",
                sample = "list {}; create {sampleName,path}",
                read = "node|edge {path,entityId,expectedProjectId?}; " +
                    "nodes|edges {path,limit?,cursor?,expectedProjectId?}; " +
                    "search {path,text,limit?,cursor?,expectedProjectId?}; " +
                    "tag {path,tag,limit?,cursor?,expectedProjectId?}; " +
                    "scope {path,nodeId,limit?,cursor?,maxDepth?," +
                    "maxVisitedNodes?,expectedProjectId?}; neighbors|dependencies {path,entityId,limit?,cursor?," +
                    "expectedProjectId?}; path {path,sourceNodeId,targetNodeId,maxDepth?,maxVisitedNodes?," +
                    "expectedProjectId?}; context {path,nodeIds,maxDepth?,maxVisitedNodes?,expectedProjectId?}",
                change = "begin {path,projectId,author,intent,includeOperations?,includeProposedGraph?}; " +
                    "show {session:{projectId,sessionId},includeOperations?,includeProposedGraph?}; affected {session}; " +
                    "focus {reference,operations,scopeParents}; " +
                    "apply|patch {reference,operations,limits?,includeOperations?,includeProposedGraph?}; " +
                    "expand {reference,limits?,includeOperations?,includeProposedGraph?}; " +
                    "review {reference,dispositions,presentedContextNodeIds,includeOperations?," +
                    "includeProposedGraph?}; validate {reference,includeOperations?,includeProposedGraph?}; " +
                    "discard {reference}; " +
                    "write {reference,bypassAiReview?}",
                changeSemantics = "apply replaces the complete pending batch; patch merges only the supplied entity " +
                    "operations into it. includeOperations and includeProposedGraph default true for compatibility; " +
                    "set them false during bounded iterative work.",
                ai = "status {}; semantic review automatically gates change.write when enabled and configured",
                operations = "{operations:[{kind:add|replace|remove,entityKind:node|edge,entityId,node|null,edge|null}]}",
                review = "dispositions use {nodeId,kind:updated|reviewedNoChange|notApplicable|pending,rationale?}",
            },
        }, false);
    }

    private (object, bool) HostExit(JsonElement payload)
    {
        _ = CliJson.Payload<EmptyRequest>(payload);
        return (new { warnings = application.GetExitWarnings().Select(CliDto.Warning).ToArray() }, true);
    }

    private object ProjectInit(JsonElement payload)
    {
        var request = CliJson.Payload<ProjectInitRequest>(payload);
        return CliDto.Stored(application.Initialize(request.Path, GraphProtocol.FromDto(request.Graph)));
    }

    private object ProjectStatus(JsonElement payload)
    {
        var request = CliJson.Payload<PathRequest>(payload);
        return CliDto.Status(application.Status(request.Path));
    }

    private object ProjectOpen(JsonElement payload)
    {
        var request = CliJson.Payload<PathRequest>(payload);
        var loaded = application.Load(request.Path);
        return new LoadedProjectDto(CliDto.Stored(loaded), GraphProtocol.ToDto(loaded.Graph));
    }

    private object ProjectVerify(JsonElement payload)
    {
        var request = CliJson.Payload<PathRequest>(payload);
        return CliDto.Verification(application.Verify(request.Path));
    }

    private object ProjectBackup(JsonElement payload)
    {
        var request = CliJson.Payload<ProjectBackupRequest>(payload);
        return CliDto.Stored(application.Backup(request.SourcePath, request.DestinationPath));
    }

    private object ProjectExportSql(JsonElement payload)
    {
        var request = CliJson.Payload<PathRequest>(payload);
        var result = application.ExportSql(request.Path);
        return new SqlExportDto(result.Path, result.StateFingerprint, result.Sql);
    }

    private object ProjectDiff(JsonElement payload)
    {
        var request = CliJson.Payload<ProjectDiffRequest>(payload);
        return CliDto.Diff(application.Diff(
            request.BasePath,
            request.TargetPath,
            CliDto.Page(request.Limit, request.Cursor)));
    }

    private static object SampleList(JsonElement payload)
    {
        _ = CliJson.Payload<EmptyRequest>(payload);
        return new { samples = SampleProjectCatalog.Names };
    }

    private object SampleCreate(JsonElement payload)
    {
        var request = CliJson.Payload<SampleCreateRequest>(payload);
        return CliDto.Stored(application.CreateSample(request.SampleName, request.Path));
    }

    private object ReadNode(JsonElement payload)
    {
        var request = CliJson.Payload<ReadEntityRequest>(payload);
        return GraphProtocol.ToDto(Queries(request.Path, request.ExpectedProjectId)
            .GetNode(new EntityId(request.EntityId)));
    }

    private object ReadEdge(JsonElement payload)
    {
        var request = CliJson.Payload<ReadEntityRequest>(payload);
        return GraphProtocol.ToDto(Queries(request.Path, request.ExpectedProjectId)
            .GetEdge(new EntityId(request.EntityId)));
    }

    private object ReadNodes(JsonElement payload)
    {
        var request = CliJson.Payload<ReadPageRequest>(payload);
        return CliDto.Nodes(Queries(request.Path, request.ExpectedProjectId)
            .ListNodes(CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadEdges(JsonElement payload)
    {
        var request = CliJson.Payload<ReadPageRequest>(payload);
        return CliDto.Edges(Queries(request.Path, request.ExpectedProjectId)
            .ListEdges(CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadSearch(JsonElement payload)
    {
        var request = CliJson.Payload<SearchRequest>(payload);
        return CliDto.Search(Queries(request.Path, request.ExpectedProjectId)
            .Search(request.Text, CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadTag(JsonElement payload)
    {
        var request = CliJson.Payload<TagRequest>(payload);
        return CliDto.Search(Queries(request.Path, request.ExpectedProjectId)
            .SearchByTag(request.Tag, CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadScope(JsonElement payload)
    {
        var request = CliJson.Payload<ScopeRequest>(payload);
        return CliDto.Scope(Queries(request.Path, request.ExpectedProjectId).GetScope(
            new EntityId(request.NodeId),
            CliDto.Page(request.Limit, request.Cursor),
            CliDto.Traversal(request.MaxDepth, request.MaxVisitedNodes, cancellationToken)));
    }

    private object ReadNeighbors(JsonElement payload)
    {
        var request = CliJson.Payload<ReadEntityPageRequest>(payload);
        return CliDto.Neighbors(Queries(request.Path, request.ExpectedProjectId).GetNeighbors(
            new EntityId(request.EntityId), CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadDependencies(JsonElement payload)
    {
        var request = CliJson.Payload<ReadEntityPageRequest>(payload);
        return CliDto.Dependencies(Queries(request.Path, request.ExpectedProjectId).GetDependencies(
            new EntityId(request.EntityId), CliDto.Page(request.Limit, request.Cursor)));
    }

    private object ReadPath(JsonElement payload)
    {
        var request = CliJson.Payload<PathQueryRequest>(payload);
        return CliDto.Path(Queries(request.Path, request.ExpectedProjectId).FindDependencyPath(
            new EntityId(request.SourceNodeId), new EntityId(request.TargetNodeId),
            CliDto.Traversal(request.MaxDepth, request.MaxVisitedNodes, cancellationToken)));
    }

    private object ReadContext(JsonElement payload)
    {
        var request = CliJson.Payload<ContextRequest>(payload);
        ArgumentNullException.ThrowIfNull(request.NodeIds);
        return CliDto.Context(Queries(request.Path, request.ExpectedProjectId).GetContext(
            request.NodeIds.Select(id => new EntityId(id)),
            CliDto.Traversal(request.MaxDepth, request.MaxVisitedNodes, cancellationToken)));
    }

    private object ChangeBegin(JsonElement payload)
    {
        var request = CliJson.Payload<SessionBeginRequest>(payload);
        return CliDto.Snapshot(
            application.BeginChange(
                request.Path, new ProjectId(request.ProjectId), request.Author, request.Intent),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangeShow(JsonElement payload)
    {
        var request = CliJson.Payload<SessionShowRequest>(payload);
        return CliDto.Snapshot(
            application.ShowChange(CliDto.Locator(request.Session)),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangeFocus(JsonElement payload)
    {
        var request = CliJson.Payload<FocusRequest>(payload);
        ArgumentNullException.ThrowIfNull(request.ScopeParents);
        var result = application.FocusChange(
            CliDto.Reference(request.Reference),
            GraphProtocol.FromDto(request.Operations),
            request.ScopeParents.Select(selection => new ScopeParentSelection(
                new EntityId(selection.ChildId), new EntityId(selection.ParentId), new EntityId(selection.EdgeId))));
        return new FocusResultDto(
            GraphProtocol.ToDto(result.ExpandedOperations), result.OperationFingerprint, result.ProposedFingerprint);
    }

    private object ChangeApply(JsonElement payload)
    {
        var request = CliJson.Payload<ChangeOperationsRequest>(payload);
        return CliDto.Snapshot(
            application.ApplyChange(
                CliDto.Reference(request.Reference),
                GraphProtocol.FromDto(request.Operations),
                CliDto.AffectedOptions(request.MaxTraversalDepth, request.MaxAffectedNodes,
                    request.MaxOutputItems, cancellationToken)),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangePatch(JsonElement payload)
    {
        var request = CliJson.Payload<ChangeOperationsRequest>(payload);
        return CliDto.Snapshot(
            application.PatchChange(
                CliDto.Reference(request.Reference),
                GraphProtocol.FromDto(request.Operations),
                CliDto.AffectedOptions(request.MaxTraversalDepth, request.MaxAffectedNodes,
                    request.MaxOutputItems, cancellationToken)),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangeExpand(JsonElement payload)
    {
        var request = CliJson.Payload<ExpandRequest>(payload);
        return CliDto.Snapshot(
            application.ExpandChange(
                CliDto.Reference(request.Reference),
                CliDto.AffectedOptions(request.MaxTraversalDepth, request.MaxAffectedNodes,
                    request.MaxOutputItems, cancellationToken)),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangeAffected(JsonElement payload)
    {
        var request = CliJson.Payload<SessionLocatorRequest>(payload);
        return CliDto.Affected(application.GetAffected(CliDto.Locator(request.Session)));
    }

    private object ChangeReview(JsonElement payload)
    {
        var request = CliJson.Payload<ReviewRequest>(payload);
        ArgumentNullException.ThrowIfNull(request.Dispositions);
        ArgumentNullException.ThrowIfNull(request.PresentedContextNodeIds);
        return CliDto.Snapshot(
            application.ReviewChange(
                CliDto.Reference(request.Reference),
                new ChangeReviewUpdate(
                    request.Dispositions.Select(disposition => new ReviewDisposition(
                        new EntityId(disposition.NodeId), disposition.Kind, disposition.Rationale)),
                    request.PresentedContextNodeIds.Select(id => new EntityId(id)))),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private object ChangeValidate(JsonElement payload)
    {
        var request = CliJson.Payload<SessionValidateRequest>(payload);
        return CliDto.Snapshot(
            application.ValidateChange(CliDto.Reference(request.Reference)),
            request.IncludeOperations,
            request.IncludeProposedGraph);
    }

    private async Task<object> ChangeWrite(JsonElement payload)
    {
        var request = CliJson.Payload<ChangeWriteRequest>(payload);
        return CliDto.Write(await application.WriteChangeAsync(
            CliDto.Reference(request.Reference),
            new ChangeWriteOptions(request.BypassAiReview),
            cancellationToken));
    }

    private object ChangeDiscard(JsonElement payload)
    {
        var request = CliJson.Payload<SessionReferenceRequest>(payload);
        var result = application.DiscardChange(CliDto.Reference(request.Reference));
        return new DiscardResultDto(
            result.ProjectId.Value,
            result.SessionId,
            result.DiscardedUtc.ToUniversalTime().ToString(
                "O", System.Globalization.CultureInfo.InvariantCulture));
    }

    private object AiStatus(JsonElement payload)
    {
        _ = CliJson.Payload<EmptyRequest>(payload);
        return CliDto.Availability(application.SemanticReviewAvailability);
    }

    private ProjectQueries Queries(string path, string? expectedProjectId) => application.Queries(
        path,
        expectedProjectId is null ? null : new ProjectId(expectedProjectId));

    private async Task WriteResult(string command, string status, object payload)
    {
        var element = JsonSerializer.SerializeToElement(payload, payload.GetType(), CliJson.Options);
        var result = new ProtocolResult(Protocol.CurrentVersion, command, status, element);
        await output.WriteLineAsync(CliJson.Serialize(result));
        await output.FlushAsync(cancellationToken);
    }

    private async Task WriteExitWarnings()
    {
        foreach (var warning in application.GetExitWarnings())
        {
            await error.WriteLineAsync(
                $"warning[session-loss]: project={warning.ProjectId.Value} session={warning.SessionId} " +
                $"operations={warning.OperationCount} pending={warning.PendingReviewCount} {warning.Message}");
        }
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new JsonException($"'{name}' is required.");
        return value;
    }
}
