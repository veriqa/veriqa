// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The parts of an approval that are plain data: its entry in the pane, its approval chain, the
// status line and the pause between two reads of the result. The hooks module runs it.

import type { ApprovalView } from '../types'
import { GateError } from './gate'
import type { Account } from './veriqa'

export const Wait = {
  PollSeconds: 2,
  // The host limits result reads per client, and one client serves a whole team: that is a reason
  // to slow down, not to refuse.
  ThrottledPollSeconds: 10,
  // A result arrives a moment after the deadline of the transaction; polling a little past it
  // keeps a last-second answer from being read as "expired".
  DeadlineGraceMs: 15_000,
  // How long a pause command may run past its own length before the engine kills it: the start of
  // a process, PowerShell above all, can take this long on a machine whose antivirus scans it.
  PauseSlackMs: 30_000,
  MsPerSecond: 1_000,
}

/** The value of the OS environment variable on Windows, where the pause is a PowerShell sleep. */
export const WindowsOs = 'Windows_NT'

export const ApproverSlot = 'approver'

/** Where the enrolled approvers of one host or facade are stored: one account is good for one bot. */
export function approversStoreKey(baseUrl: string): string {
  return `approvers:${baseUrl}`
}

export type Approver = Account & { name: string }

export type ApprovalRequest = {
  label: string
  actionType: string
  slots: Record<string, string>
  /** The approver names of the chain in order; empty for one step anybody holding the link may confirm. */
  chain: readonly string[]
}

export function newApprovalView(id: string, request: ApprovalRequest): ApprovalView {
  return {
    id,
    label: request.label,
    actionType: request.actionType,
    slots: request.slots,
    steps: request.chain.length > 0
      ? request.chain.map((name) => ({ approver: name, state: 'waiting' }))
      : [{ approver: null, state: 'waiting' }],
    current: 0,
    entry: null,
    isRefusedHere: false,
  }
}

/**
 * Resolves every name of a chain to an enrolled account before anything is asked: a chain with a
 * missing approver is refused at once rather than after the first person has answered.
 */
export function approversOf(stored: unknown, names: readonly string[], baseUrl: string): Approver[] {
  const enrolled = asRecord(stored)
  const approvers = names.map((name) => {
    const account = asRecord(enrolled[name])
    if (typeof account.channel_type !== 'string' || typeof account.channel_user_id !== 'string') {
      throw new GateError(`The approver "${name}" is not enrolled with ${baseUrl}: run /veriqa-enroll ${name} first.`)
    }
    return { name, channel_type: account.channel_type, channel_user_id: account.channel_user_id }
  })
  // Two approvals from one account are one approval: the chain exists to require different people.
  const accounts = approvers.map((approver) => `${approver.channel_type}:${approver.channel_user_id}`)
  const repeated = approvers.find((_, at) => accounts.indexOf(accounts[at] ?? '') !== at)
  if (repeated) {
    throw new GateError(`The approver "${repeated.name}" is enrolled with the same account as another approver of the chain: enroll a different person.`)
  }
  return approvers
}

/** The stored approvers with one more, an earlier enrollment of the name replaced. */
export function withApprover(stored: unknown, name: string, account: Account, enrolledAt: string): Record<string, unknown> {
  return { ...asRecord(stored), [name]: { ...account, enrolled_at: enrolledAt } }
}

export function approvedBy(approvers: readonly Approver[]): string {
  return approvers.length > 0 ? approvers.map((approver) => approver.name).join(', then ') : 'a person'
}

export function stepLabel(approver: Approver | undefined, step: number, total: number): string {
  return approver ? `step ${step + 1} of ${total} (${approver.name})` : ''
}

/**
 * The plugin's one status line: the first approval in progress, how many more wait, and how to see
 * the code while the pane is not drawn. Undefined clears it.
 */
export function statusText(list: readonly ApprovalView[], isPaneShown: boolean, paneCommand: string): string | undefined {
  const first = list[0]
  if (first === undefined) return undefined
  const step = first.steps[first.current]
  const waitingFor = step?.approver
    ? `${step.approver} (${first.current + 1} of ${first.steps.length})`
    : `approval of ${first.label}`
  const more = list.length > 1 ? ` · ${list.length - 1} more` : ''
  const hint = isPaneShown ? '' : ` · type /${paneCommand} to show the code`
  return `Veriqa: waiting for ${waitingFor}${more}${hint}`
}

/** A pause of whole seconds by a host command: PowerShell on Windows, `sleep` elsewhere. */
export function pauseArgv(isWindows: boolean, seconds: number): string[] {
  return isWindows
    ? ['powershell', '-NoProfile', '-Command', `Start-Sleep -Seconds ${seconds}`]
    : ['sleep', String(seconds)]
}

function asRecord(value: unknown): Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : {}
}
