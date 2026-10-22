using System.Globalization;
using System.Text;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddHttpClient("influx", client => client.Timeout = TimeSpan.FromSeconds(15));

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/status", async (
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!TryGetConfiguration(configuration, out var influxUrl, out var org, out var bucket, out var token))
    {
        return Results.Ok(new
        {
            connected = false,
            configured = false,
            message = "Podesi org, bucket i read token aplikacije."
        });
    }

    var flux = $$"""
        from(bucket: "{{EscapeFluxString(bucket)}}")
          |> range(start: -1m)
          |> limit(n: 1)
        """;
    var queryResult = await ExecuteFluxQueryAsync(
        clients, influxUrl, org, token, flux, logger, cancellationToken);
    if (queryResult is null)
    {
        return Results.Ok(new
        {
            connected = false,
            configured = true,
            message = "InfluxDB radi, ali provera pristupa nije uspela. Proveri org, bucket i read dozvolu tokena."
        });
    }

    return Results.Ok(new { connected = true, configured = true, message = "Povezano sa InfluxDB-jem." });
});

app.MapGet("/api/metrics", async (
    string? range,
    DateTimeOffset? start,
    DateTimeOffset? end,
    int? aggregateSeconds,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!TryGetConfiguration(configuration, out var influxUrl, out var org, out var bucket, out var token))
    {
        return ConfigurationProblem();
    }

    var pointIntervalSeconds = aggregateSeconds ?? 60;
    if (!TryGetMetricsRange(start, end, pointIntervalSeconds, range, out var rangeFlux, out var window, out var error))
    {
        return Results.BadRequest(new { message = error });
    }

    var csv = await ExecuteFluxQueryAsync(
        clients, influxUrl, org, token, BuildMetricsFluxQuery(bucket, rangeFlux, window), logger, cancellationToken);
    if (csv is null)
    {
        return Results.Problem("InfluxDB upit nije uspeo.", statusCode: StatusCodes.Status502BadGateway);
    }

    var metrics = BuildMetrics(ParseFluxCsv(csv));
    return Results.Ok(new { start, end, aggregateSeconds = pointIntervalSeconds, metrics });
});

app.MapGet("/api/peaks", async (
    string? range,
    double cpuThreshold,
    double memoryThreshold,
    double diskThreshold,
    double networkThreshold,
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!TryGetConfiguration(configuration, out var influxUrl, out var org, out var bucket, out var token))
    {
        return ConfigurationProblem();
    }

    if (!TryGetRange(range, out var duration, out _))
    {
        return Results.BadRequest(new { message = "Dozvoljeni opsezi su 1h, 6h i 24h." });
    }

    if (!IsPercentage(cpuThreshold) || !IsPercentage(memoryThreshold) || !IsPercentage(diskThreshold)
        || !double.IsFinite(networkThreshold) || networkThreshold < 0 || networkThreshold > 1_000_000_000_000)
    {
        return Results.BadRequest(new { message = "Procentualni pragovi moraju biti 0–100, a mrežni prag 0–1.000.000.000.000 B/s." });
    }

    var flux = BuildPeaksFluxQuery(bucket, duration, cpuThreshold, memoryThreshold, diskThreshold, networkThreshold);
    var csv = await ExecuteFluxQueryAsync(clients, influxUrl, org, token, flux, logger, cancellationToken);
    if (csv is null)
    {
        return Results.Problem("InfluxDB upit nije uspeo.", statusCode: StatusCodes.Status502BadGateway);
    }

    var peaks = ParseFluxCsv(csv)
        .Select(record =>
        {
            if (!DateTimeOffset.TryParse(record.GetValueOrDefault("_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
                || !double.TryParse(record.GetValueOrDefault("_value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return null;
            }

            return new PeakResult(
                record.GetValueOrDefault("_field") ?? "metric",
                value,
                time,
                record.GetValueOrDefault("interface")
                    ?? record.GetValueOrDefault("path")
                    ?? record.GetValueOrDefault("device"));
        })
        .Where(peak => peak is not null)
        .Cast<PeakResult>()
        .OrderByDescending(peak => peak.Value)
        .ToArray();

    return Results.Ok(new
    {
        range = duration,
        thresholds = new { cpu = cpuThreshold, memory = memoryThreshold, disk = diskThreshold, networkBytesPerSecond = networkThreshold },
        peaks
    });
});

app.MapGet("/api/diagnostics/network", async (
    IHttpClientFactory clients,
    IConfiguration configuration,
    ILogger<Program> logger,
    CancellationToken cancellationToken) =>
{
    if (!TryGetConfiguration(configuration, out var influxUrl, out var org, out var bucket, out var token))
    {
        return ConfigurationProblem();
    }

    var flux = $$"""
        from(bucket: "{{EscapeFluxString(bucket)}}")
          |> range(start: -15m)
          |> filter(fn: (r) => r["_measurement"] == "net")
          |> keep(columns: ["_time", "_field", "interface"])
          |> limit(n: 200)
        """;

    var csv = await ExecuteFluxQueryAsync(clients, influxUrl, org, token, flux, logger, cancellationToken);
    if (csv is null)
    {
        return Results.Problem("InfluxDB upit nije uspeo.", statusCode: StatusCodes.Status502BadGateway);
    }

    var records = ParseFluxCsv(csv);
    return Results.Ok(new
    {
        fields = records.Select(record => record.GetValueOrDefault("_field")).Where(value => value is not null).Distinct().Order().ToArray(),
        interfaces = records.Select(record => record.GetValueOrDefault("interface")).Where(value => value is not null).Distinct().Order().ToArray(),
        samples = records.Count
    });
});

app.Run();

static bool TryGetConfiguration(
    IConfiguration configuration,
    out string influxUrl,
    out string org,
    out string bucket,
    out string token)
{
    influxUrl = configuration["InfluxDB:Url"] ?? "http://localhost:8086";
    org = Environment.GetEnvironmentVariable("INFLUXDB_ORG") ?? configuration["InfluxDB:Org"] ?? "";
    bucket = Environment.GetEnvironmentVariable("INFLUXDB_BUCKET") ?? configuration["InfluxDB:Bucket"] ?? "";
    token = Environment.GetEnvironmentVariable("INFLUXDB_TOKEN") ?? "";
    return !string.IsNullOrWhiteSpace(org) && !string.IsNullOrWhiteSpace(bucket) && !string.IsNullOrWhiteSpace(token);
}

static IResult ConfigurationProblem() =>
    Results.Problem(
        "Podesi INFLUXDB_ORG, INFLUXDB_BUCKET i INFLUXDB_TOKEN pre učitavanja metrika.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

static bool TryGetRange(string? range, out string duration, out string window)
{
    (duration, window) = range switch
    {
        "6h" => ("6h", "1m"),
        "24h" => ("24h", "1m"),
        null or "" or "1h" => ("1h", "1m"),
        _ => ("", "")
    };

    return duration.Length > 0;
}

static bool TryGetMetricsRange(
    DateTimeOffset? start,
    DateTimeOffset? end,
    int aggregateSeconds,
    string? range,
    out string rangeFlux,
    out string window,
    out string error)
{
    rangeFlux = "";
    window = "";
    error = "";

    if (aggregateSeconds is < 10 or > 300 || aggregateSeconds % 10 != 0)
    {
        error = "Rezolucija mora biti od 10 do 300 sekundi u koracima od 10 sekundi.";
        return false;
    }

    if (start.HasValue != end.HasValue)
    {
        error = "Početni i krajnji datum moraju biti navedeni zajedno.";
        return false;
    }

    window = $"{aggregateSeconds}s";
    if (start.HasValue && end.HasValue)
    {
        if (start.Value >= end.Value)
        {
            error = "Početni datum mora biti pre krajnjeg datuma.";
            return false;
        }

        if (end.Value > DateTimeOffset.UtcNow)
        {
            error = "Krajnji datum ne može biti u budućnosti.";
            return false;
        }

        if (end.Value - start.Value > TimeSpan.FromDays(7))
        {
            error = "Izabrani opseg ne može biti duži od 7 dana.";
            return false;
        }

        rangeFlux = $"range(start: time(v: \"{start.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}\"), stop: time(v: \"{end.Value.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}\"))";
        return true;
    }

    if (!TryGetRange(range, out var duration, out _))
    {
        error = "Dozvoljeni opsezi su 1h, 6h i 24h.";
        return false;
    }

    rangeFlux = $"range(start: -{duration})";
    return true;
}

static bool IsPercentage(double value) => double.IsFinite(value) && value is >= 0 and <= 100;

static async Task<string?> ExecuteFluxQueryAsync(
    IHttpClientFactory clients,
    string influxUrl,
    string org,
    string token,
    string flux,
    ILogger logger,
    CancellationToken cancellationToken)
{
    using var request = new HttpRequestMessage(
        HttpMethod.Post,
        $"{influxUrl.TrimEnd('/')}/api/v2/query?org={Uri.EscapeDataString(org)}");
    request.Headers.TryAddWithoutValidation("Authorization", $"Token {token}");
    request.Headers.Accept.ParseAdd("application/csv");
    request.Content = new StringContent(flux, Encoding.UTF8, "application/vnd.flux");

    try
    {
        using var response = await clients.CreateClient("influx").SendAsync(request, cancellationToken);
        var csv = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("InfluxDB query returned HTTP {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        return csv;
    }
    catch (HttpRequestException exception)
    {
        logger.LogError(exception, "InfluxDB query failed");
        return null;
    }
    catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
    {
        logger.LogError(exception, "InfluxDB query timed out");
        return null;
    }
}

static string BuildMetricsFluxQuery(string bucket, string rangeFlux, string window)
{
    var escapedBucket = EscapeFluxString(bucket);
    return $$"""
        cpu = from(bucket: "{{escapedBucket}}")
          |> {{rangeFlux}}
          |> filter(fn: (r) => r["_measurement"] == "cpu" and r["_field"] == "usage_idle" and r["cpu"] == "cpu-total")
          |> map(fn: (r) => ({r with _field: "cpu_usage_percent", _value: 100.0 - r._value}))
          |> aggregateWindow(every: {{window}}, fn: mean, createEmpty: false)
        memory = from(bucket: "{{escapedBucket}}")
          |> {{rangeFlux}}
          |> filter(fn: (r) => r["_measurement"] == "mem" and r["_field"] == "used_percent")
          |> map(fn: (r) => ({r with _field: "memory_used_percent"}))
          |> aggregateWindow(every: {{window}}, fn: mean, createEmpty: false)
        disk = from(bucket: "{{escapedBucket}}")
          |> {{rangeFlux}}
          |> filter(fn: (r) => r["_measurement"] == "disk" and r["_field"] == "used_percent")
          |> map(fn: (r) => ({r with _field: "disk_used_percent"}))
          |> aggregateWindow(every: {{window}}, fn: mean, createEmpty: false)
        network = from(bucket: "{{escapedBucket}}")
          |> {{rangeFlux}}
          |> filter(fn: (r) => r["_measurement"] == "net" and (r["_field"] == "bytes_recv" or r["_field"] == "bytes_sent"))
          |> aggregateWindow(every: {{window}}, fn: last, createEmpty: false)
          |> derivative(unit: 1s, nonNegative: true)
          |> map(fn: (r) => ({r with _field: if r._field == "bytes_recv" then "network_received_bytes_per_second" else "network_sent_bytes_per_second"}))
        union(tables: [cpu, memory, disk, network])
          |> keep(columns: ["_time", "_value", "_field", "interface"])
        """;
}

static string BuildPeaksFluxQuery(
    string bucket,
    string duration,
    double cpuThreshold,
    double memoryThreshold,
    double diskThreshold,
    double networkThreshold)
{
    var escapedBucket = EscapeFluxString(bucket);
    var cpu = cpuThreshold.ToString("R", CultureInfo.InvariantCulture);
    var memory = memoryThreshold.ToString("R", CultureInfo.InvariantCulture);
    var disk = diskThreshold.ToString("R", CultureInfo.InvariantCulture);
    var network = networkThreshold.ToString("R", CultureInfo.InvariantCulture);

    return $$"""
        cpu = from(bucket: "{{escapedBucket}}")
          |> range(start: -{{duration}})
          |> filter(fn: (r) => r["_measurement"] == "cpu" and r["_field"] == "usage_idle" and r["cpu"] == "cpu-total")
          |> map(fn: (r) => ({r with _field: "CPU", _value: 100.0 - r._value}))
          |> filter(fn: (r) => r._value >= {{cpu}})
          |> max()
        memory = from(bucket: "{{escapedBucket}}")
          |> range(start: -{{duration}})
          |> filter(fn: (r) => r["_measurement"] == "mem" and r["_field"] == "used_percent")
          |> map(fn: (r) => ({r with _field: "Memorija", _value: float(v: r._value)}))
          |> filter(fn: (r) => r._value >= {{memory}})
          |> max()
        disk = from(bucket: "{{escapedBucket}}")
          |> range(start: -{{duration}})
          |> filter(fn: (r) => r["_measurement"] == "disk" and r["_field"] == "used_percent")
          |> map(fn: (r) => ({r with _field: "Disk", _value: float(v: r._value)}))
          |> filter(fn: (r) => r._value >= {{disk}})
          |> max()
        network = from(bucket: "{{escapedBucket}}")
          |> range(start: -{{duration}})
          |> filter(fn: (r) => r["_measurement"] == "net" and (r["_field"] == "bytes_recv" or r["_field"] == "bytes_sent"))
          |> aggregateWindow(every: 1m, fn: last, createEmpty: false)
          |> derivative(unit: 1s, nonNegative: true)
          |> map(fn: (r) => ({r with _field: if r._field == "bytes_recv" then "Mreža · primljeno" else "Mreža · poslato"}))
          |> filter(fn: (r) => r._value >= {{network}})
          |> max()
        union(tables: [cpu, memory, disk, network])
          |> keep(columns: ["_time", "_value", "_field", "interface", "path", "device"])
        """;
}

static string EscapeFluxString(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("\"", "\\\"", StringComparison.Ordinal);

static List<Dictionary<string, string>> ParseFluxCsv(string csv)
{
    var rows = new List<Dictionary<string, string>>();
    string[]? headers = null;

    using var reader = new StringReader(csv);
    while (reader.ReadLine() is { } line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            continue;
        }

        var fields = ParseCsvLine(line);
        if (fields.Contains("_time", StringComparer.Ordinal) && fields.Contains("_value", StringComparer.Ordinal))
        {
            headers = fields.ToArray();
            continue;
        }

        if (line.StartsWith('#') || headers is null || fields.Count != headers.Length)
        {
            continue;
        }

        var row = new Dictionary<string, string>(headers.Length, StringComparer.Ordinal);
        for (var index = 0; index < headers.Length; index++)
        {
            row[headers[index]] = fields[index];
        }

        rows.Add(row);
    }

    return rows;
}

static List<string> ParseCsvLine(string line)
{
    var fields = new List<string>();
    var value = new StringBuilder();
    var quoted = false;

    for (var index = 0; index < line.Length; index++)
    {
        var character = line[index];
        if (character == '"')
        {
            if (quoted && index + 1 < line.Length && line[index + 1] == '"')
            {
                value.Append('"');
                index++;
            }
            else
            {
                quoted = !quoted;
            }
        }
        else if (character == ',' && !quoted)
        {
            fields.Add(value.ToString());
            value.Clear();
        }
        else
        {
            value.Append(character);
        }
    }

    fields.Add(value.ToString());
    return fields;
}

static Dictionary<string, IReadOnlyList<MetricPoint>> BuildMetrics(List<Dictionary<string, string>> records)
{
    var cpu = new List<MetricPoint>();
    var memory = new List<MetricPoint>();
    var diskValues = new Dictionary<DateTimeOffset, List<double>>();
    var received = new List<MetricPoint>();
    var sent = new List<MetricPoint>();

    foreach (var record in records)
    {
        if (!DateTimeOffset.TryParse(record.GetValueOrDefault("_time"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            || !double.TryParse(record.GetValueOrDefault("_value"), NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
        {
            continue;
        }

        switch (record.GetValueOrDefault("_field"))
        {
            case "cpu_usage_percent":
                cpu.Add(new MetricPoint(time, Math.Clamp(value, 0, 100)));
                break;
            case "memory_used_percent":
                memory.Add(new MetricPoint(time, Math.Clamp(value, 0, 100)));
                break;
            case "disk_used_percent":
                if (!diskValues.TryGetValue(time, out var values))
                {
                    values = [];
                    diskValues[time] = values;
                }

                values.Add(Math.Clamp(value, 0, 100));
                break;
            case "network_received_bytes_per_second":
                received.Add(new MetricPoint(time, Math.Max(value, 0)));
                break;
            case "network_sent_bytes_per_second":
                sent.Add(new MetricPoint(time, Math.Max(value, 0)));
                break;
        }
    }

    var disk = diskValues.Select(item => new MetricPoint(item.Key, item.Value.Average()));
    return new Dictionary<string, IReadOnlyList<MetricPoint>>
    {
        ["cpu"] = Sort(cpu),
        ["memory"] = Sort(memory),
        ["disk"] = Sort(disk),
        ["networkReceived"] = Sort(received),
        ["networkSent"] = Sort(sent)
    };
}

static List<MetricPoint> Sort(IEnumerable<MetricPoint> points) =>
    points.OrderBy(point => point.Time).ToList();

internal sealed record MetricPoint(DateTimeOffset Time, double Value);
internal sealed record PeakResult(string Metric, double Value, DateTimeOffset Time, string? Resource);
