using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;

namespace ServiceDashboard.Configuration;

/// <summary>
/// Redacts anything that looks like a password, secret or token from log properties before any sink sees it.
/// Matches on property names (including inside structured objects) and on "password=value" text in strings.
/// </summary>
public sealed partial class SensitiveDataEnricher : ILogEventEnricher
{
    public const string Mask = "[REDACTED]";

    [GeneratedRegex("pass(word|wd)?$|pwd|secret|token|authorization|cookie|apikey|unicodepwd|credential", RegexOptions.IgnoreCase)]
    private static partial Regex SensitiveName();

    [GeneratedRegex("(?<key>\\b(password|passwd|pwd|secret|client_secret|token|access_token|id_token)\\b\\s*[=:]\\s*)(\"[^\"]*\"|'[^']*'|[^\\s,;&]+)", RegexOptions.IgnoreCase)]
    private static partial Regex InlineSecret();

    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var p in logEvent.Properties.ToList())
        {
            var redacted = Redact(p.Key, p.Value);
            if (!ReferenceEquals(redacted, p.Value))
                logEvent.AddOrUpdateProperty(new LogEventProperty(p.Key, redacted));
        }
    }

    public static string RedactText(string s) => InlineSecret().Replace(s, m => m.Groups["key"].Value + Mask);

    private static LogEventPropertyValue Redact(string? name, LogEventPropertyValue value)
    {
        if (name != null && SensitiveName().IsMatch(name)) return new ScalarValue(Mask);

        switch (value)
        {
            case ScalarValue { Value: string s }:
                var cleaned = RedactText(s);
                return ReferenceEquals(cleaned, s) || cleaned == s ? value : new ScalarValue(cleaned);
            case StructureValue sv:
                var props = sv.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Name, p.Value))).ToList();
                return new StructureValue(props, sv.TypeTag);
            case SequenceValue seq:
                return new SequenceValue(seq.Elements.Select(e => Redact(null, e)));
            case DictionaryValue dv:
                return new DictionaryValue(dv.Elements.Select(kv =>
                    new KeyValuePair<ScalarValue, LogEventPropertyValue>(kv.Key,
                        Redact(kv.Key.Value as string, kv.Value))));
            default:
                return value;
        }
    }
}
