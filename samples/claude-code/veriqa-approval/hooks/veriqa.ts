// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The Veriqa protocol as plain data: paths, request bodies, credentials and how answers are read.
// In own-host mode the calls go to the integrator's host with the client's credentials; in
// shared-stand mode to the stand's facade, which answers the same paths, bodies and responses and
// authenticates the installation by a key the plugin makes itself, so no client secret leaves the
// server. The requests themselves are made by the hooks module.

import type { HttpResponse } from 'claude-code'

import { GateError } from './gate'
import type { Settings } from './settings'

export const Api = {
  Token: '/connect/token',
  Create: '/api/transaction/confirmation',
  Result: (id: string) => `/api/transaction/${encodeURIComponent(id)}/result`,
}

export const InstallKey = {
  Header: 'X-Veriqa-Install-Key',
  StoreKey: 'install-key',
  Bytes: 32,
  Pattern: /^[A-Za-z0-9_-]{43}$/,
}

const ConfirmationGrant = {
  Type: 'urn:veriqa:params:oauth:grant-type:confirmation',
  Scope: 'openid channel',
}

const ClientCredentialsGrant = 'client_credentials'

/** Header names; the engine hands response headers over with lower-cased names. */
export const HttpHeader = { Authorization: 'Authorization', ContentType: 'Content-Type', RetryAfter: 'retry-after' }

const AuthScheme = { Basic: 'Basic', Bearer: 'Bearer' }

export const ContentType = { Form: 'application/x-www-form-urlencoded', Json: 'application/json' }

export const Outcome = { Pending: 'pending', Confirmed: 'confirmed', Expired: 'expired' } as const

export const HttpStatus = { Unauthorized: 401, NotFound: 404, TooManyRequests: 429 }

/** The comparable identity type the host fragment declares for the client: the messenger account. */
export const IdentityType = 'channel_user_id'

export type ChannelEntry = { url: string; qr?: string | null; valid_until?: string }

export type Transaction = {
  transaction_id: string
  expires_at: string
  channel_entry?: ChannelEntry | null
}

export type ConfirmationRequest = {
  actionType: string
  slotValues: Record<string, string>
  ttlSeconds: number
  channelTypes: readonly string[]
  /** The account expected to confirm, for a step of an approval chain. */
  expectedAccount?: string
}

export type Account = { channel_type: string; channel_user_id: string }

/** The headers of one approval's calls: the API's, and the token endpoint's. */
export type Access = { api: Record<string, string>; token: Record<string, string> }

/** Own-host: the token endpoint takes the client's credentials, the API the token it issues. */
export function ownHostAccess(settings: Settings, accessToken: string): Access {
  return { api: { [HttpHeader.Authorization]: `${AuthScheme.Bearer} ${accessToken}` }, token: basicAuthHeader(settings) }
}

/** Shared stand: the facade takes the installation key in place of either. */
export function sharedStandAccess(installKey: string): Access {
  const header = { [InstallKey.Header]: installKey }
  return { api: header, token: header }
}

// RFC 6749 §2.3.1: the credentials are form-encoded before they go into the Basic header.
export function basicAuthHeader(settings: Settings): Record<string, string> {
  const credentials = `${encodeURIComponent(settings.clientId)}:${encodeURIComponent(settings.clientSecret)}`
  return { [HttpHeader.Authorization]: `${AuthScheme.Basic} ${btoa(credentials)}` }
}

export function clientCredentialsForm(): string {
  return new URLSearchParams({ grant_type: ClientCredentialsGrant }).toString()
}

export function confirmationGrantForm(transactionId: string): string {
  return new URLSearchParams({
    grant_type: ConfirmationGrant.Type,
    transaction_id: transactionId,
    scope: ConfirmationGrant.Scope,
  }).toString()
}

// With an approver, the transaction names the account expected to confirm and keeps to that
// account's channel, so that the comparison of accounts is made within one messenger.
export function confirmationBody(settings: Settings, request: ConfirmationRequest): string {
  return JSON.stringify({
    action_type: request.actionType,
    slot_values: request.slotValues,
    ttl_seconds: request.ttlSeconds,
    locale: settings.locale,
    allowed_channel_types: request.channelTypes,
    expected_identities: request.expectedAccount ? { [IdentityType]: request.expectedAccount } : undefined,
  })
}

export function accessTokenOf(response: HttpResponse): string {
  const token = bodyOf(response, 'The token response').access_token
  if (typeof token !== 'string' || token === '') throw new GateError('The token response has no access_token.')
  return token
}

export function transactionOf(response: HttpResponse): Transaction {
  const body = bodyOf(response, 'The confirmation response')
  if (typeof body.transaction_id !== 'string' || typeof body.expires_at !== 'string') {
    throw new GateError('The confirmation response has no transaction_id or expires_at.')
  }
  return body as Transaction
}

/** The outcome of a result read and, for a step of a chain, whether the expected account confirmed. */
export function resultOf(response: HttpResponse): { outcome: string; matchedType: string | null } {
  const body = bodyOf(response, 'The confirmation result')
  return {
    outcome: String(body.outcome ?? ''),
    matchedType: typeof body.matched_type === 'string' ? body.matched_type : null,
  }
}

// Reads the messenger account of the person who confirmed from the id_token of the confirmation
// grant. The token comes straight from the token endpoint over TLS in answer to an authenticated
// request, so its signature is not checked here (OpenID Connect Core §3.1.3.7 allows TLS validation
// in its place for a token received this way).
export function accountOf(response: HttpResponse): Account {
  const payload = jwtPayload(bodyOf(response, 'The token response').id_token)
  if (typeof payload.channel_type !== 'string' || payload.channel_user_id === undefined || payload.channel_user_id === null) {
    throw new GateError('The id_token names no messenger account: is the "channel" scope allowed to the client?')
  }
  return { channel_type: payload.channel_type, channel_user_id: String(payload.channel_user_id) }
}

/**
 * The refusal for a non-2xx answer. The API answers Problem Details (code in "title"), the token
 * endpoint an OAuth error ("error" / "error_description"); neither echoes a submitted value.
 */
export function failure(response: HttpResponse, what: string): GateError {
  let code = ''
  try {
    const body = JSON.parse(response.text) as Record<string, unknown>
    code = [body.title ?? body.error, body.detail ?? body.error_description]
      .filter((part) => typeof part === 'string' && part !== '')
      .join(': ')
      .replace(/\.+$/, '')
  } catch {
    // Not a JSON body: the status alone says what happened.
  }
  return new GateError(`${what} failed with HTTP ${response.status}${code ? ` — ${code}` : ''}.`)
}

/** Seconds a 429 answer asks to wait, 0 when it does not say. */
export function retryAfterSeconds(response: HttpResponse): number {
  return Number(response.headers[HttpHeader.RetryAfter]) || 0
}

/** An installation key: random bytes in base64url without padding. */
export function installKeyOf(bytes: Uint8Array): string {
  return btoa(String.fromCharCode(...bytes)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
}

export function originOf(url: string): string {
  try {
    return new URL(url).origin
  } catch {
    return url
  }
}

function bodyOf(response: HttpResponse, what: string): Record<string, unknown> {
  try {
    const body: unknown = JSON.parse(response.text)
    if (typeof body === 'object' && body !== null && !Array.isArray(body)) return body as Record<string, unknown>
  } catch {
    // Reported below.
  }
  throw new GateError(`${what} from Veriqa is not a JSON object.`)
}

function jwtPayload(token: unknown): Record<string, unknown> {
  const part = typeof token === 'string' ? token.split('.')[1] : undefined
  if (part !== undefined) {
    try {
      const binary = atob(part.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(part.length / 4) * 4, '='))
      const payload: unknown = JSON.parse(new TextDecoder().decode(Uint8Array.from(binary, (c) => c.charCodeAt(0))))
      if (typeof payload === 'object' && payload !== null) return payload as Record<string, unknown>
    } catch {
      // Reported below.
    }
  }
  throw new GateError('The token response has no readable id_token.')
}
