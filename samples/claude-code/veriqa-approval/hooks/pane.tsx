// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The approval pane: per approval in progress, what is asked, the QR code and link of the current
// step, and a Refuse button. A sample's pane, not a product page: it is not styled after the
// host's own pages.

import type { Elements, RenderElement } from 'claude-code'

import type { ApprovalView } from '../types'

/** The elements every surface has, which the pane is built from. */
export type CommonElements = Pick<Elements['desktop'], 'Box' | 'Text' | 'Button' | 'Link'>
/** Draws the QR code of an approval with the surface's own element. */
export type DrawCode = (view: ApprovalView, qr: boolean[][]) => RenderElement

// The code is drawn in fixed colours, not the theme's: a scanner needs dark modules on light.
const Color = { Dark: 0x000000, Light: 0xffffff }
const UpperHalfBlock = 0x2580
const RasterQuietModules = 2
// A Raster cell holds two modules, one above the other.
const ModulesPerCell = 2
// A Raster cell is a little-endian u32 triplet: code point, foreground, background.
const CellWord = { Bytes: 4, CodePoint: 0, Foreground: 1, Background: 2, Count: 3 }
const CellBytes = CellWord.Count * CellWord.Bytes
// String.fromCharCode takes its arguments on the stack, so the bytes go to base64 in slices.
const Base64SliceBytes = 0x8000
// The "HH:MM" of an ISO 8601 timestamp.
const IsoTimeOfDay = { Start: 11, End: 16 }
const SvgQuietModules = 4
const SvgPixelsPerModule = 4
const QrAlt = 'QR code of the approval link'
const SvgNamespace = 'http://www.w3.org/2000/svg'

/** The terminal draws the code as a Raster, two modules a cell, while the pane is wide enough. */
export function rasterCode(
  { Raster, Text }: Pick<Elements['terminal'], 'Raster' | 'Text'>, bodyColumns: number,
): DrawCode {
  return (view, qr) => {
    const columns = qr.length + 2 * RasterQuietModules
    if (bodyColumns < columns) return <Text>Widen the pane to see the code.</Text>
    return <Raster key={`qr-${view.id}`} columns={columns} rows={Math.ceil(columns / ModulesPerCell)} cells={rasterCells(qr)} />
  }
}

/** The desktop, the editor and the phone draw the code as an SVG picture. */
export function svgCode({ Svg }: Pick<Elements['desktop'], 'Svg'>): DrawCode {
  return (_, qr) => {
    const pixels = (qr.length + 2 * SvgQuietModules) * SvgPixelsPerModule
    return <Svg source={svgSource(qr)} alt={QrAlt} width={pixels} height={pixels} />
  }
}

/** The pane: each approval in progress, one under another. */
export function drawPane(
  list: readonly ApprovalView[], elements: CommonElements, drawCode: DrawCode, onRefuse: (id: string) => void,
): RenderElement {
  const { Box, Text } = elements
  if (list.length === 0) return <Text dimColor>No approval is waiting.</Text>
  return (
    <Box flexDirection="column" gap={1}>
      {list.map((view) => drawApproval(view, elements, drawCode, onRefuse))}
    </Box>
  )
}

function drawApproval(
  view: ApprovalView, { Box, Text, Button, Link }: CommonElements, drawCode: DrawCode, onRefuse: (id: string) => void,
): RenderElement {
  const step = view.steps[view.current]
  const nameWidth = Math.max(0, ...Object.keys(view.slots).map((name) => name.length))
  return (
    <Box key={`approval-${view.id}`} flexDirection="column">
      <Text bold>{view.label} · {stepLine(view)}</Text>
      {Object.entries(view.slots).map(([name, value]) => (
        <Box key={`slot-${view.id}-${name}`} gap={1}>
          <Text dimColor>{name.padEnd(nameWidth)}</Text>
          <Text>{value}</Text>
        </Box>
      ))}
      {view.entry === null && <Text dimColor>Asking Veriqa…</Text>}
      {view.entry !== null && view.entry.qr !== null && drawCode(view, view.entry.qr)}
      {view.entry !== null && view.entry.qr === null && <Text>QR code unavailable — open the link.</Text>}
      {view.entry !== null && (
        <Box gap={1}>
          <Link href={view.entry.url} />
          <Text dimColor>valid until {timeOf(view.entry.validUntil)}</Text>
        </Box>
      )}
      {view.current > 0 && step?.approver && (
        <Text dimColor>For the demo only: in a real setup this code reaches {step.approver} another way.</Text>
      )}
      {view.isRefusedHere
        ? <Text>Refused here: the action will not run.</Text>
        : <Button key={`refuse-${view.id}`} label="Refuse" onPress={() => onRefuse(view.id)} />}
    </Box>
  )
}

// "step 1 of 1", or for a chain who has confirmed and who is due: "✓ developer · ⋯ manager".
function stepLine(view: ApprovalView): string {
  if (view.steps.length === 1 && view.steps[0]?.approver === null) return 'step 1 of 1'
  return view.steps
    .map((step, at) => `${step.state === 'confirmed' ? '✓' : at === view.current ? '⋯' : ' '} ${step.approver ?? ''}`)
    .join(' · ')
}

function timeOf(iso: string): string {
  const time = Date.parse(iso)
  return Number.isNaN(time) ? iso : `${new Date(time).toISOString().slice(IsoTimeOfDay.Start, IsoTimeOfDay.End)} UTC`
}

/**
 * The cells of a Raster: two modules per cell, the upper one the foreground of "▀" and the lower
 * one its background, inside a light quiet zone of two modules.
 */
function rasterCells(qr: boolean[][]): string {
  const size = qr.length + 2 * RasterQuietModules
  const rows = Math.ceil(size / ModulesPerCell)
  const isDark = (row: number, column: number): boolean =>
    qr[row - RasterQuietModules]?.[column - RasterQuietModules] === true
  const words = new DataView(new ArrayBuffer(size * rows * CellBytes))
  const word = (at: number, index: number, value: number): void =>
    words.setUint32(at + index * CellWord.Bytes, value, true)
  let at = 0
  for (let row = 0; row < rows; row++) {
    const upper = ModulesPerCell * row
    for (let column = 0; column < size; column++) {
      word(at, CellWord.CodePoint, UpperHalfBlock)
      word(at, CellWord.Foreground, isDark(upper, column) ? Color.Dark : Color.Light)
      word(at, CellWord.Background, isDark(upper + 1, column) ? Color.Dark : Color.Light)
      at += CellBytes
    }
  }
  return toBase64(new Uint8Array(words.buffer))
}

/**
 * The code as an SVG document: white background, a quiet zone of four modules, one rectangle per run
 * of dark modules in a row, which keeps a large code within the size an Svg element takes.
 */
function svgSource(qr: boolean[][]): string {
  const size = qr.length + 2 * SvgQuietModules
  let path = ''
  qr.forEach((line, row) => {
    for (let column = 0; column < line.length;) {
      if (!line[column]) {
        column++
        continue
      }
      const start = column
      while (line[column]) column++
      path += `M${start + SvgQuietModules} ${row + SvgQuietModules}h${column - start}v1h-${column - start}z`
    }
  })
  return `<svg xmlns="${SvgNamespace}" viewBox="0 0 ${size} ${size}" shape-rendering="crispEdges">`
    + `<rect width="${size}" height="${size}" fill="#ffffff"/><path d="${path}" fill="#000000"/></svg>`
}

function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let at = 0; at < bytes.length; at += Base64SliceBytes) {
    binary += String.fromCharCode(...bytes.subarray(at, at + Base64SliceBytes))
  }
  return btoa(binary)
}
