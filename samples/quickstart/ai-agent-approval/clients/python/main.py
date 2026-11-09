# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The support agent of the approval sample on Python: Flask and requests.

A support agent handles a refund ticket with three tools known in advance. Two run on their own;
refund_payment moves money, so every call of it waits until a person approves it in Telegram. The
approval is a Veriqa server-to-server confirmation: the backend calls the confirmation API with its
own token, and the person reads a wording declared on Veriqa, never text the agent wrote.

Every step is driven by the page: each read of a run checks the approval that holds the agent and,
once a person has answered, lets the agent go on with its next tool.

Configuration (environment variables, all optional on the local stand):
  VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
  VERIQA_CLIENT_ID     - the client registered on Veriqa, `support-agent`;
  VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
  VERIQA_CA_FILE       - the certificate to trust for the issuer, ../../built-in/certs/localhost.crt
                         by default (used when the file exists);
  PORT                 - the port of this client, 7040.
"""

import base64
import json
import os
import secrets
import sys
import threading
import time
from datetime import datetime
from pathlib import Path
from urllib.parse import quote

import requests
from flask import Flask, Response, jsonify, request

HERE = Path(__file__).resolve().parent
STAND = HERE / "../../built-in"
PAGE = HERE / "../page/index.html"

ACTION_TYPE = "approve-refund"
CONFIRMATION_GRANT = "urn:veriqa:params:oauth:grant-type:confirmation"
EXPIRY_MARGIN_SECONDS = 30


def stand_env(key):
    """Reads one KEY=VALUE line of the stand's .env, or None."""
    env_file = STAND / ".env"
    if not env_file.exists():
        return None
    for line in env_file.read_text(encoding="utf-8").splitlines():
        line = line.strip()
        if line and not line.startswith("#") and "=" in line:
            name, value = line.split("=", 1)
            if name.strip() == key:
                return value.strip()
    return None


ISSUER = os.environ.get("VERIQA_ISSUER", "https://localhost:8443").rstrip("/")
CLIENT_ID = os.environ.get("VERIQA_CLIENT_ID", "support-agent")
CLIENT_SECRET = os.environ.get("VERIQA_CLIENT_SECRET") or stand_env("CLIENT_SECRET")
CA_FILE = Path(os.environ.get("VERIQA_CA_FILE", STAND / "certs/localhost.crt"))
PORT = int(os.environ.get("PORT", "7040"))

if not CLIENT_SECRET:
    sys.exit("No client secret: set VERIQA_CLIENT_SECRET, or CLIENT_SECRET in ../../built-in/.env")

# The stand's certificate is self-signed; an issuer with a public certificate needs none of this.
VERIFY = str(CA_FILE) if CA_FILE.exists() else True


class VeriqaError(Exception):
    """A refusal of Veriqa: its status and body, shown as they are."""

    def __init__(self, status, body):
        super().__init__(f"Veriqa answered {status}: {body}")
        self.status = status
        self.body = body


class Veriqa:
    """The confirmation API of Veriqa, called as this backend."""

    def __init__(self):
        self._lock = threading.Lock()
        self._token = None
        self._valid_until = 0.0

    def _request_token(self, form):
        response = requests.post(
            f"{ISSUER}/connect/token",
            data=form,
            auth=(quote(CLIENT_ID, safe=""), quote(CLIENT_SECRET, safe="")),
            verify=VERIFY,
            timeout=30,
        )
        if not response.ok:
            raise VeriqaError(response.status_code, response.text)
        return response.json()

    def _access_token(self):
        # The token is held until shortly before expires_in: the page polls every two seconds, and a
        # new token per poll runs into the rate limit of /connect/token.
        with self._lock:
            if self._token and time.time() < self._valid_until:
                return self._token
            token = self._request_token({"grant_type": "client_credentials"})
            valid_until = time.time() + token["expires_in"] - EXPIRY_MARGIN_SECONDS
            # A token with less life left than the margin is used once and not held.
            self._token = token["access_token"] if valid_until > time.time() else None
            self._valid_until = valid_until
            return token["access_token"]

    def _drop(self, stale):
        with self._lock:
            if self._token == stale:
                self._token = None

    def _call(self, method, path, body=None):
        # A held token can stop being accepted early (Veriqa restarted); a 401 gets one more try
        # with a fresh token.
        for attempt in range(2):
            token = self._access_token()
            response = requests.request(
                method,
                f"{ISSUER}{path}",
                json=body,
                headers={"Authorization": f"Bearer {token}"},
                verify=VERIFY,
                timeout=30,
            )
            if response.status_code == 401 and attempt == 0:
                self._drop(token)
                continue
            if not response.ok:
                raise VeriqaError(response.status_code, response.text)
            return response.json()

    def create(self, action_type, slot_values):
        # A retry with the same key answers with the same transaction instead of a second one.
        return self._call("POST", "/api/transaction/confirmation", {
            "action_type": action_type,
            "slot_values": slot_values,
            "idempotency_key": secrets.token_hex(16),
        })

    def result(self, transaction_id):
        return self._call("GET", f"/api/transaction/{quote(transaction_id, safe='')}/result")

    def claims(self, transaction_id):
        """Exchanges a confirmed transaction for the id_token of the person who confirmed."""
        token = self._request_token({
            "grant_type": CONFIRMATION_GRANT,
            "transaction_id": transaction_id,
            "scope": "openid channel",
        })
        # The id_token came straight from the issuer's token endpoint over TLS, so the payload is read
        # without validating the signature (OpenID Connect Core 3.1.3.7, item 6). A token received
        # any other way must be validated against the issuer's JWKS.
        payload = token["id_token"].split(".")[1]
        return json.loads(base64.urlsafe_b64decode(payload + "=" * (-len(payload) % 4)))


veriqa = Veriqa()

# --- the tools -------------------------------------------------------------------------------------

ORDERS = {
    "A-1042": {"item": "Wireless headphones", "amount": "89.00 EUR", "customer": "anna@example.com"},
    "A-1043": {"item": "Standing desk", "amount": "420.00 EUR", "customer": "ben@example.com"},
}
refunded = set()


def lookup_order(args):
    order = ORDERS.get(args["order"])
    if order is None:
        return f"Order {args['order']} does not exist."
    state = "already refunded." if args["order"] in refunded else "not refunded."
    return (f"Order {args['order']}: {order['item']}, paid {order['amount']}, "
            f"customer {order['customer']}, {state}")


def refund_payment(args):
    refunded.add(args["order"])
    return f"Refunded {args['amount']} for order {args['order']}."


def send_customer_email(args):
    return f"E-mail sent to the customer of order {args['order']}: \"{args['text']}\""


# The approval policy is data of the tool: the gate sits in the tool-invocation path, not in the
# prompt, so no output of a model can skip it. The person sees the slots, never text the agent wrote.
TOOLS = {
    "lookup_order": {"approval": None, "execute": lookup_order},
    "refund_payment": {
        "approval": {
            "action_type": ACTION_TYPE,
            "slots": lambda a: {"amount": a["amount"], "order": a["order"], "reason": a["reason"]},
        },
        "execute": refund_payment,
    },
    "send_customer_email": {"approval": None, "execute": send_customer_email},
}

# --- the runs --------------------------------------------------------------------------------------

runs = {}
runs_lock = threading.Lock()


def log(run, kind, text):
    run["log"].append({"at": datetime.now().strftime("%H:%M:%S"), "kind": kind, "text": text})


def log_call(run, tool, args):
    log(run, "call", f"{tool}({', '.join(f'{k}: {v}' for k, v in args.items())})")


def invoke(run, tool, args):
    """Runs a tool without approval, or stops the run at the approval of one that needs it."""
    log_call(run, tool, args)
    approval = TOOLS[tool]["approval"]
    if approval is None:
        log(run, "result", TOOLS[tool]["execute"](args))
        return
    log(run, "approval", f"{tool} needs a person's approval — waiting.")
    created = veriqa.create(approval["action_type"], approval["slots"](args))
    entry = created["channel_entry"]
    run["transaction_id"] = created["transaction_id"]
    run["held"] = {"tool": tool, "args": args}
    run["pending"] = {"tool": tool, "url": entry["url"], "qr": entry.get("qr")}


def resume(run):
    """Reads the approval that holds the run once; when a person has answered, finishes the ticket."""
    result = veriqa.result(run["transaction_id"])
    outcome = result["outcome"]
    if outcome == "pending":
        return
    held = run["held"]
    executed = False
    if outcome == "confirmed":
        # Who approved goes into the agent's own log - the record of the run.
        claims = veriqa.claims(run["transaction_id"])
        log(run, "approval", f"Approved by {claims.get('sub')}.")
        log(run, "result", TOOLS[held["tool"]]["execute"](held["args"]))
        executed = True
    else:
        log(run, "result", f"Not executed: the approval ended as {outcome}.")
    run["pending"] = None
    amount = held["args"]["amount"]
    text = (f"Your refund of {amount} is on its way." if executed
            else "We could not approve a refund right now; a colleague will contact you.")
    invoke(run, "send_customer_email", {"order": held["args"]["order"], "text": text})
    run["completion"] = "Done."


def stop(run, failure):
    run["pending"] = None
    run["completion"] = f"Stopped: {failure}"


def snapshot(run):
    return {"id": run["id"], "log": run["log"], "pending": run["pending"], "completion": run["completion"]}


app = Flask(__name__)


@app.get("/")
def page():
    return Response(PAGE.read_text(encoding="utf-8"), mimetype="text/html")


@app.post("/runs")
def start_run():
    ticket = request.get_json(force=True)
    run = {"id": secrets.token_hex(16), "log": [], "pending": None, "completion": None}
    with runs_lock:
        runs[run["id"]] = run
        try:
            log(run, "agent", f"Ticket: the customer asks to refund order {ticket['order']} — "
                              f"\"{ticket['reason']}\".")
            invoke(run, "lookup_order", {"order": ticket["order"]})
            invoke(run, "refund_payment", {
                "order": ticket["order"], "amount": ticket["amount"], "reason": ticket["reason"]})
        except (VeriqaError, requests.RequestException) as failure:
            stop(run, failure)
    return jsonify({"id": run["id"]})


@app.get("/runs/<run_id>")
def read_run(run_id):
    with runs_lock:
        run = runs.get(run_id)
        if run is None:
            return jsonify({"error": "unknown run"}), 404
        if run["pending"] is not None:
            try:
                resume(run)
            except (VeriqaError, requests.RequestException) as failure:
                stop(run, failure)
        return jsonify(snapshot(run))


if __name__ == "__main__":
    app.run(host="localhost", port=PORT)
