using System.Text;

namespace ServiceDashboard.Services;

/// <summary>CSV output with formula-injection protection: cells starting with = + - @ (or tab/CR) get a leading apostrophe.</summary>
public static class CsvWriter
{
    public static string Cell(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var v = value;
        if (v[0] is '=' or '+' or '-' or '@' or '\t' or '\r') v = "'" + v;
        if (v.AsSpan().IndexOfAny(",\"\r\n") >= 0) v = "\"" + v.Replace("\"", "\"\"") + "\"";
        return v;
    }

    public static string Row(IEnumerable<string?> cells) => string.Join(',', cells.Select(Cell));

    /// <summary>Streams a CSV file straight to the response. Nothing is buffered beyond one row.</summary>
    public static async Task WriteAsync(HttpResponse response, string fileName, IEnumerable<string> header, IAsyncEnumerable<IEnumerable<string?>> rows)
    {
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers.ContentDisposition = $"attachment; filename=\"{fileName}\"";
        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(true), leaveOpen: true);
        await writer.WriteLineAsync(Row(header));
        await foreach (var r in rows) await writer.WriteLineAsync(Row(r));
        await writer.FlushAsync();
    }
}
