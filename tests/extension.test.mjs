import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import { test } from "node:test";
import vm from "node:vm";

const source = await readFile(new URL("../extension/background.js", import.meta.url), "utf8");
const event = () => ({ addListener() {} });

function extension() {
  const focused = { id: 14, focused: true, incognito: false };
  const tab = { id: 29, active: true, url: "https://example.test/path?private=value" };
  const messages = [];
  let reads = 0;
  let listener;
  const port = {
    onMessage: { addListener(callback) { listener = callback; } },
    onDisconnect: event(),
    postMessage(message) { messages.push(JSON.parse(JSON.stringify(message))); }
  };
  const chrome = {
    runtime: { connectNative: () => port, onInstalled: event(), onStartup: event() },
    action: { setBadgeText() {}, setTitle() {}, onClicked: event() },
    alarms: { create() {}, onAlarm: event() },
    windows: { getLastFocused: async () => focused, onFocusChanged: event() },
    tabs: { onActivated: event(), onUpdated: event(), async query() { reads++; return [tab]; } }
  };
  vm.runInNewContext(source, { chrome, URL, setInterval() {}, clearInterval() {} });
  return {
    focused, tab, messages, get reads() { return reads; },
    async receive(message) { listener(message); await new Promise(resolve => setImmediate(resolve)); }
  };
}

test("requires permission before reading the active tab", async () => {
  const worker = extension();
  await worker.receive({ type: "status", connected: true });
  assert.deepEqual(worker.messages, [{ type: "check", focused: true }]);
  assert.equal(worker.reads, 0);
});

test("sends domain only by default and keeps the one-time grant", async () => {
  const worker = extension();
  await worker.receive({ type: "permission", connected: true, grant: "test", fullLinks: false });
  assert.deepEqual(worker.messages.at(-1), { type: "site", grant: "test", url: "https://example.test" });
});

test("allows full links only when the companion asks for them", async () => {
  const worker = extension();
  await worker.receive({ type: "permission", connected: true, grant: "test", fullLinks: true });
  assert.equal(worker.messages.at(-1).url, worker.tab.url);
});

test("ignores private and unfocused windows", async () => {
  const worker = extension();
  worker.focused.incognito = true;
  await worker.receive({ type: "permission", grant: "test" });
  worker.focused.incognito = false;
  worker.focused.focused = false;
  await worker.receive({ type: "permission", grant: "test" });
  assert.equal(worker.reads, 0);
  assert.ok(worker.messages.every(message => message.type === "check"));
});

test("clears context on internal or credential-bearing URLs", async () => {
  const worker = extension();
  for (const url of ["edge://settings", "file:///C:/private.txt", "https://user:password@example.test"]) {
    worker.tab.url = url;
    await worker.receive({ type: "permission", grant: "test", fullLinks: true });
    assert.equal(worker.messages.at(-1).url, null);
  }
});

test("marks an in-progress navigation without forwarding its URL", async () => {
  const worker = extension();
  worker.tab.pendingUrl = "https://pending.test";
  await worker.receive({ type: "permission", grant: "test", fullLinks: true });
  assert.deepEqual(worker.messages.at(-1), { type: "pending", grant: "test", url: null });
});
