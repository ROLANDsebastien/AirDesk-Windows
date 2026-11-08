import assert from "node:assert/strict";
import { spawn, execFile } from "node:child_process";
import { existsSync } from "node:fs";
import { mkdtemp, readFile, writeFile } from "node:fs/promises";
import http from "node:http";
import { tmpdir } from "node:os";
import { dirname, join } from "node:path";
import { setTimeout as wait } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { promisify } from "node:util";

const [binary, data] = process.argv.slice(2);
const edge = [process.env["ProgramFiles(x86)"], process.env.ProgramFiles]
  .filter(Boolean).map(path => join(path, "Microsoft", "Edge", "Application", "msedge.exe"))
  .find(existsSync);
assert.ok(edge, "Microsoft Edge is unavailable on the runner");
const profile = await mkdtemp(join(tmpdir(), "airdesk-edge-"));
const journal = join(data, "journal.json");
const readState = async () => JSON.parse(await readFile(journal, "utf8"));
const state = await readState();
Object.assign(state.settings, { trackSites: true, fullLinks: true, captureOnSiteChange: true, idleMinutes: 30 });
await writeFile(journal, JSON.stringify(state));
const app = spawn(binary, ["--tray"], { stdio: "ignore" });
const server = http.createServer((_, response) => {
  response.setHeader("Content-Type", "text/html");
  response.end("<title>AirDesk fixture</title><p>Page synthétique pour la vérification Windows</p>");
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const browser = spawn(edge, [
  `--user-data-dir=${profile}`, "--remote-debugging-port=0", "--no-first-run",
  "--no-default-browser-check", "--disable-background-networking", `${base}/first`
], { stdio: "ignore" });
let socket;
const pending = new Map();
let sequence = 0;

async function until(check, message, limit = 25000) {
  const deadline = Date.now() + limit;
  while (Date.now() < deadline) {
    const value = await check();
    if (value) return value;
    await wait(400);
  }
  throw new Error(message);
}

function command(method, params = {}, sessionId) {
  return new Promise((resolve, reject) => {
    const id = ++sequence;
    const timeout = setTimeout(() => {
      pending.delete(id);
      reject(new Error(`No CDP reply: ${method}`));
    }, 6000);
    pending.set(id, reply => {
      clearTimeout(timeout);
      if (reply.error) reject(new Error(`${method}: ${reply.error.message}`));
      else resolve(reply.result);
    });
    socket.send(JSON.stringify({ id, method, params, sessionId }));
  });
}

try {
  const devtoolsPath = join(profile, "DevToolsActivePort");
  const [port, route] = await until(async () => {
    if (!existsSync(devtoolsPath)) return null;
    const lines = (await readFile(devtoolsPath, "utf8")).trim().split(/\r?\n/);
    return lines.length >= 2 ? lines : null;
  }, "Edge DevTools failed to start");
  socket = new WebSocket(`ws://127.0.0.1:${port}${route}`);
  await new Promise((resolve, reject) => {
    socket.addEventListener("open", resolve, { once: true });
    socket.addEventListener("error", reject, { once: true });
  });
  socket.addEventListener("message", event => {
    const reply = JSON.parse(event.data);
    const callback = pending.get(reply.id);
    pending.delete(reply.id);
    callback?.(reply);
  });
  const installed = await command("Extensions.loadUnpacked", { path: join(dirname(binary), "Extension Edge") });
  assert.ok(installed.id, "Edge did not return an extension ID");
  const worker = await until(async () => (await command("Target.getTargets")).targetInfos.find(
    target => target.type === "service_worker" && target.url === `chrome-extension://${installed.id}/background.js`
  ), "Edge did not start the service worker");
  const workerSession = (await command("Target.attachToTarget", { targetId: worker.targetId, flatten: true })).sessionId;
  await until(async () => {
    const response = await command("Runtime.evaluate", {
      expression: "chrome.action.getBadgeText({})", awaitPromise: true, returnByValue: true
    }, workerSession);
    return response.result.value === "✓";
  }, "Real Edge did not connect to the installed native host");
  await command("Runtime.evaluate", {
    expression: "globalThis.edgeTestReplies = []; port.onMessage.addListener(({type, reason}) => { edgeTestReplies.push(reason ?? type); if (edgeTestReplies.length > 20) edgeTestReplies.shift(); })"
  }, workerSession);
  console.log("Edge service worker and native messaging verified.");

  const page = await until(async () => (await command("Target.getTargets")).targetInfos.find(
    target => target.type === "page" && target.url === `${base}/first`
  ), "Fixture page did not open");
  await command("Target.activateTarget", { targetId: page.targetId });
  const pageSession = (await command("Target.attachToTarget", { targetId: page.targetId, flatten: true })).sessionId;
  await command("Page.bringToFront", {}, pageSession);
  const system = await command("SystemInfo.getProcessInfo");
  const browserPid = system.processInfo.find(info => info.type === "browser").id;
  const focusScript = fileURLToPath(new URL("../installer/focus-edge.ps1", import.meta.url));
  await promisify(execFile)("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", focusScript, "-ProcessId", String(browserPid)]);
  await command("Runtime.evaluate", {
    expression: `chrome.tabs.query({}).then(tabs => chrome.windows.update(tabs.find(tab => tab.url === ${JSON.stringify(base + "/first")}).windowId, {focused:true}))`,
    awaitPromise: true
  }, workerSession);
  try {
    await until(async () => (await readState()).pending.find(item => item.browserUrl === `${base}/first`),
      "The foreground Edge URL was not recorded");
  } catch (error) {
    const current = await readState();
    const focus = await command("Runtime.evaluate", {
      expression: "chrome.windows.getAll({windowTypes:['normal'],populate:true}).then(windows => ({browser, replies:edgeTestReplies, windows:windows.map(w => ({id:w.id,focused:w.focused,state:w.state,incognito:w.incognito,active:w.tabs.find(t=>t.active)?.url}))}))",
      awaitPromise: true, returnByValue: true
    }, workerSession);
    const { latest, settings } = current;
    throw new Error(`${error.message}. Desktop state=${latest.state}, idle=${latest.idleSeconds}, app=${latest.appBundleId}, domain=${latest.browserDomain}, Edge protocol=${JSON.stringify(focus.result.value)}, paused=${settings.paused}, tracking=${settings.trackSites}`);
  }
  await command("Page.navigate", { url: `${base}/second` }, pageSession);
  await until(async () => {
    const updated = await readState();
    return updated.captures.length > state.captures.length &&
      updated.pending.some(item => item.browserUrl === `${base}/second`);
  }, "Site navigation did not record the new URL and trigger a capture");
  console.log("Real Edge URL and screenshot on site change verified.");
} finally {
  socket?.close();
  await Promise.all([browser, app].map(process => new Promise(resolve => {
    if (!process.pid) return resolve();
    execFile("taskkill.exe", ["/PID", String(process.pid), "/T", "/F"], () => resolve());
  })));
  server.close();
}
