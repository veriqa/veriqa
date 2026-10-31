// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// Veriqa approval gate for Claude Code, as a mod: the hook holds a tool call, a slash command or a
// prompt, shows the way in to the approval in a pane, waits for the person's answer in the
// messenger and lets the action through only on "confirmed". Every guard refuses when it fails.
//
// Every call on the engine is made in this file; the modules beside it are plain functions.

import { atom, read, update } from 'claude-code'
import type { EngineInterface, HttpInit, HttpResponse, Register } from 'claude-code'

import type { ApprovalView } from '../types'
import {
  ApproverSlot, Wait, WindowsOs, approvedBy, approversOf, approversStoreKey, newApprovalView, pauseArgv, stepLabel, statusText,
  withApprover, type ApprovalRequest, type Approver,
} from './approval'
import { GateError, approvedNoteText, messageOf, notApprovedText, reasonOf, refusalText } from './gate'
import { drawPane, rasterCode, svgCode } from './pane'
import { qrMatrixFromPng } from './qr'
import {
  RulesFile, chainFor, matchCommand, matchPrompt, matchToolCall, parseRules, projectNameOf, resolveSlots,
  type Match, type Rule, type RuleSet,
} from './rules'
import { Mode, ensureConfigured, readSettings, type Settings } from './settings'
import {
  Api, ContentType, HttpHeader, HttpStatus, IdentityType, InstallKey, Outcome, accessTokenOf, accountOf, basicAuthHeader,
  clientCredentialsForm, confirmationBody, confirmationGrantForm, failure, installKeyOf, originOf, resultOf,
  retryAfterSeconds, sharedStandAccess, ownHostAccess, transactionOf, type Access, type Account,
  type ConfirmationRequest, type Transaction,
} from './veriqa'

// The tool's and the commands' names, the pane's id and the state's reference are also written
// out as literals where a hook or a registration names them: `claude plugin validate` and the
// engine read those off the source, and a constant there reads as an unknown name.
const Pane = { Id: 'veriqa-approval', Title: 'Veriqa approval (sample)', Command: 'veriqa-approval' }

const ApproveRun = {
  Tool: 'mcp__veriqa-approval__approve_run',
  ActionType: 'agent-costly-run',
  Label: 'costly-run',
  MaxRunLength: 64,
} as const

const Enroll = {
  Command: 'veriqa-enroll',
  ActionType: 'agent-approver-enroll',
  Name: /^[\w.-]{1,32}$/,
}

const OwnCommands = new Set<string>([Enroll.Command, Pane.Command])
const GateLabel = 'veriqa-approval'
const GateFailed = 'The approval gate failed.'

// How $.fs.stat rejects a path that is not there.
const MissingPath = /\bENOENT\b/

// The tool.call fields that are the engine's, not the tool's input.
const ReservedToolFields = new Set(['tool', 'tool_use_id', 'agentId', 'requestMeta', 'consent'])

// The approvals in progress, one entry each: what the pane draws.
const approvals = atom({ plugin: 'veriqa-approval', key: 'approvals' } as const, [])

// Whether the last open of the pane got it drawn. An approval's own open is unasked and waits
// undrawn on a narrow terminal; the person's /veriqa-approval places it at any width.
let isPaneShown = false

// The approvals refused with the pane's button. The press also marks the approval in the session
// state for the pane, but a hook's reads of that state stand at one moment for its whole dispatch,
// so the hook that waits learns of the press here.
const refusedHere = new Set<string>()

// The installation key being made for the shared stand: calls made at the same time share it.
let installKeyInFlight: Promise<string> | undefined

/** The answer to a guarded event: not ours (null), approved, or refused with the reason. */
type Verdict = null | { refusal: string | null; label: string }

const Interrupted = 'Not approved: the turn was interrupted while the approval waited.'

export const register: Register = (on, options) => {
  const settings = readSettings(options)

  on('session.start', async ($, e, next) => {
    await $.tool.register({
      name: 'approve_run',
      description: 'Ask the configured approvers, in turn, to approve a costly run from its token estimate in Veriqa. '
        + 'Waits for their answer. Start the run only when this tool succeeds; on an error do not start it in any form.',
      inputSchema: {
        type: 'object',
        properties: {
          run: { type: 'string', maxLength: ApproveRun.MaxRunLength, description: 'A short name of the run.' },
          input_mtok: { type: 'number', minimum: 0, description: 'Estimated input tokens, in millions.' },
          output_mtok: { type: 'number', minimum: 0, description: 'Estimated output tokens, in millions.' },
        },
        required: ['run', 'input_mtok', 'output_mtok'],
      },
      isDeferred: false,
    })
    await $.command.register({
      name: 'veriqa-enroll',
      description: 'Register the messenger account that confirms in Veriqa as an approver of the costly-run chain',
      argumentHint: '<name>',
    })
    await $.command.register({
      name: 'veriqa-approval',
      description: 'Show the Veriqa approval pane with the QR code of the approval that waits',
      immediate: true,
    })
    // A reload of the module ends the hooks that waited; the approvals they left in the session
    // state are nobody's any more.
    await update($, approvals, () => [])
    await $.ui.close({ id: Pane.Id }).catch(() => undefined)
    $.ui.status(undefined)
    return next(e)
  })

  // Demo 1 and any tool rule: the call waits for the approval and runs only once it is confirmed.
  on('tool.call', async ($, e, next) => {
    if (e.tool === ApproveRun.Tool) return next(e)
    const verdict = await guard($, settings, (rules) => matchToolCall(rules, e.tool, toolInput(e)), next.signal)
    if (verdict === null) return next(e)
    if (verdict.refusal) return { deny: verdict.refusal }
    return withApprovalNote(await next(e), verdict.label)
  }).catch(($, e, next) => (next.called ? next(e) : { deny: refusalText(GateLabel, GateFailed) }))

  // Demo 2: a slash command the person typed, such as /cycle, starts only once it is approved.
  on('command.run', async ($, e, next) => {
    if (OwnCommands.has(e.command)) return next(e)
    const verdict = await guard($, settings, (rules) => matchCommand(rules, e.command, e.args), next.signal)
    if (verdict === null) return next(e)
    if (verdict.refusal) return { text: verdict.refusal }
    return withApprovalNote(await next(e), verdict.label)
  }).catch(($, e, next) => (next.called ? next(e) : { text: refusalText(GateLabel, GateFailed) }))

  // Free-text prompts: a matching prompt reaches the model only once it is approved.
  on('prompt.submit', async ($, e, next) => {
    const verdict = await guard($, settings, (rules) => matchPrompt(rules, e.text), next.signal)
    return verdict?.refusal ? { drop: verdict.refusal } : next(e)
  }).catch(($, e, next) => (next.called ? next(e) : { drop: refusalText(GateLabel, GateFailed) }))

  // Demo 3: the costly run the skill asks for, approved by the chain of its action type in turn.
  on('tool.call', { tool: 'mcp__veriqa-approval__approve_run' }, async ($, e, next) => {
    const input = parseRunInput(e)
    if (typeof input === 'string') return { deny: notApprovedText(input) }
    const project = projectNameOf(await $.session.cwd())
    const slots: Record<string, string> = {
      run: input.run,
      input_mtok: String(input.inputMtok),
      output_mtok: String(input.outputMtok),
      cost_usd: String(Math.round(input.inputMtok * settings.priceInput + input.outputMtok * settings.priceOutput)),
      ...(project ? { project } : {}),
    }
    try {
      const rules = await loadRules($, settings)
      const request = { label: ApproveRun.Label, actionType: ApproveRun.ActionType, slots, chain: chainFor(rules.chains, ApproveRun.ActionType) }
      const { approvers } = await approve($, settings, request, next.signal)
      const asked = Object.entries(slots).map(([name, value]) => `${name}=${value}`).join(' ')
      return { result: `APPROVED: by ${approvedBy(approvers)} in Veriqa. Asked: ${asked}` }
    } catch (error) {
      return { deny: notApprovedText(reasonOf(error)) }
    }
  }).catch(($, e, next) => (next.called ? next(e) : { deny: notApprovedText(GateFailed) }))

  // Enrollment: whoever confirms this question is stored as the named approver, so the link has
  // to reach exactly that person. The hook reads next only for its signal: the person interrupting
  // the turn ends the wait.
  on('command.run', { command: 'veriqa-enroll' }, async ($, e, next) => {
    const name = e.args.trim()
    if (!Enroll.Name.test(name)) {
      return { text: `NOT ENROLLED: usage /${Enroll.Command} <name>, a name of letters, digits, "_", "-" and "." up to 32.` }
    }
    try {
      const project = projectNameOf(await $.session.cwd())
      const slots: Record<string, string> = project ? { name, project } : { name }
      const request = { label: `enroll ${name}`, actionType: Enroll.ActionType, slots, chain: [] }
      const { access, transactionId } = await approve($, settings, request, next.signal)
      const account = await redeemConfirmation($, settings, access, transactionId)
      const stored = await $.store.get(approversStoreKey(settings.baseUrl))
      const enrolledAt = new Date().toISOString()
      await $.store.set(approversStoreKey(settings.baseUrl), withApprover(stored, name, account, enrolledAt))
      return { text: `ENROLLED: "${name}" is the ${account.channel_type} account that just confirmed.` }
    } catch (error) {
      return { text: `NOT ENROLLED: ${reasonOf(error)}` }
    }
  }).catch(() => ({ text: 'NOT ENROLLED: the enrollment failed unexpectedly.' }))

  // Shows the pane on the person's request: an approval's own pane waits undrawn on a narrow terminal.
  on('command.run', { command: 'veriqa-approval' }, async ($) => {
    if ((await read($, approvals)).length === 0) return { text: 'No approval is waiting.' }
    const opened = await $.ui.open({ id: Pane.Id, title: Pane.Title })
    isPaneShown = opened.isPlaced
    $.ui.status(statusText(await read($, approvals), isPaneShown, Pane.Command))
    return { text: 'The Veriqa approval pane is open.' }
  }).catch(() => ({ text: 'The Veriqa approval pane could not be opened.' }))

  // The person closing the pane brings the status line's hint to show the code back with the next
  // change of the approval. The hook does not redraw the status itself: its read of the approvals
  // stands at one moment and may still hold one that has just ended.
  on('ui.close', { id: 'veriqa-approval' }, async ($, e, next) => {
    const result = await next(e)
    if (e.origin.kind === 'person') isPaneShown = false
    return result
  }).catch(($, e, next) => next(e))

  on('ui.render', { component: 'Pane', requestId: 'veriqa-approval' }, async ($, e) => {
    const list = await read($, approvals)
    const onRefuse = (id: string): void => {
      refusedHere.add(id)
      void update($, approvals, (all) => all.map((view) => (view.id === id ? { ...view, isRefusedHere: true } : view)))
    }
    if (e.surface === 'terminal') {
      const { Box, Text, Button, Link, Raster } = $.ui.resolve(e)
      return drawPane(list, { Box, Text, Button, Link }, rasterCode({ Raster, Text }, e.props.bodyColumns), onRefuse)
    }
    const { Box, Text, Button, Link, Svg } = $.ui.resolve(e)
    return drawPane(list, { Box, Text, Button, Link }, svgCode({ Svg }), onRefuse)
  })
}

// Finds the rule for the event and, when there is one, runs its approval. A rules file that cannot
// be used refuses the event: a broken file must not turn the gate off.
async function guard(
  $: EngineInterface, settings: Settings, match: (rules: readonly Rule[]) => Match | null, signal: AbortSignal,
): Promise<Verdict> {
  let rules: RuleSet
  let found: Match | null
  try {
    rules = await loadRules($, settings)
    found = match(rules.rules)
  } catch (error) {
    return { refusal: refusalText(GateLabel, `Veriqa approval rules are unusable: ${reasonOf(error)}`), label: GateLabel }
  }
  if (found === null) return null
  const { rule, context } = found
  try {
    const project = projectNameOf(await $.session.cwd())
    const slots = resolveSlots(rule.slots, { ...context, project, rule: rule.id })
    await approve($, settings, { label: rule.id, actionType: rule.action_type, slots, chain: chainFor(rules.chains, rule.action_type) }, signal)
    return { refusal: null, label: rule.id }
  } catch (error) {
    return { refusal: refusalText(rule.id, reasonOf(error)), label: rule.id }
  }
}

// Adds the approval note to what the model reads after an action the gate let through; a refusal
// from a hook below goes back as it came.
function withApprovalNote<T extends { deny?: string; context?: readonly string[] }>(answer: T, label: string): T {
  if (answer.deny !== undefined) return answer
  const context: readonly string[] = [...(answer.context ?? []), approvedNoteText(label)]
  return { ...answer, context }
}

// Rules are looked up in this order: the file the settings name, the project's own file, the
// plugin's default. The project's file is optional: only when the path itself is missing the
// default applies. Anything there, a link that leads nowhere included, has to be a readable file,
// like the other two.
async function loadRules($: EngineInterface, settings: Settings): Promise<RuleSet> {
  if (settings.rulesFile) return parseRules(settings.rulesFile, await readRulesFile($, settings.rulesFile))
  const projectFile = `${await $.session.cwd()}/${RulesFile.Project}`
  const isProjectFile = await $.fs.stat(projectFile).then(() => true, (error: unknown) => {
    if (MissingPath.test(messageOf(error))) return false
    throw new GateError(`The rules file ${projectFile} cannot be looked up (${messageOf(error)}).`)
  })
  if (isProjectFile) return parseRules(projectFile, await readRulesFile($, projectFile))
  const defaultFile = `${$.plugin.root}/${RulesFile.Default}`
  return parseRules(defaultFile, await readRulesFile($, defaultFile))
}

async function readRulesFile($: EngineInterface, file: string): Promise<string> {
  try {
    return await $.fs.read(file)
  } catch (error) {
    throw new GateError(`The rules file ${file} cannot be read (${messageOf(error)}).`)
  }
}

/**
 * Runs one approval to its end and resolves only when every step is confirmed; throws a GateError
 * with the reason otherwise. The settings and the chain are checked before anything is sent. The
 * signal, when there is one, is the dispatch of the hook that holds the action: the person
 * interrupting the turn refuses.
 */
async function approve(
  $: EngineInterface, settings: Settings, request: ApprovalRequest, signal: AbortSignal | undefined,
): Promise<{ approvers: Approver[]; access: Access; transactionId: string }> {
  ensureConfigured(settings)
  const approvers = request.chain.length > 0
    ? approversOf(await $.store.get(approversStoreKey(settings.baseUrl)), request.chain, settings.baseUrl)
    : []
  let access = await accessOf($, settings)

  const id = crypto.randomUUID()
  await update($, approvals, (list) => [...list, newApprovalView(id, request)])
  await showPane($)
  try {
    let transactionId = ''
    for (let step = 0; step < Math.max(approvers.length, 1); step++) {
      // Each later step of a chain gets its own token: the earlier one may not outlive the wait.
      if (step > 0) access = await accessOf($, settings)
      transactionId = await approveStep($, settings, access, request, id, approvers, step, signal)
    }
    return { approvers, access, transactionId }
  } finally {
    refusedHere.delete(id)
    const left = await update($, approvals, (list) => list.filter((view) => view.id !== id))
    if (left.length === 0) {
      await $.ui.close({ id: Pane.Id }).catch(() => undefined)
      isPaneShown = false
    }
    $.ui.status(statusText(left, isPaneShown, Pane.Command))
  }
}

// One step: its own transaction, shown in the pane, waited for to the end. A step of a chain names
// the approver's account and is refused when another account confirms it: Veriqa reports whether
// the expected account confirmed (matched_type) but does not refuse on a mismatch.
async function approveStep(
  $: EngineInterface, settings: Settings, access: Access, request: ApprovalRequest, id: string,
  approvers: readonly Approver[], step: number, signal: AbortSignal | undefined,
): Promise<string> {
  const approver = approvers[step]
  const slots = approver ? { ...request.slots, [ApproverSlot]: approver.name } : request.slots
  await changeApproval($, id, (view) => ({ ...view, current: step, slots, entry: null }))
  const transaction = await createConfirmation($, settings, access, {
    actionType: request.actionType,
    slotValues: slots,
    ttlSeconds: settings.ttlSeconds,
    channelTypes: approver ? [approver.channel_type] : settings.channelTypes,
    expectedAccount: approver?.channel_user_id,
  })
  const entry = transaction.channel_entry
  if (!entry?.url) throw new GateError('Veriqa returned no way in for this approval.')
  await changeApproval($, id, (view) => ({
    ...view,
    entry: { url: entry.url, qr: qrMatrixFromPng(entry.qr ?? null), validUntil: entry.valid_until ?? transaction.expires_at },
  }))

  const label = stepLabel(approver, step, approvers.length)
  const { outcome, matchedType } = await waitForOutcome($, settings, access, transaction, id, signal)
  if (outcome !== Outcome.Confirmed) {
    throw new GateError(`Not approved in Veriqa${label ? `: ${label}` : ''}: the outcome is "${outcome}".`)
  }
  if (approver && matchedType !== IdentityType) {
    throw new GateError(`Not approved in Veriqa: ${label} was confirmed by another account than the one enrolled as "${approver.name}".`)
  }
  await changeApproval($, id, (view) => ({
    ...view,
    steps: view.steps.map((one, at) => (at === step ? { ...one, state: 'confirmed' } : one)),
  }))
  return transaction.transaction_id
}

// Polls the result until it is no longer pending or the transaction's deadline has passed. The
// pauses are host commands: the time a hook spends in a call on the engine is not taken from its
// budget, while a sleep on the engine's clock would be. The own host's token may expire before
// the transaction does: a 401 gets a new token once, a second 401 in a row refuses.
async function waitForOutcome(
  $: EngineInterface, settings: Settings, access: Access, transaction: Transaction, id: string, signal: AbortSignal | undefined,
): Promise<{ outcome: string; matchedType: string | null }> {
  const expiresAt = Date.parse(transaction.expires_at)
  if (Number.isNaN(expiresAt)) throw new GateError('The confirmation response has an unreadable expires_at.')
  const deadline = expiresAt + Wait.DeadlineGraceMs
  const isWindows = (await $.env.get('OS')) === WindowsOs
  let headers = access.api
  let isTokenRenewed = false

  for (;;) {
    if (signal?.aborted) throw new GateError(Interrupted)
    if (refusedHere.has(id)) {
      throw new GateError('Not approved: refused with the Refuse button of the approval pane.')
    }
    if (Date.now() >= deadline) return { outcome: Outcome.Expired, matchedType: null }

    const response = await send($, settings.baseUrl + Api.Result(transaction.transaction_id), { headers })
    const isTokenRefused = response.status === HttpStatus.Unauthorized && settings.mode === Mode.OwnHost
    if (isTokenRefused && !isTokenRenewed) {
      headers = (await accessOf($, settings)).api
      isTokenRenewed = true
      continue
    }
    isTokenRenewed = false
    const now = Date.now()
    // An expired transaction is removed, and the result then answers 404 instead of "expired".
    if (response.status === HttpStatus.NotFound && now >= expiresAt) return { outcome: Outcome.Expired, matchedType: null }
    if (response.status === HttpStatus.TooManyRequests) {
      const untilDeadline = Math.ceil((deadline - now) / Wait.MsPerSecond)
      await pause($, isWindows, Math.min(Math.max(retryAfterSeconds(response), Wait.ThrottledPollSeconds), untilDeadline), signal)
      continue
    }
    if (!response.ok) throw failure(response, 'Reading the confirmation result')
    const result = resultOf(response)
    if (result.outcome !== Outcome.Pending) return result
    await pause($, isWindows, Wait.PollSeconds, signal)
  }
}

// A surface that runs no host commands cannot wait, and refuses. A pause cut short because the
// hook's dispatch was aborted is an interruption, not a surface that cannot wait.
async function pause($: EngineInterface, isWindows: boolean, seconds: number, signal: AbortSignal | undefined): Promise<void> {
  const whole = Math.max(1, Math.ceil(seconds))
  const argv = pauseArgv(isWindows, whole)
  let exitCode: number
  try {
    ({ exitCode } = await $.process.run(argv, { timeoutMs: whole * Wait.MsPerSecond + Wait.PauseSlackMs }))
  } catch (error) {
    if (signal?.aborted) throw new GateError(Interrupted)
    throw new GateError(`Not approved: the approval cannot wait on this surface (${messageOf(error)}).`)
  }
  if (exitCode !== 0) {
    throw new GateError(`Not approved: the approval cannot wait on this surface (${argv[0]} exited with ${exitCode}).`)
  }
}

// The pane an approval opens itself waits undrawn on a narrow terminal. The status line alone was
// easy to miss there, so the transcript also gets one line saying how to see the code.
async function showPane($: EngineInterface): Promise<void> {
  const opened = await $.ui.open({ id: Pane.Id, title: Pane.Title }).catch(() => ({ isPlaced: false }))
  isPaneShown = opened.isPlaced
  if (!isPaneShown) $.ui.log(`Veriqa: an approval waits for you. Type /${Pane.Command} to see its QR code.`)
  $.ui.status(statusText(await read($, approvals), isPaneShown, Pane.Command))
}

async function changeApproval($: EngineInterface, id: string, change: (view: ApprovalView) => ApprovalView): Promise<void> {
  const list = await update($, approvals, (all) => all.map((view) => (view.id === id ? change(view) : view)))
  $.ui.status(statusText(list, isPaneShown, Pane.Command))
}

async function send($: EngineInterface, url: string, init: HttpInit): Promise<HttpResponse> {
  try {
    return await $.http.fetch(url, init)
  } catch (error) {
    throw new GateError(`Veriqa at ${originOf(url)} is unreachable (${messageOf(error)}).`)
  }
}

async function accessOf($: EngineInterface, settings: Settings): Promise<Access> {
  return settings.mode === Mode.SharedStand
    ? sharedStandAccess(await installKey($))
    : ownHostAccess(settings, await clientToken($, settings))
}

async function clientToken($: EngineInterface, settings: Settings): Promise<string> {
  const response = await send($, settings.baseUrl + Api.Token, {
    method: 'POST',
    headers: { ...basicAuthHeader(settings), [HttpHeader.ContentType]: ContentType.Form },
    body: clientCredentialsForm(),
  })
  if (!response.ok) throw failure(response, 'Getting a Veriqa token')
  return accessTokenOf(response)
}

async function createConfirmation($: EngineInterface, settings: Settings, access: Access, request: ConfirmationRequest): Promise<Transaction> {
  const response = await send($, settings.baseUrl + Api.Create, {
    method: 'POST',
    headers: { ...access.api, [HttpHeader.ContentType]: ContentType.Json },
    body: confirmationBody(settings, request),
  })
  if (!response.ok) throw failure(response, `Creating the "${request.actionType}" confirmation`)
  return transactionOf(response)
}

// Exchanges a confirmed transaction, once, for the account of the person who confirmed.
async function redeemConfirmation($: EngineInterface, settings: Settings, access: Access, transactionId: string): Promise<Account> {
  const response = await send($, settings.baseUrl + Api.Token, {
    method: 'POST',
    headers: { ...access.token, [HttpHeader.ContentType]: ContentType.Form },
    body: confirmationGrantForm(transactionId),
  })
  if (!response.ok) throw failure(response, 'Learning who confirmed')
  return accountOf(response)
}

// The key of this installation for the shared stand: made once, by the first call that needs it,
// and kept in the plugin's store.
function installKey($: EngineInterface): Promise<string> {
  installKeyInFlight ??= loadOrCreateInstallKey($).finally(() => {
    installKeyInFlight = undefined
  })
  return installKeyInFlight
}

async function loadOrCreateInstallKey($: EngineInterface): Promise<string> {
  const stored = await $.store.get(InstallKey.StoreKey)
  if (typeof stored === 'string' && InstallKey.Pattern.test(stored)) return stored
  const key = installKeyOf(crypto.getRandomValues(new Uint8Array(InstallKey.Bytes)))
  await $.store.set(InstallKey.StoreKey, key)
  return key
}

function toolInput(e: object): Record<string, unknown> {
  return Object.fromEntries(Object.entries(e).filter(([field]) => !ReservedToolFields.has(field)))
}

// The tool's input as its schema states it, or the reason it is refused.
function parseRunInput(e: object): { run: string; inputMtok: number; outputMtok: number } | string {
  const { run, input_mtok: inputMtok, output_mtok: outputMtok } = e as Record<string, unknown>
  if (typeof run !== 'string' || run.trim() === '' || run.length > ApproveRun.MaxRunLength) {
    return `"run" has to be a name of 1 to ${ApproveRun.MaxRunLength} characters.`
  }
  const isCount = (value: unknown): value is number => typeof value === 'number' && Number.isFinite(value) && value >= 0
  if (!isCount(inputMtok) || !isCount(outputMtok)) return '"input_mtok" and "output_mtok" have to be numbers of 0 or more.'
  return { run: run.trim(), inputMtok, outputMtok }
}
