#!/usr/bin/env node
// Neon Deck status line for Claude Code.
// 1) Prints a compact status line: model · context % · 5h usage % · weekly usage %.
// 2) Mirrors the rate-limit data Claude Code hands us into %LOCALAPPDATA%\NeonDeck\claude-status.json,
//    so the MX Keypad "Claude Usage" key can show it without calling the usage API itself.
'use strict';
const fs = require('fs');
const path = require('path');

let raw = '';
process.stdin.setEncoding('utf8');
process.stdin.on('data', (d) => (raw += d));
process.stdin.on('end', () => {
  let d = {};
  try { d = JSON.parse(raw); } catch { /* keep going with an empty object */ }

  const rl = d.rate_limits || {};
  const five = rl.five_hour;
  const week = rl.seven_day;
  const ctx = d.context_window && d.context_window.used_percentage;
  const model = d.model && d.model.display_name;

  // ---- mirror for the keypad (atomic write) ----
  try {
    const dir = path.join(process.env.LOCALAPPDATA || path.join(process.env.USERPROFILE || '', 'AppData', 'Local'), 'NeonDeck');
    fs.mkdirSync(dir, { recursive: true });
    const out = {
      updatedAt: new Date().toISOString(),
      session_id: d.session_id || null,
      model: model || null,
      context_used_percentage: ctx == null ? null : ctx,
      rate_limits: rl,
    };
    const target = path.join(dir, 'claude-status.json');
    const tmp = target + '.tmp';
    fs.writeFileSync(tmp, JSON.stringify(out, null, 2));
    fs.renameSync(tmp, target);
  } catch { /* the status line must never fail because of the mirror */ }

  // ---- what Claude Code shows at the bottom ----
  const parts = [];
  if (model) parts.push(model);
  if (ctx != null) parts.push(`ctx ${Math.round(ctx)}%`);
  if (five && five.used_percentage != null) parts.push(`5h ${Math.round(five.used_percentage)}%`);
  if (week && week.used_percentage != null) parts.push(`wk ${Math.round(week.used_percentage)}%`);
  process.stdout.write(parts.join('  ·  '));
});
