// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// Synthetic QR pictures for the tests, laid out as the Veriqa host draws its own: a module matrix
// with finder patterns, drawn in squares of whole pixels inside a light quiet zone of four
// modules at the top of the canvas, centred when the canvas is wider, a mark band under it; an
// 8-bit truecolor PNG whose rows repeating the row above go under the Up filter, the rest under None.

import { zlibSync } from '../hooks/vendor/fflate.mjs'

const QuietModules = 4
const FinderSize = 7
const Rgb = { Dark: [0, 0, 0], Light: [255, 255, 255], Mark: [43, 108, 176] } as const
const Filter = { None: 0, Up: 2 }

/** A synthetic code: the three finder patterns with their separators, timing patterns, random data. */
export function syntheticMatrix(size: number, seed: number): boolean[][] {
  let state = seed
  const random = (): boolean => {
    state = (state * 1103515245 + 12345) & 0x7fffffff
    return ((state >> 16) & 1) === 1
  }
  const modules = Array.from({ length: size }, () => Array.from({ length: size }, random))
  for (let at = 0; at < size; at++) {
    const isDark = at % 2 === 0
    modules[6]![at] = isDark
    modules[at]![6] = isDark
  }
  const last = size - FinderSize
  for (const [top, left] of [[0, 0], [0, last], [last, 0]] as const) {
    for (let row = -1; row <= FinderSize; row++) {
      for (let column = -1; column <= FinderSize; column++) {
        const line = modules[top + row]
        if (line === undefined || left + column < 0 || left + column >= size) continue
        const ring = Math.max(Math.abs(row - 3), Math.abs(column - 3))
        line[left + column] = ring <= 3 && ring !== 2
      }
    }
  }
  return modules
}

export type PngLayout = {
  pixelsPerModule: number
  /** Extra canvas pixels on each side of the code: 0 for a canvas as wide as the code. */
  sideMargin: number
  /** A row whose filter byte is written as this value instead (an invalid filter). */
  badFilter?: { row: number; filter: number }
}

/** The code as a data URI of the PNG the host answers in channel_entry.qr. */
export function pngDataUri(matrix: boolean[][], layout: PngLayout): string {
  const { pixelsPerModule: scale, sideMargin } = layout
  const codeSize = (matrix.length + 2 * QuietModules) * scale
  const width = codeSize + 2 * sideMargin
  const markHeight = 2 * scale
  const height = codeSize + markHeight + QuietModules * scale
  const stride = width * 3

  const pixel = (x: number, y: number): readonly number[] => {
    if (y >= codeSize) {
      const isMark = y < codeSize + markHeight && x >= sideMargin / 2 && x < width - sideMargin / 2
      return isMark ? Rgb.Mark : Rgb.Light
    }
    const column = Math.floor((x - sideMargin) / scale) - QuietModules
    const row = Math.floor(y / scale) - QuietModules
    return matrix[row]?.[column] === true ? Rgb.Dark : Rgb.Light
  }

  const raw = new Uint8Array(height * (stride + 1))
  let above: Uint8Array | null = null
  for (let y = 0; y < height; y++) {
    const line = new Uint8Array(stride)
    for (let x = 0; x < width; x++) line.set(pixel(x, y), x * 3)
    const repeats = above !== null && line.every((value, at) => value === above![at])
    const start = y * (stride + 1)
    raw[start] = repeats ? Filter.Up : Filter.None
    if (!repeats) raw.set(line, start + 1)
    above = line
  }
  if (layout.badFilter) raw[layout.badFilter.row * (stride + 1)] = layout.badFilter.filter

  const header = new Uint8Array(13)
  const view = new DataView(header.buffer)
  view.setUint32(0, width)
  view.setUint32(4, height)
  header.set([8, 2, 0, 0, 0], 8)
  const png = concat([
    Uint8Array.of(0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a),
    chunk('IHDR', header),
    chunk('IDAT', zlibSync(raw)),
    chunk('IEND', new Uint8Array(0)),
  ])
  return `data:image/png;base64,${toBase64(png)}`
}

function chunk(type: string, data: Uint8Array): Uint8Array {
  const typed = concat([Uint8Array.from(type, (c) => c.charCodeAt(0)), data])
  const out = new Uint8Array(12 + data.length)
  const view = new DataView(out.buffer)
  view.setUint32(0, data.length)
  out.set(typed, 4)
  view.setUint32(8 + data.length, crc32(typed))
  return out
}

// CRC-32 of a PNG chunk: the reflected polynomial 0xEDB88320.
function crc32(bytes: Uint8Array): number {
  let crc = 0xffffffff
  for (const byte of bytes) {
    crc ^= byte
    for (let bit = 0; bit < 8; bit++) crc = crc & 1 ? (crc >>> 1) ^ 0xedb88320 : crc >>> 1
  }
  return (crc ^ 0xffffffff) >>> 0
}

function concat(parts: Uint8Array[]): Uint8Array {
  const all = new Uint8Array(parts.reduce((total, part) => total + part.length, 0))
  let at = 0
  for (const part of parts) {
    all.set(part, at)
    at += part.length
  }
  return all
}

function toBase64(bytes: Uint8Array): string {
  let binary = ''
  for (let at = 0; at < bytes.length; at += 0x8000) binary += String.fromCharCode(...bytes.subarray(at, at + 0x8000))
  return btoa(binary)
}

/** The cells of a Raster as [code point, foreground, background] triplets. */
export function rasterCells(base64: string): [number, number, number][] {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
  const view = new DataView(bytes.buffer)
  const cells: [number, number, number][] = []
  for (let at = 0; at + 12 <= bytes.length; at += 12) {
    cells.push([view.getUint32(at, true), view.getUint32(at + 4, true), view.getUint32(at + 8, true)])
  }
  return cells
}
