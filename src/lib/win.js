// Windows helpers: PowerShell runner, toast notifications, clipboard, SendKeys.
import { spawn } from 'node:child_process';

const PS_PRELUDE = `
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
`;

/** Run a PowerShell script (passed as -EncodedCommand, so no quoting issues). Resolves stdout. */
export function ps(script, { timeout = 60_000 } = {}) {
  return new Promise((resolve, reject) => {
    const encoded = Buffer.from(PS_PRELUDE + script, 'utf16le').toString('base64');
    const child = spawn(
      'powershell.exe',
      ['-NoProfile', '-NonInteractive', '-ExecutionPolicy', 'Bypass', '-EncodedCommand', encoded],
      { windowsHide: true },
    );
    let out = '';
    let err = '';
    child.stdout.on('data', (d) => (out += d));
    child.stderr.on('data', (d) => (err += d));
    const timer = setTimeout(() => {
      child.kill();
      reject(new Error(`powershell timed out after ${timeout}ms`));
    }, timeout);
    child.on('error', (e) => {
      clearTimeout(timer);
      reject(e);
    });
    child.on('close', (code) => {
      clearTimeout(timer);
      if (code === 0) resolve(out.trim());
      else reject(new Error(err.trim() || `powershell exited with ${code}`));
    });
  });
}

/** Fire-and-forget launcher via cmd.exe (resolves .cmd shims like `code`, `claude`). */
export function launch(commandLine, { cwd } = {}) {
  const child = spawn('cmd.exe', ['/d', '/s', '/c', commandLine], {
    cwd,
    detached: true,
    stdio: 'ignore',
    windowsHide: true,
  });
  child.unref();
}

/** Encode a JS string as a PowerShell expression that yields the same string. */
export function psString(text) {
  const b64 = Buffer.from(String(text), 'utf8').toString('base64');
  return `([System.Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('${b64}')))`;
}

const escapeXml = (s) =>
  String(s).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');

// AppUserModelId that Windows already trusts, so toasts show without an installer.
const TOAST_APP_ID = '{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\\WindowsPowerShell\\v1.0\\powershell.exe';

/** Show a Windows toast notification. */
export async function toast(title, body = '', { silent = false } = {}) {
  const audio = silent ? '<audio silent="true"/>' : '';
  const xml = `<toast duration="short"><visual><binding template="ToastGeneric"><text>${escapeXml(title)}</text><text>${escapeXml(body)}</text></binding></visual>${audio}</toast>`;
  const script = `
[Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
[Windows.Data.Xml.Dom.XmlDocument, Windows.Data.Xml.Dom.XmlDocument, ContentType = WindowsRuntime] | Out-Null
$xml = ${psString(xml)}
$doc = New-Object Windows.Data.Xml.Dom.XmlDocument
$doc.LoadXml($xml)
$toast = New-Object Windows.UI.Notifications.ToastNotification $doc
[Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('${TOAST_APP_ID}').Show($toast)
`;
  try {
    await ps(script, { timeout: 15_000 });
  } catch (e) {
    console.error('[NeonDeck] toast failed:', e.message);
  }
}

export async function getClipboard() {
  try {
    return await ps(`Get-Clipboard -Raw`);
  } catch {
    return '';
  }
}

export async function setClipboard(text) {
  await ps(`Set-Clipboard -Value ${psString(text)}`);
}

/** Send keystrokes to the foreground window (SendKeys syntax, e.g. '^c'). */
export async function sendKeys(keys) {
  await ps(`Add-Type -AssemblyName System.Windows.Forms
[System.Windows.Forms.SendKeys]::SendWait('${keys}')`);
}

const sleep = (ms) => new Promise((r) => setTimeout(r, ms));

/** Copy the current selection and return it (falls back to whatever is on the clipboard). */
export async function copySelection() {
  await sendKeys('^c');
  await sleep(250);
  return getClipboard();
}

/** Put text on the clipboard and paste it into the foreground window. */
export async function pasteText(text) {
  await setClipboard(text);
  await sleep(120);
  await sendKeys('^v');
}
