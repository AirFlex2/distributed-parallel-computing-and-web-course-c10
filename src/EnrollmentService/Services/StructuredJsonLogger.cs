using System.Text.Json;

namespace EnrollmentService.Services;

// Потокобезпечний однорядковий JSON-журнал у stdout (О1.6, Д1.4 Web та Д1.4 РПО).
// Працює напряму через Console.Out навіть при builder.Logging.ClearProviders() і ніколи не виводить секретів/токенів.
public static class StructuredJsonLogger
{
    private static readonly object ConsoleSync = new();

    public static void LogEvent(string instanceId, string eventName, IDictionary<string, object?>? fields = null)
    {
        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["ts"] = DateTimeOffset.UtcNow.ToString("O"),
            ["event"] = eventName,
            ["instance"] = instanceId
        };

        if (fields != null)
        {
            foreach (var kv in fields)
            {
                if (kv.Value != null)
                {
                    payload[kv.Key] = kv.Value;
                }
            }
        }

        WriteRawLocked(JsonSerializer.Serialize(payload));
    }

    public static void LogHttpRequest(IDictionary<string, object?> entry)
    {
        WriteRawLocked(JsonSerializer.Serialize(entry));
    }

    private static void WriteRawLocked(string jsonLine)
    {
        lock (ConsoleSync)
        {
            Console.Out.WriteLine(jsonLine);
            Console.Out.Flush();
        }
    }
}
