// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

// The backend of the confirmation and step-up sample on Go: net/http from the standard library.
// Two Veriqa server-to-server confirmations over the public HTTP API, both a client_credentials token,
// POST /api/transaction/confirmation and GET /api/transaction/{id}/result until the person answers:
//
//  1. approve a payment - once confirmed, the confirmation is exchanged for the id_token of the person
//     who confirmed, and the page lists its claims;
//  2. a step-up before deleting a project - the confirmation names the owner of the account in
//     expected_identities, and the project is deleted only when matched_type says the owner confirmed.
//
// Every step is driven by a request of the page (../page/index.html). Run it from this directory.
// Configuration (environment variables, all optional on the local stand):
//
//	VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
//	VERIQA_CLIENT_ID     - the client registered on the issuer, `sample-app`;
//	VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET from ../../built-in/.env;
//	VERIQA_CA_FILE       - the certificate to trust for the issuer, ../../built-in/certs/localhost.crt
//	                       by default; when the file does not exist, the system trust store applies;
//	PORT                 - the port to listen on, 7030.
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
	"strconv"
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
	clientID = env("VERIQA_CLIENT_ID", "sample-app")
	caFile   = env("VERIQA_CA_FILE", "../../built-in/certs/localhost.crt")
	port     = env("PORT", "7030")
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

// create asks for a confirmation. expected, when not nil, names the person who must confirm: Veriqa
// compares it with the identity of whoever does and answers with matched_type.
func (v *veriqaClient) create(actionType string, slots, expected map[string]string) (*createdConfirmation, error) {
	request := map[string]any{
		"action_type": actionType,
		"slot_values": slots,
		// A retry with the same key answers with the same transaction instead of a second one.
		"idempotency_key": randomHex(),
	}
	if expected != nil {
		request["expected_identities"] = expected
	}
	body, err := v.sendAuthorized(http.MethodPost, "/api/transaction/confirmation", request)
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

// --- the application -----------------------------------------------------------------------------

// The identity a step-up names in expected_identities; veriqa.json declares it for this client.
const ownerIdentityType = "telegram_user_id"

// The verdicts of a deletion, as the page names them.
const (
	verdictDeleted                  = "deleted"
	verdictDeletedOwnerBound        = "deleted_owner_bound"
	verdictConfirmedByAnotherPerson = "confirmed_by_another_person"
	verdictNotDeleted               = "not_deleted"
)

type project struct {
	ID        string `json:"id"`
	Name      string `json:"name"`
	Documents int    `json:"documents"`
}

type deletion struct {
	TransactionID string  `json:"transaction_id"`
	ProjectID     string  `json:"project_id"`
	Outcome       string  `json:"outcome"`
	Verdict       *string `json:"verdict"`
}

// workspace is the state of the sample application, in memory. The owner of the account is the first
// person who confirms a deletion: a real application knows it from account linking.
type workspace struct {
	veriqa *veriqaClient
	lock   sync.Mutex

	projects []project
	owner    *string
	// The transaction is remembered against the project on the server: the page never tells the
	// backend which project a confirmed transaction deletes.
	deletions map[string]*deletion
	// A transaction is redeemed for an id_token once, so the claims are kept for the page to read again.
	paymentClaims map[string]map[string]any
}

func (ws *workspace) snapshot() map[string]any {
	return map[string]any{"owner": ws.owner, "projects": ws.projects}
}

func (ws *workspace) find(projectID string) (int, bool) {
	for index, candidate := range ws.projects {
		if candidate.ID == projectID {
			return index, true
		}
	}
	return -1, false
}

func (ws *workspace) remove(projectID string) {
	if index, ok := ws.find(projectID); ok {
		ws.projects = append(ws.projects[:index], ws.projects[index+1:]...)
	}
}

// conclude settles a deletion: the project goes only on a verdict that deletes it.
func (ws *workspace) conclude(d *deletion, outcome, verdict string) {
	d.Outcome, d.Verdict = outcome, &verdict
	if verdict == verdictDeleted || verdict == verdictDeletedOwnerBound {
		ws.remove(d.ProjectID)
	}
}

// --- HTTP ----------------------------------------------------------------------------------------

func writeJSON(w http.ResponseWriter, status int, value any) {
	w.Header().Set("Content-Type", "application/json")
	w.WriteHeader(status)
	_ = json.NewEncoder(w).Encode(value)
}

// writeFailure passes a refusal of Veriqa through as it came; a failure to reach Veriqa is a 502.
func writeFailure(w http.ResponseWriter, err error) {
	var refused *veriqaError
	if errors.As(err, &refused) {
		w.Header().Set("Content-Type", "application/json")
		w.WriteHeader(refused.status)
		_, _ = io.WriteString(w, refused.body)
		return
	}
	writeJSON(w, http.StatusBadGateway, map[string]string{"error": err.Error()})
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
	ws := &workspace{
		veriqa: veriqa,
		projects: []project{
			{"q3-report", "Q3 report", 12},
			{"customer-onboarding", "Customer onboarding", 48},
			{"archive-2024", "Archive 2024", 305},
		},
		deletions:     map[string]*deletion{},
		paymentClaims: map[string]map[string]any{},
	}

	http.HandleFunc("GET /{$}", func(w http.ResponseWriter, _ *http.Request) {
		page, err := os.ReadFile(pagePath)
		if err != nil {
			http.Error(w, err.Error(), http.StatusInternalServerError)
			return
		}
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write(page)
	})

	// 1. A server-to-server confirmation: approve a payment.
	http.HandleFunc("POST /payments", func(w http.ResponseWriter, r *http.Request) {
		var form struct{ Amount, Payee string }
		if err := json.NewDecoder(r.Body).Decode(&form); err != nil {
			http.Error(w, err.Error(), http.StatusBadRequest)
			return
		}
		created, err := veriqa.create("approve-payment", map[string]string{"amount": form.Amount, "payee": form.Payee}, nil)
		if err != nil {
			writeFailure(w, err)
			return
		}
		// channel_entry.qr is a PNG data URI: the page shows it as an image for the phone to scan.
		writeJSON(w, http.StatusOK, map[string]any{
			"transaction_id": created.TransactionID,
			"url":            created.ChannelEntry.URL,
			"qr":             created.ChannelEntry.QR,
			"valid_until":    created.ChannelEntry.ValidUntil,
		})
	})

	http.HandleFunc("GET /payments/{tid}", func(w http.ResponseWriter, r *http.Request) {
		transactionID := r.PathValue("tid")
		result, err := veriqa.result(transactionID)
		if err != nil {
			writeFailure(w, err)
			return
		}
		if result.Outcome != confirmedOutcome {
			writeJSON(w, http.StatusOK, map[string]string{"outcome": result.Outcome})
			return
		}
		ws.lock.Lock()
		defer ws.lock.Unlock()
		claims, ok := ws.paymentClaims[transactionID]
		if !ok {
			if claims, err = veriqa.exchange(transactionID); err != nil {
				writeFailure(w, err)
				return
			}
			ws.paymentClaims[transactionID] = claims
		}
		writeJSON(w, http.StatusOK, map[string]any{"outcome": result.Outcome, "claims": claims})
	})

	// 2. A step-up: delete a project only when the owner of the account confirms.
	http.HandleFunc("GET /workspace", func(w http.ResponseWriter, _ *http.Request) {
		ws.lock.Lock()
		defer ws.lock.Unlock()
		writeJSON(w, http.StatusOK, ws.snapshot())
	})

	http.HandleFunc("POST /workspace/owner/reset", func(w http.ResponseWriter, _ *http.Request) {
		ws.lock.Lock()
		defer ws.lock.Unlock()
		ws.owner = nil
		writeJSON(w, http.StatusOK, ws.snapshot())
	})

	http.HandleFunc("POST /projects/{pid}/deletion", func(w http.ResponseWriter, r *http.Request) {
		ws.lock.Lock()
		defer ws.lock.Unlock()
		index, ok := ws.find(r.PathValue("pid"))
		if !ok {
			http.NotFound(w, r)
			return
		}
		target := ws.projects[index]
		// The step-up itself: once an owner is known, the confirmation is addressed to that person.
		var expected map[string]string
		if ws.owner != nil {
			expected = map[string]string{ownerIdentityType: *ws.owner}
		}
		created, err := veriqa.create("delete-project", map[string]string{
			"project":   target.Name,
			"documents": strconv.Itoa(target.Documents),
		}, expected)
		if err != nil {
			writeFailure(w, err)
			return
		}
		ws.deletions[created.TransactionID] = &deletion{TransactionID: created.TransactionID, ProjectID: target.ID, Outcome: pendingOutcome}
		writeJSON(w, http.StatusOK, map[string]any{
			"transaction_id": created.TransactionID,
			"url":            created.ChannelEntry.URL,
			"qr":             created.ChannelEntry.QR,
			"expects_owner":  expected != nil,
		})
	})

	http.HandleFunc("GET /deletions/{tid}", func(w http.ResponseWriter, r *http.Request) {
		ws.lock.Lock()
		defer ws.lock.Unlock()
		d, ok := ws.deletions[r.PathValue("tid")]
		if !ok {
			http.NotFound(w, r)
			return
		}
		if d.Verdict != nil {
			writeJSON(w, http.StatusOK, d)
			return
		}
		result, err := veriqa.result(d.TransactionID)
		if err != nil {
			writeFailure(w, err)
			return
		}
		switch {
		case result.Outcome == pendingOutcome:
		case result.Outcome != confirmedOutcome:
			ws.conclude(d, result.Outcome, verdictNotDeleted)
		case ws.owner == nil:
			// No owner yet: the first person to confirm becomes the owner of the account.
			claims, err := veriqa.exchange(d.TransactionID)
			if err != nil {
				writeFailure(w, err)
				return
			}
			owner, _ := claims["channel_user_id"].(string)
			ws.owner = &owner
			ws.conclude(d, result.Outcome, verdictDeletedOwnerBound)
		case result.MatchedType != nil && *result.MatchedType == ownerIdentityType:
			ws.conclude(d, result.Outcome, verdictDeleted)
		default:
			// Veriqa does not refuse a mismatch itself: the result still says confirmed, and this check
			// is the decision a step-up exists for.
			ws.conclude(d, result.Outcome, verdictConfirmedByAnotherPerson)
		}
		writeJSON(w, http.StatusOK, d)
	})

	address := "localhost:" + port
	log.Printf("Sample application on http://%s", address)
	log.Fatal(http.ListenAndServe(address, nil))
}
