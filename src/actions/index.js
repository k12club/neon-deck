import { AskClaudeAction, PolishTextAction, TranslateFlipAction } from './ai.js';
import { ThemeFlipAction, FocusModeAction, PomodoroAction } from './workspace.js';
import { DevCockpitAction, ClaudeCodeHereAction, SysPulseAction } from './dev.js';

/** Suggested 3x3 layout on the keypad (left to right, top to bottom). */
export const ALL_ACTIONS = [
  AskClaudeAction, PolishTextAction, TranslateFlipAction,
  ThemeFlipAction, FocusModeAction, PomodoroAction,
  DevCockpitAction, ClaudeCodeHereAction, SysPulseAction,
];
