using System.Globalization;

namespace Saha360.Domain.Rules;

/// <summary>Veritabanında UTC saklanır; ekranda ve formlarda Türkiye saati kullanılır. Dönüşümler yalnız burada yapılır.</summary>
public static class TrTime
{
    public static readonly CultureInfo Culture = new("tr-TR");
    private static readonly TimeZoneInfo Zone = FindZone();

    private static TimeZoneInfo FindZone()
    {
        foreach (var id in new[] { "Europe/Istanbul", "Turkey Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (TimeZoneNotFoundException) { }
            catch (InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "Türkiye", "Türkiye");
    }

    public static DateTime ToLocal(DateTime utc) => TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), Zone);

    public static DateTime ToUtc(DateTime local) =>
        TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), Zone);

    public static DateTime NowLocal => ToLocal(DateTime.UtcNow);
    public static DateTime TodayLocal => NowLocal.Date;

    public static string Format(DateTime? utc, string format = "dd.MM.yyyy HH:mm") =>
        utc is null ? "—" : ToLocal(utc.Value).ToString(format, Culture);

    public static string Date(DateTime? utc) => Format(utc, "dd.MM.yyyy");

    /// <summary>"28.10.2026 08:00 – 17:00" ya da günler farklıysa iki tam tarih.</summary>
    public static string Range(DateTime startUtc, DateTime endUtc)
    {
        var s = ToLocal(startUtc);
        var e = ToLocal(endUtc);
        return s.Date == e.Date
            ? $"{s.ToString("dd.MM.yyyy HH:mm", Culture)} – {e.ToString("HH:mm", Culture)}"
            : $"{s.ToString("dd.MM.yyyy HH:mm", Culture)} – {e.ToString("dd.MM.yyyy HH:mm", Culture)}";
    }

    /// <summary>datetime-local alanı için değer (yyyy-MM-ddTHH:mm).</summary>
    public static string Input(DateTime? utc) => utc is null ? "" : ToLocal(utc.Value).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture);

    public static string Duration(TimeSpan span)
    {
        if (span.TotalMinutes < 1) return "0 dk";
        var parts = new List<string>();
        if (span.Days > 0) parts.Add($"{span.Days} gün");
        if (span.Hours > 0) parts.Add($"{span.Hours} sa");
        if (span.Minutes > 0 && span.Days == 0) parts.Add($"{span.Minutes} dk");
        return string.Join(" ", parts);
    }
}
