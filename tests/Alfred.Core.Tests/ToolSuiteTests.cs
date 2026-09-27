using System.Text.Json.Nodes;
using Alfred.Core;

namespace Alfred.Core.Tests;

/// <summary>Ported from the Mac's MediaAndSystemTests.swift.</summary>
public class MediaAndSystemTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("Play some music", "play")]
    [InlineData("pause the music", "pause")]
    [InlineData("Skip this song.", "next")]
    [InlineData("previous track", "previous")]
    [InlineData("resume", "play")]
    [InlineData("Play.", "play")]
    [InlineData("stop", "pause")]
    [InlineData("play some music on Spotify", "play")]
    public void SpotifyTransport(string transcript, string action)
    {
        var match = _world.Direct(SpokenText.Clean(transcript))!.Value;
        Assert.Equal("controlSpotify", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
    }

    [Fact]
    public void PlaySong()
    {
        var args = _world.Direct("play bohemian rhapsody on spotify")!.Value.Args;
        Assert.Equal("playSong", args.Str("action"));
        Assert.Equal("bohemian rhapsody", args.Str("query"));
    }

    [Fact]
    public void PlayPlaylist()
    {
        var args = _world.Direct("Play my chill vibes playlist")!.Value.Args;
        Assert.Equal("playPlaylist", args.Str("action"));
        Assert.Equal("chill vibes", args.Str("query"));
    }

    /// <summary>The canonical command is for the browser, never Spotify.</summary>
    [Fact]
    public void YouTubeIsNotSpotify()
    {
        Assert.Null(_world.Direct("Open Chrome and play the Mat Armstrong YouTube video"));
        Assert.NotEqual("controlSpotify", _world.Direct("play the mat armstrong video")?.Tool);
    }

    [Theory]
    [InlineData("Mute", "mute", null)]
    [InlineData("unmute the sound", "unmute", null)]
    [InlineData("Turn the volume up", "volumeUp", null)]
    [InlineData("quieter", "volumeDown", null)]
    [InlineData("set the volume to 40%", "setVolume", "40")]
    [InlineData("volume 75", "setVolume", "75")]
    [InlineData("put my PC to sleep", "sleep", null)]
    [InlineData("Make the screen brighter", "brightnessUp", null)]
    [InlineData("dimmer", "brightnessDown", null)]
    [InlineData("set the brightness to 60", "setBrightness", "60")]
    [InlineData("brightness 30%", "setBrightness", "30")]
    [InlineData("full brightness", "setBrightness", "100")]
    [InlineData("Lock my PC.", "lock", null)]
    [InlineData("lock the screen", "lock", null)]
    [InlineData("Turn on do not disturb", "doNotDisturbOn", null)]
    [InlineData("do not disturb off", "doNotDisturbOff", null)]
    [InlineData("turn on DND", "doNotDisturbOn", null)]
    public void System(string transcript, string action, string? value)
    {
        var match = _world.Direct(SpokenText.Clean(transcript))!.Value;
        Assert.Equal("systemControl", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
        Assert.Equal(value, match.Args.Str("value"));
    }

    [Theory]
    [InlineData("set the volume to 140")]
    [InlineData("volume loud")]
    [InlineData("sleep on it later")]
    [InlineData("open my lock screen wallpaper")]
    public void SystemLeftToModel(string transcript) => Assert.NotEqual("systemControl", _world.Direct(transcript)?.Tool);

    [Fact]
    public async Task VolumeAndBrightnessChange()
    {
        Assert.Equal("Volume 60%", (await _world.Say("volume up")).Text);
        Assert.Equal("Volume 40%", (await _world.Say("set the volume to 40")).Text);
        Assert.Equal("Brightness 70%", (await _world.Say("brighter")).Text);
        await _world.Say("mute");
        Assert.True(_world.System.Muted);
    }

    [Fact]
    public async Task ModelNumbersArriveAsText()
    {
        var result = await _world.Run("systemControl", "set the volume to 30", ("action", "setVolume"), ("value", "30"));
        Assert.Equal("Volume 30%", result.Text);
    }

    [Fact]
    public async Task DoNotDisturbOpensSettingsAndSaysWhy()
    {
        var result = await _world.Say("turn on do not disturb");
        Assert.Contains("notification settings", result.Text);
        Assert.Equal(["notifications"], _world.System.Calls);
    }

    /// <summary>Given "Hi how are you", the Mac's model turned the volume down.</summary>
    [Fact]
    public async Task ChitChatCannotChangeVolume() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("systemControl", "Hi how are you", ("action", "volumeDown")));

    /// <summary>The Mac model's mistake: Spotify "play DND" for "turn on DND".</summary>
    [Fact]
    public async Task SpotifyNeedsMusicWords() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("controlSpotify", "turn on DND", ("action", "play"), ("query", "DND")));

    [Fact]
    public async Task TransportUsesMediaControls()
    {
        await _world.Say("pause");
        await _world.Say("next track");
        Assert.Equal(["pause", "next"], _world.Media.Calls);
    }

    [Fact]
    public async Task PausingNeedsSpotifyRunning()
    {
        _world.Media.Running = false;
        var error = await Assert.ThrowsAsync<ToolException>(() => _world.Say("pause"));
        Assert.Equal("Spotify isn't running", error.Message);
    }

    [Fact]
    public async Task PlayLaunchesSpotify()
    {
        _world.Media.Running = false;
        Assert.Equal("Playing on Spotify", (await _world.Say("play some music")).Text);
        Assert.Equal(["launch", "play"], _world.Media.Calls);
    }

    [Fact]
    public async Task SongsWithoutTheWebApiSearchInTheApp()
    {
        var result = await _world.Say("play bohemian rhapsody");
        Assert.Equal("Searched Spotify for “bohemian rhapsody”", result.Text);
        Assert.Equal(["open spotify:search:bohemian%20rhapsody"], _world.Media.Calls);
    }

    [Theory]
    [InlineData("liked songs")]
    [InlineData("My Liked Songs")]
    [InlineData("my likes")]
    [InlineData("favorites")]
    public void LikedSongsRecognised(string query) => Assert.True(SpotifyWebApi.IsLikedSongs(query));

    [Fact]
    public void OrdinaryPlaylistIsNotLikedSongs()
    {
        Assert.False(SpotifyWebApi.IsLikedSongs("liked songs 2019"));
        Assert.False(SpotifyWebApi.IsLikedSongs("chill vibes"));
    }

    [Fact]
    public void RedirectUriIsExact() => Assert.Equal("http://127.0.0.1:43821/callback", SpotifyWebApi.RegisteredRedirectUri);
}

/// <summary>Ported from the Mac's YouTubeAndSmallTalkTests.swift and DesktopToolTests.swift.</summary>
public class DesktopAndYouTubeTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    private JsonObject? YouTube(string transcript) => _world.Direct(transcript) is { Tool: "playYouTube" } m ? m.Args : null;

    [Fact]
    public void OpenLatestVideo()
    {
        var args = YouTube("open matt armstrong s latest video on youtube")!;
        Assert.Equal("matt armstrong", args.Str("query"));
        Assert.Equal("true", args.Str("latest"));
        Assert.Null(args.Str("browser"));
    }

    [Fact]
    public void PlayVideoOnYouTube() => Assert.Equal("matt armstrong", YouTube("Play Matt Armstrong's YouTube video on YouTube")!.Str("query"));

    [Fact]
    public void PlayInChromeBrowser()
    {
        var args = YouTube("Play Matt Armstrong video on YouTube in chrome browser")!;
        Assert.Equal("matt armstrong", args.Str("query"));
        Assert.Equal("chrome", args.Str("browser"));
    }

    [Fact]
    public void OthersUnaffected()
    {
        Assert.Equal("openURL", _world.Direct("open youtube")?.Tool);
        Assert.Equal("controlSpotify", _world.Direct("play bohemian rhapsody")?.Tool);
        Assert.Equal("openFile", _world.Direct("open my invoice from last month")?.Tool);
    }

    [Fact]
    public void SearchUrls()
    {
        Assert.Equal("https://www.youtube.com/results?search_query=matt%20armstrong", PlayYouTubeTool.SearchUrl("matt armstrong", false));
        Assert.Equal("https://www.youtube.com/results?search_query=matt%20armstrong&sp=CAI%3D", PlayYouTubeTool.SearchUrl("matt armstrong", true));
        Assert.Null(PlayYouTubeTool.SearchUrl("  ", false));
    }

    [Fact]
    public async Task OpensResultsInTheNamedBrowser()
    {
        var result = await _world.Say("play mat armstrong latest video on youtube in chrome");
        Assert.Equal("YouTube results for latest “mat armstrong” videos in Google Chrome", result.Text);
        Assert.Equal(["url https://www.youtube.com/results?search_query=mat%20armstrong&sp=CAI%3D in Google Chrome"], _world.Launcher.Launched);
    }

    [Fact]
    public async Task UnsaidLatestIsDropped()
    {
        await _world.Run("playYouTube", "play the mat armstrong video on youtube", ("query", "mat armstrong"), ("latest", "true"));
        Assert.Equal(["url https://www.youtube.com/results?search_query=mat%20armstrong"], _world.Launcher.Launched);
    }

    [Theory]
    [InlineData("put Chrome on the left half", "leftHalf", "chrome")]
    [InlineData("move this window to the right", "rightHalf", null)]
    [InlineData("snap Edge to the left side", "leftHalf", "edge")]
    [InlineData("maximize the window", "maximize", null)]
    [InlineData("make Notepad full screen", "fullScreen", "notepad")]
    [InlineData("exit full screen", "exitFullScreen", null)]
    [InlineData("minimise this", "minimize", null)]
    [InlineData("move this to the other display", "nextDisplay", null)]
    [InlineData("center the window", "center", null)]
    [InlineData("top half", "topHalf", null)]
    public void Windows(string said, string action, string? app)
    {
        var match = _world.Direct(said)!.Value;
        Assert.Equal("arrangeWindow", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
        Assert.Equal(app, match.Args.Str("app"));
    }

    [Theory]
    [InlineData("what's on the left")]
    [InlineData("open the left door")]
    [InlineData("play full screen video on youtube")]
    [InlineData("move my screenshot to documents")]
    [InlineData("turn right at the lights")]
    public void NotWindows(string said) => Assert.NotEqual("arrangeWindow", _world.Direct(SpokenText.Clean(said))?.Tool);

    [Fact]
    public async Task ArrangesTheNamedApp()
    {
        var result = await _world.Say("put Chrome on the left half");
        Assert.Equal("Google Chrome: left half", result.Text);
        Assert.Equal(["Google Chrome LeftHalf"], _world.Windows.Calls);
    }

    [Fact]
    public async Task AppThatIsntOpenIsRefused() =>
        await Assert.ThrowsAsync<ToolException>(() => _world.Run("arrangeWindow", "put discord on the left", ("action", "leftHalf"), ("app", "discord")));

    [Fact]
    public void HalvesAndCentre()
    {
        var work = new Rect(0, 0, 1920, 1032);
        var window = new Rect(10, 100, 800, 600);
        Assert.Equal(new Rect(0, 0, 960, 1032), WindowTool.Frame(WindowAction.LeftHalf, work, window));
        Assert.Equal(new Rect(960, 0, 960, 1032), WindowTool.Frame(WindowAction.RightHalf, work, window));
        Assert.Equal(new Rect(0, 0, 1920, 516), WindowTool.Frame(WindowAction.TopHalf, work, window));
        Assert.Equal(new Rect(0, 516, 1920, 516), WindowTool.Frame(WindowAction.BottomHalf, work, window));
        Assert.Equal(new Rect(560, 216, 800, 600), WindowTool.Frame(WindowAction.Center, work, window));
        Assert.Equal(work, WindowTool.Frame(WindowAction.Maximize, work, window));
    }

    [Fact]
    public void MovesToAnotherDisplayProportionally()
    {
        var laptop = new Rect(0, 0, 1000, 800);
        var external = new Rect(1000, 0, 2000, 1600);
        Assert.Equal(new Rect(1000, 0, 1000, 1600), WindowTool.Moved(new Rect(0, 0, 500, 800), laptop, external));
    }

    [Theory]
    [InlineData("what's on my clipboard", "read")]
    [InlineData("what did I copy", "read")]
    [InlineData("clear the clipboard", "clear")]
    [InlineData("remove formatting from the clipboard", "plainText")]
    [InlineData("paste as plain text", "pastePlainText")]
    public void Clipboard(string said, string action)
    {
        var match = _world.Direct(said)!.Value;
        Assert.Equal("clipboard", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
    }

    [Fact]
    public async Task PrivateClipboardIsNeverReadOut()
    {
        _world.Clipboard.Content = new ClipboardContent("hunter2", [], false, IsPrivate: true);
        var result = await _world.Say("what's on my clipboard");
        Assert.DoesNotContain("hunter2", result.Text);
        Assert.Contains("private", result.Text);
    }

    [Fact]
    public void ClipboardDescriptions()
    {
        Assert.Equal("A file: a.txt", ClipboardTool.Describe(new(null, [Path.Combine("x", "a.txt")], false, false)));
        Assert.Equal("An image", ClipboardTool.Describe(new(null, [], true, false)));
        Assert.Equal("The clipboard is empty", ClipboardTool.Describe(new(" ", [], false, false)));
    }

    [Theory]
    [InlineData("Note that the Wi-Fi password is on the router.", "the Wi-Fi password is on the router")]
    [InlineData("take a note: buy more coffee", "buy more coffee")]
    [InlineData("Make a note saying call the plumber on Monday", "call the plumber on Monday")]
    [InlineData("jot down 42 is the answer", "42 is the answer")]
    [InlineData("write down pick up the dry cleaning", "pick up the dry cleaning")]
    public void Notes(string said, string text)
    {
        var match = _world.Direct(said)!.Value;
        Assert.Equal("takeNote", match.Tool);
        Assert.Equal(text, match.Args.Str("text"));
    }

    [Theory]
    [InlineData("notes")]
    [InlineData("show my notes")]
    [InlineData("take a note")]
    public void NotNotes(string said) => Assert.NotEqual("takeNote", _world.Direct(said)?.Tool);

    [Fact]
    public async Task NotesAppendToTheDaysFile()
    {
        await _world.Say("note that the wifi password is on the router");
        await _world.Say("jot down call the plumber");
        var file = Path.Combine(_world.Tree.Documents, "Alfred Notes", "2026-09-27.md");
        var lines = File.ReadAllLines(file);
        Assert.StartsWith("# Notes, Sunday 27 September 2026", lines[0]);
        Assert.Equal("- 14:05 The wifi password is on the router", lines[2]);
        Assert.Equal("- 14:05 Call the plumber", lines[3]);
    }
}

/// <summary>Ported from the Mac's WeatherTests.swift and CalendarTests.swift.</summary>
public class WeatherAndCalendarTests : IDisposable
{
    private readonly World _world = new(new FakeCalendar(
        new CalendarEvent("Dentist", DateTime.Today.AddDays(1).AddHours(15).AddMinutes(-10), DateTime.Today.AddDays(1).AddHours(15.5), false)));

    public void Dispose() => _world.Dispose();

    private static readonly Forecast Sample = new(21.5, "clear", [21.5, 19.4], [11.4, 15.5], ["overcast", "overcast"], [0, 45]);

    [Fact]
    public void TodayAnswer() => Assert.Equal("It's 22° and clear in Amsterdam. High 22°, low 11°.", WeatherTool.Answer(Sample, "Amsterdam", false));

    [Fact]
    public void TomorrowWithRain() =>
        Assert.Equal("Tomorrow in Amsterdam: overcast, high 19°, low 16°. 45% chance of rain.", WeatherTool.Answer(Sample, "Amsterdam", true));

    [Theory]
    [InlineData("How's the weather?")]
    [InlineData("what's the weather like")]
    [InlineData("What's the temperature outside")]
    [InlineData("is it going to rain")]
    public void QuestionsAreAnswered(string transcript)
    {
        var match = _world.Direct(SpokenText.Clean(transcript))!.Value;
        Assert.Equal("getWeather", match.Tool);
        Assert.Equal("today", match.Args.Str("day"));
    }

    [Fact]
    public void PlaceAndDay()
    {
        var args = _world.Direct("what's the weather in Paris tomorrow")!.Value.Args;
        Assert.Equal("paris", args.Str("place"));
        Assert.Equal("tomorrow", args.Str("day"));
    }

    [Fact]
    public void ExplicitSearchStaysASearch() => Assert.Equal("webSearch", _world.Direct("search for the weather in Paris")?.Tool);

    [Fact]
    public void HomeCityFromTimeZone()
    {
        var city = WeatherTool.City(new Settings());
        Assert.NotEmpty(city);
        Assert.DoesNotContain("_", city);
        Assert.Equal("Leiden", WeatherTool.City(new Settings { WeatherCity = " Leiden " }));
    }

    [Fact]
    public async Task UnsaidPlaceUsesTheHomeCity()
    {
        _world.Settings.Update(s => s.WeatherCity = "Utrecht");
        var result = await _world.Run("getWeather", "how's the weather", ("day", "today"), ("place", "Paris"));
        Assert.Contains("in Utrecht", result.Text);
        Assert.True(result.IsAnswer);
    }

    [Fact]
    public void ParsesGoogleEvents()
    {
        const string json = """
            {"items": [
              {"summary": "Standup", "status": "confirmed",
               "start": {"dateTime": "2026-09-28T09:30:00+02:00"}, "end": {"dateTime": "2026-09-28T09:45:00+02:00"}},
              {"summary": "Holiday", "start": {"date": "2026-09-28"}, "end": {"date": "2026-09-29"}},
              {"summary": "Gone", "status": "cancelled", "start": {"dateTime": "2026-09-28T10:00:00Z"}, "end": {"dateTime": "2026-09-28T11:00:00Z"}}
            ]}
            """;
        var events = Accounts.ParseGoogle(json);
        Assert.Equal(["Standup", "Holiday"], events.Select(e => e.Title));
        Assert.Equal(new DateTimeOffset(2026, 9, 28, 7, 30, 0, TimeSpan.Zero).LocalDateTime, events[0].Start);
        Assert.True(events[1].IsAllDay);
        Assert.Equal(new DateTime(2026, 9, 28), events[1].Start);
    }

    [Fact]
    public void SelectsTickedGoogleCalendars() =>
        Assert.Equal(["me@gmail.com"], Accounts.SelectedGoogleCalendars("""{"items": [{"id": "me@gmail.com", "selected": true}, {"id": "holidays", "selected": false}, {"id": "other"}]}"""));

    [Fact]
    public void ParsesOutlookEvents()
    {
        const string json = """
            {"value": [
              {"subject": "Review", "isAllDay": false, "isCancelled": false,
               "start": {"dateTime": "2026-09-28T13:00:00.0000000", "timeZone": "UTC"},
               "end": {"dateTime": "2026-09-28T14:00:00.0000000", "timeZone": "UTC"}},
              {"subject": "Off", "isAllDay": true,
               "start": {"dateTime": "2026-09-29T00:00:00.0000000"}, "end": {"dateTime": "2026-09-30T00:00:00.0000000"}},
              {"subject": "Cancelled", "isCancelled": true,
               "start": {"dateTime": "2026-09-28T15:00:00.0000000"}, "end": {"dateTime": "2026-09-28T16:00:00.0000000"}}
            ]}
            """;
        var events = Accounts.ParseOutlook(json);
        Assert.Equal(["Review", "Off"], events.Select(e => e.Title));
        Assert.Equal(new DateTime(2026, 9, 28, 13, 0, 0, DateTimeKind.Utc).ToLocalTime(), events[0].Start);
        Assert.True(events[1].IsAllDay);
        Assert.Equal(new DateTime(2026, 9, 29), events[1].Start);
    }

    [Fact]
    public void SpeaksAnAgenda()
    {
        var now = DateTime.Now;
        var day = now.Date;
        var events = new[]
        {
            new CalendarEvent("Holiday", day, day.AddDays(1), true),
            new CalendarEvent("Standup", day.AddHours(9), day.AddHours(9.25), false),
        };
        Assert.StartsWith("You have 2 events today: Holiday, all day, Standup at ", CalendarTool.Agenda(events, day, now));
        Assert.Equal("Nothing on your calendar today", CalendarTool.Agenda([], day, now));
    }

    [Theory]
    [InlineData("what's on my calendar today", "agenda")]
    [InlineData("What's on my calendar tomorrow?", "agenda")]
    [InlineData("do I have any meetings on Friday", "agenda")]
    [InlineData("what's my schedule", "agenda")]
    [InlineData("when is my next meeting", "next")]
    [InlineData("what's my next event", "next")]
    [InlineData("am I free at 3", "free")]
    [InlineData("am I busy tomorrow at 10 am", "free")]
    public void CalendarPhrases(string said, string action)
    {
        var match = _world.Direct(SpokenText.Clean(said))!.Value;
        Assert.Equal("calendar", match.Tool);
        Assert.Equal(action, match.Args.Str("action"));
    }

    [Theory]
    [InlineData("open calendar")]
    [InlineData("add a meeting tomorrow at 3")]
    [InlineData("remind me about the meeting")]
    [InlineData("set a timer for 10 minutes")]
    public void LeavesOthers(string said) => Assert.NotEqual("calendar", _world.Direct(said)?.Tool);

    [Fact]
    public void ReminderBeatsCalendar() => Assert.Equal("reminder", _world.Direct("remind me about the meeting at 5")?.Tool);

    [Fact]
    public async Task FreeOrBusy()
    {
        Assert.StartsWith("You have Dentist", (await _world.Say("am I free at 3 pm tomorrow")).Text);
        Assert.StartsWith("You're free", (await _world.Say("am I free at 5 pm tomorrow")).Text);
    }

    [Fact]
    public async Task NoCalendarConnectedSaysWhere()
    {
        var accounts = new Accounts(_world.Settings, _world.Secrets, _world.Launcher, _world.Clock);
        var error = await Assert.ThrowsAsync<ToolException>(() => accounts.EventsAsync(DateTime.Now, DateTime.Now.AddDays(1), CancellationToken.None));
        Assert.Contains("Settings › Calendar", error.Message);
    }
}

public class OAuthTests
{
    /// <summary>The Mac's reference value, computed independently with Python's hashlib/base64.</summary>
    [Fact]
    public void PkceChallengeMatchesReference() =>
        Assert.Equal("275qpbS05D8IrK00ea_kxBaq-4_KkVGztyYLWpSOQRo", Pkce.Challenge("notch-assistant-pkce-check~._-0123456789abcdefABCDEF"));

    /// <summary>A real HTTP request to the loopback receiver, as the browser makes it.</summary>
    [Fact]
    public async Task LoopbackReceivesCallback()
    {
        using var loopback = new LoopbackRedirect("Spotify", "/callback");
        var received = loopback.CallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var client = new HttpClient();
        var body = await client.GetStringAsync($"http://127.0.0.1:{loopback.Port}/callback?code=abc123&state=xyz");
        var query = await received;
        Assert.Contains("Spotify is connected", body);
        Assert.Equal("abc123", query["code"]);
        Assert.Equal("xyz", query["state"]);
    }

    [Fact]
    public async Task OtherPathsAreIgnored()
    {
        using var loopback = new LoopbackRedirect("Outlook");
        var received = loopback.CallbackAsync(TimeSpan.FromSeconds(5), CancellationToken.None);
        using var client = new HttpClient();
        var favicon = await client.GetAsync($"http://127.0.0.1:{loopback.Port}/favicon.ico");
        Assert.Equal(System.Net.HttpStatusCode.NotFound, favicon.StatusCode);
        await client.GetStringAsync($"http://127.0.0.1:{loopback.Port}/?code=a&state=b");
        Assert.Equal("a", (await received)["code"]);
    }

    [Fact]
    public async Task LoopbackTimesOut()
    {
        using var loopback = new LoopbackRedirect("Google");
        await Assert.ThrowsAsync<ToolException>(() => loopback.CallbackAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None));
    }

    [Fact]
    public void SignedOutWithoutAClientId()
    {
        var settings = new SettingsStore(null);
        var api = new SpotifyWebApi(settings, new MemorySecretStore(), new FakeLauncher());
        Assert.False(api.IsSignedIn);
    }
}

/// <summary>Cases the extended intent corpus found (Sep 27, 2026).</summary>
public class CorpusRegressionTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Fact]
    public void DownloadsFolderMeansTheFolderNotFolders()
    {
        var args = _world.Direct("delete everything in my downloads folder")!.Value.Args;
        Assert.Equal("trash", args.Str("operation"));
        Assert.Equal("Downloads", args.Str("folder"));
        Assert.Null(args.Str("kind"));
    }

    [Fact]
    public void ComputedFieldsStayOutOfArguments()
    {
        var args = _world.Direct("delete everything in downloads")!.Value.Args;
        Assert.False(args.ContainsKey("files"));
        Assert.False(args.ContainsKey("summary"));
    }

    [Theory]
    [InlineData("find out who won the f1 race yesterday")]
    [InlineData("find how many people live in tokyo")]
    public void QuestionsAreNotFileSearches(string said) => Assert.NotEqual("findFiles", _world.Direct(said)?.Tool);

    [Theory]
    [InlineData("turn the volume down to twenty", "setVolume", "20")]
    [InlineData("volume to twenty five", "setVolume", "25")]
    [InlineData("set the brightness to seventy", "setBrightness", "70")]
    public void SpokenPercentages(string said, string action, string value)
    {
        var args = _world.Direct(said)!.Value.Args;
        Assert.Equal(action, args.Str("action"));
        Assert.Equal(value, args.Str("value"));
    }

    [Fact]
    public void HalfSixIsSixThirty()
    {
        var when = SpokenWhen.Parse("tomorrow at half six", SpokenWhenTests.Now)!;
        Assert.Equal(new DateTime(2026, 9, 27, 6, 30, 0), when.Date);
        Assert.Equal(new DateTime(2026, 9, 26, 17, 30, 0), SpokenWhen.Parse("half past 5", SpokenWhenTests.Now)!.Date);
        Assert.Equal(1800, SpokenDuration.Parse("half an hour"));
    }

    [Fact]
    public void GetMeUpIsAnAlarm()
    {
        var match = _world.Direct("get me up at half six tomorrow")!.Value;
        Assert.Equal("alarm", match.Tool);
        Assert.Equal("set", match.Args.Str("action"));
    }

    [Fact]
    public void DontLetMeForgetIsAReminder()
    {
        var match = _world.Direct("don't let me forget to call the dentist at 4")!.Value;
        Assert.Equal("reminder", match.Tool);
        Assert.Equal("call the dentist", match.Args.Str("task"));
        Assert.Equal("at 4", match.Args.Str("when"));
    }

    [Fact]
    public void SiteNamesOfferTheWebsiteTool() =>
        Assert.Contains("openURL", ToolRouter.Relevant("i want to watch something on netflix", _world.Tools).Select(t => t.Name));

    [Fact]
    public async Task WindowAppFromAnyWord()
    {
        var match = _world.Direct("make chrome take up the left side of the screen")!.Value;
        Assert.Equal("chrome", match.Args.Str("app"));
        Assert.Equal("Google Chrome: left half", (await _world.Say("make chrome take up the left side of the screen")).Text);
        await _world.Run("arrangeWindow", "put chrome on the left please now", ("action", "leftHalf"), ("app", "chrome please now"));
    }
}

public class ReminderPhrasingTests : IDisposable
{
    private readonly World _world = new();

    public void Dispose() => _world.Dispose();

    [Theory]
    [InlineData("Please remind me to water the plants at 6", "water the plants")]
    [InlineData("could you remind me to call Sam tomorrow", "call Sam")]
    [InlineData("remember to buy milk tonight at 8", "buy milk")]
    [InlineData("don't forget to feed the cat at 7", "feed the cat")]
    public void Openings(string said, string task) => Assert.Equal(task, _world.Direct(said)!.Value.Args.Str("task"));
}
