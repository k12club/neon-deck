namespace Loupedeck.NeonDeckPlugin
{
    using System;
    using System.Threading.Tasks;

    // Row 1 - AI keys. All three ride on the Claude Code CLI already on this machine.

    public enum AiState { Idle, Working, Done }

    /// <summary>Clipboard text -> concise Claude answer -> toast + clipboard. Key shows thinking / done.</summary>
    public sealed class AskClaudeCommand : NeonCommand
    {
        private AiState _state = AiState.Idle;
        private DateTime _doneUntil;

        public AskClaudeCommand()
            : base("Ask Claude", "Sends the clipboard text to Claude and shows a short answer as a notification (answer is also copied).", "Neon Deck | AI")
        {
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "ask_claude.svg",
            Accent = Neon.Cyan,
            Title = "Ask Claude",
            Subtitle = this._state == AiState.Working ? "thinking…" : this._state == AiState.Done && DateTime.UtcNow < this._doneUntil ? "✓ copied" : null,
            Busy = this._state == AiState.Working,
            Active = this._state != AiState.Idle,
        };

        protected override void Execute(String actionParameter)
        {
            if (this._state == AiState.Working)
            {
                return;
            }
            this.RunAsync("Ask Claude", async () =>
            {
                var text = (await Win.GetClipboard()).Trim();
                if (text.Length == 0)
                {
                    await Win.Toast("Ask Claude", "Clipboard is empty - copy a question first.");
                    return;
                }
                this.Set(AiState.Working);
                _ = Win.Toast("Ask Claude", "Thinking…", silent: true);
                var answer = await ClaudeCli.Ask(
                    "Answer the question or request below directly and concisely (max 80 words). Reply in the same language as the input. No preamble.",
                    text);
                await Win.SetClipboard(answer);
                this.Set(AiState.Done);
                ClaudeUsageCommand.NotifyUsageChanged();
                await Win.Toast("Claude", Win.Clip(answer, 220));
            }, () => this.Set(AiState.Idle));
        }

        private void Set(AiState s)
        {
            this._state = s;
            this._doneUntil = DateTime.UtcNow.AddSeconds(20);
            this.Refresh();
            if (s == AiState.Done)
            {
                _ = Task.Delay(20_500).ContinueWith(_ => { if (this._state == AiState.Done) { this._state = AiState.Idle; this.Refresh(); } });
            }
        }
    }

    /// <summary>Selected text -> rewritten cleanly -> pasted back over the selection.</summary>
    public sealed class PolishTextCommand : NeonCommand
    {
        private AiState _state = AiState.Idle;

        public PolishTextCommand()
            : base("Polish Text", "Copies the selected text, has Claude fix grammar and clarity (same language, same meaning), and pastes it back.", "Neon Deck | AI")
        {
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "polish_text.svg",
            Accent = Neon.Magenta,
            Title = "Polish",
            Subtitle = this._state == AiState.Working ? "rewriting…" : this._state == AiState.Done ? "✓ pasted" : null,
            Busy = this._state == AiState.Working,
            Active = this._state != AiState.Idle,
        };

        protected override void Execute(String actionParameter)
        {
            if (this._state == AiState.Working)
            {
                return;
            }
            this.RunAsync("Polish Text", async () =>
            {
                var text = (await Win.CopySelection()).Trim();
                if (text.Length == 0)
                {
                    await Win.Toast("Polish Text", "Select some text first.");
                    return;
                }
                this.Set(AiState.Working);
                var polished = await ClaudeCli.Ask(
                    "Rewrite the text below so it is correct, clear and natural. Keep the same language, meaning, tone and formatting. Output only the rewritten text - no quotes, no explanations.",
                    text);
                await Win.PasteText(polished);
                this.Set(AiState.Done);
                ClaudeUsageCommand.NotifyUsageChanged();
            }, () => this.Set(AiState.Idle));
        }

        private void Set(AiState s)
        {
            this._state = s;
            this.Refresh();
            if (s == AiState.Done)
            {
                _ = Task.Delay(6_000).ContinueWith(_ => { if (this._state == AiState.Done) { this._state = AiState.Idle; this.Refresh(); } });
            }
        }
    }

    /// <summary>Thai <-> English flip of the selection, pasted back in place. Key shows the detected direction.</summary>
    public sealed class TranslateFlipCommand : NeonCommand
    {
        private AiState _state = AiState.Idle;
        private String _direction;

        public TranslateFlipCommand()
            : base("TH ⇄ EN", "Translates the selected text Thai→English or English→Thai (auto-detected) and pastes the translation back.", "Neon Deck | AI")
        {
        }

        protected override Neon.Face BuildFace() => new Neon.Face
        {
            Icon = "translate_flip.svg",
            Accent = Neon.Lime,
            Title = "TH ⇄ EN",
            Subtitle = this._state == AiState.Working ? this._direction : this._state == AiState.Done ? "✓ " + this._direction : null,
            Busy = this._state == AiState.Working,
            Active = this._state != AiState.Idle,
        };

        protected override void Execute(String actionParameter)
        {
            if (this._state == AiState.Working)
            {
                return;
            }
            this.RunAsync("Translate", async () =>
            {
                var text = (await Win.CopySelection()).Trim();
                if (text.Length == 0)
                {
                    await Win.Toast("TH ⇄ EN", "Select some text first.");
                    return;
                }
                var toEnglish = ClaudeCli.HasThai(text);
                this._direction = toEnglish ? "TH → EN" : "EN → TH";
                this.Set(AiState.Working);
                var target = toEnglish ? "English" : "Thai";
                var translated = await ClaudeCli.Ask(
                    $"Translate the text below into natural {target}. Preserve formatting and line breaks. Output only the translation.",
                    text);
                await Win.PasteText(translated);
                this.Set(AiState.Done);
                ClaudeUsageCommand.NotifyUsageChanged();
            }, () => this.Set(AiState.Idle));
        }

        private void Set(AiState s)
        {
            this._state = s;
            this.Refresh();
            if (s == AiState.Done)
            {
                _ = Task.Delay(6_000).ContinueWith(_ => { if (this._state == AiState.Done) { this._state = AiState.Idle; this.Refresh(); } });
            }
        }
    }
}
