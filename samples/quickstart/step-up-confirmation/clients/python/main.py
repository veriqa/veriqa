# Copyright (c) Dmitrii Erusov
# SPDX-License-Identifier: MIT
"""The backend of the confirmation and step-up sample on Python: Flask and requests.

Two Veriqa server-to-server confirmations, with no sign-in and no redirect:
  1. approve a payment - the backend asks a person to approve it in Telegram and, once confirmed,
     learns who confirmed from the id_token of the confirmation grant;
  2. step-up before deleting a project - the confirmation names the owner of the account in
     expected_identities, and the project is deleted only when Veriqa says the person who confirmed
     is that owner (matched_type). The first person to confirm a deletion becomes the owner.

Every step is driven by the page: each read of a transaction asks Veriqa for its result once.

Configuration (environment variables, all optional on the local stand):
  VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
  VERIQA_CLIENT_ID     - the client registered on Veriqa, `sample-app`;
  VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
  VERIQA_CA_FILE       - the certificate to trust for the issuer, ../../built-in/certs/localhost.crt
                         by default (used when the file exists);
  PORT                 - the port of this client, 7030.
"""

import base64
import json
import os
import secrets
import sys
import threading
import time
from pathlib import Path
from urllib.parse import quote

import requests
from flask import Flask, Response, jsonify, request

HERE = Path(__file__).resolve().parent
STAND = HERE / "../../built-in"
PAGE = HERE / "../page/index.html"

PAYMENT_ACTION = "approve-payment"
DELETION_ACTION = "delete-project"
# The identity type declared in IdentityMatchComparableTypes of the client entry (veriqa.json).
OWNER_IDENTITY = "telegram_user_id"
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
CLIENT_ID = os.environ.get("VERIQA_CLIENT_ID", "sample-app")
CLIENT_SECRET = os.environ.get("VERIQA_CLIENT_SECRET") or stand_env("CLIENT_SECRET")
CA_FILE = Path(os.environ.get("VERIQA_CA_FILE", STAND / "certs/localhost.crt"))
PORT = int(os.environ.get("PORT", "7030"))

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

    def create(self, action_type, slot_values, expected_identities=None):
        # A retry with the same key answers with the same transaction instead of a second one.
        body = {
            "action_type": action_type,
            "slot_values": slot_values,
            "idempotency_key": secrets.token_hex(16),
        }
        if expected_identities:
            body["expected_identities"] = expected_identities
        return self._call("POST", "/api/transaction/confirmation", body)

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

PROJECTS = [
    {"id": "q3-report", "name": "Q3 report", "documents": 12},
    {"id": "customer-onboarding", "name": "Customer onboarding", "documents": 48},
    {"id": "archive-2024", "name": "Archive 2024", "documents": 305},
]
state = {"owner": None}
# A transaction is remembered against its project on the server: the page never tells the backend
# which project a confirmed transaction deletes.
deletions = {}
# A transaction is redeemed for an id_token once, so its claims are kept for the page to read again.
payment_claims = {}
lock = threading.Lock()

app = Flask(__name__)


def refused(failure):
    """Passes a refusal of Veriqa to the page as it is."""
    if isinstance(failure, VeriqaError):
        return Response(failure.body, status=failure.status, mimetype="application/json")
    return jsonify({"error": str(failure)}), 502


def workspace():
    return {"owner": state["owner"], "projects": PROJECTS}


@app.get("/")
def page():
    return Response(PAGE.read_text(encoding="utf-8"), mimetype="text/html")


# --- 1. payment --------------------------------------------------------------------------------------

@app.post("/payments")
def ask_payment():
    form = request.get_json(force=True)
    try:
        created = veriqa.create(PAYMENT_ACTION, {"amount": form["amount"], "payee": form["payee"]})
    except (VeriqaError, requests.RequestException) as failure:
        return refused(failure)
    entry = created["channel_entry"]
    return jsonify({"transaction_id": created["transaction_id"], "url": entry["url"],
                    "qr": entry.get("qr"), "valid_until": entry.get("valid_until")})


@app.get("/payments/<transaction_id>")
def read_payment(transaction_id):
    with lock:
        try:
            outcome = veriqa.result(transaction_id)["outcome"]
            if outcome != "confirmed":
                return jsonify({"outcome": outcome})
            if transaction_id not in payment_claims:
                payment_claims[transaction_id] = veriqa.claims(transaction_id)
        except (VeriqaError, requests.RequestException) as failure:
            return refused(failure)
        return jsonify({"outcome": outcome, "claims": payment_claims[transaction_id]})


# --- 2. step-up ----------------------------------------------------------------------------------------

@app.get("/workspace")
def read_workspace():
    with lock:
        return jsonify(workspace())


@app.post("/workspace/owner/reset")
def forget_owner():
    with lock:
        state["owner"] = None
        return jsonify(workspace())


@app.post("/projects/<project_id>/deletion")
def ask_deletion(project_id):
    with lock:
        project = next((p for p in PROJECTS if p["id"] == project_id), None)
        if project is None:
            return jsonify({"error": "unknown project"}), 404
        owner = state["owner"]
        # The step-up itself: once an owner is known, the confirmation is addressed to that person.
        expected = {OWNER_IDENTITY: owner} if owner else None
        try:
            created = veriqa.create(
                DELETION_ACTION,
                {"project": project["name"], "documents": str(project["documents"])},
                expected)
        except (VeriqaError, requests.RequestException) as failure:
            return refused(failure)
        deletions[created["transaction_id"]] = {
            "transaction_id": created["transaction_id"], "project_id": project_id,
            "outcome": "pending", "verdict": None}
        entry = created["channel_entry"]
        return jsonify({"transaction_id": created["transaction_id"], "url": entry["url"],
                        "qr": entry.get("qr"), "expects_owner": owner is not None})


def delete_project(project_id):
    PROJECTS[:] = [p for p in PROJECTS if p["id"] != project_id]


@app.get("/deletions/<transaction_id>")
def read_deletion(transaction_id):
    with lock:
        deletion = deletions.get(transaction_id)
        if deletion is None:
            return jsonify({"error": "unknown deletion"}), 404
        if deletion["verdict"] is not None:
            return jsonify(deletion)
        try:
            result = veriqa.result(transaction_id)
            outcome = result["outcome"]
            if outcome == "pending":
                return jsonify(deletion)
            if outcome != "confirmed":
                verdict = "not_deleted"
            elif state["owner"] is None:
                # No owner yet: the first person to confirm becomes the owner of the account.
                state["owner"] = veriqa.claims(transaction_id)["channel_user_id"]
                verdict = "deleted_owner_bound"
            elif result.get("matched_type") == OWNER_IDENTITY:
                verdict = "deleted"
            else:
                # Veriqa does not refuse a mismatch itself: the result still says confirmed. Whether
                # it was the expected person is the decision a step-up exists for.
                verdict = "confirmed_by_another_person"
        except (VeriqaError, requests.RequestException) as failure:
            return refused(failure)
        if verdict in ("deleted", "deleted_owner_bound"):
            delete_project(deletion["project_id"])
        deletion["outcome"] = outcome
        deletion["verdict"] = verdict
        return jsonify(deletion)


if __name__ == "__main__":
    app.run(host="localhost", port=PORT)
