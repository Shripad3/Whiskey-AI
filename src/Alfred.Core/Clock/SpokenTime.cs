using System.Globalization;
using System.Text;

namespace Alfred.Core;

/// <summary>
/// A transcript split into words for time parsing. Unlike
/// <see cref="SpokenText.Normalize"/>, keeps "7:30" and "1.5" whole, and folds
/// "a.m." into "am" and "o'clock" into "oclock". <see cref="Raw"/> keeps the
/// user's capitalisation (for a reminder's text); <see cref="Lower"/> is for matching.
/// </summary>
public sealed class SpokenWords
{
    public IReadOnlyList<string> Raw { get; }
    public IReadOnlyList<string> Lower { get; }

    public SpokenWords(string text)
    {
        var chars = text.Replace('’', '\'');
        var cleaned = new StringBuilder();
        for (var i = 0; i < chars.Length; i++)
        {
            var c = chars[i];
            var between = i > 0 && i + 1 < chars.Length && char.IsDigit(chars[i - 1]) && char.IsDigit(chars[i + 1]);
            if (char.IsLetterOrDigit(c)) cleaned.Append(c);
            else if ((c == '.' || c == ':') && between) cleaned.Append(c);
            else if (c == '\'') continue; // "o'clock" → "oclock", "I'm" → "Im"
            else cleaned.Append(' ');
        }
        var raw = new List<string>();
        foreach (var word in cleaned.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var lower = word.ToLowerInvariant();
            // "7 a m" (from "7 a.m.") → "7 am".
            if (lower == "m" && raw.Count >= 2 && raw[^1].ToLowerInvariant() is "a" or "p" && IsNumeric(raw[^2]))
            {
                raw[^1] = raw[^1].ToLowerInvariant() + "m";
                continue;
            }
            // "7am", "7:30pm" → "7", "am".
            if (lower.Length > 2 && (lower.EndsWith("am", StringComparison.Ordinal) || lower.EndsWith("pm", StringComparison.Ordinal)) && IsNumeric(lower[..^2]))
            {
                raw.Add(word[..^2]);
                raw.Add(lower[^2..]);
                continue;
            }
            raw.Add(word);
        }
        // "o clock" → "oclock"; "stop watch" → "stopwatch".
        var merged = new List<string>();
        foreach (var word in raw)
        {
            var lower = word.ToLowerInvariant();
            if (lower == "clock" && merged.Count > 0 && merged[^1].ToLowerInvariant() == "o") merged[^1] = "oclock";
            else if (lower == "watch" && merged.Count > 0 && merged[^1].ToLowerInvariant() == "stop") merged[^1] = "stopwatch";
            else merged.Add(word);
        }
        Raw = merged;
        Lower = merged.Select(w => w.ToLowerInvariant()).ToList();
    }

    public int Count => Lower.Count;
    public bool Contains(string word) => Lower.Contains(word);
    public bool ContainsAny(IEnumerable<string> words) => words.Any(Lower.Contains);
    public string Joined => string.Join(' ', Lower);

    /// <summary>The raw words at these positions, in order.</summary>
    public string Text(IEnumerable<int> indices) => string.Join(' ', indices.Order().Select(i => Raw[i]));

    public static bool IsNumeric(string word) =>
        word.Length > 0 && char.IsDigit(word[0]) && word.All(c => char.IsDigit(c) || c == '.' || c == ':');

    private static readonly Dictionary<string, int> Ones = new()
    {
        ["zero"] = 0, ["one"] = 1, ["two"] = 2, ["three"] = 3, ["four"] = 4, ["five"] = 5, ["six"] = 6, ["seven"] = 7, ["eight"] = 8,
        ["nine"] = 9, ["ten"] = 10, ["eleven"] = 11, ["twelve"] = 12, ["thirteen"] = 13, ["fourteen"] = 14, ["fifteen"] = 15,
        ["sixteen"] = 16, ["seventeen"] = 17, ["eighteen"] = 18, ["nineteen"] = 19,
    };

    private static readonly Dictionary<string, int> Tens = new()
    {
        ["twenty"] = 20, ["thirty"] = 30, ["forty"] = 40, ["fifty"] = 50, ["sixty"] = 60, ["seventy"] = 70, ["eighty"] = 80, ["ninety"] = 90,
    };

    public static readonly IReadOnlySet<string> NumberWords = Ones.Keys.Concat(Tens.Keys).ToHashSet();

    /// <summary>A number starting at <paramref name="index"/>: "10", "1.5", "twenty five", "seven".</summary>
    public (double Value, int Length)? Number(int index)
    {
        if (index >= Count) return null;
        var word = Lower[index];
        if (!word.Contains(':') && double.TryParse(word, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value)) return (value, 1);
        if (Ones.TryGetValue(word, out var one)) return (one, 1);
        if (Tens.TryGetValue(word, out var ten))
        {
            if (index + 1 < Count && Ones.TryGetValue(Lower[index + 1], out var unit) && unit is >= 1 and <= 9) return (ten + unit, 2);
            return (ten, 1);
        }
        return null;
    }
}

/// <summary>
/// Lengths of time as people say them: "10 minutes", "an hour and a half",
/// "half an hour", "1 hour 30 minutes", "ninety seconds", "1.5 hours".
/// </summary>
public static class SpokenDuration
{
    public static double? Unit(string word) => word switch
    {
        "hour" or "hours" or "hr" or "hrs" => 3600,
        "minute" or "minutes" or "min" or "mins" => 60,
        "second" or "seconds" or "sec" or "secs" => 1,
        _ => null,
    };

    public static readonly IReadOnlySet<string> UnitWords =
        new HashSet<string> { "hour", "hours", "hr", "hrs", "minute", "minutes", "min", "mins", "second", "seconds", "sec", "secs" };

    /// <summary>Parses a duration said on its own ("10 minutes"), in seconds.</summary>
    public static double? Parse(string text) => Find(new SpokenWords(text))?.Seconds;

    /// <summary>Every duration said, in order: "one of 10 seconds, one of 20 seconds" → [10, 20].</summary>
    public static List<double> All(string text)
    {
        var words = new SpokenWords(text);
        var found = new List<double>();
        var start = 0;
        while (Find(words, start) is { } hit)
        {
            found.Add(hit.Seconds);
            start = hit.End;
        }
        return found;
    }

    /// <summary>The first duration in the words, and the word range [Start, End) that said it.</summary>
    public static (double Seconds, int Start, int End)? Find(SpokenWords words, int start = 0)
    {
        for (var index = start; index < words.Count; index++)
        {
            if (Duration(words, index) is { } found) return found;
        }
        return null;
    }

    /// <summary>A duration starting exactly at <paramref name="index"/>.</summary>
    public static (double Seconds, int Start, int End)? Duration(SpokenWords words, int index)
    {
        double total = 0;
        var position = index;
        var matched = false;
        while (Component(words, position) is { } part)
        {
            total += part.Seconds;
            matched = true;
            position = part.Next;
            // "1 hour and 30 minutes".
            if (position < words.Count && words.Lower[position] == "and" && Component(words, position + 1) is not null) position++;
        }
        return matched ? (total, index, position) : null;
    }

    /// <summary>One "&lt;number&gt; &lt;unit&gt;" piece, with its halves and quarters.</summary>
    private static (double Seconds, int Next)? Component(SpokenWords words, int index)
    {
        var w = words.Lower;
        string? Word(int offset) => index + offset < w.Count ? w[index + offset] : null;
        static bool IsA(string? word) => word is "a" or "an";

        // "half an hour", "half a minute".
        if (Word(0) == "half" && IsA(Word(1)) && Word(2) is { } u1 && Unit(u1) is { } half) return (half / 2, index + 3);
        // "a half hour", "a quarter hour".
        if (IsA(Word(0)) && Word(1) is { } f && Fraction(f) is { } fraction && Word(2) is { } u2 && Unit(u2) is { } unit2)
            return (unit2 * fraction, index + 3);
        if (Word(0) == "quarter" && Word(1) == "of" && IsA(Word(2)) && Word(3) is { } u3 && Unit(u3) is { } unit3) return (unit3 / 4, index + 4);
        if (Word(0) == "a" && Word(1) == "quarter" && Word(2) == "of" && IsA(Word(3)) && Word(4) is { } u4 && Unit(u4) is { } unit4)
            return (unit4 / 4, index + 5);

        // "10 minutes", "an hour", "one and a half hours".
        double value;
        int position;
        if (words.Number(index) is { } number)
        {
            value = number.Value;
            position = index + number.Length;
        }
        else if (IsA(Word(0)) && Word(1) is { } next && Unit(next) is not null)
        {
            value = 1;
            position = index + 1;
        }
        else
        {
            return null;
        }
        if (AndAHalf(words, position) is { } afterHalf)
        {
            value += 0.5;
            position = afterHalf;
        }
        if (position >= w.Count || Unit(w[position]) is not { } unit) return null;
        position++;
        var seconds = value * unit;
        // "an hour and a half".
        if (AndAHalf(words, position) is { } trailing)
        {
            seconds += unit / 2;
            position = trailing;
        }
        return (seconds, position);
    }

    private static double? Fraction(string word) => word switch { "half" => 0.5, "quarter" => 0.25, _ => null };

    private static int? AndAHalf(SpokenWords words, int index)
    {
        var w = words.Lower;
        if (index + 2 < w.Count && w[index] == "and" && w[index + 1] == "a" && w[index + 2] == "half") return index + 3;
        if (index + 1 < w.Count && w[index] == "and" && w[index + 1] == "half") return index + 2;
        return null;
    }
}

public enum Meridiem { Am, Pm }

/// <summary>
/// A point in time as people say it: "7 am", "tomorrow at 6:30", "half past
/// 5", "noon", "in 20 minutes", "Monday at 9", "tonight at 8". Works in local
/// wall-clock time: <c>now</c> is whatever the caller's clock says.
/// </summary>
public sealed record SpokenWhen(DateTime Date, bool HasTime, IReadOnlySet<int> Consumed)
{
    /// <summary>Parses a time said on its own ("7:30 am tomorrow").</summary>
    public static SpokenWhen? Parse(string text, DateTime now) => Find(new SpokenWords(text), now);

    public static SpokenWhen? Find(SpokenWords words, DateTime now, int start = 0)
    {
        var w = words.Lower;
        if (start >= w.Count) return null;

        // Relative: "in 20 minutes", "20 minutes from now".
        for (var index = start; index < w.Count; index++)
        {
            if (w[index] == "in" && SpokenDuration.Duration(words, index + 1) is { } ahead)
                return new SpokenWhen(now.AddSeconds(ahead.Seconds), true, Range(index, ahead.End));
            if (SpokenDuration.Duration(words, index) is { } span && span.End + 1 < w.Count && w[span.End] == "from" && w[span.End + 1] == "now")
                return new SpokenWhen(now.AddSeconds(span.Seconds), true, Range(span.Start, span.End + 2));
        }

        var consumed = new HashSet<int>();
        var day = Day(words, start, now, consumed);
        var time = Time(words, start, consumed);
        if (time is not null) consumed.UnionWith(time.Consumed);
        if (day is null && time is null) return null;

        var hint = time?.Meridiem ?? day?.Meridiem;
        if (time is null) return new SpokenWhen(day!.Value.Date.Date, false, consumed);

        var hour = time.Hour;
        if (time.Meridiem is null && day?.Meridiem == Alfred.Core.Meridiem.Pm && hour < 12) hint = Alfred.Core.Meridiem.Pm;
        if (hint == Alfred.Core.Meridiem.Am && hour == 12) hour = 0;
        if (hint == Alfred.Core.Meridiem.Pm && hour < 12) hour += 12;

        if (day is { } d)
        {
            // "tomorrow at 7": the hour as said (7 am) unless a hint says pm.
            return new SpokenWhen(At(d.Date, hour % 24, time.Minute), true, consumed);
        }
        // No day: the next time that hour comes round. "7" at 10 pm is 7 am
        // tomorrow; at 10 am it is 7 pm today.
        int[] hours = hint is null && hour is >= 1 and <= 12 ? [hour % 12, hour % 12 + 12] : [hour % 24];
        var date = hours.Select(h => Next(h, time.Minute, now)).Min();
        return new SpokenWhen(date, true, consumed);
    }

    private static HashSet<int> Range(int start, int end) => Enumerable.Range(start, Math.Max(0, end - start)).ToHashSet();

    public static DateTime At(DateTime day, int hour, int minute) => new(day.Year, day.Month, day.Day, hour, minute, 0, day.Kind);

    private static DateTime Next(int hour, int minute, DateTime now)
    {
        var today = At(now, hour, minute);
        return today > now ? today : today.AddDays(1);
    }

    /// <summary>
    /// Every time of day said, as (hour, minute, whether am/pm was said):
    /// "seven on weekdays and eight on weekends" → [(7, 0), (8, 0)].
    /// </summary>
    public static List<(int Hour, int Minute, bool Exact)> ClockTimes(string text)
    {
        var words = new SpokenWords(text);
        var times = new List<(int, int, bool)>();
        var index = 0;
        while (index < words.Count)
        {
            // Bare numbers count here: the model restated them, so they only need to match.
            if (TimeOfDayAt(words, index) is { } found)
            {
                var hour = found.Hour;
                if (found.Meridiem == Alfred.Core.Meridiem.Pm && hour < 12) hour += 12;
                if (found.Meridiem == Alfred.Core.Meridiem.Am && hour == 12) hour = 0;
                times.Add((hour, found.Minute, found.Meridiem is not null));
                index = Math.Max(index + 1, found.Consumed.Count > 0 ? found.Consumed.Max() + 1 : index + 1);
            }
            else
            {
                index++;
            }
        }
        return times;
    }

    /// <summary>True when a time the model gave was among those the user said.</summary>
    public static bool WasSaid(DateTime date, string text) =>
        ClockTimes(text).Any(said => said.Minute == date.Minute && (said.Exact ? said.Hour == date.Hour : said.Hour % 12 == date.Hour % 12));

    // Day

    private static readonly string[] Weekdays = ["sunday", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday"];

    private static (DateTime Date, Meridiem? Meridiem)? Day(SpokenWords words, int start, DateTime now, HashSet<int> consumed)
    {
        var w = words.Lower;
        for (var index = start; index < w.Count; index++)
        {
            var word = w[index];
            void Take(int first, int last)
            {
                for (var i = first; i <= last; i++) consumed.Add(i);
                // "on Monday", "by tomorrow".
                if (first > start && w[first - 1] is "on" or "by" or "for") consumed.Add(first - 1);
            }
            if (word == "tomorrow")
            {
                // "the day after tomorrow".
                if (index >= 2 && w[index - 1] == "after" && w[index - 2] == "day")
                {
                    Take(index >= 3 && w[index - 3] == "the" ? index - 3 : index - 2, index);
                    return (now.AddDays(2), null);
                }
                Take(index, index);
                return (now.AddDays(1), MeridiemAfter(index, w, consumed));
            }
            if (word == "tonight")
            {
                Take(index, index);
                return (now, Alfred.Core.Meridiem.Pm);
            }
            if (word == "today")
            {
                Take(index, index);
                return (now, null);
            }
            if (word == "this" && index + 1 < w.Count && PartOfDay(w[index + 1]) is { } part)
            {
                Take(index, index + 1);
                return (now, part);
            }
            var weekday = Array.IndexOf(Weekdays, word);
            if (weekday >= 0)
            {
                // "next Monday" and "this Monday" both mean the coming one.
                Take(index > 0 && w[index - 1] is "next" or "this" ? index - 1 : index, index);
                var ahead = (weekday - (int)now.DayOfWeek + 7) % 7;
                if (ahead == 0) ahead = 7;
                return (now.AddDays(ahead), MeridiemAfter(index, w, consumed));
            }
        }
        return null;
    }

    /// <summary>"tomorrow morning", "Monday evening".</summary>
    private static Meridiem? MeridiemAfter(int index, IReadOnlyList<string> w, HashSet<int> consumed)
    {
        if (index + 1 >= w.Count || PartOfDay(w[index + 1]) is not { } part) return null;
        consumed.Add(index + 1);
        return part;
    }

    private static Meridiem? PartOfDay(string word) => word switch
    {
        "morning" => Alfred.Core.Meridiem.Am,
        "afternoon" or "evening" or "night" => Alfred.Core.Meridiem.Pm,
        _ => null,
    };

    // Time of day

    public sealed class TimeOfDay
    {
        public int Hour;
        public int Minute;
        public Meridiem? Meridiem;
        public HashSet<int> Consumed = [];
        /// <summary>How sure: a meridiem or "7:30" beats "at 7", which beats "for 7".</summary>
        public int Score;
    }

    private static readonly Dictionary<string, int> Prefixes = new() { ["at"] = 2, ["for"] = 1, ["by"] = 1, ["around"] = 1, ["till"] = 1, ["until"] = 1 };

    public static TimeOfDay? Time(SpokenWords words, int start, IReadOnlySet<int> excluding)
    {
        TimeOfDay? best = null;
        for (var index = start; index < words.Count; index++)
        {
            if (excluding.Contains(index) || TimeOfDayAt(words, index) is not { Score: > 0 } found) continue;
            if (best is null || found.Score >= best.Score) best = found;
        }
        return best;
    }

    private static TimeOfDay? TimeOfDayAt(SpokenWords words, int index)
    {
        var w = words.Lower;
        int hour;
        var minute = 0;
        int position;
        var score = 0;
        var first = index;

        switch (w[index])
        {
            case "noon" or "midday" or "midnight":
            {
                var consumed = new HashSet<int> { index };
                if (index > 0 && Prefixes.ContainsKey(w[index - 1])) consumed.Add(index - 1);
                var noon = w[index] != "midnight";
                return new TimeOfDay { Hour = noon ? 12 : 0, Meridiem = noon ? Alfred.Core.Meridiem.Pm : Alfred.Core.Meridiem.Am, Consumed = consumed, Score = 3 };
            }
            case "half" when index + 1 < w.Count && words.Number(index + 1) is { } british
                                && british.Value == Math.Round(british.Value) && (int)british.Value is >= 1 and <= 12
                                && (index + 1 + british.Length >= w.Count || SpokenDuration.Unit(w[index + 1 + british.Length]) is null):
            {
                // "half six": British for 6:30.
                hour = (int)british.Value;
                minute = 30;
                position = index + 1 + british.Length;
                score = 2;
                break;
            }
            case "half" or "quarter":
            {
                // "half past 7", "quarter to 8".
                if (index + 2 >= w.Count || w[index + 1] is not ("past" or "to") || words.Number(index + 2) is not { } n
                    || n.Value != Math.Round(n.Value) || (int)n.Value is < 1 or > 12) return null;
                var offset = w[index] == "half" ? 30 : 15;
                if (w[index + 1] == "past")
                {
                    hour = (int)n.Value;
                    minute = offset;
                }
                else
                {
                    hour = (int)n.Value - 1;
                    minute = 60 - offset;
                    if (hour == 0) hour = 12;
                }
                position = index + 2 + n.Length;
                score = 2;
                break;
            }
            default:
            {
                var word = w[index];
                if (word.Contains(':'))
                {
                    var parts = word.Split(':');
                    if (parts.Length != 2 || !int.TryParse(parts[0], out var h) || !int.TryParse(parts[1], out var m) || h is < 0 or > 23 || m is < 0 or > 59)
                        return null;
                    hour = h;
                    minute = m;
                    position = index + 1;
                    score = 2;
                }
                else if (word.Length is >= 3 and <= 4 && !word.Contains('.') && int.TryParse(word, out var digits))
                {
                    // "730", "1930".
                    int h = digits / 100, m = digits % 100;
                    if (h is < 0 or > 23 || m is < 0 or > 59) return null;
                    hour = h;
                    minute = m;
                    position = index + 1;
                    score = 1;
                }
                else if (words.Number(index) is { } number && number.Value == Math.Round(number.Value))
                {
                    var n = (int)number.Value;
                    position = index + number.Length;
                    if (position + 1 < w.Count && w[position] is "past" or "to" && n is >= 1 and <= 30
                        && words.Number(position + 1) is { } hh && hh.Value == Math.Round(hh.Value) && (int)hh.Value is >= 1 and <= 12)
                    {
                        // "25 past 7", "10 to 8".
                        var before = w[position] == "to";
                        hour = before ? (int)hh.Value - 1 : (int)hh.Value;
                        if (hour == 0) hour = 12;
                        minute = before ? 60 - n : n;
                        position += 1 + hh.Length;
                        score = 2;
                    }
                    else
                    {
                        if (n is < 0 or > 24) return null;
                        hour = n % 24;
                        // "seven thirty", "7 45", "seven oh five".
                        if (position < w.Count && SpokenDuration.Unit(w[position]) is null)
                        {
                            if (w[position] == "oh" && words.Number(position + 1) is { } oh && (int)oh.Value is >= 1 and <= 9)
                            {
                                minute = (int)oh.Value;
                                position += 1 + oh.Length;
                                score = 1;
                            }
                            else if (words.Number(position) is { } mm && mm.Value == Math.Round(mm.Value) && (int)mm.Value is >= 10 and <= 59)
                            {
                                minute = (int)mm.Value;
                                position += mm.Length;
                                score = 1;
                            }
                        }
                        // "10 minutes" is a duration, not a time.
                        if (position < w.Count && SpokenDuration.Unit(w[position]) is not null) return null;
                    }
                }
                else
                {
                    return null;
                }
                break;
            }
        }

        if (position < w.Count && w[position] == "oclock")
        {
            position++;
            score = Math.Max(score, 2);
        }
        Meridiem? meridiem = null;
        if (position < w.Count)
        {
            switch (w[position])
            {
                case "am":
                    meridiem = Alfred.Core.Meridiem.Am;
                    position++;
                    break;
                case "pm":
                    meridiem = Alfred.Core.Meridiem.Pm;
                    position++;
                    break;
                case "in" when position + 2 < w.Count && w[position + 1] == "the":
                    if (PartOfDay(w[position + 2]) is { } part)
                    {
                        meridiem = part;
                        position += 3;
                    }
                    break;
                case "at" when position + 1 < w.Count && w[position + 1] == "night":
                    meridiem = Alfred.Core.Meridiem.Pm;
                    position += 2;
                    break;
                case "tonight":
                    meridiem = Alfred.Core.Meridiem.Pm;
                    position++;
                    break;
            }
        }
        if (meridiem is not null) score = 3;
        if (index > 0 && Prefixes.TryGetValue(w[index - 1], out var prefix))
        {
            score += prefix;
            first = index - 1;
        }
        if (hour > 23) return null;
        if (meridiem is not null && hour > 12) meridiem = null;
        return new TimeOfDay { Hour = hour, Minute = minute, Meridiem = meridiem, Consumed = Range(first, position), Score = score };
    }
}

/// <summary>How times and lengths are said back.</summary>
public static class ClockFormat
{
    /// <summary>"1 hour 30 minutes", "9 minutes 41 seconds". Seconds are dropped from an hour or more.</summary>
    public static string Spoken(double seconds)
    {
        var total = (int)Math.Round(seconds);
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add(Plural(hours, "hour"));
        if (minutes > 0) parts.Add(Plural(minutes, "minute"));
        if (secs > 0 && hours == 0) parts.Add(Plural(secs, "second"));
        return parts.Count == 0 ? "0 seconds" : string.Join(' ', parts);
    }

    /// <summary>"10 minute", "1 hour 30 minute": for "a 10 minute timer".</summary>
    public static string Adjective(double seconds)
    {
        var total = (int)Math.Round(seconds);
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        var parts = new List<string>();
        if (hours > 0) parts.Add($"{hours} hour");
        if (minutes > 0) parts.Add($"{minutes} minute");
        if (secs > 0 && hours == 0) parts.Add($"{secs} second");
        return string.Join(' ', parts);
    }

    /// <summary>"9:41", "1:02:03": for the pill and the tray.</summary>
    public static string Clock(double seconds)
    {
        var total = Math.Max(0, (int)Math.Ceiling(seconds));
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}" : $"{minutes}:{secs:00}";
    }

    /// <summary>"1:23.4": a stopwatch reading.</summary>
    public static string Stopwatch(double seconds)
    {
        var tenths = (int)Math.Floor(seconds * 10);
        var total = tenths / 10;
        int hours = total / 3600, minutes = total % 3600 / 60, secs = total % 60;
        return hours > 0 ? $"{hours}:{minutes:00}:{secs:00}.{tenths % 10}" : $"{minutes}:{secs:00}.{tenths % 10}";
    }

    /// <summary>"7:00 AM" in the user's own format.</summary>
    public static string Time(DateTime date) => date.ToString("t", CultureInfo.CurrentCulture);

    /// <summary>"7:00 AM", "tomorrow at 7:00 AM", "Monday at 7:00 AM", "3 October at 7:00 AM".</summary>
    public static string When(DateTime date, DateTime now, bool hasTime = true)
    {
        string? day;
        if (date.Date == now.Date) day = hasTime ? null : "today";
        else if (date.Date == now.Date.AddDays(1)) day = "tomorrow";
        else if (date < now.AddDays(6)) day = date.ToString("dddd", CultureInfo.InvariantCulture);
        else day = date.ToString("d MMMM", CultureInfo.InvariantCulture);
        if (!hasTime) return day ?? "today";
        return day is null ? Time(date) : $"{day} at {Time(date)}";
    }

    private static string Plural(int count, string unit) => $"{count} {unit}{(count == 1 ? "" : "s")}";
}
