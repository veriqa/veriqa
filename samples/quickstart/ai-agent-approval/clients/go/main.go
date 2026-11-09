// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The backend of the AI agent approval sample on Go: net/http from the standard library.
// A support agent handles a refund ticket with three tools known in advance. Two of them run on their
// own; refund_payment moves money, so every call of it waits until a person approves it in Telegram.
// The approval is a Veriqa server-to-server confirmation over the public HTTP API: a client_credentials
// token, POST /api/transaction/confirmation, then GET /api/transaction/{id}/result until the person
// answers. Every step is driven by a request of the page (../page/index.html): reading a run moves it on.
//
// Run it from this directory. Configuration (environment variables, all optional on the local stand):
//
//	VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//	VERIQA_CLIENT_ID     - the client registered on the issuer, `support-agent`;
//	VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
//	VERIQA_CA_FILE       - the certificate to trust for the issuer, ../../built-in/certs/localhost.crt
//	                       by default; when the file does not exist, the system trust store applies;
//	PORT                 - the port to listen on, 7040.
package main

import (
	"bufio"
	"bytes"
	"crypto/rand"
	"crypto/tls"
	"crypto/x509"
	"encoding/base64"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"log"
	"net/http"
	"net/url"
	"os"
	"strings"
	"sync"
	"time"
)

const (
	envFile           = "../../built-in/.env"
	pagePath          = "../page/index.html"
	confirmationGrant = "urn:veriqa:params:oauth:grant-type:confirmation"
	exchangeScope     = "openid channel"
	confirmedOutcome  = "confirmed"
	pendingOutcome    = "pending"
	// A held access token is dropped this long before it expires.
	tokenExpiryMargin = 30 * time.Second
)

var (
	issuer   = strings.TrimRight(env("VERIQA_ISSUER", "https://localhost:8443"), "/")
	clientID = env("VERIQA_CLIENT_ID", "support-agent")
	caFile   = env("VERIQA_CA_FILE", "../../built-in/certs/localhost.crt")
	port     = env("PORT", "7040")
)

func env(name, fallback string) string {
	if value := os.Getenv(name); value != "" {
		return value
	}
	return fallback
}

// clientSecret takes VERIQA_CLIENT_SECRET, or CLIENT_SECRET from the .env of the stand, where
// docker-compose.yml reads the same value for Veriqa.
func clientSecret() (string, error) {
	if value := os.Getenv("VERIQA_CLIENT_SECRET"); value != "" {
		return value, nil
	}
	file, err := os.Open(envFile)
	if err == nil {
		defer file.Close()
		scanner := bufio.NewScanner(file)
		for scanner.Scan() {
			line := strings.TrimSpace(scanner.Text())
			if strings.HasPrefix(line, "#") {
				continue
			}
			if key, value, found := strings.Cut(line, "="); found && strings.TrimSpace(key) == "CLIENT_SECRET" && strings.TrimSpace(value) != "" {
				return strings.TrimSpace(value), nil
			}
		}
	}
	return "", errors.New("no client secret: set VERIQA_CLIENT_SECRET or CLIENT_SECRET in " + envFile)
}

func randomHex() string {
	buffer := make([]byte, 16)
	if _, err := rand.Read(buffer); err != nil {
		panic(err)
	}
	return hex.EncodeToString(buffer)
}

// --- Veriqa confirmation API ---------------------------------------------------------------------

// veriqaError is a refusal of Veriqa: Problem Details or an OAuth error body, kept whole so the
// sample shows what went wrong.
type veriqaError struct {
	status int
	body   string
}

func (e *veriqaError) Error() string { return fmt.Sprintf("Veriqa answered %d: %s", e.status, e.body) }

type channelEntry struct {
	Kind        string  `json:"kind"`
	ChannelType string  `json:"channel_type"`
	URL         string  `json:"url"`
	QR          *string `json:"qr"`
	ValidUntil  string  `json:"valid_until"`
}

type createdConfirmation struct {
	TransactionID string       `json:"transaction_id"`
	ExpiresAt     string       `json:"expires_at"`
	ChannelEntry  channelEntry `json:"channel_entry"`
}

type confirmationResult struct {
	Outcome     string  `json:"outcome"`
	MatchedType *string `json:"matched_type"`
}

type tokenResponse struct {
	AccessToken string `json:"access_token"`
	IDToken     string `json:"id_token"`
	ExpiresIn   int    `json:"expires_in"`
}

type veriqaClient struct {
	http         *http.Client
	clientSecret string

	lock       sync.Mutex
	token      string
	validUntil time.Time
}

func newVeriqaClient(secret string) (*veriqaClient, error) {
	transport := http.DefaultTransport.(*http.Transport).Clone()
	// The stand's certificate is self-signed; an issuer with a public certificate needs none of this.
	if pem, err := os.ReadFile(caFile); err == nil {
		pool := x509.NewCertPool()
		if !pool.AppendCertsFromPEM(pem) {
			return nil, fmt.Errorf("%s holds no PEM certificate", caFile)
		}
		transport.TLSClientConfig = &tls.Config{RootCAs: pool, MinVersion: tls.VersionTLS12}
	}
	return &veriqaClient{http: &http.Client{Transport: transport, Timeout: 30 * time.Second}, clientSecret: secret}, nil
}

// requestToken calls the token endpoint as this backend, with HTTP Basic client authentication.
func (v *veriqaClient) requestToken(form url.Values) (*tokenResponse, error) {
	request, err := http.NewRequest(http.MethodPost, issuer+"/connect/token", strings.NewReader(form.Encode()))
	if err != nil {
		return nil, err
	}
	request.Header.Set("Content-Type", "application/x-www-form-urlencoded")
	credentials := url.QueryEscape(clientID) + ":" + url.QueryEscape(v.clientSecret)
	request.Header.Set("Authorization", "Basic "+base64.StdEncoding.EncodeToString([]byte(credentials)))
	body, err := v.send(request)
	if err != nil {
		return nil, err
	}
	var token tokenResponse
	return &token, json.Unmarshal(body, &token)
}

// accessToken keeps the client_credentials token until shortly before expires_in: waiting for a person
// means polling every couple of seconds, and a new token per poll runs into the rate limit of
// /connect/token long before the person has answered.
func (v *veriqaClient) accessToken() (string, error) {
	v.lock.Lock()
	defer v.lock.Unlock()
	if v.token != "" && time.Now().Before(v.validUntil) {
		return v.token, nil
	}
	token, err := v.requestToken(url.Values{"grant_type": {"client_credentials"}})
	if err != nil {
		return "", err
	}
	validUntil := time.Now().Add(time.Duration(token.ExpiresIn)*time.Second - tokenExpiryMargin)
	// A token whose remaining life is shorter than the margin is used once and not held.
	v.token = ""
	if validUntil.After(time.Now()) {
		v.token, v.validUntil = token.AccessToken, validUntil
	}
	return token.AccessToken, nil
}

func (v *veriqaClient) dropToken(stale string) {
	v.lock.Lock()
	defer v.lock.Unlock()
	if v.token == stale {
		v.token = ""
	}
}

// send returns the body of a successful answer and a *veriqaError for any other status.
func (v *veriqaClient) send(request *http.Request) ([]byte, error) {
	response, err := v.http.Do(request)
	if err != nil {
		return nil, err
	}
	defer response.Body.Close()
	body, err := io.ReadAll(response.Body)
	if err != nil {
		return nil, err
	}
	if response.StatusCode < 200 || response.StatusCode > 299 {
		return nil, &veriqaError{status: response.StatusCode, body: string(body)}
	}
	return body, nil
}

// sendAuthorized calls the API with the held token. A 401 means Veriqa no longer knows that token (the
// stand keeps tokens in memory, so a restart forgets them): it is dropped and the request is sent once
// more with a fresh one.
func (v *veriqaClient) sendAuthorized(method, path string, payload any) ([]byte, error) {
	var content []byte
	if payload != nil {
		var err error
		if content, err = json.Marshal(payload); err != nil {
			return nil, err
		}
	}
	for attempt := 0; ; attempt++ {
		token, err := v.accessToken()
		if err != nil {
			return nil, err
		}
		request, err := http.NewRequest(method, issuer+path, bytes.NewReader(content))
		if err != nil {
			return nil, err
		}
		request.Header.Set("Authorization", "Bearer "+token)
		if payload != nil {
			request.Header.Set("Content-Type", "application/json")
		}
		body, err := v.send(request)
		var refused *veriqaError
		if attempt == 0 && errors.As(err, &refused) && refused.status == http.StatusUnauthorized {
			v.dropToken(token)
			continue
		}
		return body, err
	}
}

func (v *veriqaClient) create(actionType string, slots map[string]string) (*createdConfirmation, error) {
	body, err := v.sendAuthorized(http.MethodPost, "/api/transaction/confirmation", map[string]any{
		"action_type": actionType,
		"slot_values": slots,
		// A retry with the same key answers with the same transaction instead of a second one.
		"idempotency_key": randomHex(),
	})
	if err != nil {
		return nil, err
	}
	var created createdConfirmation
	return &created, json.Unmarshal(body, &created)
}

func (v *veriqaClient) result(transactionID string) (*confirmationResult, error) {
	body, err := v.sendAuthorized(http.MethodGet, "/api/transaction/"+url.PathEscape(transactionID)+"/result", nil)
	if err != nil {
		return nil, err
	}
	var result confirmationResult
	return &result, json.Unmarshal(body, &result)
}

// exchange redeems a confirmed transaction for the id_token of the person who confirmed and returns its
// claims.
func (v *veriqaClient) exchange(transactionID string) (map[string]any, error) {
	token, err := v.requestToken(url.Values{
		"grant_type":     {confirmationGrant},
		"transaction_id": {transactionID},
		"scope":          {exchangeScope},
	})
	if err != nil {
		return nil, err
	}
	// The id_token came straight from the issuer's token endpoint over TLS, so the payload is read
	// without validating the signature (OpenID Connect Core 3.1.3.7, item 6). A token received any
	// other way must be validated against the issuer's JWKS.
	parts := strings.Split(token.IDToken, ".")
	if len(parts) != 3 {
		return nil, errors.New("the token endpoint answered without an id_token")
	}
	payload, err := base64.RawURLEncoding.DecodeString(strings.TrimRight(parts[1], "="))
	if err != nil {
		return nil, err
	}
	var claims map[string]any
	return claims, json.Unmarshal(payload, &claims)
}

// --- the agent -----------------------------------------------------------------------------------

type order struct{ id, item, amount, customer string }

// approvalPolicy says which declared action type asks the person and how the arguments of a call
// become its slot values. The person reads the slots, never text the agent wrote.
type approvalPolicy struct {
	actionType string
	slots      func(args map[string]string) map[string]string
}

// tool is a plain function the agent may call; one with consequences carries an approval policy, so
// the gate sits in the tool-invocation path and no output of the model can skip it.
type tool struct {
	params   []string
	approval *approvalPolicy
	execute  func(args map[string]string) string
}

var (
	orders = map[string]order{
		"A-1042": {"A-1042", "Wireless headphones", "89.00 EUR", "anna@example.com"},
		"A-1043": {"A-1043", "Standing desk", "420.00 EUR", "ben@example.com"},
	}
	refunded = map[string]bool{}
)

var tools = map[string]tool{
	// Reading is harmless: no approval.
	"lookup_order": {
		params: []string{"order"},
		execute: func(args map[string]string) string {
			found, ok := orders[args["order"]]
			if !ok {
				return fmt.Sprintf("Order %s does not exist.", args["order"])
			}
			state := "not refunded."
			if refunded[found.id] {
				state = "already refunded."
			}
			return fmt.Sprintf("Order %s: %s, paid %s, customer %s, %s", found.id, found.item, found.amount, found.customer, state)
		},
	},
	// Moving money is not: a person approves every call, with the amount and the order in front of them.
	"refund_payment": {
		params: []string{"order", "amount", "reason"},
		approval: &approvalPolicy{
			actionType: "approve-refund",
			slots: func(args map[string]string) map[string]string {
				return map[string]string{"amount": args["amount"], "order": args["order"], "reason": args["reason"]}
			},
		},
		execute: func(args map[string]string) string {
			refunded[args["order"]] = true
			return fmt.Sprintf("Refunded %s for order %s.", args["amount"], args["order"])
		},
	},
	// Writing to the customer about a decision already taken: no approval in this sample.
	"send_customer_email": {
		params: []string{"order", "text"},
		execute: func(args map[string]string) string {
			return fmt.Sprintf("E-mail sent to the customer of order %s: \"%s\"", args["order"], args["text"])
		},
	},
}

type logEntry struct {
	At   string `json:"at"`
	Kind string `json:"kind"`
	Text string `json:"text"`
}

type pendingApproval struct {
	Tool string  `json:"tool"`
	URL  string  `json:"url"`
	QR   *string `json:"qr"`
}

// run is one ticket handed to the agent. The page reads it every two seconds; while an approval holds
// it, each read checks the result once.
type run struct {
	ID         string           `json:"id"`
	Log        []logEntry       `json:"log"`
	Pending    *pendingApproval `json:"pending"`
	Completion *string          `json:"completion"`

	request       map[string]string
	pendingArgs   map[string]string
	transactionID string
}

func (r *run) log(kind, text string) {
	r.Log = append(r.Log, logEntry{At: time.Now().Format("15:04:05"), Kind: kind, Text: text})
}

func (r *run) complete(text string) {
	r.Pending = nil
	r.Completion = &text
}

// stop ends the run on a failed call to Veriqa.
func (r *run) stop(err error) {
	r.complete("Stopped: " + err.Error())
}

// agent runs the tools of the ticket in order and holds the one that needs an approval.
type agent struct {
	veriqa *veriqaClient
	lock   sync.Mutex
	runs   map[string]*run
}

// invoke calls a tool. A tool without an approval runs at once; one with an approval creates the
// confirmation and holds the run until a later read of it sees the person's answer.
func (a *agent) invoke(r *run, name string, args map[string]string) error {
	t := tools[name]
	described := make([]string, 0, len(t.params))
	for _, param := range t.params {
		described = append(described, param+": "+args[param])
	}
	r.log("call", fmt.Sprintf("%s(%s)", name, strings.Join(described, ", ")))
	if t.approval == nil {
		r.log("result", t.execute(args))
		return nil
	}
	r.log("approval", name+" needs a person's approval — waiting.")
	created, err := a.veriqa.create(t.approval.actionType, t.approval.slots(args))
	if err != nil {
		return err
	}
	r.Pending = &pendingApproval{Tool: name, URL: created.ChannelEntry.URL, QR: created.ChannelEntry.QR}
	r.pendingArgs, r.transactionID = args, created.TransactionID
	return nil
}

func (a *agent) start(request map[string]string) *run {
	r := &run{ID: randomHex(), Log: []logEntry{}, request: request}
	r.log("agent", fmt.Sprintf("Ticket: the customer asks to refund order %s — \"%s\".", request["order"], request["reason"]))
	err := a.invoke(r, "lookup_order", map[string]string{"order": request["order"]})
	if err == nil {
		err = a.invoke(r, "refund_payment", map[string]string{
			"order": request["order"], "amount": request["amount"], "reason": request["reason"],
		})
	}
	if err != nil {
		r.stop(err)
	}
	return r
}

// advance reads the result of the approval that holds the run and, once the person has answered,
// finishes the ticket.
func (a *agent) advance(r *run) {
	if r.Pending == nil || r.Completion != nil {
		return
	}
	result, err := a.veriqa.result(r.transactionID)
	if err != nil {
		r.stop(err)
		return
	}
	if result.Outcome == pendingOutcome {
		return
	}
	executed := false
	if result.Outcome == confirmedOutcome {
		// Who approved goes into the agent's own log: the audit of the run.
		claims, err := a.veriqa.exchange(r.transactionID)
		if err != nil {
			r.stop(err)
			return
		}
		r.log("approval", fmt.Sprintf("Approved by %v.", claims["sub"]))
		r.log("result", tools[r.Pending.Tool].execute(r.pendingArgs))
		executed = true
	} else {
		r.log("result", "Not executed: the approval ended as "+result.Outcome+".")
	}
	r.Pending = nil

	text := "We could not approve a refund right now; a colleague will contact you."
	if executed {
		text = "Your refund of " + r.request["amount"] + " is on its way."
	}
	if err := a.invoke(r, "send_customer_email", map[string]string{"order": r.request["order"], "text": text}); err != nil {
		r.stop(err)
		return
	}
	r.complete("Done.")
}

// --- HTTP ----------------------------------------------------------------------------------------

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

func main() {
	secret, err := clientSecret()
	if err != nil {
		log.Fatal(err)
	}
	veriqa, err := newVeriqaClient(secret)
	if err != nil {
		log.Fatal(err)
	}
	a := &agent{veriqa: veriqa, runs: map[string]*run{}}

	http.HandleFunc("GET /{$}", func(w http.ResponseWriter, _ *http.Request) {
		page, err := os.ReadFile(pagePath)
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write(page)
	})

	http.HandleFunc("POST /runs", func(w http.ResponseWriter, r *http.Request) {
		var ticket struct{ Order, Amount, Reason string }
		if err := json.NewDecoder(r.Body).Decode(&ticket); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		// One lock for the whole state: the tools and the runs are a few maps in memory.
		a.lock.Lock()
		defer a.lock.Unlock()
		started := a.start(map[string]string{"order": ticket.Order, "amount": ticket.Amount, "reason": ticket.Reason})
		a.runs[started.ID] = started
		writeJSON(w, http.StatusOK, map[string]string{"id": started.ID})
	})

	http.HandleFunc("GET /runs/{id}", func(w http.ResponseWriter, r *http.Request) {
		a.lock.Lock()
		defer a.lock.Unlock()
		found, ok := a.runs[r.PathValue("id")]
		if !ok {
			http.NotFound(w, r)
			return
		}
		a.advance(found)
		writeJSON(w, http.StatusOK, found)
	})

	address := "localhost:" + port
	log.Printf("Support agent on http://%s", address)
	log.Fatal(http.ListenAndServe(address, nil))
}
