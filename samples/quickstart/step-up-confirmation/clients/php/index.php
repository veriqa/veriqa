<?php
// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

/**
 * The backend of the sample application on PHP, the router script of the built-in server:
 *
 *   php -S localhost:7030 index.php
 *
 * Two Veriqa server-to-server confirmations, both asked of a person in Telegram:
 *   - a payment approval: the backend asks, and once confirmed learns from an id_token who confirmed;
 *   - a step-up before deleting a project: the confirmation is addressed to the owner of the account
 *     (expected_identities), and the project is deleted only when Veriqa answers that the person who
 *     confirmed is that owner (matched_type). The first person to confirm a deletion becomes the owner.
 * The backend gets its token with client_credentials. No PHP package is needed beyond the curl
 * extension.
 *
 * The built-in server keeps nothing between requests, so the state - the projects, the owner, the
 * deletions in progress, the held access token - lives in $_SESSION: per browser, gone with the
 * session. Every step is driven by a request from the page.
 *
 * Configuration (environment variables, all optional on the local stand):
 *   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
 *   VERIQA_CLIENT_ID     - the client registered on the issuer, `sample-app`;
 *   VERIQA_CLIENT_SECRET - its secret; by default CLIENT_SECRET of ../../built-in/.env;
 *   VERIQA_CA_FILE       - the certificate to trust for the issuer, ../../built-in/certs/localhost.crt
 *                          by default; when the file is missing, the system trust store applies.
 */

declare(strict_types=1);

const TOKEN_EXPIRY_MARGIN_SECONDS = 30;
const CONFIRMATION_GRANT_TYPE = 'urn:veriqa:params:oauth:grant-type:confirmation';
const EXCHANGE_SCOPE = 'openid channel';
const CONFIRMED = 'confirmed';
const PENDING = 'pending';
// The identity type declared in IdentityMatchComparableTypes of the client entry (veriqa.json).
const OWNER_IDENTITY_TYPE = 'telegram_user_id';

/** A refusal from Veriqa: its status and its body, surfaced whole so the sample shows what went wrong. */
final class VeriqaRefused extends RuntimeException
{
    public function __construct(public readonly int $status, public readonly string $body)
    {
        parent::__construct("Veriqa answered {$status}: {$body}");
    }
}

/** Reads one KEY=VALUE line of a .env file; null when the file or the key is missing. */
function dotenv_value(string $file, string $key): ?string
{
    if (!is_file($file)) {
        return null;
    }
    foreach (file($file, FILE_IGNORE_NEW_LINES) as $line) {
        $line = trim($line);
        if ($line === '' || $line[0] === '#' || !str_contains($line, '=')) {
            continue;
        }
        [$name, $value] = explode('=', $line, 2);
        if (trim($name) === $key) {
            $value = trim($value);
            return $value === '' ? null : $value;
        }
    }
    return null;
}

function issuer(): string
{
    return rtrim(getenv('VERIQA_ISSUER') ?: 'https://localhost:8443', '/');
}

function client_id(): string
{
    return getenv('VERIQA_CLIENT_ID') ?: 'sample-app';
}

function client_secret(): ?string
{
    return getenv('VERIQA_CLIENT_SECRET') ?: dotenv_value(__DIR__ . '/../../built-in/.env', 'CLIENT_SECRET');
}

/** The stand's certificate is self-signed; an issuer with a public certificate needs none of this. */
function ca_file(): ?string
{
    $file = getenv('VERIQA_CA_FILE') ?: __DIR__ . '/../../built-in/certs/localhost.crt';
    return is_file($file) ? $file : null;
}

/** One HTTPS call: [status, body]. A connection failure throws a RuntimeException with curl's error. */
function send(string $method, string $url, array $headers, ?string $body): array
{
    $curl = curl_init($url);
    curl_setopt_array($curl, [
        CURLOPT_CUSTOMREQUEST => $method,
        CURLOPT_RETURNTRANSFER => true,
        CURLOPT_HTTPHEADER => $headers,
    ]);
    if ($body !== null) {
        curl_setopt($curl, CURLOPT_POSTFIELDS, $body);
    }
    $ca = ca_file();
    if ($ca !== null) {
        curl_setopt($curl, CURLOPT_CAINFO, $ca);
    }
    $response = curl_exec($curl);
    if ($response === false) {
        $error = curl_error($curl);
        curl_close($curl);
        throw new RuntimeException($error);
    }
    $status = (int) curl_getinfo($curl, CURLINFO_RESPONSE_CODE);
    curl_close($curl);
    return [$status, $response];
}

/** POST /connect/token, authenticated as this backend with HTTP Basic. */
function request_token(array $form): array
{
    $credentials = base64_encode(rawurlencode(client_id()) . ':' . rawurlencode((string) client_secret()));
    [$status, $body] = send('POST', issuer() . '/connect/token', [
        'Authorization: Basic ' . $credentials,
        'Content-Type: application/x-www-form-urlencoded',
    ], http_build_query($form));
    if ($status >= 400) {
        throw new VeriqaRefused($status, $body);
    }
    return json_decode($body, true);
}

// The access token is held until shortly before expires_in: waiting for a person means reading the
// result every couple of seconds, and a fresh token per read runs into the rate limit of
// /connect/token long before the person has answered.
function access_token(): string
{
    $held = $_SESSION['token'] ?? null;
    if ($held !== null && time() < $held['valid_until']) {
        return $held['value'];
    }
    $token = request_token(['grant_type' => 'client_credentials']);
    $validUntil = time() + (int) $token['expires_in'] - TOKEN_EXPIRY_MARGIN_SECONDS;
    // A token whose remaining life is shorter than the margin is used once and not held.
    $_SESSION['token'] = $validUntil > time() ? ['value' => $token['access_token'], 'valid_until' => $validUntil] : null;
    return $token['access_token'];
}

// Sends a request with the held token. Veriqa keeps its tokens in memory on the stand, so a restart
// makes a held token unknown before it expires: a 401 drops it, and the request goes once more with
// a fresh one.
function veriqa(string $method, string $path, ?array $json = null): array
{
    $body = $json === null ? null : json_encode($json, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
    $headers = $json === null ? [] : ['Content-Type: application/json'];
    $token = access_token();
    [$status, $response] = send($method, issuer() . $path, [...$headers, 'Authorization: Bearer ' . $token], $body);
    if ($status === 401) {
        if (($_SESSION['token']['value'] ?? null) === $token) {
            $_SESSION['token'] = null;
        }
        [$status, $response] = send($method, issuer() . $path, [...$headers, 'Authorization: Bearer ' . access_token()], $body);
    }
    if ($status >= 400) {
        throw new VeriqaRefused($status, $response);
    }
    return json_decode($response, true);
}

/**
 * Creates a confirmation of a declared action type; a retry with the same key gets the same
 * transaction. $expectedIdentities addresses it to one person: Veriqa then answers in matched_type
 * whether the person who confirmed is that one.
 */
function create_confirmation(string $actionType, array $slotValues, ?array $expectedIdentities = null): array
{
    $request = [
        'action_type' => $actionType,
        'slot_values' => $slotValues,
        'idempotency_key' => bin2hex(random_bytes(16)),
    ];
    if ($expectedIdentities !== null) {
        $request['expected_identities'] = $expectedIdentities;
    }
    return veriqa('POST', '/api/transaction/confirmation', $request);
}

function confirmation_result(string $transactionId): array
{
    return veriqa('GET', '/api/transaction/' . rawurlencode($transactionId) . '/result');
}

/** Exchanges a confirmed transaction, once, for the id_token of the person who confirmed: its claims. */
function exchange_for_claims(string $transactionId): array
{
    $token = request_token([
        'grant_type' => CONFIRMATION_GRANT_TYPE,
        'transaction_id' => $transactionId,
        'scope' => EXCHANGE_SCOPE,
    ]);
    // The id_token came straight from the issuer's token endpoint over TLS, so the payload is read
    // without validating the signature (OpenID Connect Core 3.1.3.7, item 6). A token received any
    // other way must be validated against the issuer's JWKS.
    $payload = explode('.', $token['id_token'])[1];
    return json_decode(base64_decode(strtr($payload, '-_', '+/')), true);
}

// --- the workspace ---------------------------------------------------------------------------------

const PROJECTS = [
    ['id' => 'q3-report', 'name' => 'Q3 report', 'documents' => 12],
    ['id' => 'customer-onboarding', 'name' => 'Customer onboarding', 'documents' => 48],
    ['id' => 'archive-2024', 'name' => 'Archive 2024', 'documents' => 305],
];

function workspace(): array
{
    return ['owner' => $_SESSION['owner'], 'projects' => array_values($_SESSION['projects'])];
}

/** Ends a deletion with its verdict; the deleting verdicts remove the project. */
function conclude(array &$deletion, string $outcome, string $verdict): void
{
    $deletion['outcome'] = $outcome;
    $deletion['verdict'] = $verdict;
    if ($verdict === 'deleted' || $verdict === 'deleted_owner_bound') {
        unset($_SESSION['projects'][$deletion['project_id']]);
    }
}

// --- routes ---------------------------------------------------------------------------------------

function respond_json(mixed $value, int $status = 200): void
{
    http_response_code($status);
    header('Content-Type: application/json');
    echo json_encode($value, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
}

/** A refusal from Veriqa goes to the page unchanged: its status and its body. */
function respond_refusal(RuntimeException $failure): void
{
    if ($failure instanceof VeriqaRefused) {
        http_response_code($failure->status);
        header('Content-Type: application/json');
        echo $failure->body;
        return;
    }
    respond_json(['error' => 'veriqa_unreachable', 'detail' => $failure->getMessage()], 502);
}

if (client_secret() === null) {
    $message = 'No client secret: set VERIQA_CLIENT_SECRET, or CLIENT_SECRET in ../../built-in/.env.';
    error_log($message);
    http_response_code(500);
    header('Content-Type: text/plain; charset=utf-8');
    echo $message;
    return;
}

session_start();
if (!isset($_SESSION['projects'])) {
    $_SESSION['projects'] = array_column(PROJECTS, null, 'id');
    $_SESSION['owner'] = null;
    $_SESSION['deletions'] = [];
    $_SESSION['claims'] = [];
}
$method = $_SERVER['REQUEST_METHOD'];
$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);

try {
    if ($method === 'GET' && $path === '/') {
        header('Content-Type: text/html; charset=utf-8');
        readfile(__DIR__ . '/../page/index.html');
    } elseif ($method === 'POST' && $path === '/payments') {
        $form = json_decode(file_get_contents('php://input'), true) ?? [];
        $created = create_confirmation('approve-payment', [
            'amount' => (string) ($form['amount'] ?? ''),
            'payee' => (string) ($form['payee'] ?? ''),
        ]);
        // channel_entry.qr is a PNG data URI: the page shows it as an image for the phone to scan.
        respond_json([
            'transaction_id' => $created['transaction_id'],
            'url' => $created['channel_entry']['url'],
            'qr' => $created['channel_entry']['qr'] ?? null,
            'valid_until' => $created['channel_entry']['valid_until'],
        ]);
    } elseif ($method === 'GET' && preg_match('#^/payments/([^/]+)$#', $path, $match)) {
        $transactionId = urldecode($match[1]);
        $result = confirmation_result($transactionId);
        if ($result['outcome'] !== CONFIRMED) {
            respond_json(['outcome' => $result['outcome']]);
            return;
        }
        // A transaction is redeemed for an id_token once, so the claims are kept for the next read.
        $_SESSION['claims'][$transactionId] ??= exchange_for_claims($transactionId);
        respond_json(['outcome' => CONFIRMED, 'claims' => $_SESSION['claims'][$transactionId]]);
    } elseif ($method === 'GET' && $path === '/workspace') {
        respond_json(workspace());
    } elseif ($method === 'POST' && $path === '/workspace/owner/reset') {
        $_SESSION['owner'] = null;
        respond_json(workspace());
    } elseif ($method === 'POST' && preg_match('#^/projects/([^/]+)/deletion$#', $path, $match)) {
        $project = $_SESSION['projects'][urldecode($match[1])] ?? null;
        if ($project === null) {
            respond_json(['error' => 'project_not_found'], 404);
            return;
        }
        // The step-up itself: once an owner is known, the confirmation is addressed to that person.
        $owner = $_SESSION['owner'];
        $created = create_confirmation(
            'delete-project',
            ['project' => $project['name'], 'documents' => (string) $project['documents']],
            $owner === null ? null : [OWNER_IDENTITY_TYPE => $owner],
        );
        // The transaction is remembered against the project on the server: the page never tells the
        // backend which project a confirmed transaction deletes.
        $_SESSION['deletions'][$created['transaction_id']] = [
            'transaction_id' => $created['transaction_id'],
            'project_id' => $project['id'],
            'outcome' => PENDING,
            'verdict' => null,
        ];
        respond_json([
            'transaction_id' => $created['transaction_id'],
            'url' => $created['channel_entry']['url'],
            'qr' => $created['channel_entry']['qr'] ?? null,
            'expects_owner' => $owner !== null,
        ]);
    } elseif ($method === 'GET' && preg_match('#^/deletions/([^/]+)$#', $path, $match)) {
        $transactionId = urldecode($match[1]);
        if (!isset($_SESSION['deletions'][$transactionId])) {
            respond_json(['error' => 'deletion_not_found'], 404);
            return;
        }
        $deletion = &$_SESSION['deletions'][$transactionId];
        if ($deletion['verdict'] === null) {
            $result = confirmation_result($transactionId);
            if ($result['outcome'] === PENDING) {
                // Nothing to decide yet.
            } elseif ($result['outcome'] !== CONFIRMED) {
                conclude($deletion, $result['outcome'], 'not_deleted');
            } elseif ($_SESSION['owner'] === null) {
                // No owner yet: the first person to confirm becomes the owner of the account.
                $_SESSION['owner'] = exchange_for_claims($transactionId)['channel_user_id'];
                conclude($deletion, $result['outcome'], 'deleted_owner_bound');
            } else {
                // The decision a step-up exists for: Veriqa does not refuse a confirmation by somebody
                // else, the result still says confirmed. It has to be the expected person.
                conclude(
                    $deletion,
                    $result['outcome'],
                    ($result['matched_type'] ?? null) === OWNER_IDENTITY_TYPE ? 'deleted' : 'confirmed_by_another_person',
                );
            }
        }
        respond_json($deletion);
    } else {
        respond_json(['error' => 'not_found'], 404);
    }
} catch (RuntimeException $failure) {
    respond_refusal($failure);
}
