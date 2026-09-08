// Thin wrapper around the Claude Code CLI (`claude -p`) that is already installed on this machine.
import { spawn } from 'node:child_process';
import { config } from '../config.js';

/**
 * Ask Claude. `instruction` is the task, `input` is the user text (sent via stdin so length is not an issue).
 * Runs in a neutral cwd so no project CLAUDE.md leaks into the prompt.
 */
export function askClaude(instruction, input = '', { timeout = 120_000 } = {}) {
  return new Promise((resolve, reject) => {
    const prompt = input ? `${instruction}\n\n<input>\n${input}\n</input>` : instruction;
    const args = ['-p', '--output-format', 'text'];
    if (config.claudeModel) args.push('--model', config.claudeModel);
    const child = spawn('cmd.exe', ['/d', '/s', '/c', `claude ${args.join(' ')}`], {
      cwd: config.claudeCwd,
      windowsHide: true,
      env: { ...process.env, CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC: '1' },
    });
    let out = '';
    let err = '';
    child.stdout.on('data', (d) => (out += d));
    child.stderr.on('data', (d) => (err += d));
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`claude timed out after ${timeout}ms`));
    }, timeout);
    child.on('error', (e) => {
      clearTimeout(timer);
      reject(e);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(out.trim());
      else reject(new Error(err.trim() || `claude exited with ${code}`));
    });
    child.stdin.end(prompt, 'utf8');
  });
}

export const hasThai = (s) => /[฀-๿]/.test(s);
