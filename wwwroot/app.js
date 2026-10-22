const rangeSelect = document.querySelector("#range");
const notice = document.querySelector("#notice");
const connection = document.querySelector("#connection");
const analyzeButton = document.querySelector("#analyze-button");
const analysisMessage = document.querySelector("#analysis-message");
const chartStartInput = document.querySelector("#chart-start");
const chartEndInput = document.querySelector("#chart-end");
const chartPrecisionInput = document.querySelector("#chart-precision");
const chartPrecisionValue = document.querySelector("#chart-precision-value");
const chartRangeError = document.querySelector("#chart-range-error");
const applyChartRangeButton = document.querySelector("#apply-chart-range");
const numberFormat = new Intl.NumberFormat("sr-RS", { maximumFractionDigits: 1 });

const chartDefinitions = {
  cpu: { id: "cpu-chart", color: "#5e9bff", percent: true, valueId: "cpu-value" },
  memory: { id: "memory-chart", color: "#b085ff", percent: true, valueId: "memory-value" },
  disk: { id: "disk-chart", color: "#42d6b0", percent: true, valueId: "disk-value" }
};

function formatPercent(value) {
  return `${numberFormat.format(value)}%`;
}

function formatRate(value) {
  if (!Number.isFinite(value) || value < 0) return "—";
  if (value < 1024) return `${numberFormat.format(value)} B/s`;
  if (value < 1024 * 1024) return `${numberFormat.format(value / 1024)} KiB/s`;
  return `${numberFormat.format(value / (1024 * 1024))} MiB/s`;
}

function formatPrecision(seconds) {
  if (seconds < 60) return `${seconds} sekundi`;
  const minutes = Math.floor(seconds / 60);
  const remainingSeconds = seconds % 60;
  return remainingSeconds === 0 ? `${minutes} min` : `${minutes} min ${remainingSeconds} s`;
}

function setDefaultChartRange() {
  const end = new Date();
  const start = new Date(end.getTime() - 60 * 60 * 1000);
  const localInputValue = date => {
    const offsetDate = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);
    return offsetDate.toISOString().slice(0, 16);
  };
  chartStartInput.value = localInputValue(start);
  chartEndInput.value = localInputValue(end);
}

function getChartQuery() {
  const start = new Date(chartStartInput.value);
  const end = new Date(chartEndInput.value);
  if (!Number.isFinite(start.getTime()) || !Number.isFinite(end.getTime())) {
    throw new Error("Unesi ispravan početni i krajnji datum.");
  }
  if (start >= end) {
    throw new Error("Početni datum mora biti pre krajnjeg datuma.");
  }
  if (end > new Date()) {
    throw new Error("Krajnji datum ne može biti u budućnosti.");
  }
  if (end.getTime() - start.getTime() > 7 * 24 * 60 * 60 * 1000) {
    throw new Error("Izabrani opseg ne može biti duži od 7 dana.");
  }

  return new URLSearchParams({
    start: start.toISOString(),
    end: end.toISOString(),
    aggregateSeconds: chartPrecisionInput.value
  });
}

function showNotice(message) {
  notice.textContent = message;
  notice.hidden = !message;
}

async function refreshStatus() {
  try {
    const response = await fetch("/api/status");
    if (!response.ok) throw new Error("Status servera nije dostupan.");
    const status = await response.json();
    connection.textContent = status.message;
    connection.classList.toggle("connected", status.connected);
    connection.classList.toggle("disconnected", !status.connected);
    if (!status.configured) {
      showNotice("InfluxDB radi, ali aplikacija još nije podešena. Dodaj org, bucket i token kao environment varijable, pa ponovo pokreni aplikaciju.");
    }
  } catch (error) {
    connection.textContent = "Nema veze sa aplikacijom.";
    connection.classList.remove("connected");
    connection.classList.add("disconnected");
    showNotice(error.message);
  }
}

function drawChart(elementId, series, options = {}) {
  const element = document.getElementById(elementId);
  const allPoints = series.flatMap(item => item.points);
  if (allPoints.length === 0) {
    element.innerHTML = '<div class="empty-chart">Još nema podataka za izabrani period</div>';
    return;
  }

  const width = 800;
  const height = 220;
  const left = 48;
  const right = 12;
  const top = 12;
  const bottom = 28;
  const plotWidth = width - left - right;
  const plotHeight = height - top - bottom;
  const minTime = Math.min(...allPoints.map(point => new Date(point.time).getTime()));
  const maxTime = Math.max(...allPoints.map(point => new Date(point.time).getTime()));
  const maxValue = options.percent ? 100 : Math.max(1, ...allPoints.map(point => point.value)) * 1.12;
  const x = time => left + (maxTime === minTime ? 0.5 : (new Date(time).getTime() - minTime) / (maxTime - minTime)) * plotWidth;
  const y = value => top + plotHeight - (value / maxValue) * plotHeight;
  const labels = options.percent ? [0, 25, 50, 75, 100] : [0, maxValue / 2, maxValue];

  const grid = labels.map(value => {
    const position = y(value);
    const label = options.percent ? `${Math.round(value)}%` : formatRate(value);
    return `<line class="grid-line" x1="${left}" y1="${position}" x2="${width - right}" y2="${position}"></line>
      <text class="axis-label" x="${left - 8}" y="${position + 3}" text-anchor="end">${label}</text>`;
  }).join("");

  const lines = series.map(item => {
    const points = [...item.points].sort((a, b) => new Date(a.time) - new Date(b.time));
    if (points.length === 1) {
      return `<circle cx="${x(points[0].time)}" cy="${y(points[0].value)}" r="3" fill="${item.color}"></circle>`;
    }

    const path = points.map((point, index) => `${index === 0 ? "M" : "L"} ${x(point.time)} ${y(point.value)}`).join(" ");
    return `<path class="chart-line" d="${path}" stroke="${item.color}"></path>`;
  }).join("");

  const firstDate = new Date(minTime);
  const lastDate = new Date(maxTime);
  const timeLabel = date => `${date.toLocaleTimeString("sr-RS", { hour: "2-digit", minute: "2-digit" })}`;
  element.innerHTML = `<svg viewBox="0 0 ${width} ${height}" role="img" aria-label="Grafik metrika kroz vreme">
    ${grid}
    ${lines}
    <text class="axis-label" x="${left}" y="${height - 7}">${timeLabel(firstDate)}</text>
    <text class="axis-label" x="${width - right}" y="${height - 7}" text-anchor="end">${timeLabel(lastDate)}</text>
  </svg>`;
}

function updateStat(points, elementId, formatter) {
  const latest = [...points].sort((a, b) => new Date(a.time) - new Date(b.time)).at(-1);
  document.getElementById(elementId).textContent = latest ? formatter(latest.value) : "—";
}

async function refreshMetrics() {
  showNotice("");
  chartRangeError.hidden = true;
  try {
    const query = getChartQuery();
    const response = await fetch(`/api/metrics?${query}`);
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail || result.message || "Nije moguće učitati metrike.");

    for (const [key, definition] of Object.entries(chartDefinitions)) {
      const points = result.metrics[key] || [];
      drawChart(definition.id, [{ points, color: definition.color }], { percent: definition.percent });
      updateStat(points, definition.valueId, formatPercent);
    }

    const received = result.metrics.networkReceived || [];
    const sent = result.metrics.networkSent || [];
    drawChart("network-chart", [
      { points: received, color: "#4ba8ff" },
      { points: sent, color: "#f4a261" }
    ]);
    updateStat(received, "received-value", formatRate);
    updateStat(sent, "sent-value", formatRate);

    if (received.length === 0 && sent.length === 0) {
      const diagnosticResponse = await fetch("/api/diagnostics/network");
      const diagnostic = await diagnosticResponse.json();
      if (!diagnosticResponse.ok) throw new Error(diagnostic.detail || "Ne mogu da proverim Telegraf network podatke.");
      if (diagnostic.samples === 0) {
        showNotice("U poslednjih 15 minuta nema `net` podataka. Proveri da li je u Telegraf konfiguraciji uključen `[[inputs.net]]` i da li Telegraf šalje u ovaj bucket.");
      } else {
        showNotice(`InfluxDB prima net podatke (${diagnostic.fields.join(", ") || "bez prepoznatih polja"}; interfejsi: ${diagnostic.interfaces.join(", ") || "nepoznati"}), ali ne može da izračuna protok. Očekivana polja su bytes_recv i bytes_sent.`);
      }
    }
  } catch (error) {
    showNotice(error.message);
  }
}

async function analyzePeaks() {
  const thresholds = {
    cpuThreshold: document.querySelector("#cpu-threshold").value,
    memoryThreshold: document.querySelector("#memory-threshold").value,
    diskThreshold: document.querySelector("#disk-threshold").value,
    networkThreshold: Number(document.querySelector("#network-threshold").value) * 1024 * 1024
  };
  const query = new URLSearchParams({
    range: rangeSelect.value,
    ...thresholds,
    networkThreshold: String(thresholds.networkThreshold)
  });

  analyzeButton.disabled = true;
  analysisMessage.textContent = "Računam pikove u InfluxDB-ju…";
  document.querySelector("#peak-results").replaceChildren();
  try {
    const response = await fetch(`/api/peaks?${query}`);
    const result = await response.json();
    if (!response.ok) throw new Error(result.detail || result.message || "Analiza pikova nije uspela.");

    if (result.peaks.length === 0) {
      analysisMessage.textContent = `Nije bilo vrednosti iznad podešenih pragova u poslednjih ${rangeSelect.value}.`;
      return;
    }

    analysisMessage.textContent = `${result.peaks.length} pik(ova) iznad praga u poslednjih ${rangeSelect.value}, izračunato Flux upitom.`;
    const results = document.querySelector("#peak-results");
    for (const peak of result.peaks) {
      const isNetwork = peak.metric.startsWith("Mreža");
      const item = document.createElement("article");
      item.className = "peak-item";
      const name = document.createElement("span");
      name.className = "peak-name";
      name.textContent = peak.resource ? `${peak.metric} · ${peak.resource}` : peak.metric;
      const value = document.createElement("strong");
      value.className = "peak-value";
      value.textContent = isNetwork ? formatRate(peak.value) : formatPercent(peak.value);
      const detail = document.createElement("time");
      detail.className = "peak-detail";
      detail.dateTime = peak.time;
      detail.textContent = new Date(peak.time).toLocaleString("sr-RS");
      item.append(name, value, detail);
      results.append(item);
    }
  } catch (error) {
    analysisMessage.textContent = error.message;
  } finally {
    analyzeButton.disabled = false;
  }
}

async function refresh() {
  await refreshStatus();
  if (connection.classList.contains("connected")) await refreshMetrics();
}

chartPrecisionInput.addEventListener("input", () => {
  chartPrecisionValue.textContent = formatPrecision(Number(chartPrecisionInput.value));
});
applyChartRangeButton.addEventListener("click", () => {
  try {
    getChartQuery();
    refreshMetrics();
  } catch (error) {
    chartRangeError.textContent = error.message;
    chartRangeError.hidden = false;
  }
});
rangeSelect.addEventListener("change", () => {
  document.querySelector("#peak-results").replaceChildren();
  analysisMessage.textContent = "Period je promenjen. Pokreni analizu ponovo.";
});
analyzeButton.addEventListener("click", analyzePeaks);
setDefaultChartRange();
chartPrecisionValue.textContent = formatPrecision(Number(chartPrecisionInput.value));
refresh();
window.setInterval(refresh, 60_000);
