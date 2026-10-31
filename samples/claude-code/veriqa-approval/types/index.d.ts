// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The session state the approval pane draws from: one entry per approval in progress.

export type ApprovalStepState = 'waiting' | 'confirmed' | 'refused'

export type ApprovalStep = {
  /** The enrolled approver this step is for; null without an approval chain. */
  approver: string | null
  state: ApprovalStepState
}

export type ApprovalEntry = {
  /** The link Veriqa returned for the current step. */
  url: string
  /** The modules of the QR code Veriqa drew, quiet zone left out; null when it drew none or it was not read. */
  qr: boolean[][] | null
  /** When the link stops working, as Veriqa answered it. */
  validUntil: string
}

export type ApprovalView = {
  /** Unique per approval in progress. */
  id: string
  /** The rule id, "costly-run" or "enroll <name>". */
  label: string
  actionType: string
  /** The values sent to Veriqa, the approver of the step included. */
  slots: Record<string, string>
  /** One step without a chain. */
  steps: ApprovalStep[]
  /** Index into steps. */
  current: number
  /** The way in of the current step; null until Veriqa answered. */
  entry: ApprovalEntry | null
  /** Set by the Refuse button of the pane. */
  isRefusedHere: boolean
}

declare module 'claude-code' {
  interface PluginState {
    'veriqa-approval': { approvals: ApprovalView[] }
  }
}
