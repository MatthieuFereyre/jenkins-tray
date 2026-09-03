namespace JenkinsTray.Services;

public static class Humanize
{
    public static string Duration(TimeSpan value)
    {
        if (value <= TimeSpan.Zero)
            return "—";

        // The unit abbreviations are the same in both languages, so they stay in the code rather
        // than becoming six resource keys that would always hold identical values.
        if (value.TotalSeconds < 60)
            return $"{value.TotalSeconds:0} s";

        if (value.TotalMinutes < 60)
            return value.Seconds == 0 ? $"{value.Minutes} min" : $"{value.Minutes} min {value.Seconds} s";

        return value.Minutes == 0
            ? $"{(int)value.TotalHours} h"
            : $"{(int)value.TotalHours} h {value.Minutes} min";
    }

    public static string RelativeTime(DateTimeOffset value)
    {
        var delta = DateTimeOffset.Now - value;

        if (delta < TimeSpan.Zero)
            return Loc.T("Time_JustNow");

        if (delta.TotalSeconds < 60)
            return Loc.T("Time_JustNow");

        if (delta.TotalMinutes < 60)
            return Loc.T("Time_MinutesAgo", (int)delta.TotalMinutes);

        if (delta.TotalHours < 24)
            return Loc.T("Time_HoursAgo", (int)delta.TotalHours);

        if (delta.TotalDays < 7)
            return Loc.T("Time_DaysAgo", (int)delta.TotalDays);

        return value.LocalDateTime.ToString("d MMM yyyy", Loc.FormatCulture);
    }

    public static string Percent(double? value) =>
        value is null ? "—" : value.Value.ToString("0.#", Loc.FormatCulture) + " %";
}
