const host = "com.airdesk.companion";
let port;
let timer;
let checking = false;

function connect() {
  if (port) return;
  const connection = chrome.runtime.connectNative(host);
  port = connection;
  connection.onMessage.addListener(message => void share(message, connection));
  connection.onDisconnect.addListener(() => {
    void chrome.runtime.lastError;
    if (port !== connection) return;
    port = undefined;
    clearInterval(timer);
    badge(false);
  });
  timer = setInterval(check, 1500);
  void check();
}

function badge(connected) {
  void chrome.action.setBadgeText({ text: connected ? "✓" : "" });
  void chrome.action.setTitle({ title: connected ? "AirDesk Companion · connecté" : "AirDesk Companion · ouvrir le compagnon Windows" });
}

async function check() {
  if (!port || checking) return;
  const connection = port;
  checking = true;
  try {
    const window = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (connection === port) connection.postMessage({ type: "check", focused: window.focused && !window.incognito });
  } catch {
    // A browser window may close during a query.
  } finally {
    checking = false;
  }
}

async function share(message, connection) {
  if (connection !== port) return;
  badge(message.connected === true);
  if (message.type !== "permission" || typeof message.grant !== "string") return;
  try {
    const window = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (!window.focused || window.incognito) return;
    const [tab] = await chrome.tabs.query({ active: true, windowId: window.id });
    if (connection !== port || tab?.incognito) return;
    let address = null;
    if (tab?.url && !tab.pendingUrl) {
      const url = new URL(tab.url);
      if (["http:", "https:"].includes(url.protocol) && !url.username && !url.password)
        address = message.fullLinks ? url.href : url.origin;
    }
    const latest = await chrome.windows.getLastFocused({ windowTypes: ["normal"] });
    if (connection === port && latest.id === window.id && latest.focused)
      connection.postMessage({ type: "site", grant: message.grant, url: address });
  } catch {
    // Unavailable or non-web tabs are ignored.
  }
}

chrome.alarms.onAlarm.addListener(alarm => { if (alarm.name === "airdesk-connect") connect(); });
chrome.action.onClicked.addListener(() => { connect(); void check(); });
chrome.windows.onFocusChanged.addListener(() => { connect(); void check(); });
chrome.runtime.onInstalled.addListener(() => {
  void chrome.alarms.create("airdesk-connect", { periodInMinutes: 0.5 });
  connect();
});
chrome.runtime.onStartup.addListener(connect);
chrome.tabs.onActivated.addListener(() => void check());
chrome.tabs.onUpdated.addListener((_id, change, tab) => { if (tab.active && change.status === "complete") void check(); });
connect();
