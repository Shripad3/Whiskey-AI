using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Alfred.Core;

/// <summary>A place and its time zone (IANA id).</summary>
public sealed record Place(string Name, double Latitude, double Longitude, string? TimeZone);

/// <summary>Finds places by name (Open-Meteo geocoding in the app).</summary>
public interface IPlaceFinder
{
    Task<Place> LocateAsync(string name, CancellationToken cancellationToken);
    Task<Forecast> ForecastAsync(Place place, bool fahrenheit, CancellationToken cancellationToken);
}

/// <summary>A forecast in the user's units, conditions in words. Index 0 is today, 1 tomorrow.</summary>
public sealed record Forecast(double CurrentTemperature, string CurrentConditions, IReadOnlyList<double> Highs,
    IReadOnlyList<double> Lows, IReadOnlyList<string> Conditions, IReadOnlyList<int> RainChances);

/// <summary>Open-Meteo: free, no key (spec §8). Only a place name leaves the PC.</summary>
public sealed class OpenMeteo : IPlaceFinder
{
    private readonly Dictionary<string, Place> _cache = [];

    public async Task<Place> LocateAsync(string name, CancellationToken cancellationToken)
    {
        var key = name.Trim().ToLowerInvariant();
        lock (_cache)
        {
            if (_cache.TryGetValue(key, out var cached)) return cached;
        }
        var url = $"https://geocoding-api.open-meteo.com/v1/search?name={Uri.EscapeDataString(name.Trim())}&count=1&language=en&format=json";
        var response = await Http.Guard("The weather service", () => Http.Client.GetFromJsonAsync<GeocodingResponse>(url, cancellationToken));
        var first = response?.Results?.FirstOrDefault() ?? throw new ToolException($"I couldn't find a place called {name}");
        var place = new Place(first.Name, first.Latitude, first.Longitude, first.Timezone);
        lock (_cache) _cache[key] = place;
        return place;
    }

    public async Task<Forecast> ForecastAsync(Place place, bool fahrenheit, CancellationToken cancellationToken)
    {
        var latitude = place.Latitude.ToString(CultureInfo.InvariantCulture);
        var longitude = place.Longitude.ToString(CultureInfo.InvariantCulture);
        var url = $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}" +
                  "&current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min,weather_code,precipitation_probability_max" +
                  $"&timezone=auto&forecast_days=2&temperature_unit={(fahrenheit ? "fahrenheit" : "celsius")}";
        var r = await Http.Guard("The weather service", () => Http.Client.GetFromJsonAsync<ForecastResponse>(url, cancellationToken))
            ?? throw new ToolException("The weather service didn't answer");
        return new Forecast(
            r.Current.Temperature, WeatherTool.Describe(r.Current.WeatherCode),
            r.Daily.Max, r.Daily.Min, r.Daily.WeatherCode.Select(WeatherTool.Describe).ToList(),
            (r.Daily.Rain ?? []).Select(v => v ?? 0).ToList());
    }

    private sealed record GeocodingResponse(List<GeocodingResult>? Results);
    private sealed record GeocodingResult(string Name, double Latitude, double Longitude, string? Timezone);

    private sealed record ForecastResponse(CurrentBlock Current, DailyBlock Daily);
    private sealed record CurrentBlock(
        [property: JsonPropertyName("temperature_2m")] double Temperature,
        [property: JsonPropertyName("weather_code")] int WeatherCode);
    private sealed record DailyBlock(
        [property: JsonPropertyName("temperature_2m_max")] List<double> Max,
        [property: JsonPropertyName("temperature_2m_min")] List<double> Min,
        [property: JsonPropertyName("weather_code")] List<int> WeatherCode,
        [property: JsonPropertyName("precipitation_probability_max")] List<int?>? Rain);
}

public sealed record WeatherArguments(string Day, string? Place = null);

/// <summary>Answers weather questions aloud: now, the day's high and low, and the chance of rain.</summary>
public sealed class WeatherTool(IPlaceFinder places, SettingsStore settings) : AssistantTool<WeatherArguments>
{
    public override string Name => "getWeather";
    public override string Title => "Weather";
    public override bool RequiresNetwork => true;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string>
    {
        "weather", "temperature", "rain", "raining", "forecast", "hot", "cold", "sunny", "umbrella", "degrees", "warm", "snow", "snowing", "outside",
    };

    public override string Description =>
        "Tell the weather: now, the high and low, and the chance of rain. " +
        "\"how's the weather\" → day \"today\". \"will it rain tomorrow in Paris\" → day \"tomorrow\", place \"Paris\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("day", "Which day", Choices: ["today", "tomorrow"]),
        new("place", "City or place, only if the user named one, e.g. \"Paris\"", Required: false),
    ];

    protected override string Target(WeatherArguments a) =>
        string.Join(" · ", new[] { a.Place ?? City(settings.Current), a.Day == "tomorrow" ? "tomorrow" : null }.OfType<string>());

    protected override async Task<ToolResult> ExecuteAsync(WeatherArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        // A place the user didn't say is the model's guess: use the home city.
        var place = arguments.Place?.Trim() ?? "";
        if (place.Length == 0 || (context.Transcript is { } t && !Grounding.Mentions(place, t))) place = City(settings.Current);
        var tomorrow = arguments.Day == "tomorrow" && (context.Transcript is null || Grounding.Mentions("tomorrow", context.Transcript));
        var fahrenheit = settings.Current.Fahrenheit ?? !RegionInfo.CurrentRegion.IsMetric;
        var location = await places.LocateAsync(place, cancellationToken);
        var forecast = await places.ForecastAsync(location, fahrenheit, cancellationToken);
        return new ToolResult(Answer(forecast, location.Name, tomorrow), IsAnswer: true);
    }

    /// <summary>The chosen city, else the one in this PC's time zone ("Europe/Amsterdam" → "Amsterdam").</summary>
    public static string City(Settings settings)
    {
        if (!string.IsNullOrWhiteSpace(settings.WeatherCity)) return settings.WeatherCity.Trim();
        var id = TimeZoneInfo.Local.Id;
        if (!id.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) id = iana;
        var city = id.Split('/').Last().Replace('_', ' ');
        return city.Contains(' ') && city.EndsWith("Time", StringComparison.Ordinal) || city == "UTC" ? "London" : city;
    }

    /// <summary>"It's 21° and clear in Amsterdam. High 22°, low 11°." Pure, for testing.</summary>
    public static string Answer(Forecast forecast, string place, bool tomorrow)
    {
        var day = tomorrow ? 1 : 0;
        if (forecast.Highs.Count <= day || forecast.Lows.Count <= day) return $"I couldn't get the forecast for {place}";
        var high = (int)Math.Round(forecast.Highs[day]);
        var low = (int)Math.Round(forecast.Lows[day]);
        var rain = forecast.RainChances.Count > day ? forecast.RainChances[day] : 0;
        var rainNote = rain >= 20 ? $" {rain}% chance of rain." : "";
        if (tomorrow)
        {
            var conditions = forecast.Conditions.Count > 1 ? forecast.Conditions[1] : forecast.CurrentConditions;
            return $"Tomorrow in {place}: {conditions}, high {high}°, low {low}°.{rainNote}";
        }
        return $"It's {(int)Math.Round(forecast.CurrentTemperature)}° and {forecast.CurrentConditions} in {place}. High {high}°, low {low}°.{rainNote}";
    }

    /// <summary>WMO weather codes, as Open-Meteo reports them.</summary>
    public static string Describe(int code) => code switch
    {
        0 => "clear",
        1 => "mostly clear",
        2 => "partly cloudy",
        3 => "overcast",
        45 or 48 => "foggy",
        51 or 53 or 55 => "drizzly",
        56 or 57 => "freezing drizzle",
        61 or 63 => "rainy",
        65 => "heavy rain",
        66 or 67 => "freezing rain",
        71 or 73 or 75 or 77 => "snowy",
        80 or 81 => "showery",
        82 => "heavy showers",
        85 or 86 => "snow showers",
        95 => "stormy",
        96 or 99 => "stormy with hail",
        _ => "unsettled",
    };

    private static readonly HashSet<string> Triggers = ["weather", "temperature", "forecast", "rain", "raining", "umbrella", "snow", "snowing"];

    /// <summary>"how's the weather", "what's the weather in Paris tomorrow". Not "search for the weather".</summary>
    protected override WeatherArguments? Direct(DirectCommand command)
    {
        var words = command.Text.Split(' ');
        if (!words.Any(Triggers.Contains) || words[0] is "search" or "google" or "look" || words.Length > 12) return null;
        string? place = null;
        var index = Array.LastIndexOf(words, "in");
        if (index >= 0 && index + 1 < words.Length)
        {
            var after = words[(index + 1)..].Where(w => w is not ("today" or "tomorrow" or "right" or "now" or "the")).ToList();
            if (after.Count > 0) place = string.Join(' ', after);
        }
        return new WeatherArguments(words.Contains("tomorrow") ? "tomorrow" : "today", place);
    }
}

public sealed record CurrentTimeArguments(string What, string? Place = null);

/// <summary>"What time is it", "what's the date", "what time is it in Tokyo".</summary>
public sealed class CurrentTimeTool(IPlaceFinder places) : AssistantTool<CurrentTimeArguments>
{
    public override string Name => "currentTime";
    public override string Title => "Clock";
    public override bool RequiresNetwork => false;
    public override IReadOnlySet<string> Keywords { get; } = new HashSet<string> { "time", "date", "day", "today", "clock" };

    public override string Description =>
        "Say the current time or date. \"what time is it in Tokyo\" → what \"time\", place \"Tokyo\". \"what's the date\" → what \"date\".";

    public override IReadOnlyList<ArgumentSpec> Arguments { get; } =
    [
        new("what", "What the user asked for", Choices: ["time", "date"]),
        new("place", "City or place, only if the user named one, e.g. \"Tokyo\"", Required: false),
    ];

    protected override string Target(CurrentTimeArguments a) => a.Place ?? (a.What == "date" ? "Today" : "Now");

    protected override async Task<ToolResult> ExecuteAsync(CurrentTimeArguments arguments, ToolContext context, CancellationToken cancellationToken)
    {
        var utc = DateTime.UtcNow;
        var zone = TimeZoneInfo.Local;
        string? placeName = null;
        if (ClockPhrases.Grounded(arguments.Place, context) is { } spoken)
            (placeName, zone) = await ZoneFor(spoken, cancellationToken);
        var there = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var invariant = CultureInfo.InvariantCulture;
        if (arguments.What == "date")
        {
            var date = there.ToString("dddd d MMMM yyyy", invariant);
            return new ToolResult(placeName is null ? $"It's {date}" : $"In {placeName} it's {date}", IsAnswer: true);
        }
        var time = ClockFormat.Time(there);
        if (placeName is null) return new ToolResult($"It's {time}", IsAnswer: true);
        // Say the day too when it differs from here.
        var day = there.Date != DateTime.Now.Date ? $", {there.ToString("dddd", invariant)}" : "";
        return new ToolResult($"It's {time}{day} in {placeName}", IsAnswer: true);
    }

    /// <summary>A zone this PC knows by city ("Tokyo"), else the geocoder's time zone for the place.</summary>
    private async Task<(string, TimeZoneInfo)> ZoneFor(string place, CancellationToken cancellationToken)
    {
        if (KnownZone(place) is { } known) return (CultureInfo.InvariantCulture.TextInfo.ToTitleCase(place), known);
        var found = await places.LocateAsync(place, cancellationToken);
        if (found.TimeZone is not null && TryZone(found.TimeZone) is { } zone) return (found.Name, zone);
        throw new ToolException($"I couldn't find the time zone for {place}");
    }

    /// <summary>
    /// Offline: IANA ids end in a city ("Asia/Tokyo"), and Windows zones list
    /// cities in their display names ("(UTC+09:00) Osaka, Sapporo, Tokyo").
    /// </summary>
    public static TimeZoneInfo? KnownZone(string place)
    {
        var key = SpokenText.Key(place);
        foreach (var zone in TimeZoneInfo.GetSystemTimeZones())
        {
            var id = zone.Id;
            if (!id.Contains('/') && TimeZoneInfo.TryConvertWindowsIdToIanaId(id, out var iana)) id = iana;
            if (SpokenText.Key(id.Split('/').Last().Replace('_', ' ')) == key) return zone;
            var cities = zone.DisplayName.Split(')', 2).Last().Split(',').Select(c => SpokenText.Key(c));
            if (cities.Contains(key)) return zone;
        }
        return null;
    }

    private static TimeZoneInfo? TryZone(string id)
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    private static readonly HashSet<string> TimePhrases =
    [
        "time", "what time is it", "whats the time", "what is the time", "tell me the time", "current time",
        "what time it is", "do you know what time it is", "what s the time", "the time",
    ];

    private static readonly HashSet<string> DatePhrases =
    [
        "date", "whats the date", "what s the date", "what is the date", "what is today s date", "what s today s date",
        "whats todays date", "todays date", "today s date", "what day is it", "what day is today", "what s today",
        "whats today", "what is today", "what date is it", "what is the date today", "what s the date today", "the date",
    ];

    protected override CurrentTimeArguments? Direct(DirectCommand command)
    {
        var text = command.Text;
        string? place = null;
        var index = text.IndexOf(" in ", StringComparison.Ordinal);
        if (index >= 0)
        {
            place = text[(index + 4)..];
            text = text[..index];
        }
        if (TimePhrases.Contains(text) || (place is not null && text.StartsWith("time", StringComparison.Ordinal))) return new("time", place);
        return DatePhrases.Contains(text) ? new("date", place) : null;
    }
}
