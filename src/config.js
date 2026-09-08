// Deck configuration. Defaults below; override any key in %LOCALAPPDATA%\NeonDeck\deck.config.json
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';

export const DATA_DIR = path.join(process.env.LOCALAPPDATA || os.homedir(), 'NeonDeck');
const CONFIG_FILE = path.join(DATA_DIR, 'deck.config.json');

const defaults = {
  /** Project the Dev Cockpit / Claude Code keys open. */
  projectPath: 'E:\\projects\\mx-keypad',
  /** Pomodoro length in minutes. */
  pomodoroMinutes: 25,
  /** Claude model for the AI keys (empty = CLI default). e.g. 'haiku' for fastest replies. */
  claudeModel: '',
  /** Neutral working dir for `claude -p` so project instructions do not leak in. */
  claudeCwd: DATA_DIR,
};

function load() {
  try {
    fs.mkdirSync(DATA_DIR, { recursive: true });
    if (!fs.existsSync(CONFIG_FILE)) {
      fs.writeFileSync(CONFIG_FILE, JSON.stringify(defaults, null, 2));
      return { ...defaults };
    }
    return { ...defaults, ...JSON.parse(fs.readFileSync(CONFIG_FILE, 'utf8')) };
  } catch (e) {
    console.error('[NeonDeck] config load failed, using defaults:', e.message);
    return { ...defaults };
  }
}

export const config = load();
export const stateFile = path.join(DATA_DIR, 'state.json');

export function readState() {
  try {
    return JSON.parse(fs.readFileSync(stateFile, 'utf8'));
  } catch {
    return {};
  }
}

export function writeState(patch) {
  const next = { ...readState(), ...patch };
  fs.mkdirSync(DATA_DIR, { recursive: true });
  fs.writeFileSync(stateFile, JSON.stringify(next, null, 2));
  return next;
}
