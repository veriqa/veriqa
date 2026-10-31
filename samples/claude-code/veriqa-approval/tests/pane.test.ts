// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The QR code read back from the host's PNG, the approval pane on each surface and its Refuse button.

import { expect, test } from 'claude-code/testing'

import { qrMatrixFromPng } from '../hooks/qr'
import { DoNotRetry, EntryUrl, GitPush, OwnHost, PaneId, Plugin, Surfaces, paneProps, world } from './harness'
import { pngDataUri, rasterCells, syntheticMatrix } from './qr-png'

const Code = syntheticMatrix(21, 7)
const UpperHalfBlock = 0x2580
const Black = 0x000000
const White = 0xffffff

for (const size of [21, 25]) {
  for (const sideMargin of [0, 30]) {
    test(`T10 reads a ${size}x${size} code back from a PNG ${sideMargin > 0 ? 'wider than' : 'as wide as'} the code`, () => {
      const matrix = syntheticMatrix(size, size)

      const read = qrMatrixFromPng(pngDataUri(matrix, { pixelsPerModule: 3, sideMargin }))

      expect(read).toEqual(matrix)
    })
  }
}

test('T10 a row under filter 5 or no picture reads as null', () => {
  expect(qrMatrixFromPng(pngDataUri(Code, { pixelsPerModule: 3, sideMargin: 0, badFilter: { row: 20, filter: 5 } }))).toBeNull()
  expect(qrMatrixFromPng(null)).toBeNull()
})

for (const surface of Surfaces) {
  test(`T11 the pane draws the code of a waiting approval (${surface})`, { options: OwnHost }, async ($, on) => {
    const w = world(on, { qr: pngDataUri(Code, { pixelsPerModule: 4, sideMargin: 12 }), results: [['pending', 'declined']], holdPauses: true })

    const call = $.tool.call(GitPush)
    await w.nextPause()
    const ui = await $.ui.mount({ plugin: Plugin, surface, component: 'Pane', requestId: PaneId, props: paneProps(80) })
    const raster = await ui.find({ type: 'Raster' })
    const svg = await ui.find({ type: 'Svg' })
    expect((await ui.find({ type: 'Link' }))?.props.href).toBe(EntryUrl)

    if (surface === 'terminal') {
      expect(svg).toBeUndefined()
      expect(raster).toBeDefined()
      const columns = Code.length + 4
      expect(raster?.props.columns).toBe(columns)
      const cells = rasterCells(String(raster?.props.cells))
      expect(cells.length).toBe(columns * Number(raster?.props.rows))
      expect(cells.every(([glyph]) => glyph === UpperHalfBlock)).toBe(true)
      expect(cells.every(([, fg, bg]) => [Black, White].includes(fg) && [Black, White].includes(bg))).toBe(true)
      // Two modules a cell inside a quiet zone of two: the cells give the code back.
      const isDark = (row: number, column: number): boolean => Code[row - 2]?.[column - 2] === true
      const drawn = cells.every(([, fg, bg], at) => {
        const row = Math.floor(at / columns)
        const column = at % columns
        return (fg === Black) === isDark(2 * row, column) && (bg === Black) === isDark(2 * row + 1, column)
      })
      expect(drawn).toBe(true)

      await ui.redraw(paneProps(columns - 1))
      expect(await ui.find({ type: 'Raster' })).toBeUndefined()
      expect(await ui.find({ text: /Widen the pane to see the code/ })).toBeDefined()
    } else {
      expect(raster).toBeUndefined()
      expect(svg).toBeDefined()
      expect(svg?.props.alt).toBe('QR code of the approval link')
    }

    await ui.unmount()
    w.release()
    expect((await call).deny).toContain('declined')
  })

  test(`T11 an unreadable QR leaves the link and says the code is unavailable (${surface})`, { options: OwnHost }, async ($, on) => {
    // A synthetic picture that is not a PNG.
    const w = world(on, { qr: 'data:image/png;base64,AAAA', results: [['pending', 'declined']], holdPauses: true })

    const call = $.tool.call(GitPush)
    await w.nextPause()
    const ui = await $.ui.mount({ plugin: Plugin, surface, component: 'Pane', requestId: PaneId, props: paneProps(80) })

    expect(await ui.find({ text: /QR code unavailable — open the link/ })).toBeDefined()
    expect((await ui.find({ type: 'Link' }))?.props.href).toBe(EntryUrl)
    expect(await ui.find({ type: surface === 'terminal' ? 'Raster' : 'Svg' })).toBeUndefined()

    await ui.unmount()
    w.release()
    await call
  })

  test(`T12 Refuse during the wait refuses the held call (${surface})`, { options: OwnHost }, async ($, on) => {
    // The person answers in the messenger only later: the refusal has to come from the button.
    const w = world(on, { results: [['pending', 'pending', 'declined']], holdPauses: true })

    const call = $.tool.call(GitPush)
    await w.nextPause()
    const ui = await $.ui.mount({ plugin: Plugin, surface, component: 'Pane', requestId: PaneId, props: paneProps(80) })
    const refuse = await ui.find({ type: 'Button', text: 'Refuse' })
    expect(refuse?.key).toBeDefined()
    await ui.press({ key: refuse!.key! })
    expect(w.approvals).toEqual([expect.objectContaining({ label: 'git-push', isRefusedHere: true })])
    await ui.unmount()
    w.release()
    const result = await call

    expect(w.pauses.length).toBeGreaterThan(0)
    expect(result.deny).toContain('Refuse')
    expect(result.deny).toEndWith(DoNotRetry)
    expect(w.toolRuns).toBe(0)
  })
}
