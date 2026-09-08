// Row 1 - AI keys. All three ride on the Claude Code CLI already on this machine.
import { CommandAction } from '@logitech/plugin-sdk';
import { askClaude, hasThai } from '../lib/claude.js';
import { toast, getClipboard, setClipboard, copySelection, pasteText } from '../lib/win.js';

const GROUP = 'Neon Deck / AI';
const clip = (s, n = 220) => (s.length > n ? s.slice(0, n - 1) + '…' : s);

/** Clipboard text -> concise Claude answer -> toast + clipboard. */
export class AskClaudeAction extends CommandAction {
  name = 'ask_claude';
  displayName = 'Ask Claude';
  description = 'Sends the clipboard text to Claude and shows a short answer as a notification (answer is also copied).';
  groupName = GROUP;

  async onKeyDown() {
    const text = (await getClipboard()).trim();
    if (!text) return toast('Ask Claude', 'Clipboard is empty - copy a question first.');
    toast('Ask Claude', 'Thinking…', { silent: true });
    try {
      const answer = await askClaude(
        'Answer the question or request below directly and concisely (max 80 words). Reply in the same language as the input. No preamble.',
        text,
      );
      await setClipboard(answer);
      await toast('Claude', clip(answer));
    } catch (e) {
      await toast('Ask Claude failed', clip(e.message, 160));
    }
  }
}

/** Selected text -> rewritten cleanly -> pasted back over the selection. */
export class PolishTextAction extends CommandAction {
  name = 'polish_text';
  displayName = 'Polish Text';
  description = 'Copies the selected text, has Claude fix grammar and clarity (same language, same meaning), and pastes it back.';
  groupName = GROUP;

  async onKeyDown() {
    const text = (await copySelection()).trim();
    if (!text) return toast('Polish Text', 'Select some text first.');
    toast('Polish Text', 'Rewriting…', { silent: true });
    try {
      const polished = await askClaude(
        'Rewrite the text below so it is correct, clear and natural. Keep the same language, meaning, tone and formatting. Output only the rewritten text - no quotes, no explanations.',
        text,
      );
      await pasteText(polished);
    } catch (e) {
      await toast('Polish Text failed', clip(e.message, 160));
    }
  }
}

/** Thai <-> English flip of the selection, pasted back in place. */
export class TranslateFlipAction extends CommandAction {
  name = 'translate_flip';
  displayName = 'TH ⇄ EN';
  description = 'Translates the selected text Thai→English or English→Thai (auto-detected) and pastes the translation back.';
  groupName = GROUP;

  async onKeyDown() {
    const text = (await copySelection()).trim();
    if (!text) return toast('TH ⇄ EN', 'Select some text first.');
    const target = hasThai(text) ? 'English' : 'Thai';
    toast('TH ⇄ EN', `Translating to ${target}…`, { silent: true });
    try {
      const out = await askClaude(
        `Translate the text below into natural ${target}. Preserve formatting and line breaks. Output only the translation.`,
        text,
      );
      await pasteText(out);
    } catch (e) {
      await toast('Translate failed', clip(e.message, 160));
    }
  }
}
