using Microsoft.Extensions.Configuration;
using ValidatedWorld.Application;

namespace ValidatedWorld.Mcp;

internal sealed record McpSemanticReviewConfiguration(
    bool Enabled,
    string Provider,
    string Model,
    int TimeoutSeconds,
    bool LiveTests,
    string? ApiKey,
    int MaxRequestBytes = 1_000_000,
    int MaxRequestItems = 20_000,
    int MaxRequestTokens = 250_000)
{
    private const bool DefaultEnabled = true;
    private const string DefaultProvider = "openai";
    private const string DefaultModel = "gpt-5.6-terra";
    private const int DefaultTimeoutSeconds = 1200;

    private bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) &&
        StringComparer.OrdinalIgnoreCase.Equals(Provider, DefaultProvider);

    private bool IsEffectivelyEnabled => Enabled && IsConfigured;

    public static McpSemanticReviewConfiguration Load()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(McpAssembly).Assembly, optional: true)
            .AddEnvironmentVariables("VW_")
            .Build();
        var section = configuration.GetSection("AiReview");
        var apiKey = section["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return new McpSemanticReviewConfiguration(
            Boolean(section["Enabled"], DefaultEnabled, "AiReview:Enabled"),
            Text(section["Provider"], DefaultProvider),
            Text(section["Model"], DefaultModel),
            PositiveInteger(section["TimeoutSeconds"], DefaultTimeoutSeconds, "AiReview:TimeoutSeconds"),
            Boolean(section["LiveTests"], false, "AiReview:LiveTests"),
            apiKey,
            PositiveInteger(section["MaxRequestBytes"], 1_000_000, "AiReview:MaxRequestBytes"),
            PositiveInteger(section["MaxRequestItems"], 20_000, "AiReview:MaxRequestItems"),
            PositiveInteger(section["MaxRequestTokens"], 250_000, "AiReview:MaxRequestTokens"));
    }

    public SemanticReviewRuntimeOptions RuntimeOptions() => new(
        IsEffectivelyEnabled,
        IsConfigured,
        Provider,
        Model,
        TimeoutSeconds,
        LiveTests,
        MaxRequestBytes,
        MaxRequestItems,
        MaxRequestTokens);

    public ISemanticReviewProvider? CreateProvider(HttpClient httpClient)
    {
        if (!IsEffectivelyEnabled) return null;
        return new OpenAiResponsesSemanticReviewProvider(httpClient, ApiKey!, Model,
            TimeSpan.FromSeconds(TimeoutSeconds));
    }

    private static string Text(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;

    private static bool Boolean(string? value, bool fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return bool.TryParse(value, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"Configuration '{name}' must be true or false.");
    }

    private static int PositiveInteger(string? value, int fallback, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return fallback;
        return int.TryParse(value, System.Globalization.NumberStyles.None,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed > 0
            ? parsed
            : throw new InvalidOperationException($"Configuration '{name}' must be a positive integer.");
    }
}
