namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Threading.Tasks;

    /// <summary>Windows helpers: PowerShell runner, toast notifications, clipboard, SendKeys, launcher.</summary>
    internal static class Win
    {
        private const String PsPrelude = "$ErrorActionPreference = 'Stop'\n[Console]::OutputEncoding = [System.Text.Encoding]::UTF8\n";

        // AppUserModelId Windows already trusts, so toasts show without an installer.
        private const String ToastAppId = @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\WindowsPowerShell\v1.0\powershell.exe";

        /// <summary>Run a PowerShell script via -EncodedCommand (no quoting issues). Returns stdout.</summary>
        public static async Task<String> Ps(String script, Int32 timeoutMs = 60_000)
        {
            var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(PsPrelude + script));
            var psi = new ProcessStartInfo("powershell.exe")
            {
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("powershell failed to start");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException($"powershell timed out after {timeoutMs} ms");
            }
            var out_ = (await stdout).Trim();
            var err = (await stderr).Trim();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(err.Length > 0 ? err : $"powershell exited with {p.ExitCode}");
            }
            return out_;
        }

        /// <summary>PowerShell expression that evaluates to the given string (base64 round-trip).</summary>
        public static String PsString(String text) =>
            $"([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('{Convert.ToBase64String(Encoding.UTF8.GetBytes(text ?? ""))}')))";

        private static String EscapeXml(String s) =>
            (s ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

        /// <summary>Show a Windows toast notification. Never throws.</summary>
        public static async Task Toast(String title, String body = "", Boolean silent = false)
        {
            var audio = silent ? "<audio silent=\"true\"/>" : "";
            var xml = $"<toast duration=\"short\"><visual><binding template=\"ToastGeneric\"><text>{EscapeXml(title)}</text><text>{EscapeXml(body)}</text></binding></visual>{audio}</toast>";
            var script = $@"
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = {PsString(xml)}
$doc = New-Object Windows.Data.Xml.Dom.XmlDocument
$doc.LoadXml($xml)
$toast = New-Object Windows.UI.Notifications.ToastNotification $doc
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('{ToastAppId}').Show($toast)
";
            try
            {
                await Ps(script, 15_000);
            }
            catch (Exception ex)
            {
                PluginLog.Warning(ex, "toast failed");
            }
        }

        public static async Task<String> GetClipboard()
        {
            try
            {
                return await Ps("Get-Clipboard -Raw");
            }
            catch
            {
                return "";
            }
        }

        public static Task SetClipboard(String text) => Ps($"Set-Clipboard -Value {PsString(text)}");

        /// <summary>Send keystrokes to the foreground window (SendKeys syntax, e.g. "^c").</summary>
        public static Task SendKeys(String keys) =>
            Ps($"Add-Type -AssemblyName System.Windows.Forms\n[System.Windows.Forms.SendKeys]::SendWait('{keys}')");

        /// <summary>Copy the current selection and return it (falls back to whatever is on the clipboard).</summary>
        public static async Task<String> CopySelection()
        {
            await SendKeys("^c");
            await Task.Delay(250);
            return await GetClipboard();
        }

        /// <summary>Put text on the clipboard and paste it into the foreground window.</summary>
        public static async Task PasteText(String text)
        {
            await SetClipboard(text);
            await Task.Delay(120);
            await SendKeys("^v");
        }

        /// <summary>Fire-and-forget launcher through cmd.exe so .cmd shims like `code` and `claude` resolve.</summary>
        public static void Launch(String commandLine, String workingDirectory = null)
        {
            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"{commandLine}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory ?? "",
            };
            Process.Start(psi)?.Dispose();
        }

        public static String Clip(String s, Int32 n) => String.IsNullOrEmpty(s) ? "" : s.Length > n ? s.Substring(0, n - 1) + "…" : s;
    }
}
