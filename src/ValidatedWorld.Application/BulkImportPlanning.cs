using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValidatedWorld.Core;
using ValidatedWorld.Serialization;
using ValidatedWorld.Validation;

namespace ValidatedWorld.Application;

public static class BulkImportContract
{
    public const int Version = 1;
    public const string Format = "validated-world-bulk-manifest";
    public const int DefaultChunkSize = 100;
    public const int MaximumChunkSize = 5_000;
    public const int MaximumOperations = 1_000_000;
    public const int MaximumManifestLineLength = 1_048_576;
    public const long MaximumManifestBytes = 256L * 1_024 * 1_024;
}

public sealed record BulkImportPlanOptions(
    int ChunkSize = BulkImportContract.DefaultChunkSize,
    string? Cursor = null)
{
    public BulkImportPlanOptions Validate()
    {
        if (ChunkSize is < 1 or > BulkImportContract.MaximumChunkSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(ChunkSize),
                $"Bulk chunk size must be between 1 and {BulkImportContract.MaximumChunkSize}.");
        }

        return this;
    }
}

/// <summary>
/// The first JSONL record in a bulk manifest. Subsequent records are
/// <see cref="OperationDto"/> values.
/// </summary>
public sealed record BulkImportManifestHeader(
    int Version,
    string Format,
    string ProjectId,
    string BaseFingerprint,
    string Intent);

/// <summary>A bounded, read-only page of a validated bulk manifest.</summary>
public sealed record BulkImportPlan(
    string ManifestPath,
    string ProjectId,
    string BaseFingerprint,
    string ManifestFingerprint,
    string Intent,
    int OperationCount,
    int ChunkSize,
    int ChunkIndex,
    int OperationStart,
    int ChunkOperationCount,
    int ChunkCount,
    GraphOperationBatch Operations,
    string? NextCursor);

public sealed partial class ProjectApplication
{
    /// <summary>
    /// Scans a strict local JSONL manifest and returns one bounded operation
    /// chunk. The manifest is never written or applied by this method. Every
    /// chunk boundary must form a valid graph checkpoint, so a caller can
    /// accumulate the returned chunks in one ordinary reviewed change session
    /// and perform one final atomic write.
    /// </summary>
    public BulkImportPlan PlanBulkImport(
        string projectPath,
        string manifestPath,
        BulkImportPlanOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        options = (options ?? new BulkImportPlanOptions()).Validate();
        var project = _store.Load(projectPath);
        var normalizedManifestPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(normalizedManifestPath))
        {
            throw InvalidManifest($"Bulk manifest '{normalizedManifestPath}' does not exist.");
        }

        var fileInfo = new FileInfo(normalizedManifestPath);
        if (fileInfo.Length > BulkImportContract.MaximumManifestBytes)
        {
            throw InvalidManifest(
                $"Bulk manifest exceeds the {BulkImportContract.MaximumManifestBytes} byte bound.");
        }

        var cursor = ParseCursor(options.Cursor);
        if (cursor is not null && cursor.ChunkSize != options.ChunkSize)
        {
            throw InvalidManifest("The bulk continuation cursor was created with a different chunk size.");
        }

        var selectedStart = cursor?.NextOperationIndex ?? 0;
        if (selectedStart < 0 || selectedStart % options.ChunkSize != 0)
        {
            throw InvalidManifest("The bulk continuation cursor has an invalid operation boundary.");
        }

        var selected = new List<GraphOperation>(options.ChunkSize);
        var currentChunk = new List<GraphOperation>(options.ChunkSize);
        var seenIds = new HashSet<EntityId>();
        var currentGraph = project.Graph;
        var operationCount = 0;
        BulkImportManifestHeader? header = null;
        using var fingerprint = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        try
        {
            using var stream = new FileStream(
                normalizedManifestPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 32 * 1_024,
                options: FileOptions.SequentialScan);
            using var reader = new StreamReader(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 32 * 1_024);

            var headerLine = reader.ReadLine();
            if (headerLine is null || string.IsNullOrWhiteSpace(headerLine))
            {
                throw InvalidManifest("A bulk manifest must start with one header record.");
            }

            header = DeserializeHeader(headerLine);
            ValidateHeader(header, project);
            AppendFingerprint(fingerprint, JsonSerializer.SerializeToUtf8Bytes(header, JsonOptions));

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (line is null) break;
                if (line.Length == 0 || line.Length > BulkImportContract.MaximumManifestLineLength)
                {
                    throw InvalidManifest(
                        $"Bulk manifest operation lines must contain 1 to {BulkImportContract.MaximumManifestLineLength} characters.");
                }

                if (operationCount == BulkImportContract.MaximumOperations)
                {
                    throw InvalidManifest(
                        $"Bulk manifest exceeds the {BulkImportContract.MaximumOperations} operation bound.");
                }

                var operation = DeserializeOperation(line);
                if (!seenIds.Add(operation.EntityId))
                {
                    throw InvalidManifest(
                        $"Bulk manifest contains more than one operation for entity '{operation.EntityId.Value}'.");
                }

                AppendFingerprint(
                    fingerprint,
                    JsonSerializer.SerializeToUtf8Bytes(GraphProtocol.ToDto(operation), JsonOptions));

                if (operationCount >= selectedStart &&
                    operationCount < selectedStart + options.ChunkSize)
                {
                    selected.Add(operation);
                }

                currentChunk.Add(operation);
                operationCount++;
                if (currentChunk.Count == options.ChunkSize)
                {
                    currentGraph = ValidateCheckpoint(currentGraph, currentChunk,
                        operationCount / options.ChunkSize - 1);
                    currentChunk.Clear();
                }
            }

            if (header is null) throw InvalidManifest("The bulk manifest header is missing.");
            if (currentChunk.Count > 0)
            {
                currentGraph = ValidateCheckpoint(currentGraph, currentChunk,
                    operationCount / options.ChunkSize);
            }
        }
        catch (BulkImportException)
        {
            throw;
        }
        catch (JsonException exception)
        {
            throw InvalidManifest($"Bulk manifest JSON is invalid: {exception.Message}", exception);
        }
        catch (DecoderFallbackException exception)
        {
            throw InvalidManifest("Bulk manifest must be valid UTF-8 JSONL.", exception);
        }
        catch (GraphOperationException exception)
        {
            throw InvalidManifest($"Bulk manifest operation is invalid: {exception.Message}", exception);
        }

        if (operationCount == 0)
        {
            throw InvalidManifest("Bulk manifest must contain at least one operation.");
        }

        var manifestFingerprint = Convert.ToHexString(fingerprint.GetHashAndReset()).ToLowerInvariant();
        if (cursor is not null &&
            (!StringComparer.Ordinal.Equals(cursor.ManifestFingerprint, manifestFingerprint) ||
             !StringComparer.Ordinal.Equals(cursor.ProjectId, project.Graph.ProjectId.Value) ||
             !StringComparer.Ordinal.Equals(cursor.BaseFingerprint, project.StateFingerprint)))
        {
            throw InvalidManifest(
                "The bulk continuation cursor is stale for this manifest or project state; start a new plan.");
        }

        if (selectedStart >= operationCount)
        {
            throw InvalidManifest("The bulk continuation cursor has no remaining operations.");
        }

        var selectedCount = Math.Min(options.ChunkSize, operationCount - selectedStart);
        if (selected.Count != selectedCount)
        {
            throw InvalidManifest("The bulk manifest changed while it was being planned.");
        }

        var nextStart = selectedStart + selectedCount;
        return new BulkImportPlan(
            normalizedManifestPath,
            project.Graph.ProjectId.Value,
            project.StateFingerprint,
            manifestFingerprint,
            header.Intent,
            operationCount,
            options.ChunkSize,
            selectedStart / options.ChunkSize,
            selectedStart,
            selectedCount,
            (operationCount + options.ChunkSize - 1) / options.ChunkSize,
            new GraphOperationBatch(selected),
            nextStart < operationCount
                ? CreateCursor(manifestFingerprint, project.Graph.ProjectId.Value,
                    project.StateFingerprint, options.ChunkSize, nextStart)
                : null);
    }

    private static readonly JsonSerializerOptions JsonOptions = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = Protocol.CreateJsonOptions();
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    private static BulkImportManifestHeader DeserializeHeader(string line)
    {
        var header = JsonSerializer.Deserialize<BulkImportManifestHeader>(line, JsonOptions);
        return header ?? throw InvalidManifest("The bulk manifest header cannot be null.");
    }

    private static GraphOperation DeserializeOperation(string line)
    {
        var dto = JsonSerializer.Deserialize<OperationDto>(line, JsonOptions)
            ?? throw InvalidManifest("A bulk manifest operation cannot be null.");
        return GraphProtocol.FromDto(dto);
    }

    private static void ValidateHeader(BulkImportManifestHeader header, StoredProject project)
    {
        if (header.Version != BulkImportContract.Version ||
            !StringComparer.Ordinal.Equals(header.Format, BulkImportContract.Format))
        {
            throw InvalidManifest(
                $"Unsupported bulk manifest format; expected {BulkImportContract.Format} version {BulkImportContract.Version}.");
        }

        if (string.IsNullOrWhiteSpace(header.ProjectId) ||
            !StringComparer.Ordinal.Equals(header.ProjectId, project.Graph.ProjectId.Value))
        {
            throw InvalidManifest("The bulk manifest project ID does not match the selected project.");
        }

        if (string.IsNullOrWhiteSpace(header.BaseFingerprint) ||
            !StringComparer.Ordinal.Equals(header.BaseFingerprint, project.StateFingerprint))
        {
            throw InvalidManifest("The bulk manifest base fingerprint does not match the selected project.");
        }

        if (string.IsNullOrWhiteSpace(header.Intent) ||
            header.Intent.Length > GraphLimits.TextMaxLength ||
            header.Intent.Any(char.IsControl))
        {
            throw InvalidManifest("The bulk manifest intent must be bounded, nonempty, and contain no control characters.");
        }
    }

    private ProjectGraph ValidateCheckpoint(
        ProjectGraph currentGraph,
        IReadOnlyList<GraphOperation> operations,
        int chunkIndex)
    {
        var projection = new GraphProjector().Project(
            currentGraph,
            new GraphOperationBatch(operations),
            ValidateGraph);
        if (!projection.Validation.IsValid)
        {
            var diagnostic = projection.Diagnostics.FirstOrDefault()?.Message ??
                "The checkpoint graph is not valid.";
            throw InvalidManifest($"Bulk manifest chunk {chunkIndex} does not form a valid graph checkpoint: {diagnostic}");
        }

        return projection.Graph;
    }

    private static string CreateCursor(
        string manifestFingerprint,
        string projectId,
        string baseFingerprint,
        int chunkSize,
        int nextOperationIndex)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new BulkImportCursor(
            BulkImportContract.Version,
            manifestFingerprint,
            projectId,
            baseFingerprint,
            chunkSize,
            nextOperationIndex), JsonOptions);
        return Convert.ToBase64String(payload)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static BulkImportCursor? ParseCursor(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded)) return null;
        try
        {
            var padded = encoded.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var cursor = JsonSerializer.Deserialize<BulkImportCursor>(
                Convert.FromBase64String(padded), JsonOptions);
            if (cursor is null || cursor.Version != BulkImportContract.Version)
                throw new FormatException();
            return cursor;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or ArgumentException)
        {
            throw InvalidManifest("The bulk continuation cursor is malformed.", exception);
        }
    }

    private static void AppendFingerprint(IncrementalHash fingerprint, byte[] bytes)
    {
        Span<byte> length = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(length, bytes.Length);
        fingerprint.AppendData(length);
        fingerprint.AppendData(bytes);
    }

    private static BulkImportException InvalidManifest(string message, Exception? inner = null) =>
        new(message, inner);

    private sealed record BulkImportCursor(
        int Version,
        string ManifestFingerprint,
        string ProjectId,
        string BaseFingerprint,
        int ChunkSize,
        int NextOperationIndex);

}

public sealed class BulkImportException : Exception
{
    public BulkImportException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }

    public ProjectStorageErrorCode Code => ProjectStorageErrorCode.InvalidBulkManifest;
}
