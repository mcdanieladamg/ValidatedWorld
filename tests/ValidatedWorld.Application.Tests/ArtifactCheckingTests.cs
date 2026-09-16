using System.Security.Cryptography;
using System.Text;
using ValidatedWorld.Application;
using ValidatedWorld.Core;

namespace ValidatedWorld.Application.Tests;

public sealed class ArtifactCheckingTests
{
    [Fact]
    public void Caller_selected_sample_size_does_not_preallocate_the_requested_capacity()
    {
        using var temporary = new TemporaryDirectory();
        var file = Path.Combine(temporary.Path, "small.txt");
        File.WriteAllText(file, "Small artifact.");
        var report = new ArtifactCheckService().Check(Path.Combine(temporary.Path, "project.vw.db"),
            Graph(Anchor("sample", "small.txt", Hash(file))),
            options: new ArtifactCheckOptions(int.MaxValue, int.MaxValue, [temporary.Path]));
        var item = Assert.Single(report.Items);
        Assert.Equal(ArtifactCheckStatus.Matched, item.Status);
        Assert.Equal("Small artifact.", Encoding.UTF8.GetString(Convert.FromBase64String(item.ContentSampleBase64!)));
        Assert.False(item.ContentSampleTruncated);
    }

    [Fact]
    public void Filesystem_checker_distinguishes_match_drift_and_missing_artifacts()
    {
        using var temporary = new TemporaryDirectory();
        var projectPath = Path.Combine(temporary.Path, "project.vw.db");
        var matchingPath = Path.Combine(temporary.Path, "matching.txt");
        var driftedPath = Path.Combine(temporary.Path, "drifted.txt");
        File.WriteAllText(matchingPath, "matching bytes", Encoding.UTF8);
        File.WriteAllText(driftedPath, "changed bytes", Encoding.UTF8);

        var graph = Graph(
            Anchor("matching", "matching.txt", Hash(matchingPath)),
            Anchor("drifted", "drifted.txt", HashOf("original bytes")),
            Anchor("missing", "missing.txt", HashOf("missing bytes")));

        var report = new ArtifactCheckService().Check(projectPath, graph,
            options: new ArtifactCheckOptions(AllowedRoots: [temporary.Path]));

        Assert.Equal(3, report.TotalAnchorCount);
        Assert.Equal(ArtifactCheckStatus.Matched, report.Items.Single(item => item.NodeId.Value == "matching").Status);
        Assert.Equal(ArtifactCheckStatus.Drifted, report.Items.Single(item => item.NodeId.Value == "drifted").Status);
        Assert.Equal(ArtifactCheckStatus.Missing, report.Items.Single(item => item.NodeId.Value == "missing").Status);
        Assert.Equal(1, report.MatchedCount);
        Assert.Equal(1, report.DriftedCount);
        Assert.Equal(1, report.MissingCount);
        Assert.All(report.Items.Where(item => item.Status != ArtifactCheckStatus.Missing),
            item => Assert.NotNull(item.ContentSampleBase64));
    }

    [Fact]
    public void Invalid_and_unsupported_anchors_are_reported_without_file_access()
    {
        using var temporary = new TemporaryDirectory();
        var graph = Graph(
            new GraphNode(new EntityId("invalid"), "Invalid", "external-anchor", ["artifact"],
                [new(ArtifactAnchorMetadata.Path, GraphValue.FromText("invalid.txt"))]),
            Anchor("unsupported", "anything.txt", HashOf("anything"), "unknown", 1),
            Anchor("wrong-version", "anything.txt", HashOf("anything"), ArtifactCheckerContract.FileSystemAdapterId, 99));

        var report = new ArtifactCheckService().Check(Path.Combine(temporary.Path, "project.vw.db"), graph);

        Assert.Equal(ArtifactCheckStatus.InvalidAnchor, report.Items.Single(item => item.NodeId.Value == "invalid").Status);
        Assert.Equal(ArtifactCheckStatus.UnsupportedAdapter, report.Items.Single(item => item.NodeId.Value == "unsupported").Status);
        Assert.Equal(ArtifactCheckStatus.UnsupportedAdapter, report.Items.Single(item => item.NodeId.Value == "wrong-version").Status);
        Assert.Equal(3, report.InvalidAnchorCount + report.UnsupportedAdapterCount);
    }

    [Fact]
    public void Anchor_scan_is_bounded_and_samples_are_bounded()
    {
        using var temporary = new TemporaryDirectory();
        var path = Path.Combine(temporary.Path, "large.txt");
        File.WriteAllText(path, new string('x', 100));
        var graph = Graph(Anchor("large", "large.txt", Hash(path)));

        var report = new ArtifactCheckService().Check(
            Path.Combine(temporary.Path, "project.vw.db"), graph,
            options: new ArtifactCheckOptions(MaxAnchors: 1, MaxSampleBytes: 8, AllowedRoots: [temporary.Path]));

        var item = Assert.Single(report.Items);
        Assert.True(report.IsComplete);
        Assert.True(item.ContentSampleTruncated);
        Assert.Equal(8, Convert.FromBase64String(item.ContentSampleBase64!).Length);

        var twoAnchors = Graph(
            Anchor("first", "large.txt", Hash(path)),
            Anchor("second", "large.txt", Hash(path)));
        var omitted = new ArtifactCheckService().Check(
            Path.Combine(temporary.Path, "project.vw.db"), twoAnchors,
            options: new ArtifactCheckOptions(MaxAnchors: 1, MaxSampleBytes: 8, AllowedRoots: [temporary.Path]));
        Assert.False(omitted.IsComplete);
        Assert.Contains("first 1", omitted.OmissionMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Filesystem_reads_are_denied_without_host_owned_authority_and_parent_escapes_stay_denied()
    {
        using var projectDirectory = new TemporaryDirectory();
        using var outsideDirectory = new TemporaryDirectory();
        var secret = Path.Combine(outsideDirectory.Path, "outside.txt");
        File.WriteAllText(secret, "must not be sampled");
        var relativeEscape = Path.GetRelativePath(projectDirectory.Path, secret);
        var graph = Graph(
            Anchor("relative", relativeEscape, Hash(secret)),
            Anchor("absolute", secret, Hash(secret)),
            Anchor("network", @"\\invalid-server\private-share\secret.txt", HashOf("secret")));

        var denied = new ArtifactCheckService().Check(
            Path.Combine(projectDirectory.Path, "project.vw.db"), graph);
        Assert.All(denied.Items, item => Assert.Equal(ArtifactCheckStatus.Unauthorized, item.Status));
        Assert.All(denied.Items, item => Assert.Null(item.ContentSampleBase64));
        Assert.Equal(3, denied.UnauthorizedCount);

        var stillDenied = new ArtifactCheckService().Check(
            Path.Combine(projectDirectory.Path, "project.vw.db"), graph,
            options: new ArtifactCheckOptions(AllowedRoots: [projectDirectory.Path]));
        Assert.All(stillDenied.Items, item => Assert.Equal(ArtifactCheckStatus.Unauthorized, item.Status));
        Assert.All(stillDenied.Items, item => Assert.Null(item.ActualSha256));
    }

    [Fact]
    public void Explicit_roots_authorize_relative_and_absolute_paths_but_not_links_that_resolve_outside()
    {
        using var projectDirectory = new TemporaryDirectory();
        using var outsideDirectory = new TemporaryDirectory();
        var inside = Path.Combine(projectDirectory.Path, "inside.txt");
        var outside = Path.Combine(outsideDirectory.Path, "outside.txt");
        var link = Path.Combine(projectDirectory.Path, "linked.txt");
        File.WriteAllText(inside, "inside");
        File.WriteAllText(outside, "outside");
        File.CreateSymbolicLink(link, outside);
        var graph = Graph(
            Anchor("relative", "inside.txt", Hash(inside)),
            Anchor("absolute", inside, Hash(inside)),
            Anchor("link", "linked.txt", Hash(outside)));

        var report = new ArtifactCheckService().Check(
            Path.Combine(projectDirectory.Path, "project.vw.db"), graph,
            options: new ArtifactCheckOptions(AllowedRoots: [projectDirectory.Path]));

        Assert.Equal(ArtifactCheckStatus.Matched, report.Items.Single(item => item.NodeId.Value == "relative").Status);
        Assert.Equal(ArtifactCheckStatus.Matched, report.Items.Single(item => item.NodeId.Value == "absolute").Status);
        var deniedLink = report.Items.Single(item => item.NodeId.Value == "link");
        Assert.Equal(ArtifactCheckStatus.Unauthorized, deniedLink.Status);
        Assert.Null(deniedLink.ContentSampleBase64);
    }

    [Fact]
    public void Host_owned_adapter_contract_can_be_extended_without_loading_graph_code()
    {
        var node = Anchor("custom", "opaque", HashOf("custom"), "test", 1);
        var checker = new TestChecker();
        var report = new ArtifactCheckService([checker]).Check(
            "project.vw.db", Graph(node));

        var item = Assert.Single(report.Items);
        Assert.Equal(ArtifactCheckStatus.Matched, item.Status);
        Assert.Equal("test", item.AdapterId);
        Assert.True(checker.WasCalled);
    }

    private static ProjectGraph Graph(params GraphNode[] anchors)
    {
        var purpose = new GraphNode(new EntityId("purpose"), "Purpose", "purpose");
        return new ProjectGraph(new ProjectId("project"), "Project", purpose.Id, [purpose, .. anchors], []);
    }

    private static GraphNode Anchor(string id, string path, string hash, string? adapter = null, int? version = null)
    {
        var attributes = new List<KeyValuePair<string, GraphValue>>
        {
            new(ArtifactAnchorMetadata.Path, GraphValue.FromText(path)),
            new(ArtifactAnchorMetadata.Sha256, GraphValue.FromText(hash)),
        };
        if (adapter is not null) attributes.Add(new(ArtifactAnchorMetadata.Adapter, GraphValue.FromSymbol(adapter)));
        if (version is not null) attributes.Add(new(ArtifactAnchorMetadata.AdapterVersion, GraphValue.FromInteger(version.Value)));
        return new GraphNode(new EntityId(id), "Anchor", "external-anchor", ["artifact"], attributes);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    private static string HashOf(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class TestChecker : IArtifactChecker
    {
        public string AdapterId => "test";

        public int ContractVersion => ArtifactCheckerContract.CurrentVersion;

        public bool WasCalled { get; private set; }

        public ArtifactCheckResult Check(ArtifactCheckRequest request)
        {
            WasCalled = true;
            return new(request.Anchor.NodeId, request.Anchor.Path, null, AdapterId, ContractVersion,
                ArtifactCheckStatus.Matched, "The test adapter matched the anchor.",
                request.Anchor.Sha256, request.Anchor.Sha256, null, false);
        }
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"ValidatedWorld.Artifact.Tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path)) Directory.Delete(Path, recursive: true);
        }
    }
}
