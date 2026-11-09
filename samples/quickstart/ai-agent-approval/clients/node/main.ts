// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The backend of the AI agent approval sample on Node.js: Express. A support agent handles a refund
// ticket with three tools known in advance; `refund_payment` moves money, so every call of it waits
// until a person approves it in Telegram through the Veriqa confirmation API. The agent here is
// scripted, not a model: the point is the gate in the tool-invocation path, which no output of a
// model could skip. Node.js runs this TypeScript file directly, with no build step.
//
// Every step is driven by the page: POST /runs starts the agent up to the approval, and each
// GET /runs/{id} reads the approval once and, once a person has answered, lets the agent finish.
//
// Configuration (environment variables, all optional on the local stand):
//   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//   VERIQA_CLIENT_ID     - the client registered on the issuer, `support-agent`;
//   VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
//   VERIQA_CA_FILE       - the certificate to trust for the issuer, by default the stand's
//                          ../../built-in/certs/localhost.crt;
//   PORT                 - the port of this backend, 7040.

import { randomBytes } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { getCACertificates, setDefaultCACertificates } from "node:tls";

import express from "express";

const ISSUER = process.env.VERIQA_ISSUER ?? "https://localhost:8443";
const CLIENT_ID = process.env.VERIQA_CLIENT_ID ?? "support-agent";
const CLIENT_SECRET = process.env.VERIQA_CLIENT_SECRET ?? readEnvFile(join(import.meta.dirname, "../../built-in/.env"), "CLIENT_SECRET");
const CA_FILE = process.env.VERIQA_CA_FILE ?? join(import.meta.dirname, "../../built-in/certs/localhost.crt");
const PORT = Number(process.env.PORT ?? 7040);
const PAGE = join(import.meta.dirname, "../page/index.html");

if (!CLIENT_SECRET) {
  console.error("No client secret: set VERIQA_CLIENT_SECRET, or CLIENT_SECRET in ../../built-in/.env.");
  process.exit(1);
}

// The stand's certificate is self-signed; an issuer with a public certificate needs none of this.
if (existsSync(CA_FILE)) {
  setDefaultCACertificates([...getCACertificates("default"), readFileSync(CA_FILE, "utf8")]);
}

/** Reads one KEY=VALUE line of a .env file; undefined when the file or the key is missing. */
function readEnvFile(path: string, key: string): string | undefined {
  if (!existsSync(path)) {
    return undefined;
  }
  for (const line of readFileSync(path, "utf8").split(/\r?\n/)) {
    const trimmed = line.trim();
    if (trimmed.startsWith(`${key}=`)) {
      return trimmed.slice(key.length + 1).trim() || undefined;
    }
  }
  return undefined;
}

// --- the Veriqa confirmation API ------------------------------------------------------------------

/** A refusal of Veriqa: its status and its body (Problem Details or an OAuth error), unchanged. */
class VeriqaError extends Error {
  status: number;
  body: string;
  constructor(status: number, body: string) {
    super(`Veriqa answered ${status}: ${body}`);
    this.status = status;
    this.body = body;
  }
}

type ChannelEntry = { kind: string; channel_type?: string; url: string; qr?: string | null; valid_until: string };
type Created = { transaction_id: string; expires_at: string; channel_entry: ChannelEntry };
type Result = { outcome: string; matched_type: string | null };

// The access token is held until shortly before it expires: waiting for a person means polling every
// couple of seconds, and a new token per poll runs into the rate limit of /connect/token.
const EXPIRY_MARGIN_MS = 30_000;
let heldToken: { value: string; validUntil: number } | undefined;

async function requestToken(form: Record<string, string>): Promise<Record<string, unknown>> {
  const basic = Buffer.from(`${encodeURIComponent(CLIENT_ID)}:${encodeURIComponent(CLIENT_SECRET!)}`).toString("base64");
  const response = await fetch(`${ISSUER}/connect/token`, {
    method: "POST",
    headers: { Authorization: `Basic ${basic}`, "Content-Type": "application/x-www-form-urlencoded" },
    body: new URLSearchParams(form),
  });
  const text = await response.text();
  if (!response.ok) {
    throw new VeriqaError(response.status, text);
  }
  return JSON.parse(text);
}

async function accessToken(): Promise<string> {
  if (heldToken && Date.now() < heldToken.validUntil) {
    return heldToken.value;
  }
  const token = await requestToken({ grant_type: "client_credentials" });
  const value = token.access_token as string;
  const validUntil = Date.now() + Number(token.expires_in) * 1000 - EXPIRY_MARGIN_MS;
  // A token whose remaining life is shorter than the margin is used once and not held.
  heldToken = validUntil > Date.now() ? { value, validUntil } : undefined;
  return value;
}

// Sends a request as this backend. A 401 means the held token is no longer accepted (a restarted
// stand forgets its tokens): it is dropped and the request is sent once more with a fresh one.
async function callVeriqa<T>(method: string, path: string, body?: unknown): Promise<T> {
  const send = async (token: string) =>
    fetch(`${ISSUER}${path}`, {
      method,
      headers: { Authorization: `Bearer ${token}`, ...(body === undefined ? {} : { "Content-Type": "application/json" }) },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
  let token = await accessToken();
  let response = await send(token);
  if (response.status === 401) {
    if (heldToken?.value === token) {
      heldToken = undefined;
    }
    token = await accessToken();
    response = await send(token);
  }
  const text = await response.text();
  if (!response.ok) {
    throw new VeriqaError(response.status, text);
  }
  return JSON.parse(text) as T;
}

function createConfirmation(actionType: string, slotValues: Record<string, string>): Promise<Created> {
  // A retry with the same key answers with the same transaction instead of a second one.
  return callVeriqa<Created>("POST", "/api/transaction/confirmation", {
    action_type: actionType,
    slot_values: slotValues,
    idempotency_key: randomBytes(16).toString("hex"),
  });
}

function confirmationResult(transactionId: string): Promise<Result> {
  return callVeriqa<Result>("GET", `/api/transaction/${encodeURIComponent(transactionId)}/result`);
}

// Who confirmed: the transaction is exchanged once for the id_token of that person.
async function exchangeForClaims(transactionId: string): Promise<Record<string, unknown>> {
  const token = await requestToken({
    grant_type: "urn:veriqa:params:oauth:grant-type:confirmation",
    transaction_id: transactionId,
    scope: "openid channel",
  });
  // The id_token came straight from the issuer's token endpoint over TLS, so the payload is read
  // without validating the signature (OpenID Connect Core 3.1.3.7, item 6). A token received any
  // other way must be validated against the issuer's JWKS.
  const payload = (token.id_token as string).split(".")[1];
  return JSON.parse(Buffer.from(payload, "base64url").toString("utf8"));
}

// --- the agent's tools -----------------------------------------------------------------------------

type Args = Record<string, string>;

/** A tool the agent may call. `approval` set - a person approves every call before it runs. */
type Tool = {
  approval: { actionType: string; slots: (args: Args) => Record<string, string> } | null;
  execute: (args: Args) => string;
};

const orders: Record<string, { item: string; amount: string; customer: string }> = {
  "A-1042": { item: "Wireless headphones", amount: "89.00 EUR", customer: "anna@example.com" },
  "A-1043": { item: "Standing desk", amount: "420.00 EUR", customer: "ben@example.com" },
};
const refunded = new Set<string>();

// The approval policy is data of the tool: the person reads the slots, never text the agent wrote.
const tools: Record<string, Tool> = {
  lookup_order: {
    approval: null,
    execute: ({ order }) => {
      const found = orders[order];
      if (!found) {
        return `Order ${order} does not exist.`;
      }
      return `Order ${order}: ${found.item}, paid ${found.amount}, customer ${found.customer}, `
        + (refunded.has(order) ? "already refunded." : "not refunded.");
    },
  },
  refund_payment: {
    approval: {
      actionType: "approve-refund",
      slots: ({ amount, order, reason }) => ({ amount, order, reason }),
    },
    execute: ({ order, amount }) => {
      refunded.add(order);
      return `Refunded ${amount} for order ${order}.`;
    },
  },
  send_customer_email: {
    approval: null,
    execute: ({ order, text }) => `E-mail sent to the customer of order ${order}: "${text}"`,
  },
};

// --- a run of the agent ----------------------------------------------------------------------------

type Run = {
  id: string;
  ticket: { order: string; amount: string; reason: string };
  log: { at: string; kind: string; text: string }[];
  pending: { tool: string; url: string; qr: string | null } | null;
  held: { transactionId: string; args: Args } | null;
  completion: string | null;
};

const runs = new Map<string, Run>();

function log(run: Run, kind: string, text: string): void {
  run.log.push({ at: new Date().toTimeString().slice(0, 8), kind, text });
}

function logCall(run: Run, name: string, args: Args): void {
  log(run, "call", `${name}(${Object.entries(args).map(([key, value]) => `${key}: ${value}`).join(", ")})`);
}

// Calls a tool. One without an approval runs at once; one with an approval only creates the
// confirmation and holds the run - nothing has run until a person answers.
async function invoke(run: Run, name: string, args: Args): Promise<void> {
  const tool = tools[name];
  logCall(run, name, args);
  if (tool.approval) {
    log(run, "approval", `${name} needs a person's approval — waiting.`);
    const created = await createConfirmation(tool.approval.actionType, tool.approval.slots(args));
    run.pending = { tool: name, url: created.channel_entry.url, qr: created.channel_entry.qr ?? null };
    run.held = { transactionId: created.transaction_id, args };
    return;
  }
  log(run, "result", tool.execute(args));
}

// Reads the approval that holds the run once. When a person has answered, the held tool runs or is
// refused, and the agent finishes the ticket.
async function advance(run: Run): Promise<void> {
  if (!run.held || !run.pending) {
    return;
  }
  const result = await confirmationResult(run.held.transactionId);
  if (result.outcome === "pending") {
    return;
  }
  const name = run.pending.tool;
  let executed = false;
  if (result.outcome === "confirmed") {
    // Who approved goes into the agent's own log - the audit of the run.
    const claims = await exchangeForClaims(run.held.transactionId);
    log(run, "approval", `Approved by ${claims.sub}.`);
    log(run, "result", tools[name].execute(run.held.args));
    executed = true;
  } else {
    log(run, "result", `Not executed: the approval ended as ${result.outcome}.`);
  }
  run.pending = null;
  run.held = null;

  const text = executed
    ? `Your refund of ${run.ticket.amount} is on its way.`
    : "We could not approve a refund right now; a colleague will contact you.";
  await invoke(run, "send_customer_email", { order: run.ticket.order, text });
  run.completion = "Done.";
}

function stop(run: Run, failure: unknown): void {
  run.pending = null;
  run.held = null;
  run.completion = `Stopped: ${failure instanceof Error ? failure.message : String(failure)}`;
}

function snapshot(run: Run) {
  return { id: run.id, log: run.log, pending: run.pending, completion: run.completion };
}

// --- the HTTP surface ------------------------------------------------------------------------------

const app = express();
app.use(express.json());

app.get("/", (_req, res) => {
  res.type("html").send(readFileSync(PAGE, "utf8"));
});

app.post("/runs", async (req, res) => {
  const { order = "", amount = "", reason = "" } = (req.body ?? {}) as Record<string, string>;
  const run: Run = { id: randomBytes(16).toString("hex"), ticket: { order, amount, reason }, log: [], pending: null, held: null, completion: null };
  runs.set(run.id, run);
  try {
    log(run, "agent", `Ticket: the customer asks to refund order ${order} — "${reason}".`);
    await invoke(run, "lookup_order", { order });
    await invoke(run, "refund_payment", { order, amount, reason });
  } catch (failure) {
    stop(run, failure);
  }
  res.json({ id: run.id });
});

app.get("/runs/:id", async (req, res) => {
  const run = runs.get(req.params.id);
  if (!run) {
    res.sendStatus(404);
    return;
  }
  try {
    await advance(run);
  } catch (failure) {
    stop(run, failure);
  }
  res.json(snapshot(run));
});

app.listen(PORT, "localhost", () => {
  console.log(`The support agent: http://localhost:${PORT}`);
});
