// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The backend of the confirmation and step-up sample on Node.js: Express. Two uses of the Veriqa
// confirmation API, with no sign-in and no redirect:
//   - a server-to-server confirmation: a person approves a payment in Telegram, and the backend
//     learns who confirmed from an id_token;
//   - a step-up: deleting a project needs a confirmation from the owner of the account, named in
//     expected_identities, and the project is deleted only when Veriqa reports that the owner matched.
// Node.js runs this TypeScript file directly, with no build step. Every step is driven by the page:
// each poll reads the outcome once.
//
// Configuration (environment variables, all optional on the local stand):
//   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//   VERIQA_CLIENT_ID     - the client registered on the issuer, `sample-app`;
//   VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
//   VERIQA_CA_FILE       - the certificate to trust for the issuer, by default the stand's
//                          ../../built-in/certs/localhost.crt;
//   PORT                 - the port of this backend, 7030.

import { randomBytes } from "node:crypto";
import { existsSync, readFileSync } from "node:fs";
import { join } from "node:path";
import { getCACertificates, setDefaultCACertificates } from "node:tls";

import express from "express";
import type { Response } from "express";

const ISSUER = process.env.VERIQA_ISSUER ?? "https://localhost:8443";
const CLIENT_ID = process.env.VERIQA_CLIENT_ID ?? "sample-app";
const CLIENT_SECRET = process.env.VERIQA_CLIENT_SECRET ?? readEnvFile(join(import.meta.dirname, "../../built-in/.env"), "CLIENT_SECRET");
const CA_FILE = process.env.VERIQA_CA_FILE ?? join(import.meta.dirname, "../../built-in/certs/localhost.crt");
const PORT = Number(process.env.PORT ?? 7030);
const PAGE = join(import.meta.dirname, "../page/index.html");

// The identity type the step-up names; declared in IdentityMatchComparableTypes of the client entry.
const OWNER_IDENTITY_TYPE = "telegram_user_id";

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

function createConfirmation(
  actionType: string,
  slotValues: Record<string, string>,
  expectedIdentities?: Record<string, string>,
): Promise<Created> {
  // A retry with the same key answers with the same transaction instead of a second one.
  return callVeriqa<Created>("POST", "/api/transaction/confirmation", {
    action_type: actionType,
    slot_values: slotValues,
    idempotency_key: randomBytes(16).toString("hex"),
    ...(expectedIdentities ? { expected_identities: expectedIdentities } : {}),
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

// --- the state of the sample -----------------------------------------------------------------------

type Project = { id: string; name: string; documents: number };
type Deletion = { transaction_id: string; project_id: string; outcome: string; verdict: string | null };

const projects: Project[] = [
  { id: "q3-report", name: "Q3 report", documents: 12 },
  { id: "customer-onboarding", name: "Customer onboarding", documents: 48 },
  { id: "archive-2024", name: "Archive 2024", documents: 305 },
];
// The owner of the account: a real application knows it from account linking. This sample binds
// the account to the first person who confirms a deletion.
let owner: string | null = null;
// The project of each deletion is kept here: the page never says which project a confirmed
// transaction deletes.
const deletions = new Map<string, Deletion>();
// A transaction is redeemed for an id_token once, so the claims are kept for the page to read again.
const paymentClaims = new Map<string, Record<string, unknown>>();

function workspace() {
  return { owner, projects };
}

function deleteProject(projectId: string): void {
  const index = projects.findIndex((project) => project.id === projectId);
  if (index >= 0) {
    projects.splice(index, 1);
  }
}

// Answers a refusal of Veriqa with its own status and body, so the page shows what went wrong.
function fail(res: Response, failure: unknown): void {
  if (failure instanceof VeriqaError) {
    res.status(failure.status).type("application/json").send(failure.body);
    return;
  }
  res.status(502).json({ error: failure instanceof Error ? failure.message : String(failure) });
}

// --- the HTTP surface ------------------------------------------------------------------------------

const app = express();
app.use(express.json());

app.get("/", (_req, res) => {
  res.type("html").send(readFileSync(PAGE, "utf8"));
});

// 1. A server-to-server confirmation of a payment.
app.post("/payments", async (req, res) => {
  const { amount = "", payee = "" } = (req.body ?? {}) as Record<string, string>;
  try {
    const created = await createConfirmation("approve-payment", { amount, payee });
    res.json({
      transaction_id: created.transaction_id,
      url: created.channel_entry.url,
      qr: created.channel_entry.qr ?? null,
      valid_until: created.channel_entry.valid_until,
    });
  } catch (failure) {
    fail(res, failure);
  }
});

app.get("/payments/:tid", async (req, res) => {
  const transactionId = req.params.tid;
  try {
    const result = await confirmationResult(transactionId);
    if (result.outcome !== "confirmed") {
      res.json({ outcome: result.outcome });
      return;
    }
    let claims = paymentClaims.get(transactionId);
    if (!claims) {
      claims = await exchangeForClaims(transactionId);
      paymentClaims.set(transactionId, claims);
    }
    res.json({ outcome: result.outcome, claims });
  } catch (failure) {
    fail(res, failure);
  }
});

// 2. A step-up before deleting a project.
app.get("/workspace", (_req, res) => {
  res.json(workspace());
});

app.post("/workspace/owner/reset", (_req, res) => {
  owner = null;
  res.json(workspace());
});

app.post("/projects/:pid/deletion", async (req, res) => {
  const project = projects.find((candidate) => candidate.id === req.params.pid);
  if (!project) {
    res.sendStatus(404);
    return;
  }
  try {
    // The step-up itself: once an owner is known, the confirmation is addressed to that person.
    const created = await createConfirmation(
      "delete-project",
      { project: project.name, documents: String(project.documents) },
      owner === null ? undefined : { [OWNER_IDENTITY_TYPE]: owner },
    );
    deletions.set(created.transaction_id, {
      transaction_id: created.transaction_id,
      project_id: project.id,
      outcome: "pending",
      verdict: null,
    });
    res.json({
      transaction_id: created.transaction_id,
      url: created.channel_entry.url,
      qr: created.channel_entry.qr ?? null,
      expects_owner: owner !== null,
    });
  } catch (failure) {
    fail(res, failure);
  }
});

app.get("/deletions/:tid", async (req, res) => {
  const deletion = deletions.get(req.params.tid);
  if (!deletion) {
    res.sendStatus(404);
    return;
  }
  if (deletion.verdict !== null) {
    res.json(deletion);
    return;
  }
  try {
    const result = await confirmationResult(deletion.transaction_id);
    if (result.outcome === "pending") {
      res.json(deletion);
      return;
    }
    deletion.outcome = result.outcome;
    if (result.outcome !== "confirmed") {
      deletion.verdict = "not_deleted";
    } else if (owner === null) {
      // No owner yet: the first person to confirm becomes the owner of the account.
      const claims = await exchangeForClaims(deletion.transaction_id);
      owner = String(claims.channel_user_id);
      deleteProject(deletion.project_id);
      deletion.verdict = "deleted_owner_bound";
    } else if (result.matched_type === OWNER_IDENTITY_TYPE) {
      // The decision a step-up exists for: confirmed is not enough, it has to be the expected
      // person. Veriqa does not refuse a confirmation by somebody else - it reports the match.
      deleteProject(deletion.project_id);
      deletion.verdict = "deleted";
    } else {
      deletion.verdict = "confirmed_by_another_person";
    }
    res.json(deletion);
  } catch (failure) {
    fail(res, failure);
  }
});

app.listen(PORT, "localhost", () => {
  console.log(`The sample application: http://localhost:${PORT}`);
});
