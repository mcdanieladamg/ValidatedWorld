using Microsoft.Extensions.Configuration;
using ValidatedWorld.Application;

namespace ValidatedWorld.Cli;

public sealed record AiReviewConfiguration(
    bool Enabled,
    string Provider,
    string Model,
    int TimeoutSeconds,
    bool LiveTests,
    string? ApiKey)
{
    public const bool DefaultEnabled = true;
    public const string DefaultProvider = "openai";
    public const string DefaultModel = "gpt-5.6-terra";
    public const int DefaultTimeoutSeconds = 1200;
    public const bool DefaultLiveTests = false;

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey) &&
        StringComparer.OrdinalIgnoreCase.Equals(Provider, DefaultProvider);

    public bool IsEffectivelyEnabled => Enabled && IsConfigured;

    public static AiReviewConfiguration Load()
    {
        var configuration = new ConfigurationBuilder()
            .AddUserSecrets(typeof(CliRunner).Assembly, optional: true)
            .AddEnvironmentVariables("VW_")
            .Build();
        var section = configuration.GetSection("AiReview");
        var apiKey = section["OpenAI:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey)) apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return new AiReviewConfiguration(
            Boolean(section["Enabled"], DefaultEnabled, "AiReview:Enabled"),
            Text(section["Provider"], DefaultProvider),
            Text(section["Model"], DefaultModel),
            PositiveInteger(section["TimeoutSeconds"], DefaultTimeoutSeconds, "AiReview:TimeoutSeconds"),
            Boolean(section["LiveTests"], DefaultLiveTests, "AiReview:LiveTests"),
            apiKey);
    }

    public SemanticReviewRuntimeOptions RuntimeOptions() => new(
        IsEffectivelyEnabled,
        IsConfigured,
        Provider,
        Model,
        TimeoutSeconds,
        LiveTests);

    public ISemanticReviewProvider? CreateProvider(
        HttpClient httpClient,
        string requestLogPath,
        string responseLogPath)
    {
        if (!IsEffectivelyEnabled) return null;
        Action<string>? requestLogger = null;
        Action<string>? responseLogger = null;
        if (LiveTests)
        {
            requestLogger = serialized => WriteLog(requestLogPath, serialized);
            responseLogger = serialized => WriteLog(responseLogPath, serialized);
        }
        return new OpenAiResponsesSemanticReviewProvider(
            httpClient,
            ApiKey!,
            Model,
            TimeSpan.FromSeconds(TimeoutSeconds),
            serializedRequestLogger: requestLogger,
            serializedResponseLogger: responseLogger);
    }

    private static void WriteLog(string path, string value)
    {
        var fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, value, new System.Text.UTF8Encoding(false));
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
