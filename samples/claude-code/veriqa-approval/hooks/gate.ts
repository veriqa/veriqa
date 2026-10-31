// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// What every refusal of the gate has in common: the error that carries its reason and the words
// the model reads. Every path that does not end in "confirmed" refuses; the gate never lets an
// action through on a failure.

/** A refusal with a reason that may be shown as it is. */
export class GateError extends Error {}

/** The reason of a refusal: a GateError's own words, anything else as an unexpected error. */
export function reasonOf(error: unknown): string {
  if (error instanceof GateError) return error.message
  return `Not approved — the approval could not be completed. Unexpected error: ${messageOf(error)}`
}

export function messageOf(error: unknown): string {
  return error instanceof Error ? error.message : String(error)
}

const DoNotRetry = 'Do not repeat it and do not work around the refusal; tell the user it was not approved.'

/** What the model reads when a rule refuses a tool call, a command or a prompt. */
export function refusalText(label: string, reason: string): string {
  return `[${label}] ${reason} ${DoNotRetry}`
}

/**
 * What the model reads after an action the gate let through. The wait looks to the model like a slow
 * call, so without this note it reads the outcome off an earlier refusal in the turn.
 */
export function approvedNoteText(label: string): string {
  return `[${label}] Approved in Veriqa: a person confirmed this action before it ran. An earlier refusal does not apply to it.`
}

/** What the model reads when the costly-run tool is refused. */
export function notApprovedText(reason: string): string {
  return `NOT APPROVED: ${reason} ${DoNotRetry}`
}
