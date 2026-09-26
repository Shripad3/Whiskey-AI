using Alfred.Core;

namespace Alfred.Core.Tests;

public class AppNameMatcherTests
{
    private static readonly string[] Installed = FakeCatalog.InstalledNames;

    [Theory]
    [InlineData("Spotify", "Spotify")]
    [InlineData("spotify", "Spotify")]
    [InlineData("the Spotify app", "Spotify")]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("chrome", "Google Chrome")]
    [InlineData("google chrome", "Google Chrome")]
    [InlineData("edge", "Microsoft Edge")]
    [InlineData("vs code", "Visual Studio Code")]
    [InlineData("VSCode", "Visual Studio Code")]
    [InlineData("settings", "Settings")]
    [InlineData("explorer", "File Explorer")]
    [InlineData("store", "Microsoft Store")]
    [InlineData("omen hub", "OMEN Gaming Hub")]
    [InlineData("omen", "OMEN Gaming Hub")]
    [InlineData("spotfy", "Spotify")]
    [InlineData("note pad", "Notepad")]
    public void Matches(string spoken, string expected) => Assert.Equal(expected, AppNameMatcher.Match(spoken, Installed));

    [Theory]
    [InlineData("YouTube")]
    [InlineData("my calendar thing")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("no")]
    [InlineData("PowerPoint")]
    public void NoConfidentMatchFails(string spoken) => Assert.Null(AppNameMatcher.Match(spoken, Installed));

    [Fact]
    public void AmbiguousWordMatchFails() => Assert.Null(AppNameMatcher.Match("remote", Installed));

    [Fact]
    public void NearTieSpellingFails() => Assert.NotEqual("Notes", AppNameMatcher.Match("noters", ["Notes", "Noter"]));

    [Fact]
    public void UserAliasesApply() =>
        Assert.Equal("Spotify", AppNameMatcher.Match("music", Installed, new Dictionary<string, string> { ["music"] = "Spotify" }));

    [Theory]
    [InlineData("browser")]
    [InlineData("my browser")]
    [InlineData("the browser")]
    [InlineData("web browser")]
    public void GenericBrowser(string spoken) => Assert.True(AppNameMatcher.IsGenericBrowser(spoken));

    [Fact]
    public void ExpandedAliasIsGrounded()
    {
        Assert.Equal("vscode", Grounding.SpokenAlias("Visual Studio Code", "open vs code"));
        Assert.Equal("code", Grounding.SpokenAlias("Visual Studio Code", "open code"));
        Assert.Null(Grounding.SpokenAlias("Visual Studio Code", "open xcode"));
        Assert.Null(Grounding.SpokenAlias("File Explorer", "open again"));
    }

    [Fact]
    public void AppsAreMentionedByTheirLastWord()
    {
        Assert.True(Grounding.MentionsApp("Google Chrome", "open chrome and go to youtube"));
        Assert.True(Grounding.MentionsApp("Microsoft Edge", "open github in edge"));
        Assert.False(Grounding.MentionsApp("Google Chrome", "open google"));
    }
}

public class DirectMatchTests
{
    private readonly IReadOnlyList<IAssistantTool> _tools = Setup.Standard().Tools;

    private (string Tool, string Json)? Match(string transcript) =>
        DirectMatcher.Match(transcript, _tools) is { } plan ? (plan.Steps[0].Tool.Name, plan.Steps[0].Arguments.ToJsonString()) : null;

    [Fact]
    public void InstalledAppBeatsWebsite()
    {
        Assert.Equal("openApp", Match("open Spotify")?.Tool);
        Assert.Equal("openApp", Match("launch discord")?.Tool);
    }

    [Fact]
    public void KnownSite()
    {
        Assert.Equal("openURL", Match("Open YouTube")?.Tool);
        Assert.Contains("mail.google.com", Match("go to gmail")?.Json);
    }

    [Fact]
    public void SpokenDomain()
    {
        Assert.Contains("https://github.com", Match("Go to github.com")?.Json);
        Assert.Contains("https://github.com", Match("open github dot com")?.Json);
    }

    [Fact]
    public void SiteInNamedBrowser()
    {
        var match = Match("open youtube in chrome");
        Assert.Equal("openURL", match?.Tool);
        Assert.Contains("\"browser\":\"chrome\"", match?.Json);
    }

    [Fact]
    public void Searches()
    {
        Assert.Equal("webSearch", Match("search for banana bread")?.Tool);
        Assert.Equal("webSearch", Match("google porsche 911")?.Tool);
    }

    [Fact]
    public void GenericBrowserIsAnApp() => Assert.Equal("openApp", Match("open my browser")?.Tool);

    /// <summary>Anything not clearly simple must reach the model.</summary>
    [Theory]
    [InlineData("open again")]
    [InlineData("open best comics")]
    [InlineData("Open Chrome and play the Mat Armstrong YouTube video")]
    [InlineData("open youtube in some browser i don't have")]
    [InlineData("could you bring up discord for me")]
    public void LeftToModel(string transcript) => Assert.Null(Match(transcript));
}

public class ToolRouterTests
{
    private readonly IReadOnlyList<IAssistantTool> _tools = Setup.Standard().Tools;

    [Fact]
    public void KeywordsChooseTools() =>
        Assert.Equal(["webSearch"], ToolRouter.Relevant("who won the race", _tools).Select(t => t.Name));

    [Fact]
    public void NothingMatchedFallsBackToGeneralTools() =>
        Assert.Equal(["openApp", "openURL", "webSearch"], ToolRouter.Relevant("bring up discord", _tools).Select(t => t.Name));

    [Fact]
    public void NeverMoreThanTheMaximum()
    {
        var many = Enumerable.Range(0, 10).Select(i => new OpenAppTool(new FakeCatalog(), new FakeLauncher())).ToList<IAssistantTool>();
        Assert.Equal(ToolRouter.Maximum, ToolRouter.Relevant("open it", many).Count);
    }
}
