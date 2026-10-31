// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// A tool call held by a rule: it runs only once Veriqa answers "confirmed"; everything else refuses.

import { expect, test } from 'claude-code/testing'

import {
  DoNotRetry, EntryUrl, GitPush, HostUrl, OwnHost, PaneId, PaneTitle, Plugin, SessionCwd, Surfaces, paneProps, world,
} from './harness'

for (const surface of Surfaces) {
  test(`T1 confirmed after two pending reads runs the tool and shows the pane meanwhile (${surface})`, { options: OwnHost }, async ($, on) => {
    const w = world(on, { results: [['pending', 'pending', 'confirmed']], holdPauses: true })

    const call = $.tool.call(GitPush)
    await w.nextPause()
    const ui = await $.ui.mount({ plugin: Plugin, surface, component: 'Pane', requestId: PaneId, props: paneProps(80) })
    expect((await ui.find({ text: /git-push/ }))?.text).toBeDefined()
    expect(await ui.find({ text: 'git push origin main' })).toBeDefined()
    expect((await ui.find({ type: 'Link' }))?.props.href).toBe(EntryUrl)
    await ui.unmount()
    w.release()
    const result = await call

    expect(result.deny).toBeUndefined()
    expect(w.toolRuns).toBe(1)
    expect(w.pauses).toEqual([['sleep', '2'], ['sleep', '2']])
    expect(w.opens[0]).toMatchObject({ id: PaneId, title: PaneTitle })
    expect(w.closes).toContainEqual(expect.objectContaining({ id: PaneId }))
    expect(w.log.lastIndexOf('close')).toBeLessThan(w.log.indexOf('tool ran'))
    expect(w.statuses.length).toBeGreaterThan(0)
    expect(w.statuses[w.statuses.length - 1]).toBeUndefined()
  })
}

test('T2 declined refuses the tool call and the tool does not run', { options: OwnHost }, async ($, on) => {
  const w = world(on, { results: [['pending', 'declined']] })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('declined')
  expect(result.deny).toEndWith(DoNotRetry)
  expect(w.toolRuns).toBe(0)
})

test('T3 own-host without client_secret refuses as not configured and sends nothing', { options: { host_url: HostUrl } }, async ($, on) => {
  const w = world(on)

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain('not configured')
  expect(result.deny).toEndWith(DoNotRetry)
  expect(w.fetches).toHaveLength(0)
  expect(w.toolRuns).toBe(0)
})

test('T4 an unreachable Veriqa refuses with the host address', { options: OwnHost }, async ($, on) => {
  const w = world(on, { fetchDenied: 'connect ECONNREFUSED' })

  const result = await $.tool.call(GitPush)

  expect(result.deny).toContain(HostUrl)
  expect(result.deny).toEndWith(DoNotRetry)
  expect(w.toolRuns).toBe(0)
})

test('T5 calls no rule matches pass through without a request', { options: OwnHost }, async ($, on) => {
  const w = world(on)

  const listing = await $.tool.call({ tool: 'Bash', command: 'ls -la' })
  const reading = await $.tool.call({ tool: 'Read', file_path: `${SessionCwd}/README.md` })

  expect(listing.deny).toBeUndefined()
  expect(reading.deny).toBeUndefined()
  expect(w.toolRuns).toBe(2)
  expect(w.fetches).toHaveLength(0)
  expect(w.opens).toHaveLength(0)
})
