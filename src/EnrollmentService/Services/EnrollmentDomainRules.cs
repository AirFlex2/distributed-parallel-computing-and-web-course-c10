using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace EnrollmentService.Services;

// Чисті функції предметної області для курсу «Обмін даними у Web-застосунках» (О1.1, О1.5, Д1.1, Д1.4, Д1.5)
public static partial class EnrollmentDomainRules
{
    public const decimal MaxSemesterEcts = 30.0m;

    // Д1.1 та Д1.5: Чиста функція перевірки часового вікна запису [opens_at, closes_at).
    // Поточний час now передається параметром (без звернення до системного годинника всередині функції).
    // Межа opens_at включна (now == opens_at -> true), межа closes_at невключна (now == closes_at -> false).
    public static bool IsWithinEnrollmentWindow(DateTimeOffset now, DateTimeOffset opensAt, DateTimeOffset closesAt)
    {
        return now >= opensAt && now < closesAt;
    }

    // Д1.5: Чиста функція перевірки точного ліміту кредитів ЄКТС за семестр (decimal / numeric(4,1)).
    // Сума 30.0 включно дозволена (true), 30.5 відхиляється (false -> 409 Conflict).
    public static bool CanEnrollWithEcts(decimal currentActiveEcts, decimal disciplineEcts, decimal maxEcts = MaxSemesterEcts)
    {
        return currentActiveEcts >= 0.0m
            && disciplineEcts > 0.0m
            && (currentActiveEcts + disciplineEcts) <= maxEcts;
    }

    // О1.1: Чиста функція перевірки ліміту кількості активних дисциплін K студента
    public static bool CanEnrollWithDisciplineLimit(int currentActiveCount, int maxDisciplines)
    {
        return currentActiveCount >= 0 && currentActiveCount < maxDisciplines;
    }

    // Д1.5: Перетворення моменту часу в часовий пояс університету (Europe/Kyiv) із коректним урахуванням
    // переходу між зимовим (UTC+02:00, EET) та літнім (UTC+03:00, EEST) часовим зміщенням і межі доби
    public static DateTimeOffset ConvertToUniversityTimeZone(DateTimeOffset timestamp, string timeZoneId = "Europe/Kyiv")
    {
        string[] candidateIds = string.Equals(timeZoneId, "Europe/Kyiv", StringComparison.OrdinalIgnoreCase)
            || string.Equals(timeZoneId, "Europe/Kiev", StringComparison.OrdinalIgnoreCase)
            ? [timeZoneId, "Europe/Kyiv", "Europe/Kiev", "FLE Standard Time"]
            : [timeZoneId];

        foreach (string id in candidateIds)
        {
            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(id);
                return TimeZoneInfo.ConvertTime(timestamp, tz);
            }
            catch
            {
                // Пробуємо наступний синонім часового поясу
            }
        }

        // Резервне обчислення зміщення Europe/Kyiv за правилом останньої неділі березня/жовтня о 01:00 UTC
        var utc = timestamp.UtcDateTime;
        var offset = IsEasternEuropeanSummerTimeUtc(utc)
            ? TimeSpan.FromHours(3)
            : TimeSpan.FromHours(2);
        return timestamp.ToOffset(offset);
    }

    // Д1.5: Конвертація моменту часу в часовий пояс університету (Europe/Kyiv) у форматі ISO 8601
    public static string FormatInUniversityTimeZone(DateTimeOffset timestamp, string timeZoneId = "Europe/Kyiv")
    {
        return ConvertToUniversityTimeZone(timestamp, timeZoneId)
            .ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    // Д1.5: Отримання календарної дати в часовому поясі університету (для перевірки переходу через межу доби)
    public static DateOnly GetUniversityLocalDate(DateTimeOffset timestamp, string timeZoneId = "Europe/Kyiv")
    {
        var local = ConvertToUniversityTimeZone(timestamp, timeZoneId);
        return DateOnly.FromDateTime(local.DateTime);
    }

    // Д1.5: Перевірка переходу через межу доби в часовому поясі університету між двома моментами часу
    public static bool HasCrossedUniversityDayBoundary(
        DateTimeOffset firstTimestamp,
        DateTimeOffset secondTimestamp,
        string timeZoneId = "Europe/Kyiv")
    {
        return GetUniversityLocalDate(firstTimestamp, timeZoneId) != GetUniversityLocalDate(secondTimestamp, timeZoneId);
    }

    // Д1.5: Отримання актуального часового зміщення (offset) університету для заданого моменту часу
    public static TimeSpan GetUniversityTimeZoneOffset(DateTimeOffset timestamp, string timeZoneId = "Europe/Kyiv")
    {
        return ConvertToUniversityTimeZone(timestamp, timeZoneId).Offset;
    }

    private static bool IsEasternEuropeanSummerTimeUtc(DateTime utcDateTime)
    {
        int year = utcDateTime.Year;
        DateTime marchStartUtc = GetLastSundayOfMonthUtc(year, 3, 1);   // Останній недільний перехід березня о 01:00 UTC (03:00 -> 04:00)
        DateTime octoberEndUtc = GetLastSundayOfMonthUtc(year, 10, 1);  // Останній недільний перехід жовтня о 01:00 UTC (04:00 -> 03:00)
        return utcDateTime >= marchStartUtc && utcDateTime < octoberEndUtc;
    }

    private static DateTime GetLastSundayOfMonthUtc(int year, int month, int hourUtc)
    {
        int daysInMonth = DateTime.DaysInMonth(year, month);
        var lastDay = new DateTime(year, month, daysInMonth, hourUtc, 0, 0, DateTimeKind.Utc);
        int daysBack = (int)lastDay.DayOfWeek; // Sunday == 0
        return lastDay.AddDays(-daysBack);
    }

    // О1.5: Кодування непрозорого курсора пагінації для пари впорядкування (created_at DESC, id DESC)
    public static string EncodeCursor(DateTimeOffset createdAt, Guid id)
    {
        long utcTicks = createdAt.UtcDateTime.Ticks;
        string raw = $"c10v1|{utcTicks.ToString(CultureInfo.InvariantCulture)}|{id:D}";
        byte[] bytes = Encoding.UTF8.GetBytes(raw);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    // О1.5: Декодування та перевірка цілісності курсора пагінації.
    // Повертає false для будь-якого пошкодженого або синтаксично некоректного курсора (-> 400 Bad Request).
    public static bool TryDecodeCursor(string? cursor, out DateTimeOffset createdAtUtc, out Guid id)
    {
        createdAtUtc = default;
        id = Guid.Empty;

        if (string.IsNullOrWhiteSpace(cursor) || cursor.Length > 256)
        {
            return false;
        }

        try
        {
            string base64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            switch (base64.Length % 4)
            {
                case 2: base64 += "=="; break;
                case 3: base64 += "="; break;
                case 0: break;
                default: return false;
            }

            byte[] bytes = Convert.FromBase64String(base64);
            string decoded = Encoding.UTF8.GetString(bytes);
            string[] parts = decoded.Split('|', StringSplitOptions.None);
            if (parts.Length != 3 || !string.Equals(parts[0], "c10v1", StringComparison.Ordinal))
            {
                return false;
            }

            if (!long.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out long ticks))
            {
                return false;
            }

            if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            {
                return false;
            }

            if (!Guid.TryParseExact(parts[2], "D", out id) || id == Guid.Empty)
            {
                return false;
            }

            createdAtUtc = new DateTimeOffset(ticks, TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // Д1.4: Перевірка заголовка W3C Trace Context (traceparent) за стандартом:
    // version (00) - trace-id (32 шістнадцяткові символи, не всі нулі) - parent-id (16 символів, не всі нулі) - flags (2 символи)
    [GeneratedRegex(@"^00-(?<traceId>(?!0{32})[0-9a-f]{32})-(?<spanId>(?!0{16})[0-9a-f]{16})-(?<flags>[0-9a-f]{2})$", RegexOptions.Compiled)]
    private static partial Regex TraceparentRegex();

    public static bool TryExtractTraceId(string? traceparentHeader, out string? traceId)
    {
        traceId = null;
        if (string.IsNullOrWhiteSpace(traceparentHeader))
        {
            return false;
        }

        var match = TraceparentRegex().Match(traceparentHeader.Trim());
        if (!match.Success)
        {
            return false;
        }

        traceId = match.Groups["traceId"].Value;
        return true;
    }
}
