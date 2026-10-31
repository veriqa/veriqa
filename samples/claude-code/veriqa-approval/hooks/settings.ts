// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The plugin settings, as the userConfig fields of plugin.json declare them. No field is
// required, since a required field without a value keeps the module from loading; a missing
// setting refuses every approval instead, before any request is made.

import type { PluginOptions } from 'claude-code'

import { GateError } from './gate'

export const Mode = { OwnHost: 'own-host', SharedStand: 'shared-stand' } as const
export type Mode = (typeof Mode)[keyof typeof Mode]

/** The value of the "channel" setting that lets the person pick a messenger on the phone. */
const HopChannel = 'hop'

// The channels of the sample; with "hop" every one of them is offered and the host adds the
// unified hop link to the QR code when its client entry turns HopMode on.
const HopChannelTypes = ['telegram', 'messenger', 'whatsapp', 'instagram']

const Defaults = {
  TtlSeconds: 300,
  // The host clamps a transaction's lifetime to 60…1800 s.
  MinTtlSeconds: 60,
  MaxTtlSeconds: 1800,
  // USD per million tokens: Claude Opus 5 list prices, no cache and no batch discount.
  PriceInputUsdPerMtok: 5,
  PriceOutputUsdPerMtok: 25,
}

export type Settings = {
  mode: Mode
  /** The host in own-host mode, the shared stand's facade otherwise; no trailing slash. */
  baseUrl: string
  clientId: string
  clientSecret: string
  /** What a transaction without an approval chain may be answered in. */
  channelTypes: string[]
  ttlSeconds: number
  locale: string | undefined
  rulesFile: string | undefined
  priceInput: number
  priceOutput: number
}

export function readSettings(options: PluginOptions): Settings {
  const mode = text(options.mode) === Mode.SharedStand ? Mode.SharedStand : Mode.OwnHost
  const channel = text(options.channel)
  return {
    mode,
    baseUrl: text(mode === Mode.SharedStand ? options.stand_url : options.host_url).replace(/\/+$/, ''),
    clientId: text(options.client_id),
    clientSecret: text(options.client_secret),
    channelTypes: channel === HopChannel ? [...HopChannelTypes] : [channel],
    ttlSeconds: clamp(numberOr(options.ttl_seconds, Defaults.TtlSeconds), Defaults.MinTtlSeconds, Defaults.MaxTtlSeconds),
    locale: text(options.locale) || undefined,
    rulesFile: text(options.rules_file) || undefined,
    priceInput: price(options.price_input, Defaults.PriceInputUsdPerMtok),
    priceOutput: price(options.price_output, Defaults.PriceOutputUsdPerMtok),
  }
}

/** Throws the refusal for a setting the mode needs and does not have. */
export function ensureConfigured(settings: Settings): void {
  const missing = settings.mode === Mode.SharedStand
    ? [settings.baseUrl ? null : 'stand_url']
    : [settings.baseUrl ? null : 'host_url', settings.clientId ? null : 'client_id', settings.clientSecret ? null : 'client_secret']
  const names = missing.filter((name): name is string => name !== null)
  if (names.length > 0) {
    throw new GateError(
      `Veriqa is not configured: set ${names.join(', ')} for the ${settings.mode} mode in the plugin settings (/config or pluginConfigs).`)
  }
}

function text(value: PluginOptions[string] | undefined): string {
  return typeof value === 'string' ? value.trim() : ''
}

function numberOr(value: PluginOptions[string] | undefined, fallback: number): number {
  if (typeof value === 'number') return Number.isFinite(value) ? value : fallback
  const raw = text(value)
  const parsed = Number(raw)
  return raw !== '' && Number.isFinite(parsed) ? parsed : fallback
}

// A negative price or one that is not a number falls back to the default.
function price(value: PluginOptions[string] | undefined, fallback: number): number {
  const parsed = numberOr(value, fallback)
  return parsed >= 0 ? parsed : fallback
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(Math.round(value), min), max)
}
