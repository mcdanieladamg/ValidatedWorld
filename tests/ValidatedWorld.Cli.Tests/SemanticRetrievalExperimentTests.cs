using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ValidatedWorld.Application;
using ValidatedWorld.Core;
using ValidatedWorld.Persistence.Sqlite;

namespace ValidatedWorld.Cli.Tests;

[Collection("Live OpenAI")]
public sealed class SemanticRetrievalExperimentTests
{
    private const string EmbeddingModel = "text-embedding-3-small";
    private const int EmbeddingDimensions = 512;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    [Fact]
    public void Prerecorded_cases_name_real_entities_and_capture_lexical_misses_before_semantic_scoring()
    {
        using var corpusSet = CorpusSet.Load();
        var fixture = LoadFixture();

        Assert.Equal(1, fixture.SchemaVersion);
        Assert.Equal(5, fixture.TopK);
        Assert.Contains(fixture.Cases, item => item.Corpus == "blueprint");
        Assert.Contains(fixture.Cases, item => item.Corpus == "technical-project");

        foreach (var item in fixture.Cases)
        {
            var corpus = corpusSet[item.Corpus];
            var entityIds = corpus.Documents.Select(document => document.EntityId).ToHashSet(StringComparer.Ordinal);
            Assert.NotEmpty(item.RelevantEntityIds);
            Assert.All(item.RelevantEntityIds, id => Assert.Contains(id, entityIds));
            Assert.All(item.ExpectedLexicalMissEntityIds, id => Assert.Contains(id, item.RelevantEntityIds));

            var lexicalIds = corpus.Queries.SearchRanked(
                    item.Query,
                    new QueryPageRequest(fixture.TopK))
                .Items.Select(hit => hit.EntityId.Value)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(item.ExpectedLexicalMissEntityIds, id => Assert.DoesNotContain(id, lexicalIds));
        }
    }

    [Fact]
    [Trait("Category", "LiveOpenAI")]
    public async Task Embeddings_are_measured_against_the_prerecorded_lexical_baseline()
    {
        var configuration = AiReviewConfiguration.Load();
        if (!configuration.LiveTests || !configuration.IsConfigured) return;

        using var corpusSet = CorpusSet.Load();
        var fixture = LoadFixture();
        var inputs = fixture.Cases.Select(item => item.Query)
            .Concat(corpusSet.Corpora.SelectMany(corpus => corpus.Documents.Select(document => document.Text)))
            .ToArray();
        var request = new EmbeddingRequest(EmbeddingModel, inputs, EmbeddingDimensions, "float");
        var serializedRequest = JsonSerializer.Serialize(request, JsonOptions);
        var artifactDirectory = Path.Combine(Directory.GetCurrentDirectory(), "artifacts");
        Directory.CreateDirectory(artifactDirectory);
        var requestPath = Path.Combine(artifactDirectory, "semantic-retrieval-live-request.json");
        await File.WriteAllTextAsync(requestPath, serializedRequest, new UTF8Encoding(false));

        using var httpClient = new HttpClient
        {
            BaseAddress = new Uri("https://api.openai.com/"),
            Timeout = TimeSpan.FromMinutes(5),
        };
        using var message = new HttpRequestMessage(HttpMethod.Post, "v1/embeddings");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", configuration.ApiKey);
        message.Content = new StringContent(serializedRequest, Encoding.UTF8, "application/json");
        var providerTimer = Stopwatch.StartNew();
        using var response = await httpClient.SendAsync(message);
        var serializedResponse = await response.Content.ReadAsStringAsync();
        providerTimer.Stop();
        Assert.True(response.IsSuccessStatusCode,
            $"Embedding request failed with HTTP {(int)response.StatusCode}: {serializedResponse}");

        using var responseJson = JsonDocument.Parse(serializedResponse);
        var vectors = responseJson.RootElement.GetProperty("data")
            .EnumerateArray()
            .OrderBy(item => item.GetProperty("index").GetInt32())
            .Select(item => item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray())
            .ToArray();
        Assert.Equal(inputs.Length, vectors.Length);
        Assert.All(vectors, vector => Assert.Equal(EmbeddingDimensions, vector.Length));

        var queryVectors = vectors.Take(fixture.Cases.Count).ToArray();
        var documentVectors = vectors.Skip(fixture.Cases.Count).ToArray();
        var documentOffset = 0;
        var corpusVectors = new Dictionary<string, IReadOnlyList<float[]>>(StringComparer.Ordinal);
        foreach (var corpus in corpusSet.Corpora)
        {
            corpusVectors.Add(corpus.Name, documentVectors.Skip(documentOffset).Take(corpus.Documents.Count).ToArray());
            documentOffset += corpus.Documents.Count;
        }

        var lexicalTimer = Stopwatch.StartNew();
        var evaluations = fixture.Cases.Select((item, index) =>
        {
            var corpus = corpusSet[item.Corpus];
            var lexicalRanking = corpus.Queries.SearchRanked(item.Query, new QueryPageRequest(1_000))
                .Items.Select(hit => hit.EntityId.Value).ToArray();
            var lexical = lexicalRanking.Take(fixture.TopK).ToArray();
            var semanticRanking = corpus.Documents
                .Select((document, documentIndex) => new
                {
                    document.EntityId,
                    Similarity = Cosine(queryVectors[index], corpusVectors[item.Corpus][documentIndex]),
                })
                .OrderByDescending(item => item.Similarity)
                .ThenBy(item => item.EntityId, StringComparer.Ordinal)
                .Select(item => item.EntityId)
                .ToArray();
            var semantic = semanticRanking.Take(fixture.TopK).ToArray();
            var hybrid = ReciprocalRankFusion(lexicalRanking, semanticRanking, fixture.TopK);
            return new CaseResult(
                item.Corpus,
                item.Query,
                item.RelevantEntityIds,
                lexical,
                semantic,
                hybrid,
                Recall(item.RelevantEntityIds, lexical),
                Recall(item.RelevantEntityIds, semantic),
                Recall(item.RelevantEntityIds, hybrid),
                FalsePositives(item.RelevantEntityIds, lexical),
                FalsePositives(item.RelevantEntityIds, semantic),
                FalsePositives(item.RelevantEntityIds, hybrid));
        }).ToArray();
        lexicalTimer.Stop();

        var usage = responseJson.RootElement.GetProperty("usage");
        var inputTokens = usage.TryGetProperty("prompt_tokens", out var promptTokens)
            ? promptTokens.GetInt32()
            : usage.GetProperty("total_tokens").GetInt32();
        var lexicalRecall = evaluations.Average(item => item.LexicalRecall);
        var semanticRecall = evaluations.Average(item => item.SemanticRecall);
        var hybridRecall = evaluations.Average(item => item.HybridRecall);
        var result = new ExperimentResult(
            DateOnly.FromDateTime(DateTime.UtcNow),
            EmbeddingModel,
            EmbeddingDimensions,
            fixture.TopK,
            corpusSet.Corpora.Sum(corpus => corpus.Documents.Count),
            evaluations,
            lexicalRecall,
            semanticRecall,
            hybridRecall,
            evaluations.Sum(item => item.LexicalFalsePositives),
            evaluations.Sum(item => item.SemanticFalsePositives),
            evaluations.Sum(item => item.HybridFalsePositives),
            lexicalTimer.Elapsed.TotalMilliseconds,
            providerTimer.Elapsed.TotalMilliseconds,
            inputTokens,
            inputTokens * 0.02m / 1_000_000m,
            (long)corpusSet.Corpora.Sum(corpus => corpus.Documents.Count) * EmbeddingDimensions * sizeof(float),
            "Provider embeddings send every indexed entity text and each query to OpenAI.",
            "Semantic retrieval is unavailable offline; deterministic lexical retrieval remains available.",
            "A per-entity content fingerprint can reuse unchanged vectors; a model or dimensions change invalidates all vectors.",
            hybridRecall - lexicalRecall >= 0.20 && hybridRecall >= 0.70);
        var resultPath = Path.Combine(artifactDirectory, "semantic-retrieval-experiment-result.json");
        await File.WriteAllTextAsync(resultPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));

        Assert.True(File.Exists(requestPath));
        using var loggedRequest = JsonDocument.Parse(await File.ReadAllTextAsync(requestPath));
        Assert.Equal(inputs.Length, loggedRequest.RootElement.GetProperty("input").GetArrayLength());
        Assert.Contains(fixture.Cases, item => item.Corpus == "blueprint");
        Assert.Contains(fixture.Cases, item => item.Corpus == "technical-project");
    }

    private static double Recall(IReadOnlyCollection<string> relevant, IReadOnlyCollection<string> retrieved) =>
        relevant.Count == 0 ? 1 : relevant.Count(retrieved.Contains) / (double)relevant.Count;

    private static int FalsePositives(IReadOnlyCollection<string> relevant, IReadOnlyCollection<string> retrieved) =>
        retrieved.Count(id => !relevant.Contains(id));

    private static IReadOnlyList<string> ReciprocalRankFusion(
        IReadOnlyList<string> lexical,
        IReadOnlyList<string> semantic,
        int limit)
    {
        const int rankConstant = 60;
        var scores = new Dictionary<string, double>(StringComparer.Ordinal);
        AddRanking(lexical);
        AddRanking(semantic);
        return scores.OrderByDescending(item => item.Value)
            .ThenBy(item => item.Key, StringComparer.Ordinal)
            .Take(limit)
            .Select(item => item.Key)
            .ToArray();

        void AddRanking(IReadOnlyList<string> ranking)
        {
            for (var index = 0; index < ranking.Count; index++)
            {
                scores.TryGetValue(ranking[index], out var score);
                scores[ranking[index]] = score + 1d / (rankConstant + index + 1);
            }
        }
    }

    private static double Cosine(IReadOnlyList<float> left, IReadOnlyList<float> right)
    {
        double dot = 0;
        double leftNorm = 0;
        double rightNorm = 0;
        for (var index = 0; index < left.Count; index++)
        {
            dot += left[index] * right[index];
            leftNorm += left[index] * left[index];
            rightNorm += right[index] * right[index];
        }

        return dot / (Math.Sqrt(leftNorm) * Math.Sqrt(rightNorm));
    }

    private static ExperimentFixture LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "SemanticRetrievalExperimentCases.json");
        return JsonSerializer.Deserialize<ExperimentFixture>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException("The semantic retrieval experiment fixture is empty.");
    }

    private sealed class CorpusSet : IDisposable
    {
        private readonly string _temporaryRoot;

        private CorpusSet(string temporaryRoot, IReadOnlyList<Corpus> corpora)
        {
            _temporaryRoot = temporaryRoot;
            Corpora = corpora;
        }

        public IReadOnlyList<Corpus> Corpora { get; }
        public Corpus this[string name] => Corpora.Single(corpus => corpus.Name == name);

        public static CorpusSet Load()
        {
            var repositoryRoot = FindRepositoryRoot();
            var temporaryRoot = Path.Combine(Path.GetTempPath(), $"ValidatedWorld.T20-{Guid.NewGuid():N}");
            Directory.CreateDirectory(temporaryRoot);
            var application = new ProjectApplication(new SqliteProjectStore());
            var samplePath = Path.Combine(temporaryRoot, "technical-project.vw.db");
            application.CreateSample(SampleProjectCatalog.TechnicalProject, samplePath);
            var blueprintPath = Path.Combine(repositoryRoot, "ValidatedWorld.Blueprint.vw.db");
            return new CorpusSet(temporaryRoot,
            [
                CreateCorpus("blueprint", application, blueprintPath),
                CreateCorpus("technical-project", application, samplePath),
            ]);
        }

        public void Dispose()
        {
            if (Directory.Exists(_temporaryRoot)) Directory.Delete(_temporaryRoot, recursive: true);
        }

        private static Corpus CreateCorpus(string name, ProjectApplication application, string path)
        {
            var queries = application.Queries(path);
            var graph = application.Load(path).Graph;
            var documents = graph.Nodes.Select(node => new EntityDocument(
                    node.Id.Value,
                    $"node id {node.Id.Value}; kind {node.Kind}; tags {string.Join(' ', node.Tags)}; text {node.Text}"))
                .Concat(graph.Edges.Select(edge => new EntityDocument(
                    edge.Id.Value,
                    $"edge id {edge.Id.Value}; relationship {edge.Relationship}; source {edge.Source.Value}; " +
                    $"target {edge.Target.Value}; tags {string.Join(' ', edge.Tags)}; rationale {edge.Rationale}")))
                .OrderBy(document => document.EntityId, StringComparer.Ordinal)
                .ToArray();
            return new Corpus(name, queries, documents);
        }

        private static string FindRepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory);
                 directory is not null;
                 directory = directory.Parent)
            {
                if (File.Exists(Path.Combine(directory.FullName, "ValidatedWorld.slnx")))
                    return directory.FullName;
            }

            throw new InvalidOperationException("Could not locate the repository root.");
        }
    }

    private sealed record ExperimentFixture(
        int SchemaVersion,
        int TopK,
        IReadOnlyList<ExperimentCase> Cases);

    private sealed record ExperimentCase(
        string Corpus,
        string Query,
        IReadOnlyList<string> RelevantEntityIds,
        IReadOnlyList<string> ExpectedLexicalMissEntityIds);

    private sealed record Corpus(string Name, ProjectQueries Queries, IReadOnlyList<EntityDocument> Documents);
    private sealed record EntityDocument(string EntityId, string Text);
    private sealed record EmbeddingRequest(
        string Model,
        IReadOnlyList<string> Input,
        int Dimensions,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat);

    private sealed record CaseResult(
        string Corpus,
        string Query,
        IReadOnlyList<string> RelevantEntityIds,
        IReadOnlyList<string> LexicalTopK,
        IReadOnlyList<string> SemanticTopK,
        IReadOnlyList<string> HybridTopK,
        double LexicalRecall,
        double SemanticRecall,
        double HybridRecall,
        int LexicalFalsePositives,
        int SemanticFalsePositives,
        int HybridFalsePositives);

    private sealed record ExperimentResult(
        DateOnly RunDate,
        string Model,
        int Dimensions,
        int TopK,
        int IndexedEntityCount,
        IReadOnlyList<CaseResult> Cases,
        double MeanLexicalRecall,
        double MeanSemanticRecall,
        double MeanHybridRecall,
        int LexicalFalsePositives,
        int SemanticFalsePositives,
        int HybridFalsePositives,
        double LexicalElapsedMilliseconds,
        double ProviderElapsedMilliseconds,
        int InputTokens,
        decimal EstimatedUsdAtTwoCentsPerMillionInputTokens,
        long RawFloatIndexBytes,
        string Privacy,
        string OfflineBehavior,
        string Invalidation,
        bool MaterialRecallBenefit);
}
