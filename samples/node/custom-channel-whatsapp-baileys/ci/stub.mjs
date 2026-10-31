// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// Platform stub of the stand: a stand-in WhatsApp for the adapter in the stub mode, the stand API of the
// conformance suite and a recorder.
//
// Three roles on one port, Node alone without packages:
//
// - WhatsApp, as the adapter asks it in the stub mode: POST /platform/send-text and /platform/send-poll send
//   from a record of the stand — the credential is its label, the sender must be its number — and
//   GET /platform/health/{label} gives the state of its session. Every record is "ok" until a switch of the
//   stand's own, POST /stand/record-state, says otherwise; a record that is not "ok" refuses to send.
// - The stand API of the conformance suite: /conformance/reset, /conformance/scenario,
//   /conformance/follow-link and /conformance/platform-log. follow-link plays the user: the text of the
//   wa.me link goes to the record the link names, then the user votes in the poll that comes. The switch
//   platform_cannot_confirm is not played: a personal number has no case of it, and the stand says so.
// - A recorder for the own checks: the events an adapter posts instead of Veriqa (/registrar/api/channels/…)
//   and every sending, read with GET /stand/records and cleared with POST /stand/clear.
// - Switches of the own checks, all lifted by POST /stand/clear: POST /stand/refuse-send makes every sending
//   fail with 500, as a platform that does not take a message; POST /stand/refuse-events makes the recorder
//   answer every event with 503, as a Veriqa that is down — it counts the refused deliveries in
//   "refused_events" of the records instead of recording the events.
//
// The credential of a platform action is the label of a record, which is not a secret. Phone numbers never
// reach the log of the stub.

import { createServer } from "node:http";

import { digitsOf, newMessageId, postInbound, textMessage, voteMessage } from "./whatsapp.mjs";

// Settings of the stub, from the environment of its container.
//   STUB_RECORDS      label:number,… — the WhatsApp records of the stand, each number in E.164
//   STUB_USER         the phone number of the user of the stand in E.164 (follow-link only)
//   STUB_ADAPTER_URL  the base URL of the adapter the user writes to (follow-link only)
const PORT = Number(process.env.STUB_PORT || 9000);
const USER = process.env.STUB_USER ?? "";
const ADAPTER_URL = process.env.STUB_ADAPTER_URL ?? "";

// The marker of Veriqa in the prefilled text of a deep link.
const AUTH_MARKER = /auth_([0-9A-Za-z]{43})/;

// The option of the poll each answer of the suite votes for: the confirmation first, the refusal second.
const ANSWER_OPTIONS = { confirm: 0, decline: 1 };

// How long the user waits for the poll after following a link, and how often the sendings are looked at.
const POLL_WAIT_MS = 30_000;
const POLL_LOOK_MS = 200;

/** The states of a session the adapter reads from the health of a record. */
const RecordState = Object.freeze({ OK: "ok", NOT_PAIRED: "not_paired", NOT_CONNECTED: "not_connected" });

const PLATFORM_PREFIX = "/platform/";
const HEALTH_PREFIX = "/platform/health/";
const REGISTRAR_PREFIX = "/registrar/api/channels/";

/** The records of the stand, by label and by the digits of their number. */
const records = new Map();
const byDigits = new Map();
for (const entry of (process.env.STUB_RECORDS ?? "").split(",").filter((text) => text.trim())) {
  const [label, number] = entry.trim().split(":");
  const record = { label, number };
  records.set(label, record);
  byDigits.set(digitsOf(number ?? ""), record);
}

/** The state of the stand-in WhatsApp and of the recorder. */
const state = {
  platformLog: [],
  sends: [],
  sequence: 0,
  events: [],
  refusedEvents: 0,
  recordStates: new Map(),
  refuseSend: false,
  refuseEvents: false,
};

function log(text) {
  process.stderr.write(`stub: ${text}\n`);
}

function recordStateOf(label) {
  return state.recordStates.get(label) ?? RecordState.OK;
}

// ── WhatsApp ────────────────────────────────────────────────────────────────

/**
 * A sending of the adapter: from a known record, by its own number, while its session is "ok". A text
 * gets an identifier from the stub; a poll comes with the identifier the adapter made for it. Either is
 * one "sent" action of the platform log.
 */
function send(response, kind, body) {
  const record = records.get(body.credential);
  if (record === undefined || body.from !== record.number || typeof body.to !== "string" || !body.to) {
    sendJson(response, 400, { error: "unknown record, a sender that is not its number, or no recipient" });
    return;
  }
  if (recordStateOf(record.label) !== RecordState.OK) {
    sendJson(response, 409, { error: `record ${record.label} is ${recordStateOf(record.label)}` });
    return;
  }
  if (state.refuseSend) {
    sendJson(response, 500, { error: "the stand refuses every sending" });
    return;
  }
  let messageId;
  if (kind === "poll") {
    const options = body.options;
    if (typeof body.message_id !== "string" || !body.message_id || typeof body.question !== "string"
      || !Array.isArray(options) || options.length !== 2 || !options.every((option) => typeof option === "string")) {
      sendJson(response, 400, { error: "a poll needs message_id, question and two options" });
      return;
    }
    messageId = body.message_id;
  } else {
    if (typeof body.text !== "string") {
      sendJson(response, 400, { error: "a text needs text" });
      return;
    }
    messageId = newMessageId();
  }
  state.sequence += 1;
  state.sends.push({
    sequence: state.sequence,
    kind,
    credential: record.label,
    to: body.to,
    message_id: messageId,
    question: body.question ?? null,
    options: body.options ?? null,
    text: body.text ?? null,
  });
  state.platformLog.push({ kind: "sent", credential: record.label, chat_id: body.to, message_id: messageId });
  sendJson(response, 200, kind === "poll" ? {} : { message_id: messageId });
}

function health(response, label) {
  if (!records.has(label)) {
    sendEmpty(response, 404);
    return;
  }
  sendJson(response, 200, { state: recordStateOf(label) });
}

// ── The user of the stand ───────────────────────────────────────────────────

/** Opens a wa.me link as the user, then votes ``answer`` in the poll that comes. */
async function followLink(url, answer) {
  let link;
  try {
    link = new URL(url);
  } catch {
    link = null;
  }
  const record = link === null ? undefined : byDigits.get(link.pathname.replace(/^\//, ""));
  const text = link?.searchParams.get("text") ?? "";
  if (link?.hostname !== "wa.me" || record === undefined || AUTH_MARKER.exec(text) === null) {
    log("follow-link: not a wa.me link of a record of the stand with a sign-in code");
    return;
  }

  const seen = state.sequence;
  if (!(await deliver(textMessage(record.label, USER, text)))) {
    return;
  }

  const poll = await waitForPoll(seen);
  if (poll === null) {
    log(`follow-link: no poll came to the user within ${POLL_WAIT_MS / 1000} s`);
    return;
  }
  await deliver(voteMessage(poll.credential, USER, poll.message_id, [poll.options[ANSWER_OPTIONS[answer]]]));
}

/** Posts one message of the user to the service path of the adapter of the suite. */
async function deliver(message) {
  try {
    const status = await postInbound(ADAPTER_URL, message);
    if (status === 200) {
      return true;
    }
    log(`delivery to record ${message.credential}: status ${status}`);
  } catch (error) {
    log(`delivery to record ${message.credential} failed: ${error.name}`);
  }
  return false;
}

/** The first poll to the user sent after ``seen``. */
async function waitForPoll(seen) {
  const deadline = Date.now() + POLL_WAIT_MS;
  while (Date.now() < deadline) {
    const poll = state.sends.find((entry) => entry.sequence > seen && entry.kind === "poll" && entry.to === USER);
    if (poll !== undefined) {
      return poll;
    }
    await new Promise((resolve) => setTimeout(resolve, POLL_LOOK_MS));
  }
  return null;
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

  if (request.method === "GET") {
    if (path.startsWith(HEALTH_PREFIX)) {
      health(response, decodeURIComponent(path.slice(HEALTH_PREFIX.length)));
    } else if (path === "/conformance/platform-log") {
      sendJson(response, 200, { actions: state.platformLog });
    } else if (path === "/stand/records") {
      sendJson(response, 200, { events: state.events, sends: state.sends, refused_events: state.refusedEvents });
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
  if (path === `${PLATFORM_PREFIX}send-text`) {
    send(response, "text", jsonObject(body));
  } else if (path === `${PLATFORM_PREFIX}send-poll`) {
    send(response, "poll", jsonObject(body));
  } else if (path === "/conformance/reset") {
    state.platformLog = [];
    sendEmpty(response, 204);
  } else if (path === "/conformance/scenario") {
    const scenario = jsonObject(body);
    if (typeof scenario.platform_cannot_confirm !== "boolean") {
      sendEmpty(response, 400);
    } else if (scenario.platform_cannot_confirm) {
      // The stand declares no such case (PlatformCannotConfirm = false), so the suite never asks for it.
      log("scenario: platform_cannot_confirm is not played by this stand");
      sendEmpty(response, 501);
    } else {
      sendEmpty(response, 204);
    }
  } else if (path === "/conformance/follow-link") {
    const { url, answer } = jsonObject(body);
    if (typeof url !== "string" || !Object.hasOwn(ANSWER_OPTIONS, answer)) {
      sendEmpty(response, 400);
      return;
    }
    // Accepted at once; the user acts in the background, while this server keeps answering the sendings
    // of the same chain.
    followLink(url, answer).catch((error) => log(`follow-link failed: ${error.name}`));
    sendEmpty(response, 202);
  } else if (path.startsWith(REGISTRAR_PREFIX)) {
    if (state.refuseEvents) {
      state.refusedEvents += 1;
      sendEmpty(response, 503);
      return;
    }
    state.events.push({ path, webhook_id: request.headers["webhook-id"] ?? null, body: jsonObject(body) });
    sendEmpty(response, 200);
  } else if (path === "/stand/refuse-send") {
    state.refuseSend = true;
    sendEmpty(response, 204);
  } else if (path === "/stand/refuse-events") {
    state.refuseEvents = true;
    sendEmpty(response, 204);
  } else if (path === "/stand/record-state") {
    const { credential, state: recordState } = jsonObject(body);
    if (!records.has(credential)) {
      sendEmpty(response, 404);
    } else if (!Object.values(RecordState).includes(recordState)) {
      sendEmpty(response, 400);
    } else {
      state.recordStates.set(credential, recordState);
      sendEmpty(response, 204);
    }
  } else if (path === "/stand/clear") {
    state.events = [];
    state.sends = [];
    state.refusedEvents = 0;
    state.recordStates.clear();
    state.refuseSend = false;
    state.refuseEvents = false;
    sendEmpty(response, 204);
  } else {
    sendEmpty(response, 404);
  }
}

if (records.size === 0) {
  log("STUB_RECORDS is not set");
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
server.listen(PORT, "0.0.0.0", () => log(`listening on port ${PORT} for ${records.size} record(s)`));

for (const signal of ["SIGTERM", "SIGINT"]) {
  process.once(signal, () => process.exit(0));
}
