using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>Grounding inside each tool: values that weren't said are refused or dropped, not acted on.</summary>
public class ToolTests
{
    private static async Task<(ToolResult? Result, string? Error, List<string> Launched)> Run(string tool, string transcript, params (string, string?)[] args)
    {
        var (tools, launcher, _) = Setup.Standard();
        try
        {
            var result = await tools.Tool(tool).ExecuteAsync(Setup.Args(args), new ToolContext(transcript), CancellationToken.None);
            return (result, null, launcher.Launched);
        }
        catch (ToolException error)
        {
            return (null, error.Message, launcher.Launched);
        }
    }

    [Fact]
    public async Task OpensASpokenApp()
    {
        var (result, _, launched) = await Run("openApp", "open spotify", ("appName", "Spotify"));
        Assert.Equal("Opened Spotify", result?.Text);
        Assert.Equal(["app Spotify"], launched);
    }

    [Fact]
    public async Task OpensAnAliasedApp()
    {
        var (_, _, launched) = await Run("openApp", "open vs code", ("appName", "Visual Studio Code"));
        Assert.Equal(["app Visual Studio Code"], launched);
    }

    [Fact]
    public async Task RefusesAnUnspokenApp()
    {
        var (_, error, launched) = await Run("openApp", "open again", ("appName", "File Explorer"));
        Assert.Equal("I didn't catch which app to open", error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task MyBrowserIsTheDefaultBrowser()
    {
        // The model turned "my browser" into a browser's name on the Mac.
        var (_, _, launched) = await Run("openApp", "open my browser", ("appName", "Google Chrome"));
        Assert.Equal(["app Microsoft Edge"], launched);
    }

    [Fact]
    public async Task UnknownAppSaysSo()
    {
        var (_, error, _) = await Run("openApp", "open photoshop", ("appName", "photoshop"));
        Assert.Equal("I couldn't find an app called \"photoshop\"", error);
    }

    [Fact]
    public async Task InventedPathFallsBackToHomePage()
    {
        var (_, _, launched) = await Run("openURL", "open chrome and play the mat armstrong youtube video",
            ("url", "https://www.youtube.com/watch?v=abc"));
        Assert.Equal(["url https://www.youtube.com in Google Chrome"], launched);
    }

    [Fact]
    public async Task SpokenPathIsKept()
    {
        var (_, _, launched) = await Run("openURL", "go to github.com/apple", ("url", "https://github.com/apple"));
        Assert.Equal(["url https://github.com/apple"], launched);
    }

    [Fact]
    public async Task InventedBrowserIsDropped()
    {
        var (_, _, launched) = await Run("openURL", "open youtube", ("url", "https://www.youtube.com"), ("browser", "Firefox"));
        Assert.Equal(["url https://www.youtube.com"], launched);
    }

    [Fact]
    public async Task SiteNameIsNotABrowser()
    {
        var (_, _, launched) = await Run("openURL", "go to youtube", ("url", "https://www.youtube.com"), ("browser", "YouTube"));
        Assert.Equal(["url https://www.youtube.com"], launched);
    }

    [Fact]
    public async Task NamedBrowserIsUsed()
    {
        var (result, _, launched) = await Run("openURL", "open youtube in edge", ("url", "https://www.youtube.com"), ("browser", "edge"));
        Assert.Equal(["url https://www.youtube.com in Microsoft Edge"], launched);
        Assert.Equal("Opened www.youtube.com in Microsoft Edge", result?.Text);
    }

    [Theory]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("shell:AppsFolder\\Microsoft.WindowsTerminal_8wekyb3d8bbwe!App")]
    [InlineData("ms-settings:privacy")]
    public async Task OnlyWebAddressesOpen(string url)
    {
        var (_, error, launched) = await Run("openURL", "open " + url, ("url", url));
        Assert.NotNull(error);
        Assert.Empty(launched);
    }

    [Fact]
    public async Task SearchUsesTheChosenEngine()
    {
        var launcher = new FakeLauncher();
        var tool = new WebSearchTool(launcher, () => SearchEngine.DuckDuckGo);
        var result = await tool.ExecuteAsync(Setup.Args(("query", "rust")), new ToolContext("search rust"), CancellationToken.None);
        Assert.Equal("Searched DuckDuckGo for “rust”", result.Text);
        Assert.Equal(["url https://duckduckgo.com/?q=rust"], launcher.Launched);
    }
}
