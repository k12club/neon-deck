// Dev harness: run one action without the keypad.  Usage: npm run try -- sys_pulse
import { ALL_ACTIONS } from '../src/actions/index.js';

const wanted = process.argv[2];
const actions = ALL_ACTIONS.map((A) => new A());

if (!wanted || wanted === 'list') {
  for (const a of actions) console.log(`${a.name.padEnd(18)} ${a.displayName.padEnd(14)} ${a.groupName}`);
  process.exit(0);
}

const action = actions.find((a) => a.name === wanted);
if (!action) {
  console.error(`unknown action "${wanted}". Run "npm run try -- list".`);
  process.exit(1);
}

console.log(`[try] ${action.displayName} …`);
const t0 = Date.now();
await action.onKeyDown();
console.log(`[try] done in ${Date.now() - t0} ms`);
// Pomodoro keeps a timer alive; exit explicitly so the harness returns.
process.exit(0);
