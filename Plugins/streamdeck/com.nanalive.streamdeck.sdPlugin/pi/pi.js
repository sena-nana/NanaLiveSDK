const ACTIONS = {
  SWITCH_MODEL: "com.nanalive.streamdeck.switch-model",
  PLAY_MOTION: "com.nanalive.streamdeck.play-motion",
  TRIGGER_EXPRESSION: "com.nanalive.streamdeck.trigger-expression",
  TRIGGER_HOTKEY: "com.nanalive.streamdeck.trigger-hotkey",
  WRITE_PARAMETER: "com.nanalive.streamdeck.write-parameter",
};

let websocket;
let uuid;
let action;
let settings = {};

function send(event, payload) {
  websocket?.send(JSON.stringify({ event, context: uuid, payload }));
}

function fillSelect(id, items, valueKey, labelKey, current) {
  const select = document.getElementById(id);
  select.innerHTML = "";
  const empty = document.createElement("option");
  empty.value = "";
  empty.textContent = "未选择";
  select.append(empty);
  for (const item of items) {
    const option = document.createElement("option");
    option.value = item[valueKey];
    option.textContent = item[labelKey] || item[valueKey];
    select.append(option);
  }
  select.value = current || "";
}

function showFields() {
  document.getElementById("model-field").hidden = action !== ACTIONS.SWITCH_MODEL;
  document.getElementById("motion-field").hidden = action !== ACTIONS.PLAY_MOTION;
  document.getElementById("expression-field").hidden = action !== ACTIONS.TRIGGER_EXPRESSION;
  document.getElementById("hotkey-field").hidden = action !== ACTIONS.TRIGGER_HOTKEY;
  document.getElementById("parameter-field").hidden = action !== ACTIONS.WRITE_PARAMETER;
}

function applyCatalogs(payload) {
  fillSelect("modelID", payload.models ?? [], "modelID", "name", settings.modelID);
  fillSelect("motionID", payload.motions ?? [], "motionID", "label", settings.motionID);
  fillSelect("expressionID", payload.expressions ?? [], "expressionID", "label", settings.expressionID);
  fillSelect("hotkeyID", payload.hotkeys ?? [], "hotkeyID", "name", settings.hotkeyID);
  fillSelect("parameterID", payload.parameters ?? [], "parameterID", "label", settings.parameterID);
  document.getElementById("status").textContent = "已连接 NanaLive";
}

function persist(key, value) {
  settings = { ...settings, [key]: value };
  send("setSettings", settings);
}

window.connectElgatoStreamDeckSocket = function connectElgatoStreamDeckSocket(
  port,
  inUUID,
  registerEvent,
  _info,
  actionInfo,
) {
  uuid = inUUID;
  const info = JSON.parse(actionInfo || "{}");
  action = info.action;
  settings = info.payload?.settings ?? {};
  showFields();
  websocket = new WebSocket(`ws://127.0.0.1:${port}`);
  websocket.onopen = () => {
    websocket.send(JSON.stringify({ event: registerEvent, uuid }));
    send("sendToPlugin", { event: "refreshCatalogs" });
  };
  websocket.onmessage = (event) => {
    const message = JSON.parse(event.data);
    if (message.event === "sendToPropertyInspector" && message.payload?.event === "catalogs") {
      applyCatalogs(message.payload);
    }
    if (message.event === "sendToPropertyInspector" && message.payload?.event === "catalogsError") {
      document.getElementById("status").textContent = "无法连接 NanaLive，请先在应用中开启插件 API 并授权。";
    }
  };
  for (const id of ["modelID", "motionID", "expressionID", "hotkeyID", "parameterID"]) {
    document.getElementById(id).addEventListener("change", (event) => {
      persist(id, event.target.value);
    });
  }
};
