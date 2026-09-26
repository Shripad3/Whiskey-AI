using System.Text.Json.Nodes;

namespace Alfred.Core;

public sealed record PlannedStep(IAssistantTool Tool, JsonObject Arguments, string? Transcript)
{
    public string Target()
    {
        try { return Tool.Target(Arguments); }
        catch (ToolException) { return ""; }
    }

    public Task<ToolResult> ExecuteAsync(bool isFinal, CancellationToken cancellationToken) =>
        Tool.ExecuteAsync(Arguments, new ToolContext(Transcript, isFinal), cancellationToken);
}

/// <summary>
/// The engine's output: an explicit, ordered list of steps. Empty means no
/// tool fits.
/// </summary>
public sealed record Plan(IReadOnlyList<PlannedStep> Steps)
{
    public const int MaximumSteps = 5;

    /// <summary>True when matched without the model (<see cref="DirectMatcher"/>).</summary>
    public bool IsDirect { get; init; }
    /// <summary>A text-only answer, with no steps (<see cref="SmallTalk"/>).</summary>
    public string? Reply { get; init; }
    /// <summary>The model's raw output, for the console and the intent corpus.</summary>
    public string? ModelOutput { get; init; }
    /// <summary>The tools the model was shown, when it was asked.</summary>
    public IReadOnlyList<string>? Routed { get; init; }
}

/// <summary>A failure the user sees, optionally pointing at the Settings page that fixes it.</summary>
public sealed class AssistantFailure(string message, string? settingsPage = null) : Exception(message)
{
    public string? SettingsPage { get; } = settingsPage;
}
