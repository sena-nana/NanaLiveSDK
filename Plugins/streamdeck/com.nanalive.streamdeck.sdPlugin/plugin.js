import {
  DEFAULT_PORT,
  SUBPROTOCOL,
  createNanaLiveClient,
  executableHotkeys,
  parameterValueAfterTicks,
  writeParameterCommand,
} from "@nanalive/sdk";
import { connectBinaryWebSocket, connectTextWebSocket } from "@nanalive/sdk/node-websocket";

const ACTIONS = {
  SWITCH_MODEL: "com.nanalive.streamdeck.switch-model",
  PLAY_MOTION: "com.nanalive.streamdeck.play-motion",
  TRIGGER_EXPRESSION: "com.nanalive.streamdeck.trigger-expression",
  TRIGGER_HOTKEY: "com.nanalive.streamdeck.trigger-hotkey",
  WRITE_PARAMETER: "com.nanalive.streamdeck.write-parameter",
};

function officialIdentity(version = "0.1.0") {
  return {
    pluginID: "com.nanalive.streamdeck",
    pluginName: "NanaLive Stream Deck",
    pluginDeveloper: "NanaLive",
    pluginVersion: version,
    scopes: [
      "model.read",
      "model.switch",
      "expression.read",
      "expression.trigger",
      "motion.read",
      "motion.trigger",
      "parameter.read",
      "parameter.write",
      "hotkey.read",
      "hotkey.trigger",
    ],
  };
}

function commandForAction(uuid, settings = {}) {
  if (uuid === ACTIONS.SWITCH_MODEL && settings.modelID) {
    return { messageType: "ModelLoadRequest", data: { modelID: settings.modelID } };
  }
  if (uuid === ACTIONS.PLAY_MOTION && settings.motionID) {
    return { messageType: "MotionTriggerRequest", data: { motionID: settings.motionID } };
  }
  if (uuid === ACTIONS.TRIGGER_EXPRESSION && settings.expressionID) {
    return {
      messageType: "ExpressionTriggerRequest",
      data: { expressionID: settings.expressionID, action: "toggle" },
    };
  }
  if (uuid === ACTIONS.TRIGGER_HOTKEY && settings.hotkeyID) {
    return { messageType: "HotkeyTriggerRequest", data: { hotkeyID: settings.hotkeyID } };
  }
  return null;
}

const identity = officialIdentity();
const settingsByContext = new Map();
let streamDeck;
let pluginUUID = "";
let nana;
let connecting;
let globalSettings = { authenticationToken: "", host: "127.0.0.1", port: DEFAULT_PORT };

function parseArgs(argv) {
  const parsed = {};
  for (let index = 0; index < argv.length; index += 1) {
    const key = argv[index];
    if (!key.startsWith("-")) continue;
    parsed[key.slice(1)] = argv[index + 1];
    index += 1;
  }
  return parsed;
}

function sendToStreamDeck(payload) {
  streamDeck?.send(JSON.stringify(payload));
}

function setTitle(context, title) {
  sendToStreamDeck({ event: "setTitle", context, payload: { title, target: 0 } });
}

function showOk(context) {
  sendToStreamDeck({ event: "showOk", context });
}

function showAlert(context) {
  sendToStreamDeck({ event: "showAlert", context });
}

function setGlobalSettings() {
  sendToStreamDeck({
    event: "setGlobalSettings",
    context: pluginUUID,
    payload: globalSettings,
  });
}

function sendToPropertyInspector(context, payload) {
  sendToStreamDeck({ event: "sendToPropertyInspector", context, payload });
}

async function ensureNanaLive() {
  if (nana) return nana;
  if (connecting) return connecting;
  connecting = (async () => {
    const host = globalSettings.host || "127.0.0.1";
    const port = Number(globalSettings.port) || DEFAULT_PORT;
    const socket = await connectBinaryWebSocket({
      host,
      port,
      subprotocol: SUBPROTOCOL,
      onMessage(payload) {
        nana?.receive(payload);
      },
      onClose() {
        nana = null;
        connecting = null;
      },
    });
    nana = createNanaLiveClient({
      send: (payload) => socket.send(payload),
      identity,
      token: globalSettings.authenticationToken || null,
      onToken(token) {
        globalSettings.authenticationToken = token;
        setGlobalSettings();
      },
    });
    await nana.authenticate();
    return nana;
  })();
  try {
    return await connecting;
  } catch (error) {
    nana = null;
    connecting = null;
    throw error;
  }
}

async function catalogs() {
  const client = await ensureNanaLive();
  const [models, motions, expressions, hotkeys, parameters] = await Promise.all([
    client.listModels(),
    client.listMotions().catch(() => ({ data: { motions: [] } })),
    client.listExpressions().catch(() => ({ data: { expressions: [] } })),
    client.listHotkeys().catch(() => ({ data: { hotkeys: [] } })),
    client.listParameters().catch(() => ({ data: { parameters: [] } })),
  ]);
  return {
    models: models.data?.models ?? [],
    motions: motions.data?.motions ?? [],
    expressions: expressions.data?.expressions ?? [],
    hotkeys: executableHotkeys(hotkeys.data?.hotkeys ?? []),
    parameters: parameters.data?.parameters ?? [],
  };
}

async function handlePress(context, action, settings) {
  try {
    const client = await ensureNanaLive();
    const command = commandForAction(action, settings);
    if (!command) {
      if (action !== ACTIONS.WRITE_PARAMETER) showAlert(context);
      return;
    }
    await client.request(command.messageType, command.data);
    showOk(context);
  } catch {
    showAlert(context);
  }
}

async function handleDial(context, settings, ticks) {
  try {
    const parameterID = settings.parameterID;
    if (!parameterID || !Number.isFinite(ticks) || ticks === 0) {
      showAlert(context);
      return;
    }
    const client = await ensureNanaLive();
    const listed = await client.listParameters();
    const parameter = (listed.data?.parameters ?? []).find((item) => item.parameterID === parameterID);
    if (!parameter) {
      showAlert(context);
      return;
    }
    const value = parameterValueAfterTicks(parameter, ticks);
    const command = writeParameterCommand(parameterID, value);
    if (!command) {
      showAlert(context);
      return;
    }
    await client.request(command.messageType, command.data);
    setTitle(context, `${parameter.label ?? parameterID}\n${value.toFixed(1)}`);
  } catch {
    showAlert(context);
  }
}

function handleEvent(event) {
  const context = event.context;
  const action = event.action;
  const payload = event.payload ?? {};
  if (event.event === "didReceiveGlobalSettings") {
    globalSettings = { ...globalSettings, ...(payload.settings ?? {}) };
    return;
  }
  if (context && payload.settings) {
    settingsByContext.set(context, payload.settings);
  }
  if (event.event === "keyDown") {
    void handlePress(context, action, payload.settings ?? settingsByContext.get(context) ?? {});
    return;
  }
  if (event.event === "dialDown") {
    void handlePress(context, action, payload.settings ?? settingsByContext.get(context) ?? {});
    return;
  }
  if (event.event === "dialRotate") {
    void handleDial(context, payload.settings ?? settingsByContext.get(context) ?? {}, payload.ticks);
    return;
  }
  if (event.event === "sendToPlugin") {
    if (payload.event === "refreshCatalogs") {
      void catalogs()
        .then((data) => sendToPropertyInspector(context, { event: "catalogs", ...data }))
        .catch(() => sendToPropertyInspector(context, { event: "catalogsError" }));
    }
  }
}

async function connectPlugin() {
  const args = parseArgs(process.argv.slice(2));
  pluginUUID = args.pluginUUID;
  streamDeck = await connectTextWebSocket({
    host: "127.0.0.1",
    port: Number(args.port),
    onMessage(text) {
      handleEvent(JSON.parse(text));
    },
  });
  sendToStreamDeck({ event: args.registerEvent, uuid: pluginUUID });
  sendToStreamDeck({ event: "getGlobalSettings", context: pluginUUID });
}

void connectPlugin();
