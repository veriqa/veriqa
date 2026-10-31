// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The world beneath the mod in its tests: a scripted Veriqa behind $.http.fetch, the pauses of
// $.process.run, the pane calls, the rules files behind $.fs.stat and $.fs.read and the session's
// directory.
// Every address, id, token and account here is synthetic.

import type { FsStat, HttpResponse, On, ProcessRunResult } from 'claude-code'
import { mock } from 'claude-code/testing'

export const Plugin = 'veriqa-approval'
export const PaneId = 'veriqa-approval'
export const PaneTitle = 'Veriqa approval (sample)'
export const ApproveRunTool = 'mcp__veriqa-approval__approve_run'
export const Surfaces = ['terminal', 'desktop'] as const

/** The sentence every refusal the model reads ends with. */
export const DoNotRetry = 'Do not repeat it and do not work around the refusal; tell the user it was not approved.'

// Synthetic host, stand and session directory.
export const HostUrl = 'https://veriqa.example.test'
export const StandUrl = 'https://stand.example.test'
export const SessionCwd = '/work/demo-project'
export const ProjectRulesPath = `${SessionCwd}/.claude/veriqa-approval.rules.json`
export const ApproversKey = `approvers:${HostUrl}`
export const InstallKeyStoreKey = 'install-key'

/** A Bash call the default git-push rule holds. */
export const GitPush = { tool: 'Bash', command: 'git push origin main' } as const

/** Synthetic arguments of a typed /cycle. */
export const CycleArgs = 'TASK-1'

/** What the engine stamps on a slash command the person typed. */
export const Typed = { origin: { kind: 'composer' as const }, presentation: { isFullscreen: false, columns: 120 } }

/** The settings of own-host mode against the synthetic host. */
export const OwnHost = { host_url: HostUrl, client_secret: 'synthetic-secret' }

/** The default rules as the contract states them, standing in for the plugin's rules.default.json. */
export const ContractRules = {
  chains: { 'agent-costly-run': ['developer', 'manager'] },
  rules: [
    {
      id: 'git-push', event: 'tool.call', tool: '^Bash$', input: { command: '\\bgit\\s+push\\b' },
      action_type: 'agent-tool-call',
      slots: { action: 'push the branch to the remote repository', tool: '{tool}', target: '{input.command}', project: '{project}' },
    },
    {
      id: 'recursive-delete', event: 'tool.call', tool: '^Bash$', input: { command: '\\brm\\s+-[a-zA-Z]*[rR]' },
      action_type: 'agent-tool-call',
      slots: { action: 'delete files and folders recursively', tool: '{tool}', target: '{input.command}', project: '{project}' },
    },
    {
      id: 'cycle-typed', event: 'command.run', command: '^(cycle|cycle-chain)$', action_type: 'agent-command-start',
      slots: { command: '{command}', arguments: '{args}', project: '{project}', started_by: 'person' },
    },
    {
      id: 'cycle-by-agent', event: 'tool.call', tool: '^Skill$', input: { skill: '^(cycle|cycle-chain)$' },
      action_type: 'agent-command-start',
      slots: { command: '{input.skill}', arguments: '{input.args}', project: '{project}', started_by: 'agent' },
    },
  ],
}

/** One answer of the result endpoint: an outcome with HTTP 200, or a raw HTTP answer. */
export type ResultAnswer =
  | string
  | { outcome: string; matched_type?: string | null }
  | { status: number; headers?: Record<string, string>; body?: unknown }

export type WorldOptions = {
  /** Result answers per created transaction, in creation order; the last one repeats. */
  results?: ResultAnswer[][]
  /** Seconds from now to the transactions' expires_at; negative for an expired one. */
  expiresInSeconds?: number
  /** The channel_entry.qr of every created transaction. */
  qr?: string | null
  /** The id_token payload of the confirmation grant. */
  idToken?: Record<string, unknown>
  /** Refuses every $.http.fetch with this reason. */
  fetchDenied?: string
  /** Refuses every $.process.run with this reason. */
  pauseDenied?: string
  /** What $.fs.read answers per path: text, or null for "no such file"; others fall to the defaults. */
  files?: Record<string, string | null>
  /** Paths that exist but $.fs.read refuses, with the reason it gives. */
  unreadable?: Record<string, string>
  /** Symbolic links that lead nowhere: $.fs.stat finds them, $.fs.read does not. */
  danglingLinks?: string[]
  /** The environment the mod reads. */
  env?: Record<string, string>
  /** What the mod's store holds at the start. */
  store?: Record<string, unknown>
  /** Holds every pause until the test lets it go. */
  holdPauses?: boolean
}

export type Fetch = { url: string; path: string; method: string; headers: Record<string, string>; body: string }

export type World = {
  fetches: Fetch[]
  pauses: string[][]
  opens: unknown[]
  closes: unknown[]
  statuses: (string | undefined)[]
  /** The last value the mod wrote under each store key. */
  stored: Map<string, unknown>
  /** The approvals list as the mod last wrote it to the pane's state. */
  approvals: unknown
  /** Events in order: "fetch <method> <path>", "pause", "open", "close", "tool ran", "command ran". */
  log: string[]
  toolRuns: number
  commandRuns: number
  /** Resolves once the next pause has started (with holdPauses). */
  nextPause: () => Promise<void>
  /** Lets every held pause go and stops holding later ones. */
  release: () => void
}

const Ok = 200
const NotFound = 404
const UrlPrefix = /^https?:\/\/[^/]+/
const DefaultRulesFile = '/rules.default.json'
const DefaultExpiresInSeconds = 300
const MsPerSecond = 1_000

/** A synthetic messenger link the scripted Veriqa answers as the way in. */
export const EntryUrl = 'https://t.me/synthetic_veriqa_bot?start=tx'

/**
 * Registers the test's hooks beneath the plugin. Call it before the first call on `$`.
 */
export function world(on: On, options: WorldOptions = {}): World {
  const state: World = {
    fetches: [], pauses: [], opens: [], closes: [], statuses: [], stored: new Map(), approvals: undefined, log: [],
    toolRuns: 0, commandRuns: 0,
    nextPause: () => Promise.resolve(), release: () => undefined,
  }
  mock.env(on, options.env ?? {})
  // Records the writes of the keys the contract names on their way to the store in memory.
  for (const key of [ApproversKey, InstallKeyStoreKey]) {
    on('store.set', { key }, ($, e, next) => {
      state.stored.set(e.key, e.value)
      return next(e)
    })
  }
  mock.store(on, options.store ?? {})
  // Records the pane's state as the mod writes it, on its way to the kit's state beneath.
  on('state.set', { plugin: Plugin, key: 'approvals' }, ($, e, next) => {
    state.approvals = e.value
    return next(e)
  })

  const counters: Counters = { created: 0, tokens: 0, reads: new Map() }
  let held = options.holdPauses === true
  let waiters: (() => void)[] = []
  let releasers: (() => void)[] = []
  state.nextPause = () => new Promise<void>((resolve) => waiters.push(resolve))
  state.release = () => {
    held = false
    releasers.forEach((go) => go())
    releasers = []
  }

  on('session.cwd', () => ({ value: SessionCwd }))
  on('fs.stat', ($, e) => {
    const files = options.files ?? {}
    const found = (kind: FsStat['kind'], isLink: boolean): { value: FsStat } => ({ value: { kind, size: 0, mtimeMs: 0, isLink } })
    if (options.danglingLinks?.includes(e.path)) return found('other', true)
    if (e.path in (options.unreadable ?? {})) return found('file', false)
    if (e.path in files ? typeof files[e.path] === 'string' : e.path.endsWith(DefaultRulesFile)) return found('file', false)
    return { deny: `ENOENT: ${e.path}` }
  })
  on('fs.read', ($, e) => {
    const files = options.files ?? {}
    if (options.danglingLinks?.includes(e.path)) return { deny: `ENOENT: ${e.path}` }
    const refusal = options.unreadable?.[e.path]
    if (refusal !== undefined) return { deny: refusal }
    if (e.path in files) {
      const text = files[e.path]
      return text === null || text === undefined ? { deny: `ENOENT: ${e.path}` } : { value: text }
    }
    if (e.path === ProjectRulesPath) return { deny: `ENOENT: ${e.path}` }
    if (e.path.endsWith(DefaultRulesFile)) return { value: JSON.stringify(ContractRules) }
    return { deny: `ENOENT: ${e.path}` }
  })
  on('ui.open', ($, e) => {
    state.opens.push(e)
    state.log.push('open')
    return { value: { isPlaced: true as const } }
  })
  on('ui.close', ($, e) => {
    state.closes.push(e)
    state.log.push('close')
    return { value: undefined }
  })
  on('ui.status', ($, e) => {
    state.statuses.push(e.text)
    return { value: undefined }
  })
  on('process.run', async ($, e) => {
    state.pauses.push([...e.argv])
    state.log.push('pause')
    if (options.pauseDenied !== undefined) return { deny: options.pauseDenied }
    const started = waiters
    waiters = []
    started.forEach((resolve) => resolve())
    if (held) await new Promise<void>((resolve) => releasers.push(resolve))
    const done: ProcessRunResult = { exitCode: 0, stdout: '', stderr: '', isStdoutTruncated: false, isStderrTruncated: false }
    return { value: done }
  })
  on('http.fetch', ($, e) => {
    const path = e.url.replace(UrlPrefix, '')
    const call: Fetch = {
      url: e.url, path, method: e.init?.method ?? 'GET', headers: { ...(e.init?.headers ?? {}) }, body: e.init?.body ?? '',
    }
    state.fetches.push(call)
    state.log.push(`fetch ${call.method} ${path}`)
    if (options.fetchDenied !== undefined) return { deny: options.fetchDenied }
    return { value: veriqa(call, options, counters) }
  })
  on('tool.call', () => {
    state.toolRuns++
    state.log.push('tool ran')
    return { result: 'tool ran' }
  })
  on('command.run', () => {
    state.commandRuns++
    state.log.push('command ran')
    return { text: 'command ran' }
  })
  on('prompt.submit', ($, e) => ({ text: e.text }))
  return state
}

/** How many transactions the scripted Veriqa created, and how often each result was read. */
type Counters = { created: number; tokens: number; reads: Map<string, number> }

// The scripted Veriqa: the token endpoint, transaction creation and the result endpoint.
function veriqa(call: Fetch, options: WorldOptions, counters: Counters): HttpResponse {
  if (call.path === '/connect/token') {
    const form = new URLSearchParams(call.body)
    if (form.get('grant_type') === 'client_credentials') {
      return json(Ok, { access_token: `synthetic-access-token-${++counters.tokens}`, token_type: 'Bearer', expires_in: 3600 })
    }
    return json(Ok, { access_token: 'synthetic-user-token', id_token: unsignedJwt(options.idToken ?? {}) })
  }
  if (call.path === '/api/transaction/confirmation' && call.method === 'POST') {
    const n = ++counters.created
    const expiresAt = new Date(Date.now() + (options.expiresInSeconds ?? DefaultExpiresInSeconds) * MsPerSecond).toISOString()
    return json(Ok, {
      transaction_id: `tx-${n}`,
      expires_at: expiresAt,
      channel_entry: { url: EntryUrl, qr: options.qr ?? null, valid_until: expiresAt },
    })
  }
  const result = /^\/api\/transaction\/([^/]+)\/result$/.exec(call.path)
  if (result && call.method === 'GET') {
    const id = decodeURIComponent(result[1] ?? '')
    const index = Number(id.replace('tx-', '')) - 1
    const script = options.results?.[index] ?? ['confirmed']
    const n = counters.reads.get(id) ?? 0
    counters.reads.set(id, n + 1)
    const answer = script[Math.min(n, script.length - 1)] ?? 'confirmed'
    if (typeof answer === 'string') return json(Ok, { outcome: answer, matched_type: null })
    if ('status' in answer) return json(answer.status, answer.body ?? {}, answer.headers)
    return json(Ok, { outcome: answer.outcome, matched_type: answer.matched_type ?? null })
  }
  return json(NotFound, { title: 'Not Found' })
}

function json(status: number, body: unknown, headers: Record<string, string> = {}): HttpResponse {
  return {
    status,
    ok: status >= 200 && status < 300,
    headers: { 'content-type': 'application/json', ...headers },
    text: JSON.stringify(body),
  }
}

function unsignedJwt(payload: Record<string, unknown>): string {
  const part = (value: unknown): string =>
    btoa(JSON.stringify(value)).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '')
  return `${part({ alg: 'none', typ: 'JWT' })}.${part(payload)}.signature`
}

/** The header of a recorded request, by a case-insensitive name. */
export function header(call: Fetch, name: string): string | undefined {
  const found = Object.entries(call.headers).find(([key]) => key.toLowerCase() === name.toLowerCase())
  return found?.[1]
}

/** The JSON bodies of the transactions the mod created, in order. */
export function creations(state: World): Record<string, unknown>[] {
  return state.fetches
    .filter((call) => call.method === 'POST' && call.path === '/api/transaction/confirmation')
    .map((call) => JSON.parse(call.body) as Record<string, unknown>)
}

/** The props of an approval pane as a surface hands them to its render hook. */
export function paneProps(bodyColumns: number) {
  return {
    title: PaneTitle,
    isFocused: false,
    bodyColumns,
    placement: 'dock' as const,
    scroll: { offset: 0, bodyRows: 60 },
    view: {},
  }
}
