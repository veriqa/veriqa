// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// Reads the modules of the QR code back from the PNG Veriqa returns. The code is not encoded here:
// with the host's hop mode the code carries a one-time hop link and not the approval URL, so only
// the host's own picture says what the phone should open. Pure functions, no engine calls.

import { unzlibSync } from './vendor/fflate.mjs'

const DataUriPrefix = 'data:image/png;base64,'
const PngSignature = [0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a]

// PNG (ISO/IEC 15948): the one layout the host writes — 8-bit truecolor, no interlacing.
const Header = { Length: 13, BitDepth: 8, ColorTypeTruecolor: 2, Method: 0, NoInterlace: 0 }
// The IHDR data: width and height, four bytes each, then the five one-byte fields.
const HeaderField = { Width: 0, Height: 4, Flags: 8 }
// A chunk: its length and its type, four bytes each, its data, then a four-byte CRC.
const Chunk = { LengthBytes: 4, TypeBytes: 4, CrcBytes: 4 }
const ChunkHeadBytes = Chunk.LengthBytes + Chunk.TypeBytes
const ChunkType = { Header: 'IHDR', Data: 'IDAT', End: 'IEND' }
const ByteMask = 0xff
const BytesPerPixel = 3
const Filter = { None: 0, Sub: 1, Up: 2, Average: 3, Paeth: 4 }
// Every row of the image data starts with one byte, its filter type.
const FilterTypeBytes = 1

// ISO/IEC 18004: a code is 17 + 4v modules across for version v of 1…40, a finder pattern 7.
// The finder pattern: rings around its centre module, the second ring from the centre light.
const Qr = { BaseSize: 17, SizeStep: 4, MinVersion: 1, MaxVersion: 40, FinderSize: 7, FinderCentre: 3, FinderLightRing: 2 }
const DarkThreshold = 128

type Image = { width: number; height: number; pixels: Uint8Array }

/**
 * The modules of the code in the host's PNG, quiet zone left out, `true` for dark; null when there
 * is no picture or it is not one this function reads.
 *
 * The host draws the code at the top of the canvas with a light quiet zone, centred on a wider
 * canvas, and may put a mark under it. The first dark row is therefore the top edge of the two
 * upper finder patterns: the first dark run in it is one finder (7 modules), the row's dark extent
 * is the code's width.
 */
export function qrMatrixFromPng(dataUri: string | null): boolean[][] | null {
  if (dataUri === null || !dataUri.startsWith(DataUriPrefix)) return null
  try {
    const image = decodePng(dataUri.slice(DataUriPrefix.length))
    return image === null ? null : readModules(image)
  } catch {
    return null
  }
}

function decodePng(base64: string): Image | null {
  const bytes = Uint8Array.from(atob(base64), (c) => c.charCodeAt(0))
  if (!PngSignature.every((value, at) => bytes[at] === value)) return null
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength)
  let width = 0
  let height = 0
  const data: Uint8Array[] = []
  for (let at = PngSignature.length; at + ChunkHeadBytes <= bytes.length;) {
    const length = view.getUint32(at)
    const type = String.fromCharCode(...bytes.subarray(at + Chunk.LengthBytes, at + ChunkHeadBytes))
    const dataAt = at + ChunkHeadBytes
    const body = bytes.subarray(dataAt, dataAt + length)
    if (body.length !== length) return null
    if (type === ChunkType.Header) {
      if (length !== Header.Length) return null
      width = view.getUint32(dataAt + HeaderField.Width)
      height = view.getUint32(dataAt + HeaderField.Height)
      const [bitDepth, colorType, compression, filterMethod, interlace] = body.subarray(HeaderField.Flags)
      if (bitDepth !== Header.BitDepth || colorType !== Header.ColorTypeTruecolor
        || compression !== Header.Method || filterMethod !== Header.Method || interlace !== Header.NoInterlace) {
        return null
      }
    } else if (type === ChunkType.Data) {
      data.push(body)
    } else if (type === ChunkType.End) {
      break
    }
    at = dataAt + length + Chunk.CrcBytes
  }
  if (width === 0 || height === 0 || data.length === 0) return null
  const pixels = unfilter(unzlibSync(concat(data)), width, height)
  return pixels === null ? null : { width, height, pixels }
}

// Undoes the row filters (PNG §9): each row starts with its filter type, then its filtered bytes.
function unfilter(raw: Uint8Array, width: number, height: number): Uint8Array | null {
  const stride = width * BytesPerPixel
  const rowBytes = FilterTypeBytes + stride
  if (raw.length < height * rowBytes) return null
  const pixels = new Uint8Array(height * stride)
  for (let y = 0; y < height; y++) {
    const filter = raw[y * rowBytes]
    const row = y * stride
    for (let x = 0; x < stride; x++) {
      const value = raw[y * rowBytes + FilterTypeBytes + x] ?? 0
      const left = x >= BytesPerPixel ? pixels[row + x - BytesPerPixel] ?? 0 : 0
      const up = y > 0 ? pixels[row - stride + x] ?? 0 : 0
      const upLeft = y > 0 && x >= BytesPerPixel ? pixels[row - stride + x - BytesPerPixel] ?? 0 : 0
      let predicted: number
      switch (filter) {
        case Filter.None: predicted = 0; break
        case Filter.Sub: predicted = left; break
        case Filter.Up: predicted = up; break
        case Filter.Average: predicted = (left + up) >> 1; break
        case Filter.Paeth: predicted = paeth(left, up, upLeft); break
        default: return null
      }
      pixels[row + x] = (value + predicted) & ByteMask
    }
  }
  return pixels
}

function paeth(left: number, up: number, upLeft: number): number {
  const estimate = left + up - upLeft
  const toLeft = Math.abs(estimate - left)
  const toUp = Math.abs(estimate - up)
  const toUpLeft = Math.abs(estimate - upLeft)
  if (toLeft <= toUp && toLeft <= toUpLeft) return left
  return toUp <= toUpLeft ? up : upLeft
}

function readModules(image: Image): boolean[][] | null {
  const isDark = (x: number, y: number): boolean => {
    const at = (y * image.width + x) * BytesPerPixel
    const sum = image.pixels.subarray(at, at + BytesPerPixel).reduce((total, channel) => total + channel, 0)
    return sum < DarkThreshold * BytesPerPixel
  }

  let top = -1
  for (let y = 0; y < image.height && top < 0; y++) {
    for (let x = 0; x < image.width; x++) {
      if (isDark(x, y)) {
        top = y
        break
      }
    }
  }
  if (top < 0) return null

  let left = 0
  while (!isDark(left, top)) left++
  let right = image.width - 1
  while (!isDark(right, top)) right--
  let run = 0
  while (left + run < image.width && isDark(left + run, top)) run++

  const moduleSize = run / Qr.FinderSize
  const size = (right - left + 1) / moduleSize
  if (!Number.isInteger(moduleSize) || !Number.isInteger(size) || !isQrSize(size)) return null
  if (top + size * moduleSize > image.height) return null

  const center = Math.floor(moduleSize / 2)
  const modules = Array.from({ length: size }, (_, row) =>
    Array.from({ length: size }, (_, column) => isDark(left + column * moduleSize + center, top + row * moduleSize + center)))
  return hasFinders(modules) ? modules : null
}

function isQrSize(size: number): boolean {
  const version = (size - Qr.BaseSize) / Qr.SizeStep
  return Number.isInteger(version) && version >= Qr.MinVersion && version <= Qr.MaxVersion
}

// The three finder patterns: a dark 7×7 ring, a light ring inside it, a dark 3×3 centre.
function hasFinders(modules: boolean[][]): boolean {
  const last = modules.length - Qr.FinderSize
  return [[0, 0], [0, last], [last, 0]].every(([top = 0, left = 0]) => {
    for (let row = 0; row < Qr.FinderSize; row++) {
      for (let column = 0; column < Qr.FinderSize; column++) {
        const ring = Math.max(Math.abs(row - Qr.FinderCentre), Math.abs(column - Qr.FinderCentre))
        if (modules[top + row]?.[left + column] !== (ring !== Qr.FinderLightRing)) return false
      }
    }
    return true
  })
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
