namespace ValidatedWorld.Cli.Tests;

/// <summary>
/// Paid provider checks must never overlap one another or the rest of the test
/// suite when their effective configuration opts them in.
/// </summary>
[CollectionDefinition("Live OpenAI", DisableParallelization = true)]
public sealed class LiveOpenAiCollection;
