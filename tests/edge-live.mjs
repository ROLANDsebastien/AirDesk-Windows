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
const chrome = process.env.AIRDESK_TEST_CHROME;
const processName = chrome ? "chrome" : "msedge";
const executable = chrome ?? [process.env["ProgramFiles(x86)"], process.env.ProgramFiles]
  .filter(Boolean).map(path => join(path, "Microsoft", "Edge", "Application", "msedge.exe"))
  .find(existsSync);
assert.ok(executable && existsSync(executable), `Browser ${processName} is unavailable on the runner`);
const profile = await mkdtemp(join(tmpdir(), "airdesk-browser-"));
const journal = join(data, "journal.json");
const readState = async () => JSON.parse(await readFile(journal, "utf8"));
const state = await readState();
Object.assign(state.settings, { trackSites: true, fullLinks: !chrome, captureOnSiteChange: true, idleMinutes: 30 });
await writeFile(journal, JSON.stringify(state));
const app = spawn(binary, ["--tray"], { stdio: "ignore" });
const server = http.createServer((_, response) => {
  response.setHeader("Content-Type", "text/html");
  response.end("<title>AirDesk fixture</title><p>Page synthétique pour la vérification Windows</p>");
});
await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
const base = `http://127.0.0.1:${server.address().port}`;
const second = chrome ? base.replace("127.0.0.1", "second.test") : base;
const browser = spawn(executable, [
  `--user-data-dir=${profile}`, "--remote-debugging-port=0", "--no-first-run",
  "--no-default-browser-check", "--disable-background-networking",
  ...(chrome ? [`--load-extension=${join(dirname(binary), "Extension Edge")}`, "--host-resolver-rules=MAP second.test 127.0.0.1", "--no-proxy-server"] : []),
  `${base}/first`
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
  }, "Browser DevTools failed to start");
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
  const installed = chrome ? { id: "pcmjjiolpfnkkomnkijaabopmlolgdea" } :
    await command("Extensions.loadUnpacked", { path: join(dirname(binary), "Extension Edge") });
  assert.ok(installed.id, "Edge did not return an extension ID");
  const worker = await until(async () => (await command("Target.getTargets")).targetInfos.find(
    target => target.type === "service_worker" && target.url === `chrome-extension://${installed.id}/background.js`
  ), "Browser did not start the service worker");
  const workerSession = (await command("Target.attachToTarget", { targetId: worker.targetId, flatten: true })).sessionId;
  await until(async () => {
    const response = await command("Runtime.evaluate", {
      expression: "chrome.action.getBadgeText({})", awaitPromise: true, returnByValue: true
    }, workerSession);
    return response.result.value === "✓";
  }, "Browser did not connect to the installed native host");
  await command("Runtime.evaluate", {
    expression: "globalThis.edgeTestReplies = []; port.onMessage.addListener(({type, reason, windowTitle}) => { const detail = reason ?? (windowTitle ? type + ':' + windowTitle.slice(0,100) : type); if (detail !== edgeTestReplies.at(-1)) edgeTestReplies.push(detail); if (edgeTestReplies.length > 12) edgeTestReplies.shift(); })"
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
  let lastFocus = 0;
  const ensureFocus = async () => {
    if (Date.now() - lastFocus < 3000) return;
    await promisify(execFile)("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-File", focusScript, "-ProcessId", String(browserPid)]);
    lastFocus = Date.now();
  };
  const readInForeground = async () => {
    await ensureFocus();
    return readState();
  };

  try {
    await until(async () => (await readInForeground()).pending.find(item => item.appBundleId === `windows.${processName}` &&
      (chrome ? item.browserDomain === "127.0.0.1" && !item.browserUrl : item.browserUrl === `${base}/first`)),
      "The foreground site was not recorded");
    const edgeIcon = await until(async () => (await readInForeground()).applicationIcons?.[`windows.${processName}`],
      "The foreground executable icon was not recorded");
    const png = Buffer.from(edgeIcon, "base64");
    assert.ok(png.length >= 45 && png.length <= 1536, "Edge icon exceeds its upload budget");
    assert.equal(png.subarray(0, 16).toString("hex"), "89504e470d0a1a0a0000000d49484452");
    const size = png.readUInt32BE(16);
    assert.ok(size > 0 && size <= 32 && size === png.readUInt32BE(20), "Unexpected Edge icon dimensions");
    const navigation = await command("Page.navigate", { url: `${second}/second` }, pageSession);
    assert.equal(navigation.errorText, undefined);
    await until(async () => {
      const response = await command("Runtime.evaluate", { expression: "document.title", returnByValue: true }, pageSession);
      return response.result.value === "AirDesk fixture";
    }, "Second fixture page did not load");
    await until(async () => {
      const updated = await readInForeground();
      return updated.captures.length > state.captures.length &&
        updated.pending.some(item => item.appBundleId === `windows.${processName}` &&
          (chrome ? item.browserDomain === "second.test" && !item.browserUrl : item.browserUrl === `${second}/second`));
    }, "Site navigation did not record the new URL and trigger a capture");
  } catch (error) {
    const current = await readState();
    const focus = await command("Runtime.evaluate", {
      expression: "chrome.windows.getAll({windowTypes:['normal'],populate:true}).then(windows => ({browser, replies:edgeTestReplies, windows:windows.map(w => ({id:w.id,focused:w.focused,state:w.state,active:w.tabs.find(t=>t.active)?.url,title:w.tabs.find(t=>t.active)?.title}))}))",
      awaitPromise: true, returnByValue: true
    }, workerSession);
    const { latest, settings } = current;
    const diagnostic = `${error.message}. Desktop state=${latest.state}, idle=${latest.idleSeconds}, app=${latest.appBundleId}, domain=${latest.browserDomain}, Edge protocol=${JSON.stringify(focus.result.value)}, paused=${settings.paused}, tracking=${settings.trackSites}`;
    console.log("::error::" + diagnostic.replaceAll("%", "%25").replaceAll("\r", "%0D").replaceAll("\n", "%0A"));
    throw new Error(diagnostic);
  }
  console.log(`Real ${processName} icon, site and screenshot on site change verified.`);
} finally {
  socket?.close();
  await Promise.all([browser, app].map(process => new Promise(resolve => {
    if (!process.pid) return resolve();
    execFile("taskkill.exe", ["/PID", String(process.pid), "/T", "/F"], () => resolve());
  })));
  server.close();
}
