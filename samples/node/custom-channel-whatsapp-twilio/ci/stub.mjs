// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Platform stub of the stand: a stand-in Twilio, the stand API of the conformance suite and a recorder.
//
// Three roles on one port, Node alone without packages:
//
// - Twilio REST, the resources the adapter calls, under /2010-04-01/Accounts/{sid}: create a message, read a
//   message, read the account. A request is authenticated as Twilio authenticates it — Basic with the Account
//   SID and its Auth Token — and a message is sent only from a sender of that account. A created message is
//   answered "queued"; reading it gives "sent", or "failed" with code 63016 while the switch of the scenario
//   of the suite is on (the customer service window of the user is closed). Switches of the stand's own, both
//   on until the next clear: POST /stand/refuse-create makes the creation itself answer 400 with code 63016;
//   POST /stand/fail-status with {"error_code": <code>} makes every created message read "failed" with that
//   code — a failure of another kind than the closed window.
// - The stand API of the conformance suite: /conformance/reset, /conformance/scenario,
//   /conformance/follow-link and /conformance/platform-log. follow-link plays the user: the text of the
//   wa.me link goes to the webhook of the sender the link names, then the button of the prompt that comes
//   is pressed.
// - A recorder for the own checks: the events an adapter posts instead of Veriqa (/registrar/api/channels/…)
//   and every REST request, read with GET /stand/records and cleared with POST /stand/clear.
//
// The credential of a platform action is the Account SID, which is not a secret. Auth Tokens and phone
// numbers never reach the log of the stub.

import { createServer } from "node:http";

import { WEBHOOK_PATH_PREFIX, digitsOf, newMessageSid, postWebhook, twilioSignature, whatsAppAddress } from "./twilio.mjs";

// Settings of the stub, from the environment of its container.
//   STUB_ACCOUNTS     sid:token:sender,… — the Twilio accounts of the stand, each with its WhatsApp sender
//   STUB_USER         the phone number of the user of the stand in E.164 (follow-link only)
//   STUB_ADAPTER_URL  the base URL of the adapter the user writes to (follow-link only)
const PORT = Number(process.env.STUB_PORT || 9000);
const USER = process.env.STUB_USER ?? "";
const ADAPTER_URL = (process.env.STUB_ADAPTER_URL ?? "").replace(/\/+$/, "");

// Markers of Veriqa in the prefilled text of a deep link and in the ids of the buttons of a prompt.
const AUTH_MARKER = /auth_([0-9A-Za-z]{43})/;
const BUTTON_PREFIXES = { confirm: "vq_confirm_", decline: "vq_decline_" };

// How long the user waits for the prompt after following a link, and how often the log is looked at.
const PROMPT_WAIT_MS = 30_000;
const PROMPT_POLL_MS = 200;

// Codes of Twilio the stub answers with.
const OUTSIDE_SERVICE_WINDOW_CODE = 63016;
const AUTHENTICATE_CODE = 20003;
const NOT_FOUND_CODE = 20404;
const UNKNOWN_SENDER_CODE = 63007;

const REST_PATH = /^\/2010-04-01\/Accounts\/([^/]+?)(\.json|\/Messages\.json|\/Messages\/([^/]+)\.json)$/;
const REGISTRAR_PREFIX = "/registrar/api/channels/";

const PROFILE_NAME = "Stand User";

/** The Twilio accounts of the stand, by Account SID and by the digits of their sender. */
const accounts = new Map();
const bySender = new Map();
for (const entry of (process.env.STUB_ACCOUNTS ?? "").split(",").filter((text) => text.trim())) {
  const [sid, token, sender] = entry.trim().split(":");
  const account = { sid, token, senderDigits: digitsOf(sender ?? "") };
  accounts.set(sid, account);
  bySender.set(account.senderDigits, account);
}

/** The state of the stand-in Twilio and of the recorder. */
const state = {
  platformLog: [],
  messages: new Map(),
  rest: [],
  restSequence: 0,
  events: [],
  cannotConfirm: false,
  refuseCreate: false,
  failStatusCode: null,
};

function log(text) {
  process.stderr.write(`stub: ${text}\n`);
}

// ── Twilio REST ─────────────────────────────────────────────────────────────

function twilioError(response, status, code, message) {
  sendJson(response, status, { code, message, status });
}

/** Whether the Basic credentials of a request are the Account SID of ``account`` and its Auth Token. */
function authorized(request, account) {
  const header = request.headers.authorization ?? "";
  if (!header.startsWith("Basic ")) {
    return false;
  }
  const decoded = Buffer.from(header.slice("Basic ".length), "base64").toString("utf8");
  const separator = decoded.indexOf(":");
  return separator > 0 && decoded.slice(0, separator) === account.sid && decoded.slice(separator + 1) === account.token;
}

function recordRest(method, resource, account, form) {
  state.restSequence += 1;
  state.rest.push({ sequence: state.restSequence, method, resource, credential: account.sid, form });
}

async function twilioRest(request, response, match) {
  const account = accounts.get(match[1]);
  if (account === undefined || !authorized(request, account)) {
    twilioError(response, 401, AUTHENTICATE_CODE, "Authenticate");
    return;
  }
  const [, , resource, messageSid] = match;

  if (resource === ".json" && request.method === "GET") {
    recordRest("GET", "account", account, null);
    sendJson(response, 200, { sid: account.sid, status: "active", type: "Full" });
  } else if (resource === "/Messages.json" && request.method === "POST") {
    createMessage(response, account, Object.fromEntries(new URLSearchParams((await readBody(request)).toString("utf8"))));
  } else if (messageSid !== undefined && request.method === "GET") {
    recordRest("GET", "message", account, null);
    const message = state.messages.get(messageSid);
    if (message === undefined || message.accountSid !== account.sid) {
      twilioError(response, 404, NOT_FOUND_CODE, "The requested resource was not found");
      return;
    }
    sendJson(response, 200, { sid: messageSid, account_sid: account.sid, status: message.status, error_code: message.errorCode });
  } else {
    twilioError(response, 404, NOT_FOUND_CODE, "The requested resource was not found");
  }
}

/**
 * ``POST …/Messages.json``. A message the platform takes is one ``sent`` action of the platform log; under
 * the scenario of a closed window it is created all the same — Twilio accepts it and fails it afterwards —
 * but never sent.
 */
function createMessage(response, account, form) {
  recordRest("POST", "create", account, form);
  if (state.refuseCreate) {
    twilioError(response, 400, OUTSIDE_SERVICE_WINDOW_CODE, "Message failed to send because more than 24 hours have passed since the customer last replied to this number");
    return;
  }
  if (form.From !== whatsAppAddress(`+${account.senderDigits}`)) {
    twilioError(response, 400, UNKNOWN_SENDER_CODE, "Twilio could not find a Channel with the specified From address");
    return;
  }
  const to = String(form.To ?? "");
  const sid = newMessageSid();
  const closed = state.cannotConfirm;
  const failureCode = closed ? OUTSIDE_SERVICE_WINDOW_CODE : state.failStatusCode;
  state.messages.set(sid, {
    accountSid: account.sid,
    status: failureCode === null ? "sent" : "failed",
    errorCode: failureCode,
  });
  if (failureCode === null) {
    state.platformLog.push({ kind: "sent", credential: account.sid, chat_id: to.replace(/^whatsapp:/, ""), message_id: sid });
  }
  sendJson(response, 201, { sid, account_sid: account.sid, status: "queued", error_code: null });
}

// ── The user of the stand ───────────────────────────────────────────────────

/** Opens a wa.me link as the user, then presses the button ``answer`` of the prompt that comes. */
async function followLink(url, answer) {
  let link;
  try {
    link = new URL(url);
  } catch {
    link = null;
  }
  const account = link === null ? undefined : bySender.get(link.pathname.replace(/^\//, ""));
  const text = link?.searchParams.get("text") ?? "";
  const marker = AUTH_MARKER.exec(text);
  if (link?.hostname !== "wa.me" || account === undefined || marker === null) {
    log("follow-link: not a wa.me link of a sender of the stand with a sign-in code");
    return;
  }

  const seen = state.restSequence;
  const start = { From: whatsAppAddress(USER), Body: text, MessageType: "text", NumMedia: "0" };
  if (!(await deliver(account, start))) {
    return;
  }

  const buttonId = BUTTON_PREFIXES[answer] + marker[1];
  const prompt = await waitForPrompt(buttonId, seen);
  if (prompt === null) {
    log(`follow-link: no prompt with the ${answer} button came within ${PROMPT_WAIT_MS / 1000} s`);
    return;
  }
  await deliver(prompt.account, {
    From: whatsAppAddress(USER),
    Body: prompt.title,
    ButtonText: prompt.title,
    ButtonPayload: buttonId,
    MessageType: "button",
    NumMedia: "0",
  });
}

/** Posts one message of the user to the webhook of the sender of ``account``, signed with its Auth Token. */
async function deliver(account, content) {
  const url = `${ADAPTER_URL}${WEBHOOK_PATH_PREFIX}${account.senderDigits}`;
  const parameters = {
    MessageSid: newMessageSid(),
    AccountSid: account.sid,
    To: whatsAppAddress(`+${account.senderDigits}`),
    ProfileName: PROFILE_NAME,
    WaId: digitsOf(USER),
    ...content,
  };
  try {
    const status = await postWebhook(url, parameters, twilioSignature(account.token, url, parameters));
    if (status === 200) {
      return true;
    }
    log(`delivery to the sender of ${account.sid}: status ${status}`);
  } catch (error) {
    log(`delivery to the sender of ${account.sid} failed: ${error.name}`);
  }
  return false;
}

/**
 * The first prompt to the user since ``seen`` that carries the button ``buttonId``: the account it was sent
 * from and the title of that button. The prompt is a message of the quick-reply content, and its variables
 * hold the id of each button right after its title.
 */
async function waitForPrompt(buttonId, seen) {
  const deadline = Date.now() + PROMPT_WAIT_MS;
  while (Date.now() < deadline) {
    for (const entry of state.rest) {
      if (entry.sequence <= seen || entry.resource !== "create" || entry.form?.To !== whatsAppAddress(USER)) {
        continue;
      }
      const variables = contentVariables(entry.form.ContentVariables);
      const key = Object.keys(variables).find((name) => variables[name] === buttonId);
      if (key !== undefined) {
        const account = bySender.get(digitsOf(entry.form.From));
        return { account, title: String(variables[String(Number(key) - 1)] ?? "") };
      }
    }
    await new Promise((resolve) => setTimeout(resolve, PROMPT_POLL_MS));
  }
  return null;
}

function contentVariables(text) {
  try {
    const parsed = JSON.parse(text ?? "");
    return typeof parsed === "object" && parsed !== null ? parsed : {};
  } catch {
    return {};
  }
}

// ── HTTP ────────────────────────────────────────────────────────────────────

async function readBody(request) {
  const chunks = [];
  for await (const chunk of request) {
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
}

function jsonObject(body) {
  try {
    const parsed = JSON.parse(body.toString("utf8") || "{}");
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed) ? parsed : {};
  } catch {
    return {};
  }
}

function sendJson(response, status, value) {
  const body = JSON.stringify(value);
  response.writeHead(status, { "content-type": "application/json", "content-length": Buffer.byteLength(body) });
  response.end(body);
}

function sendEmpty(response, status) {
  response.writeHead(status, { "content-length": "0" });
  response.end();
}

async function route(request, response) {
  const path = request.url.split("?", 1)[0];
  const rest = REST_PATH.exec(path);
  if (rest !== null) {
    await twilioRest(request, response, rest);
    return;
  }

  if (request.method === "GET") {
    if (path === "/conformance/platform-log") {
      sendJson(response, 200, { actions: state.platformLog });
    } else if (path === "/stand/records") {
      sendJson(response, 200, { events: state.events, rest: state.rest });
    } else {
      sendEmpty(response, 404);
    }
    return;
  }
  if (request.method !== "POST") {
    sendEmpty(response, 405);
    return;
  }

  const body = await readBody(request);
  if (path === "/conformance/reset") {
    state.platformLog = [];
    state.cannotConfirm = false;
    sendEmpty(response, 204);
  } else if (path === "/conformance/scenario") {
    const scenario = jsonObject(body);
    if (typeof scenario.platform_cannot_confirm !== "boolean") {
      sendEmpty(response, 400);
      return;
    }
    state.cannotConfirm = scenario.platform_cannot_confirm;
    sendEmpty(response, 204);
  } else if (path === "/conformance/follow-link") {
    const { url, answer } = jsonObject(body);
    if (typeof url !== "string" || !Object.hasOwn(BUTTON_PREFIXES, answer)) {
      sendEmpty(response, 400);
      return;
    }
    // Accepted at once; the user acts in the background, while this server keeps answering the REST
    // requests of the same chain.
    followLink(url, answer).catch((error) => log(`follow-link failed: ${error.name}`));
    sendEmpty(response, 202);
  } else if (path.startsWith(REGISTRAR_PREFIX)) {
    state.events.push({ path, webhook_id: request.headers["webhook-id"] ?? null, body: jsonObject(body) });
    sendEmpty(response, 200);
  } else if (path === "/stand/refuse-create") {
    state.refuseCreate = true;
    sendEmpty(response, 204);
  } else if (path === "/stand/fail-status") {
    const { error_code: errorCode } = jsonObject(body);
    if (!Number.isInteger(errorCode)) {
      sendEmpty(response, 400);
      return;
    }
    state.failStatusCode = errorCode;
    sendEmpty(response, 204);
  } else if (path === "/stand/clear") {
    state.events = [];
    state.rest = [];
    state.refuseCreate = false;
    state.failStatusCode = null;
    sendEmpty(response, 204);
  } else {
    sendEmpty(response, 404);
  }
}

if (accounts.size === 0) {
  log("STUB_ACCOUNTS is not set");
  process.exit(2);
}

const server = createServer((request, response) => {
  route(request, response).catch((error) => {
    log(`a request failed: ${error.name}`);
    if (!response.headersSent) {
      sendEmpty(response, 500);
    }
  });
});
server.listen(PORT, "0.0.0.0", () => log(`listening on port ${PORT} for ${accounts.size} account(s)`));

for (const signal of ["SIGTERM", "SIGINT"]) {
  process.once(signal, () => process.exit(0));
}
