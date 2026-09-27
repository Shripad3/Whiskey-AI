namespace Alfred.Core;

/// <summary>A local language model that answers under a grammar (llama.cpp in the app, a fake in tests).</summary>
public interface ILanguageModel
{
    /// <summary>Null when the model can run; otherwise the reason it cannot.</summary>
    AssistantFailure? UnavailableReason();

    /// <summary>Loads the model if it isn't loaded, before the planning time limit starts.</summary>
    Task PrepareAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Greedy, grammar-constrained completion of one system + user exchange.</summary>
    Task<string> CompleteAsync(string instructions, string command, string grammar, CancellationToken cancellationToken);
}

/// <summary>
/// Transcript in, planned tool calls out (spec §4, "The planning pipeline").
/// Each stage before the model exists because the model got that case wrong
/// on the Mac.
/// </summary>
public sealed class LocalModelEngine(ILanguageModel model, TimeSpan? timeout = null)
{
    public TimeSpan Timeout { get; } = timeout ?? TimeSpan.FromSeconds(10);

    public async Task<Plan> PlanAsync(string transcript, IReadOnlyList<IAssistantTool> tools, CancellationToken cancellationToken)
    {
        transcript = SpokenText.Clean(transcript);
        // Routines were matched by the coordinator before this.
        if ((SmallTalk.Reply(transcript) ?? ContentRequests.Refusal(transcript)) is { } reply)
            return new Plan([]) { IsDirect = true, Reply = reply };
        if (tools.Count == 0) return new Plan([]);
        if (DirectMatcher.Match(transcript, tools) is { } direct) return direct;

        if (model.UnavailableReason() is { } reason) throw reason;
        var routed = ToolRouter.Relevant(transcript, tools);
        // Loading can take seconds on first use; only the planning itself is timed.
        await model.PrepareAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(Timeout);
        string output;
        try
        {
            // One command, one exchange: nothing carries over between commands.
            output = await model.CompleteAsync(PlanSchema.Instructions(routed), transcript, PlanSchema.Grammar(routed), limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new AssistantFailure("The model took too long");
        }
        return PlanSchema.Decode(output, routed, transcript) with { Routed = routed.Select(t => t.Name).ToList() };
    }
}

/// <summary>The outcome of running a plan: one line per step that ran.</summary>
public sealed record PlanOutcome(IReadOnlyList<ToolResult> Results, string? Failure)
{
    public bool Succeeded => Failure is null;
}

public static class PlanRunner
{
    /// <summary>Runs the steps in order. A failing step stops the rest (spec §4).</summary>
    public static async Task<PlanOutcome> RunAsync(Plan plan, CancellationToken cancellationToken)
    {
        var results = new List<ToolResult>();
        for (var i = 0; i < plan.Steps.Count; i++)
        {
            try
            {
                results.Add(await plan.Steps[i].ExecuteAsync(isFinal: i == plan.Steps.Count - 1, cancellationToken));
            }
            catch (ToolException error)
            {
                return new PlanOutcome(results, error.Message);
            }
        }
        return new PlanOutcome(results, null);
    }
}
