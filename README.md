# PC Metrics Dashboard

ASP.NET Core web dashboard for Windows host metrics collected by Telegraf and stored in InfluxDB 2.x.

## Configure

Set the following environment variables in the same PowerShell window where you start the app. Use the organization, bucket, and read token from your InfluxDB setup; do not commit the token or paste it into the browser.

```powershell
$env:INFLUXDB_ORG = "your-organization"
$env:INFLUXDB_BUCKET = "your-telegraf-bucket"
$env:INFLUXDB_TOKEN = "your-read-token"
dotnet run
```

The default InfluxDB URL is `http://localhost:8086`. To use another address, set `InfluxDB:Url` in local configuration or define the `InfluxDB__Url` environment variable.

The dashboard expects standard Telegraf measurements:

- `cpu`: `usage_idle` with the `cpu=cpu-total` tag
- `mem`: `used_percent`
- `disk`: `used_percent`
- `net`: cumulative `bytes_recv` and `bytes_sent` counters

For Windows host networking, ensure the Telegraf config contains and enables `[[inputs.net]]`. It should write the `net` measurement with `bytes_recv` and `bytes_sent` fields and an `interface` tag. The dashboard's **Network diagnostics** request checks those fields from the last 15 minutes and reports what it finds if no rates can be plotted.

The Flux query calculates network rates server-side with `aggregateWindow(fn: last)` followed by `derivative(unit: 1s, nonNegative: true)`. This produces bytes per second and safely ignores counter resets. The charts have independent start/end controls and a point interval from 10 to 300 seconds in 10-second steps; the query rejects ranges longer than seven days. For example, a 1-minute interval yields about 60 points per hour. Chart aggregation is independent of Telegraf's collection interval, which is configured separately in `telegraf.conf`.

The **Peak analysis** section runs a separate Flux query over the selected 1, 6, or 24 hours. Set percentage thresholds for CPU, memory, and disk, plus a network threshold in MiB/s. The query computes per-series maxima in InfluxDB with `max()` and returns the time and interface/device where a threshold was exceeded.

If a chart is empty, the diagnostics endpoint can help distinguish missing Telegraf data from field-name mismatches. Disk usage is averaged across disks in the time-series chart; peak analysis reports maxima for individual disk series.
