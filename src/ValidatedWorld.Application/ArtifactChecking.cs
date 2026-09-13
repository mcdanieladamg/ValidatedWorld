using System.Security.Cryptography;
using System.Text;
using ValidatedWorld.Core;

namespace ValidatedWorld.Application;

/// <summary>Stable metadata names used by opt-in external artifact anchors.</summary>
public static class ArtifactAnchorMetadata
{
    public const string Path = "artifact.path";
    public const string Sha256 = "artifact.sha256";
    public const string Adapter = "artifact.adapter";
    public const string AdapterVersion = "artifact.adapter-version";
}

public static class ArtifactCheckerContract
{
    public const int CurrentVersion = 1;
    public const string FileSystemAdapterId = "filesystem";
    public const int DefaultMaxAnchors = 1_000;
    public const int DefaultMaxSampleBytes = 4_096;
    public const int MaximumSampleBytes = 64 * 1_024;
}

public sealed record ArtifactAnchor(
    EntityId NodeId,
    string Path,
    string Sha256,
    string AdapterId,
    int AdapterVersion)
{
    public static bool IsCandidate(GraphNode node) =>
        node.Tags.Contains("artifact", StringComparer.Ordinal) ||
        StringComparer.Ordinal.Equals(node.Kind, "external-anchor");

    public static bool TryRead(GraphNode node, out ArtifactAnchor? anchor, out string error)
    {
        ArgumentNullException.ThrowIfNull(node);
        anchor = null;
        error = string.Empty;

        if (!IsCandidate(node))
        {
            error = "The node is not marked as an artifact anchor.";
            return false;
        }

        if (!TryGetText(node, ArtifactAnchorMetadata.Path, out var path) || string.IsNullOrWhiteSpace(path))
        {
            error = $"Missing text attribute '{ArtifactAnchorMetadata.Path}'.";
            return false;
        }

        if (!TryGetText(node, ArtifactAnchorMetadata.Sha256, out var sha256) ||
            sha256.Length != 64 || sha256.Any(character => !Uri.IsHexDigit(character)))
        {
            error = $"Missing or invalid SHA-256 attribute '{ArtifactAnchorMetadata.Sha256}'.";
            return false;
        }

        var adapterId = ArtifactCheckerContract.FileSystemAdapterId;
        if (node.TryGetAttribute(ArtifactAnchorMetadata.Adapter, out var adapterValue))
        {
            if (adapterValue.Kind is not (GraphValueKind.Text or GraphValueKind.Symbol) ||
                string.IsNullOrWhiteSpace(adapterValue.ToString()))
            {
                error = $"Attribute '{ArtifactAnchorMetadata.Adapter}' must be non-empty text or a symbol.";
                return false;
            }

            adapterId = adapterValue.ToString();
        }

        var adapterVersion = ArtifactCheckerContract.CurrentVersion;
        if (node.TryGetAttribute(ArtifactAnchorMetadata.AdapterVersion, out var versionValue))
        {
            if (versionValue.Kind != GraphValueKind.Integer || versionValue.IntegerValue <= 0 ||
                versionValue.IntegerValue > int.MaxValue)
            {
                error = $"Attribute '{ArtifactAnchorMetadata.AdapterVersion}' must be a positive integer.";
                return false;
            }

            adapterVersion = (int)versionValue.IntegerValue;
        }

        anchor = new ArtifactAnchor(node.Id, path, sha256.ToLowerInvariant(), adapterId, adapterVersion);
        return true;
    }

    private static bool TryGetText(GraphNode node, string name, out string value)
    {
        if (node.TryGetAttribute(name, out var attribute) && attribute.Kind == GraphValueKind.Text)
        {
            value = attribute.TextValue;
            return true;
        }

        value = string.Empty;
        return false;
    }
}

public sealed record ArtifactCheckRequest(
    string ProjectPath,
    ArtifactAnchor Anchor,
    int MaxSampleBytes,
    CancellationToken CancellationToken = default);

public enum ArtifactCheckStatus
{
    Matched,
    Drifted,
    Missing,
    InvalidAnchor,
    UnsupportedAdapter,
    Unreadable,
}

public sealed record ArtifactCheckResult(
    EntityId NodeId,
    string? Path,
    string? ResolvedPath,
    string? AdapterId,
    int? AdapterVersion,
    ArtifactCheckStatus Status,
    string Message,
    string? ExpectedSha256,
    string? ActualSha256,
    string? ContentSampleBase64,
    bool ContentSampleTruncated);

public sealed record ArtifactCheckOptions(
    int MaxAnchors = ArtifactCheckerContract.DefaultMaxAnchors,
    int MaxSampleBytes = ArtifactCheckerContract.DefaultMaxSampleBytes)
{
    public ArtifactCheckOptions Validate()
    {
        if (MaxAnchors <= 0 || MaxAnchors > ArtifactCheckerContract.DefaultMaxAnchors)
            throw new ArgumentOutOfRangeException(nameof(MaxAnchors),
                $"The maximum artifact anchor count must be between 1 and {ArtifactCheckerContract.DefaultMaxAnchors}.");
        if (MaxSampleBytes <= 0 || MaxSampleBytes > ArtifactCheckerContract.MaximumSampleBytes)
            throw new ArgumentOutOfRangeException(nameof(MaxSampleBytes),
                $"The maximum artifact sample size must be between 1 and {ArtifactCheckerContract.MaximumSampleBytes} bytes.");
        return this;
    }
}

public sealed record ArtifactCheckReport(
    string ProjectPath,
    int TotalAnchorCount,
    IReadOnlyList<ArtifactCheckResult> Items,
    int MatchedCount,
    int DriftedCount,
    int MissingCount,
    int InvalidAnchorCount,
    int UnsupportedAdapterCount,
    int UnreadableCount,
    bool IsComplete,
    string? OmissionMessage);

/// <summary>
/// Versioned, host-owned adapter contract for checking one external anchor.
/// Implementations receive parsed metadata and may only report observations.
/// They do not receive graph commands and are never loaded from graph text.
/// </summary>
public interface IArtifactChecker
{
    string AdapterId { get; }

    int ContractVersion { get; }

    ArtifactCheckResult Check(ArtifactCheckRequest request);
}

public sealed class FileSystemArtifactChecker : IArtifactChecker
{
    public string AdapterId => ArtifactCheckerContract.FileSystemAdapterId;

    public int ContractVersion => ArtifactCheckerContract.CurrentVersion;

    public ArtifactCheckResult Check(ArtifactCheckRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.CancellationToken.ThrowIfCancellationRequested();

        var resolvedPath = ResolvePath(request.ProjectPath, request.Anchor.Path);
        if (!File.Exists(resolvedPath))
        {
            return new(request.Anchor.NodeId, request.Anchor.Path, resolvedPath, AdapterId,
                ContractVersion, ArtifactCheckStatus.Missing,
                "The anchored artifact file does not exist.", request.Anchor.Sha256, null, null, false);
        }

        try
        {
            var info = new FileInfo(resolvedPath);
            if ((info.Attributes & FileAttributes.Directory) != 0)
            {
                return new(request.Anchor.NodeId, request.Anchor.Path, resolvedPath, AdapterId,
                    ContractVersion, ArtifactCheckStatus.Unreadable,
                    "The anchored artifact path is a directory, not a file.", request.Anchor.Sha256,
                    null, null, false);
            }

            using var stream = new FileStream(
                resolvedPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 64 * 1_024, options: FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var sample = new MemoryStream(capacity: request.MaxSampleBytes);
            var buffer = new byte[64 * 1_024];
            var totalBytes = 0L;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                request.CancellationToken.ThrowIfCancellationRequested();
                hash.AppendData(buffer, 0, read);
                if (totalBytes < request.MaxSampleBytes)
                {
                    var sampleCount = (int)Math.Min(read, request.MaxSampleBytes - totalBytes);
                    sample.Write(buffer, 0, sampleCount);
                }

                totalBytes += read;
            }

            var actual = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            var status = StringComparer.Ordinal.Equals(actual, request.Anchor.Sha256)
                ? ArtifactCheckStatus.Matched
                : ArtifactCheckStatus.Drifted;
            var message = status == ArtifactCheckStatus.Matched
                ? "The artifact bytes match the anchored SHA-256."
                : "The artifact bytes differ from the anchored SHA-256.";
            return new(request.Anchor.NodeId, request.Anchor.Path, resolvedPath, AdapterId,
                ContractVersion, status, message, request.Anchor.Sha256, actual,
                Convert.ToBase64String(sample.ToArray()), totalBytes > request.MaxSampleBytes);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return new(request.Anchor.NodeId, request.Anchor.Path, resolvedPath, AdapterId,
                ContractVersion, ArtifactCheckStatus.Unreadable,
                $"The anchored artifact could not be read: {exception.Message}", request.Anchor.Sha256,
                null, null, false);
        }
    }

    private static string ResolvePath(string projectPath, string anchorPath)
    {
        var projectDirectory = Path.GetDirectoryName(Path.GetFullPath(projectPath)) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.IsPathRooted(anchorPath)
            ? anchorPath
            : Path.Combine(projectDirectory, anchorPath));
    }
}

public sealed class ArtifactCheckService
{
    private readonly IReadOnlyDictionary<string, IArtifactChecker> _checkers;

    public ArtifactCheckService(IEnumerable<IArtifactChecker>? checkers = null)
    {
        var configured = (checkers ?? [new FileSystemArtifactChecker()]).ToArray();
        if (configured.Length == 0) throw new ArgumentException("At least one artifact checker is required.", nameof(checkers));
        if (configured.Any(checker => checker is null)) throw new ArgumentException("Artifact checkers cannot be null.", nameof(checkers));
        if (configured.Any(checker => checker.ContractVersion != ArtifactCheckerContract.CurrentVersion))
            throw new ArgumentException($"Artifact checkers must implement contract version {ArtifactCheckerContract.CurrentVersion}.", nameof(checkers));
        _checkers = configured.ToDictionary(checker => checker.AdapterId, StringComparer.Ordinal);
        if (_checkers.Count != configured.Length)
            throw new ArgumentException("Artifact checker adapter identifiers must be unique.", nameof(checkers));
    }

    public ArtifactCheckReport Check(
        string projectPath,
        ProjectGraph graph,
        EntityId? nodeId = null,
        ArtifactCheckOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(projectPath);
        ArgumentNullException.ThrowIfNull(graph);
        var validated = (options ?? new ArtifactCheckOptions()).Validate();
        var candidates = graph.Nodes.Where(ArtifactAnchor.IsCandidate);
        if (nodeId is { } selected)
            candidates = candidates.Where(node => node.Id == selected);

        var allCandidates = candidates.ToArray();
        var complete = allCandidates.Length <= validated.MaxAnchors;
        var selectedCandidates = allCandidates.Take(validated.MaxAnchors).ToArray();
        var results = new List<ArtifactCheckResult>(selectedCandidates.Length);
        foreach (var node in selectedCandidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArtifactAnchor.TryRead(node, out var anchor, out var error))
            {
                results.Add(new(node.Id, null, null, null, null, ArtifactCheckStatus.InvalidAnchor,
                    error, null, null, null, false));
                continue;
            }

            if (!_checkers.TryGetValue(anchor!.AdapterId, out var checker))
            {
                results.Add(new(node.Id, anchor.Path, null, anchor.AdapterId, anchor.AdapterVersion,
                    ArtifactCheckStatus.UnsupportedAdapter,
                    $"No adapter named '{anchor.AdapterId}' is registered for contract version {anchor.AdapterVersion}.",
                    anchor.Sha256, null, null, false));
                continue;
            }

            if (checker.ContractVersion != anchor.AdapterVersion)
            {
                results.Add(new(node.Id, anchor.Path, null, anchor.AdapterId, anchor.AdapterVersion,
                    ArtifactCheckStatus.UnsupportedAdapter,
                    $"Adapter '{anchor.AdapterId}' does not support contract version {anchor.AdapterVersion}.",
                    anchor.Sha256, null, null, false));
                continue;
            }

            results.Add(checker.Check(new ArtifactCheckRequest(
                projectPath, anchor, validated.MaxSampleBytes, cancellationToken)));
        }

        var grouped = results.GroupBy(result => result.Status).ToDictionary(group => group.Key, group => group.Count());
        return new(
            projectPath,
            allCandidates.Length,
            results,
            Count(grouped, ArtifactCheckStatus.Matched),
            Count(grouped, ArtifactCheckStatus.Drifted),
            Count(grouped, ArtifactCheckStatus.Missing),
            Count(grouped, ArtifactCheckStatus.InvalidAnchor),
            Count(grouped, ArtifactCheckStatus.UnsupportedAdapter),
            Count(grouped, ArtifactCheckStatus.Unreadable),
            complete,
            complete ? null : $"Only the first {validated.MaxAnchors} of {allCandidates.Length} artifact anchors were checked.");
    }

    private static int Count(IReadOnlyDictionary<ArtifactCheckStatus, int> counts, ArtifactCheckStatus status) =>
        counts.TryGetValue(status, out var count) ? count : 0;
}
