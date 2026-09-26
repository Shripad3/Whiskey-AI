using System.Net;
using System.Text.Json.Nodes;

namespace Alfred.Core;

/// <summary>
/// Spotify Web API, with the user's own client ID (PKCE, no secret). Finds
/// songs and playlists, and starts playback where the account allows it:
/// that needs Spotify Premium (spec §13), so without it the caller opens the
/// found link in the app instead.
/// </summary>
public sealed class SpotifyWebApi
{
    /// <summary>Fixed, so it matches what is registered in the Spotify dashboard character for character.</summary>
    public const int RedirectPort = 43821;
    public static readonly string RegisteredRedirectUri = $"http://127.0.0.1:{RedirectPort}/callback";

    public OAuthSession Session { get; }

    public SpotifyWebApi(SettingsStore settings, ISecretStore secrets, ILauncher launcher)
    {
        Session = new OAuthSession(
            new("Spotify", "https://accounts.spotify.com/authorize", "https://accounts.spotify.com/api/token",
                "playlist-read-private playlist-read-collaborative user-read-playback-state user-modify-playback-state",
                "spotify.token", "127.0.0.1", new Dictionary<string, string>(), RefreshSendsScope: false,
                FixedPort: RedirectPort, RedirectPath: "/callback"),
            () => settings.Current.SpotifyClientId is { Length: > 0 } id ? (id.Trim(), null) : null,
            secrets, launcher);
    }

    public bool IsSignedIn => Session.IsSignedIn;

    public sealed record Match(string Uri, string Title);

    /// <summary>
    /// "Liked Songs" is the user's saved-tracks collection, not a playlist:
    /// searching for it found a public playlist with that name instead.
    /// </summary>
    public static bool IsLikedSongs(string query) =>
        SpokenText.Key(query) is "likedsongs" or "mylikedsongs" or "likes" or "mylikes" or "likedmusic" or "mylikedmusic" or "favorites" or "myfavorites" or "favourites" or "myfavourites";

    public static readonly Match LikedSongs = new("spotify:collection:tracks", "your Liked Songs");

    /// <summary>The best track for a spoken request ("bohemian rhapsody by queen").</summary>
    public async Task<Match?> FindTrackAsync(string query, CancellationToken cancellationToken)
    {
        var json = await Get($"/v1/search?q={Uri.EscapeDataString(query)}&type=track&limit=5", cancellationToken);
        var track = json["tracks"]?["items"]?.AsArray().FirstOrDefault(t => t is not null);
        if (track is null) return null;
        var artist = track["artists"]?.AsArray().FirstOrDefault()?["name"]?.GetValue<string>();
        return new Match(track["uri"]!.GetValue<string>(), track["name"]!.GetValue<string>() + (artist is null ? "" : $" by {artist}"));
    }

    /// <summary>The user's own playlist when one matches, otherwise the top public one.</summary>
    public async Task<Match?> FindPlaylistAsync(string query, CancellationToken cancellationToken)
    {
        var mine = new List<Match>();
        for (var offset = 0; offset < 200; offset += 50)
        {
            var page = await Get($"/v1/me/playlists?limit=50&offset={offset}", cancellationToken);
            foreach (var item in page["items"]?.AsArray() ?? [])
            {
                if (item?["uri"]?.GetValue<string>() is { } uri && item["name"]?.GetValue<string>() is { } name) mine.Add(new Match(uri, name));
            }
            if (page["next"] is null || page["next"]!.GetValueKind() == System.Text.Json.JsonValueKind.Null) break;
        }
        if (AppNameMatcher.Match(query, mine.Select(m => m.Title).ToList(), new Dictionary<string, string>()) is { } found)
            return mine.First(m => m.Title == found);
        var search = await Get($"/v1/search?q={Uri.EscapeDataString(query)}&type=playlist&limit=5", cancellationToken);
        var top = search["playlists"]?["items"]?.AsArray().FirstOrDefault(p => p is not null);
        return top is null ? null : new Match(top["uri"]!.GetValue<string>(), top["name"]!.GetValue<string>());
    }

    /// <summary>
    /// Starts playing on the user's active Spotify device. False when Spotify
    /// won't (no Premium, or no active device); the caller falls back.
    /// </summary>
    public async Task<bool> TryPlayAsync(Match match, CancellationToken cancellationToken)
    {
        var body = match.Uri.StartsWith("spotify:track:", StringComparison.Ordinal)
            ? new JsonObject { ["uris"] = new JsonArray(match.Uri) }
            : new JsonObject { ["context_uri"] = match.Uri };
        var (status, _) = await Session.SendAsync(HttpMethod.Put, "https://api.spotify.com/v1/me/player/play", body.ToJsonString(), cancellationToken: cancellationToken);
        return status is HttpStatusCode.NoContent or HttpStatusCode.OK or HttpStatusCode.Accepted;
    }

    private async Task<JsonNode> Get(string path, CancellationToken cancellationToken)
    {
        var (status, body) = await Session.SendAsync(HttpMethod.Get, "https://api.spotify.com" + path, cancellationToken: cancellationToken);
        return status switch
        {
            HttpStatusCode.OK => JsonNode.Parse(body) ?? new JsonObject(),
            HttpStatusCode.Forbidden => throw new ToolException("Spotify refused the request. Is your account on the app's user list?"),
            HttpStatusCode.TooManyRequests => throw new ToolException("Spotify is rate-limiting requests; try again shortly"),
            _ => throw new ToolException($"Spotify returned an error ({(int)status})"),
        };
    }
}

public sealed record ControlSpotifyArguments(string Action, string? Query = null, int? Value = null);

/// <summary>
/// Spotify on this PC. Transport goes through the Windows media controls: no
/// account, instant. playSong and playPlaylist need the Web API connected in
/// Settings to find what to play; without it they open Spotify's search.
/// </summary>
public sealed class ControlSpotifyTool(IMediaControl media, SpotifyWebApi api) : AssistantTool<ControlSpotifyArguments>
{
    public override string Name => "controlSpotify";
    public override string Title => "Spotify";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "play", "music", "song", "songs", "spotify", "pause", "resume", "skip", "next", "previous", "playlist", "album", "track", "listen",
    };

    public override string Description =>
        "Control music in the Spotify app. \"play some music\" → action \"play\". \"skip this song\" → action \"next\". " +
        "\"play Bohemian Rhapsody\" → action \"playSong\", query \"Bohemian Rhapsody\". " +
        "\"play my running playlist\" → action \"playPlaylist\", query \"running\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("action", "What to do", Choices: ["play", "pause", "next", "previous", "setVolume", "playSong", "playPlaylist"]),
        new("query", "Song, artist or playlist name as the user said it, only for playSong or playPlaylist", Required: false),
        new("value", "Spotify volume from 0 to 100, only for setVolume", Required: false, Digits: true),
    ];

    protected override string Target(ControlSpotifyArguments a) => a.Action switch
    {
        "playSong" or "playPlaylist" => a.Query ?? "",
        "setVolume" => $"Volume {(a.Value is { } v ? $"{v}%" : "")}",
        "next" => "Next track",
        "previous" => "Previous track",
        _ => ClockAlert.Capitalised(a.Action),
    };

    /// <summary>
    /// Something musical must have been said. Given "turn on DND", the Mac's
    /// model chose Spotify play (query "DND") and started music.
    /// </summary>
    private static readonly HashSet<string> GroundingWords =
    [
        "play", "music", "song", "songs", "spotify", "pause", "resume", "skip", "next", "previous", "track",
        "playlist", "album", "artist", "stop", "listen", "put",
    ];

    protected override async Task<ToolResult> ExecuteAsync(ControlSpotifyArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        if (context.Transcript is { } transcript && !SpokenText.Words(transcript).Any(GroundingWords.Contains))
            throw new ToolException("I didn't catch what to do");
        if (!media.IsSpotifyInstalled) throw new ToolException("Spotify isn't installed");
        cancellationToken.ThrowIfCancellationRequested();
        // Only playing is worth launching Spotify for.
        if (arguments.Action is "pause" or "next" or "previous" or "setVolume" && !media.IsSpotifyRunning)
            throw new ToolException("Spotify isn't running");
        switch (arguments.Action)
        {
            case "play":
                await LaunchIfNeeded(cancellationToken);
                await media.PlayAsync();
                if (await BecomesPlaying(cancellationToken)) return "Playing on Spotify";
                // Freshly launched Spotify can have nothing to resume.
                if (api.IsSignedIn)
                {
                    await Play(SpotifyWebApi.LikedSongs, cancellationToken);
                    if (await BecomesPlaying(cancellationToken)) return "Playing your Liked Songs";
                }
                throw new ToolException("Spotify opened but had nothing to play. Try “play” with a song or playlist name");
            case "pause":
                await media.PauseAsync();
                return "Paused Spotify";
            case "next":
                await media.NextAsync();
                return "Next track";
            case "previous":
                await media.PreviousAsync();
                return "Previous track";
            case "setVolume":
                if (arguments.Value is not ({ } value and >= 0 and <= 100)) throw new ToolException("What volume should Spotify play at?");
                media.SetSpotifyVolume(value);
                return $"Spotify volume {value}%";
            case "playSong" or "playPlaylist":
            {
                var query = arguments.Query?.Trim();
                if (string.IsNullOrEmpty(query)) throw new ToolException("What should I play?");
                if (!api.IsSignedIn)
                {
                    await media.OpenUriAsync($"spotify:search:{Uri.EscapeDataString(query)}", cancellationToken);
                    return $"Searched Spotify for “{query}”";
                }
                var playlist = arguments.Action == "playPlaylist";
                var match = SpotifyWebApi.IsLikedSongs(query) ? SpotifyWebApi.LikedSongs
                    : playlist ? await api.FindPlaylistAsync(query, cancellationToken)
                    : await api.FindTrackAsync(query, cancellationToken);
                if (match is null) throw new ToolException($"I couldn't find {(playlist ? "a playlist" : "a song")} called “{query}” on Spotify");
                cancellationToken.ThrowIfCancellationRequested();
                await LaunchIfNeeded(cancellationToken);
                await Play(match, cancellationToken);
                if (!await BecomesPlaying(cancellationToken)) throw new ToolException($"Spotify didn't start playing {match.Title}");
                return $"Playing {match.Title}";
            }
            default:
                throw new ToolException($"Spotify can't do \"{arguments.Action}\"");
        }
    }

    /// <summary>The Web API when the account allows it; otherwise open the link and press play.</summary>
    private async Task Play(SpotifyWebApi.Match match, CancellationToken cancellationToken)
    {
        if (await api.TryPlayAsync(match, cancellationToken)) return;
        await media.OpenUriAsync(match.Uri, cancellationToken);
        await Task.Delay(1200, cancellationToken);
        await media.PlayAsync();
    }

    private async Task LaunchIfNeeded(CancellationToken cancellationToken)
    {
        if (!media.IsSpotifyRunning) await media.LaunchSpotifyAsync(cancellationToken);
    }

    /// <summary>Whether Spotify reports playing within about two seconds.</summary>
    private async Task<bool> BecomesPlaying(CancellationToken cancellationToken)
    {
        for (var i = 0; i < 5; i++)
        {
            if (await media.StateAsync() == PlaybackState.Playing) return true;
            await Task.Delay(400, cancellationToken);
        }
        return false;
    }

    protected override ControlSpotifyArguments? Direct(DirectCommand command)
    {
        // Dropped first: "play some music on Spotify" is the transport phrase
        // "play some music", not a song called "some music".
        var text = StripSuffix(command.Text);
        switch (text)
        {
            case "play" or "play some music" or "play music" or "play my music" or "resume" or "resume music" or "resume the music"
                or "play spotify" or "resume spotify":
                return new("play");
            case "pause" or "stop" or "pause music" or "pause the music" or "stop the music" or "stop music" or "pause spotify":
                return new("pause");
            case "next" or "next song" or "next track" or "skip" or "skip song" or "skip this song" or "skip this":
                return new("next");
            case "previous" or "previous song" or "previous track" or "last song":
                return new("previous");
        }
        if (command.Verb != "play") return null;
        // YouTube and videos belong to the browser, not Spotify.
        if (command.Rest.Contains("youtube", StringComparison.Ordinal) || command.Rest.Contains("video", StringComparison.Ordinal)) return null;
        var query = StripSuffix(command.Rest);
        if (query.EndsWith(" playlist", StringComparison.Ordinal))
        {
            query = query[..^" playlist".Length];
            foreach (var prefix in new[] { "my ", "the " })
            {
                if (query.StartsWith(prefix, StringComparison.Ordinal)) query = query[prefix.Length..];
            }
            return query.Length == 0 ? null : new("playPlaylist", query);
        }
        return new("playSong", query);
    }

    private static string StripSuffix(string text)
    {
        foreach (var suffix in new[] { " on spotify", " in spotify" })
        {
            if (text.EndsWith(suffix, StringComparison.Ordinal)) return text[..^suffix.Length];
        }
        return text;
    }
}
