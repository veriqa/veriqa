<?php
// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

/**
 * The backend of the support agent on PHP, the router script of the built-in server:
 *
 *   php -S localhost:7040 index.php
 *
 * A support agent handles a refund ticket with three tools. Two of them run on their own;
 * refund_payment moves money, so every call of it waits until a person approves it in Telegram. The
 * approval is a Veriqa server-to-server confirmation: this backend gets a token with
 * client_credentials, creates the confirmation, reads its result and, once confirmed, learns from an
 * id_token who approved. No PHP package is needed beyond the curl extension.
 *
 * The built-in server keeps nothing between requests, so the state - the runs of the agent, the
 * refunded orders, the held access token - lives in $_SESSION: per browser, gone with the session.
 * Every step of the agent is driven by a request from the page: each read of a run moves it on.
 *
 * Configuration (environment variables, all optional on the local stand):
 *   VERIQA_ISSUER        - the Veriqa issuer, https://localhost:8443 on the stand;
 *   VERIQA_CLIENT_ID     - the client registered on the issuer, `support-agent`;
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
    return getenv('VERIQA_CLIENT_ID') ?: 'support-agent';
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

/** Creates a confirmation of a declared action type; a retry with the same key gets the same transaction. */
function create_confirmation(string $actionType, array $slotValues): array
{
    return veriqa('POST', '/api/transaction/confirmation', [
        'action_type' => $actionType,
        'slot_values' => $slotValues,
        'idempotency_key' => bin2hex(random_bytes(16)),
    ]);
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

// --- the agent --------------------------------------------------------------------------------------

const ORDERS = [
    'A-1042' => ['item' => 'Wireless headphones', 'amount' => '89.00 EUR', 'customer' => 'anna@example.com'],
    'A-1043' => ['item' => 'Standing desk', 'amount' => '420.00 EUR', 'customer' => 'ben@example.com'],
];

/**
 * The tools of the agent, known in advance: each one is plain code, and the one with consequences
 * carries an approval policy - the action type to ask about and how its arguments become the slots.
 * The person reads the slots in a wording declared on Veriqa, never text the agent wrote.
 */
function tools(): array
{
    return [
        'lookup_order' => [
            'approval' => null,
            'execute' => static function (array $args): string {
                $order = ORDERS[$args['order']] ?? null;
                if ($order === null) {
                    return "Order {$args['order']} does not exist.";
                }
                $refunded = isset($_SESSION['refunded'][$args['order']]) ? 'already refunded.' : 'not refunded.';
                return "Order {$args['order']}: {$order['item']}, paid {$order['amount']}, customer {$order['customer']}, {$refunded}";
            },
        ],
        'refund_payment' => [
            'approval' => [
                'action_type' => 'approve-refund',
                'slots' => static fn (array $args): array => [
                    'amount' => $args['amount'],
                    'order' => $args['order'],
                    'reason' => $args['reason'],
                ],
            ],
            'execute' => static function (array $args): string {
                $_SESSION['refunded'][$args['order']] = true;
                return "Refunded {$args['amount']} for order {$args['order']}.";
            },
        ],
        'send_customer_email' => [
            'approval' => null,
            'execute' => static fn (array $args): string =>
                "E-mail sent to the customer of order {$args['order']}: \"{$args['text']}\"",
        ],
    ];
}

function log_entry(array &$run, string $kind, string $text): void
{
    $run['log'][] = ['at' => date('H:i:s'), 'kind' => $kind, 'text' => $text];
}

/**
 * Calls a tool. A tool without an approval policy runs at once and the method answers true. One with
 * a policy does not run: the gate creates the confirmation and the run waits for the person - the
 * gate sits in the tool-invocation path, so nothing the agent produces can skip it.
 */
function invoke(array &$run, string $name, array $args): bool
{
    $tool = tools()[$name];
    $pairs = array_map(static fn ($key, $value) => "{$key}: {$value}", array_keys($args), $args);
    log_entry($run, 'call', $name . '(' . implode(', ', $pairs) . ')');

    if ($tool['approval'] !== null) {
        log_entry($run, 'approval', "{$name} needs a person's approval — waiting.");
        $created = create_confirmation($tool['approval']['action_type'], ($tool['approval']['slots'])($args));
        $run['transaction_id'] = $created['transaction_id'];
        $run['held'] = ['tool' => $name, 'args' => $args];
        $run['pending'] = [
            'tool' => $name,
            'url' => $created['channel_entry']['url'],
            'qr' => $created['channel_entry']['qr'] ?? null,
        ];
        return false;
    }

    log_entry($run, 'result', ($tool['execute'])($args));
    return true;
}

/** Reads the approval that holds the run once; when a person has answered, the agent goes on. */
function advance(array &$run): void
{
    $result = confirmation_result($run['transaction_id']);
    if ($result['outcome'] === PENDING) {
        return;
    }

    $held = $run['held'];
    $executed = false;
    if ($result['outcome'] === CONFIRMED) {
        // Who approved goes into the agent's own log - the audit of the run.
        $claims = exchange_for_claims($run['transaction_id']);
        log_entry($run, 'approval', "Approved by {$claims['sub']}.");
        log_entry($run, 'result', (tools()[$held['tool']]['execute'])($held['args']));
        $executed = true;
    } else {
        log_entry($run, 'result', "Not executed: the approval ended as {$result['outcome']}.");
    }
    $run['pending'] = null;

    $text = $executed
        ? "Your refund of {$run['ticket']['amount']} is on its way."
        : 'We could not approve a refund right now; a colleague will contact you.';
    invoke($run, 'send_customer_email', ['order' => $run['ticket']['order'], 'text' => $text]);
    $run['completion'] = 'Done.';
}

function stop(array &$run, Throwable $failure): void
{
    $run['pending'] = null;
    $run['completion'] = 'Stopped: ' . $failure->getMessage();
}

function snapshot(array $run): array
{
    return ['id' => $run['id'], 'log' => $run['log'], 'pending' => $run['pending'], 'completion' => $run['completion']];
}

// --- routes ---------------------------------------------------------------------------------------

function respond_json(mixed $value, int $status = 200): void
{
    http_response_code($status);
    header('Content-Type: application/json');
    echo json_encode($value, JSON_UNESCAPED_SLASHES | JSON_UNESCAPED_UNICODE);
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
$_SESSION['runs'] ??= [];
$method = $_SERVER['REQUEST_METHOD'];
$path = parse_url($_SERVER['REQUEST_URI'], PHP_URL_PATH);

if ($method === 'GET' && $path === '/') {
    header('Content-Type: text/html; charset=utf-8');
    readfile(__DIR__ . '/../page/index.html');
} elseif ($method === 'POST' && $path === '/runs') {
    $ticket = json_decode(file_get_contents('php://input'), true) ?? [];
    $ticket = [
        'order' => (string) ($ticket['order'] ?? ''),
        'amount' => (string) ($ticket['amount'] ?? ''),
        'reason' => (string) ($ticket['reason'] ?? ''),
    ];
    $run = ['id' => bin2hex(random_bytes(16)), 'ticket' => $ticket, 'log' => [], 'pending' => null, 'completion' => null];
    try {
        log_entry($run, 'agent', "Ticket: the customer asks to refund order {$ticket['order']} — \"{$ticket['reason']}\".");
        invoke($run, 'lookup_order', ['order' => $ticket['order']]);
        invoke($run, 'refund_payment', $ticket);
    } catch (RuntimeException $failure) {
        stop($run, $failure);
    }
    $_SESSION['runs'][$run['id']] = $run;
    respond_json(['id' => $run['id']]);
} elseif ($method === 'GET' && preg_match('#^/runs/([^/]+)$#', $path, $match)) {
    $id = urldecode($match[1]);
    if (!isset($_SESSION['runs'][$id])) {
        respond_json(['error' => 'run_not_found'], 404);
        return;
    }
    $run = &$_SESSION['runs'][$id];
    if ($run['pending'] !== null) {
        try {
            advance($run);
        } catch (RuntimeException $failure) {
            stop($run, $failure);
        }
    }
    respond_json(snapshot($run));
} else {
    respond_json(['error' => 'not_found'], 404);
}
