using System.Text.Json;

namespace ValidatedWorld.Cli;

public sealed class AiAssistantShell(
    IAuthoringAgentProvider provider,
    AuthoringToolHost tools,
    TextReader input,
    TextWriter output,
    TextWriter error,
    int maxToolCallsPerTurn = AiAuthoringConfiguration.DefaultMaxToolCallsPerTurn,
    CancellationToken cancellationToken = default)
{
    public const int MaximumUserInputCharacters = 16_384;
    private string? _previousResponseId;

    public async Task<int> RunAsync()
    {
        await output.WriteLineAsync($"ValidatedWorld AI assistant — {provider.Provider}/{provider.Model}");
        await output.WriteLineAsync($"Project path: {tools.Path}");
        await output.WriteLineAsync("Describe a project or change. Type 'exit' to leave; unresolved changes stay only in this process.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteAsync("you> ");
            await output.FlushAsync(cancellationToken);
            var line = await input.ReadLineAsync(cancellationToken);
            if (line is null || StringComparer.OrdinalIgnoreCase.Equals(line.Trim(), "exit")) break;
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.Length > MaximumUserInputCharacters)
            {
                await error.WriteLineAsync($"error[input-limit]: One message cannot exceed {MaximumUserInputCharacters} characters.");
                continue;
            }
            if (StringComparer.OrdinalIgnoreCase.Equals(line.Trim(), "discard"))
            {
                if (tools.Session is null)
                {
                    await output.WriteLineAsync("There is no active change to discard.");
                }
                else
                {
                    var result = await tools.ExecuteAsync("discard_change", Empty(), cancellationToken);
                    await output.WriteLineAsync(result.Output);
                    _previousResponseId = null;
                }
                continue;
            }

            try
            {
                await RunTurn(JsonSerializer.SerializeToElement(line, CliJson.Options));
            }
            catch (AuthoringProviderException exception)
            {
                await error.WriteLineAsync($"error[ai-authoring-{exception.Code}]: {exception.Message}");
                await error.WriteLineAsync(
                    "warning[conversation-reset]: Provider conversation context was reset; any in-memory change session remains available to inspect, continue, or discard.");
                _previousResponseId = null;
            }
            catch (AuthoringToolCallLimitException exception)
            {
                await error.WriteLineAsync($"error[ai-authoring-tool-limit]: {exception.Message}");
                await error.WriteLineAsync(
                    "warning[conversation-reset]: Provider conversation context was reset; any in-memory change session remains available to inspect, continue, or discard.");
                _previousResponseId = null;
            }
        }

        if (tools.Session is { } session)
        {
            await error.WriteLineAsync(
                $"warning[session-loss]: session={session.Reference.SessionId} operations={session.Operations.Operations.Count} " +
                "Exiting now will permanently lose this unresolved in-memory change session.");
        }
        return CliRunner.SuccessExitCode;
    }

    private async Task RunTurn(JsonElement turnInput)
    {
        var toolCallCount = 0;
        while (true)
        {
            var response = await provider.RespondAsync(
                new AuthoringAgentRequest(turnInput, _previousResponseId, AuthoringToolHost.Definitions),
                cancellationToken);
            _previousResponseId = response.ResponseId;
            if (!string.IsNullOrWhiteSpace(response.Text))
                await output.WriteLineAsync($"assistant> {response.Text}");
            if (response.ToolCall is null) return;
            if (toolCallCount == maxToolCallsPerTurn)
                throw new AuthoringToolCallLimitException(maxToolCallsPerTurn);
            toolCallCount++;

            var execution = await tools.ExecuteAsync(
                response.ToolCall.Name,
                response.ToolCall.Arguments,
                cancellationToken);
            var toolOutput = execution.Output;
            if (execution.ApprovalRequested)
            {
                await output.WriteLineAsync();
                await output.WriteLineAsync("Exact proposal for human review");
                await output.WriteLineAsync(tools.HumanPreview());
                await output.WriteAsync("Approve this exact proposal and record every shown affected node/context as reviewed? [yes/no] ");
                await output.FlushAsync(cancellationToken);
                var answer = await input.ReadLineAsync(cancellationToken);
                if (StringComparer.OrdinalIgnoreCase.Equals(answer?.Trim(), "yes"))
                {
                    toolOutput = CliJson.Serialize(tools.ApproveRequested());
                }
                else
                {
                    tools.DeclineRequested();
                    toolOutput = CliJson.Serialize(new
                    {
                        approved = false,
                        message = "The human declined or did not provide the exact 'yes' confirmation. Do not write.",
                    });
                }
            }

            turnInput = JsonSerializer.SerializeToElement(new[]
            {
                new
                {
                    type = "function_call_output",
                    call_id = response.ToolCall.CallId,
                    output = toolOutput,
                },
            }, CliJson.Options);
        }
    }

    private static JsonElement Empty() => JsonSerializer.SerializeToElement(new { }, CliJson.Options);

    private sealed class AuthoringToolCallLimitException(int maximum)
        : InvalidOperationException($"The authoring turn exceeded {maximum} tool calls.");
}
