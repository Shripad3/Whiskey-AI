using System.Text.Json.Nodes;
using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>The tables in shared/fixtures, which the Mac app's tests also describe (spec §12).</summary>
public class SharedFixtureTests
{
    private static JsonNode Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", name)))!;

    private static IEnumerable<object?[]> Rows(string file, string table, params string[] fields) =>
        Load(file)[table]!.AsArray().Select(row => fields.Select(f => row![f] is { } v ? (object?)v.ToString() : null).ToArray());

    private static IEnumerable<object[]> Strings(string file, string table) =>
        Load(file)[table]!.AsArray().Select(v => new object[] { v!.GetValue<string>() });

    public static IEnumerable<object?[]> Splits() => Rows("direct-commands.json", "splits", "transcript", "verb", "rest");

    [Theory, MemberData(nameof(Splits))]
    public void DirectCommandSplits(string transcript, string verb, string rest)
    {
        var command = DirectCommand.Parse(transcript);
        Assert.Equal(verb, command?.Verb);
        Assert.Equal(rest, command?.Rest);
    }

    public static IEnumerable<object[]> Empty() => Strings("direct-commands.json", "empty");

    [Theory, MemberData(nameof(Empty))]
    public void EmptyCommandIsNull(string transcript) => Assert.Null(DirectCommand.Parse(transcript));

    public static IEnumerable<object[]> Compound() =>
        Load("direct-commands.json")["compound"]!["yes"]!.AsArray().Select(v => new object[] { v!.GetValue<string>(), true })
            .Concat(Load("direct-commands.json")["compound"]!["no"]!.AsArray().Select(v => new object[] { v!.GetValue<string>(), false }));

    [Theory, MemberData(nameof(Compound))]
    public void CompoundCommands(string text, bool expected) => Assert.Equal(expected, DirectMatcher.IsCompound(SpokenText.Normalize(text)));

    public static IEnumerable<object?[]> Mentions() => Rows("grounding.json", "mentions", "phrase", "transcript", "expected");

    [Theory, MemberData(nameof(Mentions))]
    public void GroundingMentions(string phrase, string transcript, string expected) =>
        Assert.Equal(bool.Parse(expected), Grounding.Mentions(phrase, transcript));

    public static IEnumerable<object?[]> SpokenUrls() => Rows("grounding.json", "spokenUrls", "url", "transcript", "expected");

    [Theory, MemberData(nameof(SpokenUrls))]
    public void GroundingSpokenUrls(string url, string transcript, string expected) =>
        Assert.Equal(bool.Parse(expected), Grounding.IsSpoken(new Uri(url), transcript));

    public static IEnumerable<object?[]> Clean() => Rows("grounding.json", "clean", "raw", "expected");

    [Theory, MemberData(nameof(Clean))]
    public void CleaningStripsSurroundingPunctuation(string raw, string expected) => Assert.Equal(expected, SpokenText.Clean(raw));

    public static IEnumerable<object?[]> Accepts() => Rows("web-addresses.json", "accepts", "raw", "expected");

    [Theory, MemberData(nameof(Accepts))]
    public void WebAddressAccepts(string raw, string expected) => Assert.Equal(expected, WebAddress.Normalize(raw));

    public static IEnumerable<object[]> Rejects() => Strings("web-addresses.json", "rejects");

    [Theory, MemberData(nameof(Rejects))]
    public void WebAddressRejects(string raw) => Assert.Null(WebAddress.Normalize(raw));

    public static IEnumerable<object?[]> SpokenDomains() => Rows("web-addresses.json", "spokenDomains", "words", "expected");

    [Theory, MemberData(nameof(SpokenDomains))]
    public void SpokenDomainsBecomeAddresses(string words, string? expected) => Assert.Equal(expected, WebAddress.Spoken(words));

    public static IEnumerable<object?[]> SearchUrls() => Rows("web-addresses.json", "searchUrls", "engine", "query", "expected");

    [Theory, MemberData(nameof(SearchUrls))]
    public void SearchUrlsAreEncoded(string engine, string query, string? expected) =>
        Assert.Equal(expected, Enum.Parse<SearchEngine>(engine).Url(query));

    public static IEnumerable<object[]> Answered() => Strings("small-talk.json", "answered");

    [Theory, MemberData(nameof(Answered))]
    public void SmallTalkIsAnswered(string transcript) => Assert.NotNull(SmallTalk.Reply(transcript));

    public static IEnumerable<object[]> NotSmallTalk() => Strings("small-talk.json", "notSmallTalk");

    [Theory, MemberData(nameof(NotSmallTalk))]
    public void CommandsAreNotSmallTalk(string transcript) => Assert.Null(SmallTalk.Reply(transcript));

    public static IEnumerable<object[]> ContentRefusals() => Strings("small-talk.json", "contentRefusals");

    [Theory, MemberData(nameof(ContentRefusals))]
    public void FileContentRequestsAreRefused(string transcript) => Assert.NotNull(ContentRequests.Refusal(transcript));

    public static IEnumerable<object[]> NotContentRefusals() => Strings("small-talk.json", "notContentRefusals");

    [Theory, MemberData(nameof(NotContentRefusals))]
    public void OtherRequestsAreNotRefused(string transcript) => Assert.Null(ContentRequests.Refusal(transcript));
}
