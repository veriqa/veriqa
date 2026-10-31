// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// Slash commands: a typed /cycle held by its rule, the plugin's own commands and their registration.

import { expect, test } from 'claude-code/testing'

import {
  ApproveRunTool, ApproversKey, CycleArgs, DoNotRetry, OwnHost, SessionCwd, Typed, creations, header, world,
} from './harness'

test('T6 a typed /cycle runs once confirmed', { options: OwnHost }, async ($, on) => {
  const w = world(on, { results: [['pending', 'confirmed']] })

  const result = await $.command.run({ command: 'cycle', args: CycleArgs, ...Typed })

  expect(result.text).toBe('command ran')
  expect(w.commandRuns).toBe(1)
  const [body] = creations(w)
  expect(body).toMatchObject({
    action_type: 'agent-command-start',
    slot_values: { command: 'cycle', arguments: CycleArgs, project: 'demo-project', started_by: 'person' },
  })
})

test('T6 a typed /cycle does not run when declined and shows the reason', { options: OwnHost }, async ($, on) => {
  const w = world(on, { results: [['declined']] })

  const result = await $.command.run({ command: 'cycle', args: CycleArgs, ...Typed })

  expect(w.commandRuns).toBe(0)
  expect(result.text).toContain('declined')
  expect(result.text).toEndWith(DoNotRetry)
})

test('T14 /veriqa-enroll stores the account that confirmed under the host address', {
  options: OwnHost,
}, async ($, on) => {
  // Synthetic accounts: an earlier enrollment of the same name, and another approver.
  const w = world(on, {
    idToken: { sub: 'synthetic-subject', channel_type: 'telegram', channel_user_id: '1001' },
    store: {
      [ApproversKey]: {
        developer: { channel_type: 'telegram', channel_user_id: '999', enrolled_at: '2026-01-01T00:00:00.000Z' },
        manager: { channel_type: 'telegram', channel_user_id: '2002', enrolled_at: '2026-01-01T00:00:00.000Z' },
      },
    },
  })

  const result = await $.command.run({ command: 'veriqa-enroll', args: 'developer', ...Typed })

  expect(result.text).not.toContain('NOT ENROLLED')
  expect(w.commandRuns).toBe(0)
  const [body] = creations(w)
  expect(body).toMatchObject({ action_type: 'agent-approver-enroll', slot_values: { name: 'developer', project: 'demo-project' } })
  const redeem = w.fetches.find((call) => call.path === '/connect/token' && call.body.includes('confirmation'))
  expect(redeem).toBeDefined()
  const form = new URLSearchParams(redeem?.body)
  expect(form.get('grant_type')).toBe('urn:veriqa:params:oauth:grant-type:confirmation')
  expect(form.get('transaction_id')).toBe('tx-1')
  expect(form.get('scope')).toBe('openid channel')
  expect(header(redeem!, 'Authorization')).toStartWith('Basic ')

  expect(w.stored.get(ApproversKey)).toEqual({
    developer: { channel_type: 'telegram', channel_user_id: '1001', enrolled_at: expect.any(String) },
    manager: { channel_type: 'telegram', channel_user_id: '2002', enrolled_at: '2026-01-01T00:00:00.000Z' },
  })
})

test('T14 /veriqa-enroll refuses a name with a space and sends nothing', { options: OwnHost }, async ($, on) => {
  const w = world(on)

  const result = await $.command.run({ command: 'veriqa-enroll', args: 'a b', ...Typed })

  expect(result.text).toStartWith('NOT ENROLLED')
  expect(w.fetches).toHaveLength(0)
  expect(w.stored.size).toBe(0)
})

test('T17 session.start registers the approval tool and both commands', async ($, on) => {
  world(on)
  const commands: Record<string, unknown>[] = []
  const tools: Record<string, unknown>[] = []
  on('command.register', ($, e) => {
    commands.push({ ...e })
    return { value: { command: e.name } }
  })
  on('tool.register', ($, e) => {
    tools.push({ ...e })
    return { value: { tool: ApproveRunTool } }
  })
  on('session.start', ($, e) => ({ cwd: e.cwd }))

  await $.session.start({ cwd: SessionCwd, surface: 'terminal', isInteractive: true })

  expect(commands.find((spec) => spec.name === 'veriqa-approval')).toMatchObject({ immediate: true })
  const enroll = commands.find((spec) => spec.name === 'veriqa-enroll')
  expect(enroll).toBeDefined()
  expect(enroll?.immediate).toBeFalsy()
  expect(tools.find((spec) => spec.name === 'approve_run')).toMatchObject({ isDeferred: false })
})
