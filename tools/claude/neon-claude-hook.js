#!/usr/bin/env node
// Neon Deck hook for Claude Code: records what each session is doing so the MX Keypad "Claude Live" key can show it.
// Wired in ~/.claude/settings.json for UserPromptSubmit, PostToolUse, Notification, Stop, SessionStart, SessionEnd.
// Writes %LOCALAPPDATA%\NeonDeck\claude-live.json. Prints nothing (a Stop hook that prints JSON could block Claude).
'use strict';
const fs = require('fs');
const path = require('path');

const dir = path.join(process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || '', 'AppData', 'Local'), 'NeonDeck');

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (d) => (raw += d));
process.stdin.on('end', () => {
  try {
    const ev = JSON.parse(raw || '{}');
    fs.mkdirSync(dir, { recursive: true });
    const file = path.join(dir, 'claude-live.json');

    let doc = { sessions: {} };
    try { doc = JSON.parse(fs.readFileSync(file, 'utf8')); } catch { /* fresh file */ }
    if (!doc.sessions) doc.sessions = {};

    const id = ev.session_id || 'unknown';
    const now = new Date().toISOString();
    const name = ev.hook_event_name || '';
    const s = doc.sessions[id] || { cwd: ev.cwd || '', startedAt: now };
    s.cwd = ev.cwd || s.cwd || '';
    s.updatedAt = now;
    s.event = name;

    switch (name) {
      case 'SessionStart':      s.state = 'ready'; s.message = ''; break;
      case 'UserPromptSubmit':  s.state = 'working'; s.message = ''; break;
      case 'PostToolUse':       s.state = 'working'; s.message = ev.tool_name || ''; break;
      case 'Notification': {
        // Only a real question counts as "needs you": a permission prompt or an AskUserQuestion dialog.
        // idle_prompt just means Claude finished a while ago and nobody typed - that is "done/ready", not urgent.
        const kind = ev.notification_type || '';
        s.kind = kind;
        if (kind === 'permission_prompt' || kind === 'elicitation_dialog') {
          s.state = 'needs_input'; s.message = ev.message || kind;
        } else if (kind === 'idle_prompt') {
          if (s.state !== 'done' && s.state !== 'ready') { s.state = 'ready'; }
          s.message = '';
        } else if (kind === 'auth_success') {
          s.state = 'working';
        } else if (/permission|approve|question|input/i.test(ev.message || '')) {
          s.state = 'needs_input'; s.message = ev.message || kind;
        }
        break;
      }
      case 'Stop':              s.state = 'done'; s.message = ''; s.doneAt = now; break;
      case 'SessionEnd':        delete doc.sessions[id]; break;
      default:                  s.state = s.state || 'ready';
    }
    if (name !== 'SessionEnd') doc.sessions[id] = s;

    // forget sessions that went quiet long ago (12 h) and keep the file small
    const cutoff = Date.now() - 12 * 3600 * 1000;
    for (const [k, v] of Object.entries(doc.sessions)) {
      if (!v.updatedAt || Date.parse(v.updatedAt) < cutoff) delete doc.sessions[k];
    }
    doc.updatedAt = now;
    doc.lastEvent = { session: id, name, at: now };

    const tmp = file + '.' + process.pid + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(doc, null, 2));
    fs.renameSync(tmp, file);
  } catch (e) {
    // never break Claude Code because of a status light - but leave a trace for debugging
    try { fs.appendFileSync(path.join(dir, 'neon-hook-error.log'), new Date().toISOString() + ' ' + String(e && e.stack || e) + '\r\n'); } catch { /* ignore */ }
  }
});
