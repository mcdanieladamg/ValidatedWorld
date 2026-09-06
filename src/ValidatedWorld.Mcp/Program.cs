using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using ValidatedWorld.Application;
using ValidatedWorld.Persistence.Sqlite;
using ValidatedWorld.Mcp;

var options = McpHostOptions.Parse(args);
if (options.ShowHelp)
{
    await Console.Error.WriteLineAsync(McpHostOptions.HelpText);
    return 0;
}

var reviewConfiguration = McpSemanticReviewConfiguration.Load();
using var httpClient = new HttpClient { BaseAddress = new Uri("https://api.openai.com/") };
var builder = Host.CreateApplicationBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole(consoleLogOptions =>
{
    consoleLogOptions.LogToStandardErrorThreshold = LogLevel.Trace;
});
builder.Services.AddSingleton<SqliteProjectStore>();
builder.Services.AddSingleton<ValidatedWorld.Application.IProjectStore>(serviceProvider =>
    serviceProvider.GetRequiredService<SqliteProjectStore>());
builder.Services.AddSingleton(new ProjectApplication(
    new SqliteProjectStore(),
    semanticReviewProvider: reviewConfiguration.CreateProvider(httpClient),
    semanticReviewOptions: reviewConfiguration.RuntimeOptions()));
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<McpProjectService>();
builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithTools<McpTools>();

await builder.Build().RunAsync();
return 0;
