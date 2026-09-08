namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Globalization;
    using System.Net.Http;
    using System.Text.Json;
    using System.Threading.Tasks;

    /// <summary>
    /// CPU and GPU temperature from LibreHardwareMonitor's Remote Web Server (http://localhost:8085/data.json).
    /// LibreHardwareMonitor needs admin for its sensor driver, so it runs from a logon task with highest privileges
    /// ("LibreHardwareMonitor (Neon Deck)"); this reader itself needs nothing special.
    /// </summary>
    internal static class LhmTemps
    {
        private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
        private const String Url = "http://localhost:8085/data.json";

        /// <summary>(cpu °C, gpu °C) or null when LibreHardwareMonitor is not reachable. gpu is -1 when absent.</summary>
        public static async Task<(Int32 cpu, Int32 gpu)?> ReadAsync()
        {
            try
            {
                using var doc = JsonDocument.Parse(await Http.GetStringAsync(Url));
                Int32 cpuBest = -1, cpuMax = -1, gpuBest = -1, gpuAny = -1;
                Walk(doc.RootElement, ref cpuBest, ref cpuMax, ref gpuBest, ref gpuAny);
                var cpu = cpuBest >= 0 ? cpuBest : cpuMax;
                var gpu = gpuBest >= 0 ? gpuBest : gpuAny;
                return cpu >= 0 ? (cpu, gpu) : null;
            }
            catch
            {
                return null;
            }
        }

        private static void Walk(JsonElement node, ref Int32 cpuBest, ref Int32 cpuMax, ref Int32 gpuBest, ref Int32 gpuAny)
        {
            if (node.ValueKind != JsonValueKind.Object)
            {
                return;
            }
            var text = node.TryGetProperty("Text", out var t) ? t.GetString() ?? "" : "";
            var sensorId = node.TryGetProperty("SensorId", out var sid) ? sid.GetString() ?? "" : "";
            var value = node.TryGetProperty("Value", out var v) ? v.GetString() ?? "" : "";
            var isTemp = (node.TryGetProperty("Type", out var ty) && ty.GetString() == "Temperature") || value.Contains("°C");
            if (isTemp && TryParseCelsius(value, out var c))
            {
                var id = sensorId.ToLowerInvariant();
                if (id.Contains("cpu"))
                {
                    if (text.Contains("Tctl", StringComparison.OrdinalIgnoreCase) || text.Contains("Package", StringComparison.OrdinalIgnoreCase) || text.Equals("CPU", StringComparison.OrdinalIgnoreCase))
                    {
                        cpuBest = c;
                    }
                    cpuMax = Math.Max(cpuMax, c);
                }
                else if (id.Contains("gpu"))
                {
                    if (text.Contains("Core", StringComparison.OrdinalIgnoreCase))
                    {
                        gpuBest = c;
                    }
                    if (gpuAny < 0)
                    {
                        gpuAny = c;
                    }
                }
            }
            if (node.TryGetProperty("Children", out var children) && children.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in children.EnumerateArray())
                {
                    Walk(child, ref cpuBest, ref cpuMax, ref gpuBest, ref gpuAny);
                }
            }
        }

        private static Boolean TryParseCelsius(String value, out Int32 celsius)
        {
            celsius = -1;
            var s = value.Replace("°C", "").Replace(",", ".").Trim();
            if (Double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            {
                celsius = (Int32)Math.Round(d);
                return true;
            }
            return false;
        }
    }
}
