namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Diagnostics;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;

    /// <summary>Thin wrapper around the Claude Code CLI (`claude -p`) already installed on this machine.</summary>
    internal static class ClaudeCli
    {
        private static readonly Regex ThaiRegex = new Regex("[฀-๿]");

        public static Boolean HasThai(String s) => !String.IsNullOrEmpty(s) && ThaiRegex.IsMatch(s);

        /// <summary>
        /// Ask Claude. <paramref name="instruction"/> is the task, <paramref name="input"/> the user text.
        /// The whole prompt goes through stdin so length is not an issue.
        /// </summary>
        public static async Task<String> Ask(String instruction, String input = "", Int32 timeoutMs = 120_000)
        {
            var cfg = DeckConfig.Current;
            var prompt = String.IsNullOrEmpty(input) ? instruction : $"{instruction}\n\n<input>\n{input}\n</input>";
            var args = "-p --output-format text";
            if (!String.IsNullOrWhiteSpace(cfg.ClaudeModel))
            {
                args += $" --model {cfg.ClaudeModel}";
            }

            var psi = new ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/d /s /c \"claude {args}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = System.IO.Directory.Exists(cfg.ClaudeCwd) ? cfg.ClaudeCwd : DeckConfig.DataDirectory,
            };
            psi.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("claude failed to start");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            using (var w = new System.IO.StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false)))
            {
                await w.WriteAsync(prompt);
            }

            using var cts = new System.Threading.CancellationTokenSource(timeoutMs);
            try
            {
                await p.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch { }
                throw new TimeoutException($"claude timed out after {timeoutMs / 1000} s");
            }

            var out_ = (await stdout).Trim();
            var err = (await stderr).Trim();
            if (p.ExitCode != 0)
            {
                throw new InvalidOperationException(err.Length > 0 ? err : $"claude exited with {p.ExitCode}");
            }
            return out_;
        }
    }
}
