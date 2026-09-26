using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Alfred.Core;

/// <summary>
/// The shape the model must answer in: <c>{"steps": [{"tool", "arguments"} …]}</c>,
/// where each step is one of the offered tools with that tool's arguments
/// (spec §4, step 6). Built from whichever tools were routed, so adding a tool
/// never touches this file.
/// </summary>
public static class PlanSchema
{
    /// <summary>
    /// A llama.cpp GBNF grammar that only admits valid plans: a tool name the
    /// model was offered, every argument of that tool in a fixed order, and
    /// strings or null (optional arguments only). Whitespace is limited to one
    /// optional space so greedy decoding cannot loop on it.
    /// </summary>
    public static string Grammar(IReadOnlyList<IAssistantTool> tools)
    {
        var gbnf = new StringBuilder();
        var steps = tools.Count == 0 ? "" :
            $"step ({Ws} \",\" {Ws} step){{0,{Plan.MaximumSteps - 1}}}";
        gbnf.AppendLine($"root ::= \"{{\" {Ws} {Literal("\"steps\"")} {Ws} \":\" {Ws} \"[\" {Ws} {(steps.Length > 0 ? $"({steps})?" : "")} {Ws} \"]\" {Ws} \"}}\"");
        if (tools.Count > 0)
            gbnf.AppendLine("step ::= " + string.Join(" | ", tools.Select((_, i) => $"step{i}")));
        for (var i = 0; i < tools.Count; i++)
        {
            var tool = tools[i];
            gbnf.AppendLine($"step{i} ::= \"{{\" {Ws} {Literal("\"tool\"")} {Ws} \":\" {Ws} {Literal(Quote(tool.Name))} {Ws} \",\" {Ws} " +
                            $"{Literal("\"arguments\"")} {Ws} \":\" {Ws} args{i} {Ws} \"}}\"");
            var fields = tool.Arguments.Select(a =>
                $"{Literal(Quote(a.Name))} {Ws} \":\" {Ws} {Value(a)}");
            gbnf.AppendLine($"args{i} ::= \"{{\" {Ws} {string.Join($" {Ws} \",\" {Ws} ", fields)} {Ws} \"}}\"");
        }
        gbnf.AppendLine("string ::= \"\\\"\" char* \"\\\"\"");
        gbnf.AppendLine("char ::= [^\"\\\\\\x00-\\x1F\\x7F] | \"\\\\\" ([\"\\\\/bfnrt] | \"u\" [0-9a-fA-F]{4})");
        gbnf.AppendLine("ws ::= \" \"?");
        return gbnf.ToString();
    }

    private const string Ws = "ws";

    private static string Value(ArgumentSpec argument)
    {
        var value = argument.Choices is { Count: > 0 } choices
            ? "(" + string.Join(" | ", choices.Select(c => Literal(Quote(c)))) + ")"
            : "string";
        return argument.Required ? value : $"({value} | \"null\")";
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text);

    /// <summary>A GBNF literal for exact text.</summary>
    private static string Literal(string text) =>
        "\"" + text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    /// <summary>
    /// The system prompt. Kept short (spec §4: a prompt under about 2,500
    /// tokens); tool-specific guidance lives in each tool's description.
    /// </summary>
    public static string Instructions(IReadOnlyList<IAssistantTool> tools)
    {
        var text = new StringBuilder();
        text.AppendLine("You turn one spoken command into steps for a Windows PC assistant. " +
                        "Each step uses one of the tools below. List steps in the order they should run, one step per action. " +
                        "Copy names from the command as spoken. " +
                        "If the command asks for something no tool does, such as running commands or reading a file's contents, " +
                        "return an empty steps list. Do not open a related app instead. Never invent a tool.");
        text.AppendLine();
        text.AppendLine("Tools:");
        foreach (var tool in tools)
        {
            text.AppendLine($"- {tool.Name}: {tool.Description}");
            foreach (var argument in tool.Arguments)
            {
                var kind = argument.Choices is { Count: > 0 } choices ? "one of " + string.Join(", ", choices) : "string";
                text.AppendLine($"    {argument.Name} ({kind}{(argument.Required ? "" : ", or null if not said")}): {argument.Description}");
            }
        }
        text.AppendLine();
        text.Append("Answer with JSON only: {\"steps\": [{\"tool\": \"<name>\", \"arguments\": {…}}]}, or {\"steps\": []} if no tool fits.");
        return text.ToString();
    }

    /// <summary>
    /// Reads the model's answer. The grammar makes most failures here
    /// unreachable, but model output is untrusted input: never execute a tool
    /// that was not offered, or arguments that don't fit it.
    /// </summary>
    public static Plan Decode(string output, IReadOnlyList<IAssistantTool> tools, string transcript)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(output); }
        catch (JsonException) { throw new AssistantFailure("The model couldn't make sense of that"); }
        if (root?["steps"] is not JsonArray steps) throw new AssistantFailure("The model couldn't make sense of that");
        if (steps.Count > Plan.MaximumSteps) throw new AssistantFailure("That's too many steps for one command");

        var planned = new List<PlannedStep>();
        foreach (var step in steps)
        {
            var name = step?["tool"]?.GetValue<string>();
            var tool = tools.FirstOrDefault(t => t.Name == name)
                ?? throw new AssistantFailure($"The model asked for an unknown tool \"{name}\"");
            if (step?["arguments"] is not JsonObject arguments) throw new AssistantFailure("The model couldn't make sense of that");
            planned.Add(new PlannedStep(tool, Validate(arguments, tool), transcript));
        }
        return new Plan(planned) { ModelOutput = output };
    }

    /// <summary>Keeps only the tool's own arguments, and drops nulls so optional ones read as absent.</summary>
    private static JsonObject Validate(JsonObject arguments, IAssistantTool tool)
    {
        var clean = new JsonObject();
        foreach (var spec in tool.Arguments)
        {
            var node = arguments[spec.Name];
            if (node is null)
            {
                if (spec.Required) throw new AssistantFailure($"The model left out {spec.Name} for {tool.Title.ToLowerInvariant()}");
                continue;
            }
            if (node.GetValueKind() != JsonValueKind.String) throw new AssistantFailure("The model couldn't make sense of that");
            var value = node.GetValue<string>();
            if (spec.Choices is { Count: > 0 } choices && !choices.Contains(value))
                throw new AssistantFailure("The model couldn't make sense of that");
            clean[spec.Name] = value;
        }
        return clean;
    }
}
