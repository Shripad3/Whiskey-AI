using Alfred.Core;

namespace Alfred.Core.Tests;

public class PlanSchemaTests
{
    private readonly IReadOnlyList<IAssistantTool> _tools = Setup.Standard().Tools;

    [Fact]
    public void GrammarOffersOnlyTheRoutedTools()
    {
        var grammar = PlanSchema.Grammar([_tools.Tool("openURL")]);
        Assert.Contains("\"\\\"openURL\\\"\"", grammar);
        Assert.DoesNotContain("openApp", grammar);
        // The optional browser may be null; the required url may not.
        Assert.Contains("\"\\\"browser\\\"\" ws \":\" ws (string | \"null\")", grammar);
        Assert.Contains("\"\\\"url\\\"\" ws \":\" ws string", grammar);
    }

    [Fact]
    public void GrammarCapsTheSteps() => Assert.Contains("{0,4}", PlanSchema.Grammar(_tools));

    [Fact]
    public void GrammarWithNoToolsOnlyAllowsAnEmptyPlan() => Assert.DoesNotContain("step ::=", PlanSchema.Grammar([]));

    [Fact]
    public void GrammarChoicesAreLiterals()
    {
        var tool = new ChoiceTool();
        Assert.Contains("(\"\\\"up\\\"\" | \"\\\"down\\\"\")", PlanSchema.Grammar([tool]));
    }

    [Fact]
    public void InstructionsDescribeEachTool()
    {
        var text = PlanSchema.Instructions(_tools);
        Assert.Contains("- openApp: Launch an installed Windows app", text);
        Assert.Contains("browser (string, or null if not said)", text);
    }

    [Fact]
    public void DecodesOrderedSteps()
    {
        const string output = """
            {"steps": [{"tool": "openApp", "arguments": {"appName": "Google Chrome"}},
                       {"tool": "openURL", "arguments": {"url": "https://www.youtube.com", "browser": null}}]}
            """;
        var plan = PlanSchema.Decode(output, _tools, "open chrome and youtube");
        Assert.Equal(["openApp", "openURL"], plan.Steps.Select(s => s.Tool.Name));
        Assert.Equal(["Google Chrome", "www.youtube.com"], plan.Steps.Select(s => s.Target()));
        Assert.False(plan.Steps[1].Arguments.ContainsKey("browser"));
    }

    [Fact]
    public void EmptyStepsMeansNoTool() => Assert.Empty(PlanSchema.Decode("""{"steps": []}""", _tools, "").Steps);

    [Theory]
    [InlineData("""{"steps": [{"tool": "runCommand", "arguments": {"command": "rm -rf"}}]}""")]
    [InlineData("""{"steps": [{"tool": "openApp", "arguments": {}}]}""")]
    [InlineData("""{"steps": [{"tool": "openApp", "arguments": {"appName": 3}}]}""")]
    [InlineData("""{"steps": "open spotify"}""")]
    [InlineData("not json")]
    public void MalformedOrUnofferedStepsAreRejected(string output) =>
        Assert.Throws<AssistantFailure>(() => PlanSchema.Decode(output, _tools, ""));

    [Fact]
    public void ToolNotOfferedThisTimeIsRejected() =>
        Assert.Throws<AssistantFailure>(() => PlanSchema.Decode(
            """{"steps": [{"tool": "openApp", "arguments": {"appName": "Spotify"}}]}""", [_tools.Tool("webSearch")], ""));

    [Fact]
    public void ExtraArgumentsAreDropped()
    {
        var plan = PlanSchema.Decode("""{"steps": [{"tool": "webSearch", "arguments": {"query": "x", "engine": "Bing"}}]}""", _tools, "");
        Assert.Equal("""{"query":"x"}""", plan.Steps[0].Arguments.ToJsonString());
    }

    [Fact]
    public void DisabledToolIsNotRegistered()
    {
        using var world = new World();
        var registry = ToolRegistry.Standard(world.Services, isEnabled: name => name != "openURL");
        Assert.DoesNotContain("openURL", registry.EnabledTools().Select(t => t.Name));
        Assert.Equal(registry.Tools.Count - 1, registry.EnabledTools().Count);
    }

    private sealed record ChoiceArguments(string Direction);

    private sealed class ChoiceTool : AssistantTool<ChoiceArguments>
    {
        public override string Name => "volume";
        public override string Title => "Volume";
        public override string Description => "";
        public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>();
        public override IReadOnlyList<ArgumentSpec> Arguments { get; } = [new("direction", "", Choices: ["up", "down"])];
        public override bool RequiresNetwork => false;
        protected override string Target(ChoiceArguments arguments) => arguments.Direction;
        protected override Task<ToolResult> ExecuteAsync(ChoiceArguments arguments, ToolContext context, CancellationToken cancellationToken) =>
            Task.FromResult<ToolResult>("");
    }
}

public class EngineTests
{
    private readonly (IReadOnlyList<IAssistantTool> Tools, FakeLauncher Launcher, FakeCatalog Catalog) _setup = Setup.Standard();

    [Fact]
    public async Task DirectMatchSkipsTheModel()
    {
        var model = new FakeModel("{\"steps\": []}");
        var plan = await new LocalModelEngine(model).PlanAsync("Open YouTube.", _setup.Tools, CancellationToken.None);
        Assert.True(plan.IsDirect);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task SmallTalkIsARepliedNotPlanned()
    {
        var model = new FakeModel("{\"steps\": []}");
        var plan = await new LocalModelEngine(model).PlanAsync("hi how are you", _setup.Tools, CancellationToken.None);
        Assert.NotNull(plan.Reply);
        Assert.Empty(plan.Steps);
        Assert.Equal(0, model.Calls);
    }

    [Fact]
    public async Task ModelSeesOnlyRoutedTools()
    {
        var model = new FakeModel("""{"steps": [{"tool": "webSearch", "arguments": {"query": "f1 results"}}]}""");
        var plan = await new LocalModelEngine(model).PlanAsync("find out who won the f1 race", _setup.Tools, CancellationToken.None);
        Assert.Equal(["webSearch"], plan.Routed);
        Assert.DoesNotContain("openApp", model.LastGrammar);
        Assert.Equal("webSearch", plan.Steps.Single().Tool.Name);
    }

    [Fact]
    public async Task SlowModelTimesOut()
    {
        var model = new FakeModel("{\"steps\": []}") { Delay = TimeSpan.FromSeconds(5) };
        var engine = new LocalModelEngine(model, TimeSpan.FromMilliseconds(50));
        var failure = await Assert.ThrowsAsync<AssistantFailure>(() => engine.PlanAsync("bring up discord", _setup.Tools, CancellationToken.None));
        Assert.Equal("The model took too long", failure.Message);
    }

    [Fact]
    public async Task FailingStepStopsTheRest()
    {
        var model = new FakeModel("""
            {"steps": [{"tool": "openApp", "arguments": {"appName": "photoshop"}},
                       {"tool": "openURL", "arguments": {"url": "https://www.youtube.com", "browser": null}}]}
            """);
        var plan = await new LocalModelEngine(model).PlanAsync("open photoshop and then youtube", _setup.Tools, CancellationToken.None);
        var outcome = await PlanRunner.RunAsync(plan, CancellationToken.None);
        Assert.Equal("I couldn't find an app called \"photoshop\"", outcome.Failure);
        Assert.Empty(_setup.Launcher.Launched);
    }

    [Fact]
    public async Task CompoundPlanRunsInOrder()
    {
        var model = new FakeModel("""
            {"steps": [{"tool": "openApp", "arguments": {"appName": "Spotify"}},
                       {"tool": "openURL", "arguments": {"url": "https://mail.google.com", "browser": null}}]}
            """);
        var plan = await new LocalModelEngine(model).PlanAsync("open spotify and then open gmail", _setup.Tools, CancellationToken.None);
        var outcome = await PlanRunner.RunAsync(plan, CancellationToken.None);
        Assert.True(outcome.Succeeded);
        Assert.Equal(["app Spotify", "url https://mail.google.com"], _setup.Launcher.Launched);
    }
}
