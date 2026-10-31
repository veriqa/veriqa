// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The approval itself: the costly-run chain, the channel asked for, the shared stand's key, the
// wait for the outcome, the pause between reads and a rules file that cannot be used.

import { expect, test } from 'claude-code/testing'

import {
  ApproveRunTool, ApproversKey, CycleArgs, DoNotRetry, GitPush, InstallKeyStoreKey, OwnHost, ProjectRulesPath, StandUrl,
  Typed, creations, header, world,
} from './harness'

const CostlyRun = { tool: ApproveRunTool, run: 'nightly refactor', input_mtok: 1000, output_mtok: 50 } as const

// Synthetic accounts of the two approvers of the costly-run chain.
const Enrolled = {
  [ApproversKey]: {
    developer: { channel_type: 'telegram', channel_user_id: '1001', enrolled_at: '2026-01-01T00:00:00.000Z' },
    manager: { channel_type: 'whatsapp', channel_user_id: '2002', enrolled_at: '2026-01-01T00:00:00.000Z' },
  },
}
const Matched = { outcome: 'confirmed', matched_type: 'channel_user_id' }

test('T7 approve_run asks developer, then manager after the first is confirmed', { options: OwnHost }, async ($, on) => {
  const w = world(on, { store: Enrolled, results: [['pending', Matched], [Matched]] })

  const result = await $.tool.call(CostlyRun)

  expect(result.deny).toBeUndefined()
  expect(String(result.result)).toStartWith('APPROVED:')
  expect(String(result.result)).toContain('cost_usd=6250')
  const [first, second] = creations(w)
  expect(first).toMatchObject({
    action_type: 'agent-costly-run',
    allowed_channel_types: ['telegram'],
    expected_identities: { channel_user_id: '1001' },
    slot_values: { run: 'nightly refactor', project: 'demo-project', approver: 'developer' },
  })
  expect(String((first?.slot_values as Record<string, unknown>).cost_usd)).toBe('6250')
  expect(second).toMatchObject({
    action_type: 'agent-costly-run',
    allowed_channel_types: ['whatsapp'],
    expected_identities: { channel_user_id: '2002' },
    slot_values: { approver: 'manager' },
  })
  const confirmedFirst = w.log.lastIndexOf('fetch GET /api/transaction/tx-1/result')
  const createdSecond = w.log.lastIndexOf('fetch POST /api/transaction/confirmation')
  expect(confirmedFirst).toBeLessThan(createdSecond)
  expect(w.toolRuns).toBe(0)
})

test('T7 approve_run refuses when step 2 is confirmed by an account it did not expect', { options: OwnHost }, async ($, on) => {
  const w = world(on, { store: Enrolled, results: [[Matched], [{ outcome: 'confirmed', matched_type: null }]] })

  const result = await $.tool.call(CostlyRun)

  expect(result.deny).toStartWith('NOT APPROVED:')
  expect(result.deny).toContain('manager')
  expect(result.deny).toEndWith(DoNotRetry)
  expect(creations(w)).toHaveLength(2)
})

test('T7 approve_run refuses before any transaction when an approver is not enrolled', { options: OwnHost }, async ($, on) => {
  const w = world(on, { store: { [ApproversKey]: { developer: Enrolled[ApproversKey].developer } } })

  const result = await $.tool.call(CostlyRun)

  expect(result.deny).toContain('manager')
  expect(creations(w)).toHaveLength(0)
})

test('T8 channel telegram asks in Telegram alone', { options: { ...OwnHost, channel: 'telegram' } }, async ($, on) => {
  const w = world(on)

  await $.tool.call(GitPush)

  expect(creations(w)[0]?.allowed_channel_types).toEqual(['telegram'])
})

test('T8 channel hop offers the four channels', { options: { ...OwnHost, channel: 'hop' } }, async ($, on) => {
  const w = world(on)

  await $.tool.call(GitPush)

  const channels = [...((creations(w)[0]?.allowed_channel_types as string[] | undefined) ?? [])].sort()
  expect(channels).toEqual(['instagram', 'messenger', 'telegram', 'whatsapp'])
})

test('T9 shared stand: two approvals at once share one installation key and ask for no token', {
  options: { mode: 'shared-stand', stand_url: StandUrl },
}, async ($, on) => {
  const w = world(on, { results: [['pending', 'confirmed'], ['confirmed']] })

  const [one, two] = await Promise.all([
    $.tool.call(GitPush),
    $.tool.call({ tool: 'Bash', command: 'rm -rf build' }),
  ])

  expect(one.deny).toBeUndefined()
  expect(two.deny).toBeUndefined()
  expect(w.toolRuns).toBe(2)
  expect(w.fetches.filter((call) => call.path === '/connect/token')).toHaveLength(0)
  expect(w.fetches.every((call) => call.url.startsWith(StandUrl))).toBe(true)
  const keys = new Set(w.fetches.map((call) => header(call, 'X-Veriqa-Install-Key')))
  expect(keys.size).toBe(1)
  const [key] = [...keys]
  expect(key).toMatch(/^[A-Za-z0-9_-]{43}$/)
  expect(w.fetches.some((call) => header(call, 'Authorization') !== undefined)).toBe(false)
  expect(w.stored.get(InstallKeyStoreKey)).toBe(key)
})

test('T9 shared stand without stand_url refuses and sends nothing', { options: { mode: 'shared-stand' } }, async ($, on) => {
  const w = world(on)

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('not configured')
  expect(w.fetches).toHaveLength(0)
})

test('T13 a 404 result after expires_at ends the wait as expired', { options: OwnHost }, async ($, on) => {
  const w = world(on, { expiresInSeconds: -1, results: [[{ status: 404, body: { title: 'Not Found' } }]] })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('expired')
  expect(w.toolRuns).toBe(0)
})

test('T13 a 401 result gets a new token once and the wait goes on', { options: OwnHost }, async ($, on) => {
  const w = world(on, { results: [[{ status: 401 }, 'pending', { status: 401 }, 'confirmed']] })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toBeUndefined()
  expect(w.toolRuns).toBe(1)
  expect(w.fetches.filter((call) => call.path === '/connect/token')).toHaveLength(3)
  const reads = w.fetches.filter((call) => call.path.includes('/result')).map((call) => header(call, 'Authorization'))
  expect(reads).toEqual([1, 2, 2, 3].map((n) => `Bearer synthetic-access-token-${n}`))
})

test('T13 a second 401 in a row refuses', { options: OwnHost }, async ($, on) => {
  const w = world(on, { results: [[{ status: 401 }]] })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('401')
  expect(w.toolRuns).toBe(0)
  expect(w.fetches.filter((call) => call.path === '/connect/token')).toHaveLength(2)
})

test('T13 a 429 result slows the wait down and the wait goes on', { options: OwnHost }, async ($, on) => {
  const w = world(on, {
    results: [[
      { status: 429, headers: { 'retry-after': '3' } },
      { status: 429, headers: { 'retry-after': '12' } },
      'confirmed',
    ]],
  })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toBeUndefined()
  expect(w.toolRuns).toBe(1)
  const seconds = w.pauses.map(([command, length]) => (command === 'sleep' ? Number(length) : Number.NaN))
  expect(seconds).toHaveLength(2)
  expect(seconds[0]).toBeGreaterThanOrEqual(10)
  expect(seconds[1]).toBeGreaterThanOrEqual(12)
})

test('T15 a rules file that is not JSON refuses every guarded event without a request', { options: OwnHost }, async ($, on) => {
  const w = world(on, { files: { [ProjectRulesPath]: '{ "rules": [ not json' } })

  const tool = await $.tool.call(GitPush)
  const command = await $.command.run({ command: 'cycle', args: CycleArgs, ...Typed })
  const prompt = await $.prompt.submit({ text: 'hello', wait: false, origin: { kind: 'composer' } })

  expect(tool.deny).toContain('JSON')
  expect(tool.deny).toEndWith(DoNotRetry)
  expect(command.text).toContain('JSON')
  expect(prompt.drop).toContain('JSON')
  expect(w.toolRuns).toBe(0)
  expect(w.commandRuns).toBe(0)
  expect(w.fetches).toHaveLength(0)
})

test('T15 a rule of the old PreToolUse event refuses with the reason', {
  options: { ...OwnHost, rules_file: '/work/rules/old.json' },
}, async ($, on) => {
  // A synthetic rules file in the format of the command-hook plugin.
  const old = { rules: [{ id: 'git-push', event: 'PreToolUse', tool: '^Bash$', action_type: 'agent-tool-call', slots: {} }] }
  const w = world(on, { files: { '/work/rules/old.json': JSON.stringify(old) } })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('PreToolUse')
  expect(result.deny).toEndWith(DoNotRetry)
  expect(w.toolRuns).toBe(0)
  expect(w.fetches).toHaveLength(0)
})

test('T15 a project rules file that is there but cannot be read refuses rather than falling back to the default', {
  options: OwnHost,
}, async ($, on) => {
  const w = world(on, { unreadable: { [ProjectRulesPath]: 'EACCES: permission denied' } })

  const tool = await $.tool.call(GitPush)
  const command = await $.command.run({ command: 'cycle', args: CycleArgs, ...Typed })

  expect(tool.deny).toContain(ProjectRulesPath)
  expect(tool.deny).toContain('EACCES')
  expect(tool.deny).toEndWith(DoNotRetry)
  expect(command.text).toContain('EACCES')
  expect(w.toolRuns).toBe(0)
  expect(w.commandRuns).toBe(0)
  expect(w.fetches).toHaveLength(0)
})

test('T15 a project rules file that is a link leading nowhere refuses rather than falling back to the default', {
  options: OwnHost,
}, async ($, on) => {
  const w = world(on, { danglingLinks: [ProjectRulesPath] })

  const tool = await $.tool.call(GitPush)
  const prompt = await $.prompt.submit({ text: 'hello', wait: false, origin: { kind: 'composer' } })

  expect(tool.deny).toContain(ProjectRulesPath)
  expect(tool.deny).toEndWith(DoNotRetry)
  expect(prompt.drop).toContain(ProjectRulesPath)
  expect(w.toolRuns).toBe(0)
  expect(w.fetches).toHaveLength(0)
})

test('T15 a prompt.submit rule without a prompt pattern holds every prompt', {
  options: { ...OwnHost, rules_file: '/work/rules/prompt.json' },
}, async ($, on) => {
  // A synthetic rule whose pattern member is misspelt, so it has no "prompt" pattern.
  const rules = { rules: [{ id: 'deploy', event: 'prompt.submit', promt: 'deploy', action_type: 'agent-prompt', slots: {} }] }
  const w = world(on, { files: { '/work/rules/prompt.json': JSON.stringify(rules) }, results: [['declined']] })

  const prompt = await $.prompt.submit({ text: 'deploy to prod', wait: false, origin: { kind: 'composer' } })

  expect(prompt.drop).toContain('declined')
  expect(prompt.drop).toEndWith(DoNotRetry)
  expect(creations(w)).toEqual([expect.objectContaining({ action_type: 'agent-prompt' })])
})

test('T16 on Windows the pause is a PowerShell sleep of 2 seconds', { options: OwnHost }, async ($, on) => {
  const w = world(on, { env: { OS: 'Windows_NT' }, results: [['pending', 'confirmed']] })

  await $.tool.call(GitPush)

  expect(w.pauses).toEqual([['powershell', '-NoProfile', '-Command', 'Start-Sleep -Seconds 2']])
})

test('T16 elsewhere the pause is sleep 2', { options: OwnHost }, async ($, on) => {
  const w = world(on, { env: {}, results: [['pending', 'confirmed']] })

  await $.tool.call(GitPush)

  expect(w.pauses).toEqual([['sleep', '2']])
})

test('T16 a pause the surface refuses refuses the approval', { options: OwnHost }, async ($, on) => {
  const w = world(on, { pauseDenied: 'no processes on this surface', results: [['pending', 'confirmed']] })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('cannot wait on this surface')
  expect(result.deny).toEndWith(DoNotRetry)
  expect(w.toolRuns).toBe(0)
})
