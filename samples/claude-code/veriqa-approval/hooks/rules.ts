// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT
//
// The rules that decide which tool calls, slash commands and prompts need a person's approval,
// and what the person is asked.

import { GateError, messageOf } from './gate'

export const RuleEvent = { ToolCall: 'tool.call', CommandRun: 'command.run', PromptSubmit: 'prompt.submit' } as const
export type RuleEvent = (typeof RuleEvent)[keyof typeof RuleEvent]

// Rules are looked up in this order: the file the settings name, the project's own file under the
// session's directory, the plugin's default under its root.
export const RulesFile = { Project: '.claude/veriqa-approval.rules.json', Default: 'rules.default.json' }

export type Rule = {
  id: string
  event: RuleEvent
  action_type: string
  slots: Record<string, string>
  /** tool.call: the tool name, and patterns over the fields of its input. */
  tool?: RegExp
  input?: Record<string, RegExp>
  /** command.run: the command name without its slash, and its arguments. */
  command?: RegExp
  args?: RegExp
  /** prompt.submit: the prompt text; its capture groups become {match.N}. */
  prompt?: RegExp
}

export type RuleSet = {
  rules: Rule[]
  /** Approver names per action type, in the order they approve. */
  chains: Record<string, unknown>
}

/** A rule that matched, and what its slot templates are filled from. */
export type Match = { rule: Rule; context: Record<string, unknown> }

/**
 * Parses a rules file. A file that cannot be parsed or understood throws: the caller refuses every
 * guarded event rather than turning the gate off.
 */
export function parseRules(file: string, text: string): RuleSet {
  let parsed: unknown
  try {
    parsed = JSON.parse(text)
  } catch (error) {
    throw new GateError(`The rules file ${file} is not JSON (${messageOf(error)}).`)
  }
  const root = asRecord(parsed)
  if (!root || !Array.isArray(root.rules)) throw new GateError(`The rules file ${file} has no "rules" array.`)
  return {
    rules: root.rules.map((raw, at) => parseRule(file, raw, at)),
    chains: asRecord(root.chains) ?? {},
  }
}

function parseRule(file: string, raw: unknown, at: number): Rule {
  const fields = asRecord(raw)
  const where = `rule ${at + 1} of ${file}`
  if (!fields || typeof fields.id !== 'string' || fields.id === '') throw new GateError(`The ${where} has no "id".`)
  const events: readonly unknown[] = Object.values(RuleEvent)
  if (!events.includes(fields.event)) {
    throw new GateError(`The rule "${fields.id}" in ${file} has the event "${String(fields.event)}"; the events are ${events.join(', ')}.`)
  }
  if (typeof fields.action_type !== 'string' || fields.action_type === '') {
    throw new GateError(`The rule "${fields.id}" in ${file} has no "action_type".`)
  }
  const pattern = (name: string): RegExp | undefined => toPattern(fields.id as string, name, fields[name])
  const input = asRecord(fields.input)
  return {
    id: fields.id,
    event: fields.event as RuleEvent,
    action_type: fields.action_type,
    slots: Object.fromEntries(Object.entries(asRecord(fields.slots) ?? {}).map(([slot, template]) => [slot, String(template)])),
    tool: pattern('tool'),
    input: input
      ? Object.fromEntries(Object.entries(input).map(([field, value]) => [field, toPattern(fields.id as string, `input.${field}`, value) as RegExp]))
      : undefined,
    command: pattern('command'),
    args: pattern('args'),
    prompt: pattern('prompt'),
  }
}

function toPattern(ruleId: string, name: string, value: unknown): RegExp | undefined {
  if (value === undefined) return undefined
  try {
    return new RegExp(String(value))
  } catch (error) {
    throw new GateError(`The rule "${ruleId}" has an invalid "${name}" pattern (${messageOf(error)}).`)
  }
}

/** The first tool.call rule whose tool pattern and every input pattern match. */
export function matchToolCall(rules: readonly Rule[], tool: string, toolInput: Record<string, unknown>): Match | null {
  const input = normalizeToolInput(tool, toolInput)
  for (const rule of rules) {
    if (rule.event !== RuleEvent.ToolCall || !test(rule.tool, tool)) continue
    if (!Object.entries(rule.input ?? {}).every(([field, pattern]) => pattern.test(stringify(input[field])))) continue
    return { rule, context: { tool, input } }
  }
  return null
}

/** The first command.run rule whose command pattern, and arguments pattern if any, match. */
export function matchCommand(rules: readonly Rule[], command: string, args: string): Match | null {
  for (const rule of rules) {
    if (rule.event !== RuleEvent.CommandRun || !test(rule.command, command) || !test(rule.args, args)) continue
    return { rule, context: { command, args } }
  }
  return null
}

/**
 * The first prompt.submit rule whose prompt pattern matches; its capture groups become {match.N}.
 * A rule without a prompt pattern matches every prompt, as a missing pattern does for any field.
 */
export function matchPrompt(rules: readonly Rule[], text: string): Match | null {
  for (const rule of rules) {
    if (rule.event !== RuleEvent.PromptSubmit) continue
    const match = rule.prompt === undefined ? [text] : rule.prompt.exec(text)
    if (match) return { rule, context: { prompt: text, match: { ...match } } }
  }
  return null
}

// A rule without a pattern for a field matches any value of it.
function test(pattern: RegExp | undefined, value: string): boolean {
  return pattern === undefined || pattern.test(value)
}

// The Skill tool has named its input field differently across Claude Code versions; both are read
// so that a rule can always say "skill".
const SkillTool = 'Skill'

function normalizeToolInput(tool: string, toolInput: Record<string, unknown>): Record<string, unknown> {
  const input = { ...toolInput }
  if (tool === SkillTool) {
    input.skill ??= input.command
    input.args ??= ''
  }
  return input
}

/**
 * Fills "{input.command}"-style placeholders from the context. An empty value is left out of the
 * request, so the wording ladder falls back to a step that does not use that slot.
 */
export function resolveSlots(slotMap: Record<string, string>, context: Record<string, unknown>): Record<string, string> {
  const values: Record<string, string> = {}
  for (const [slot, template] of Object.entries(slotMap)) {
    const value = template.replace(/\{([\w.]+)\}/g, (_, path: string) => stringify(lookup(context, path))).trim()
    if (value !== '') values[slot] = value
  }
  return values
}

/**
 * The approvers an action type needs, in order. No chain means one step that anybody holding
 * the link may confirm.
 */
export function chainFor(chains: Record<string, unknown>, actionType: string): string[] {
  const chain = chains[actionType]
  if (chain === undefined) return []
  if (!Array.isArray(chain) || chain.some((name) => typeof name !== 'string' || name === '')) {
    throw new GateError(`The approval chain of "${actionType}" is not a list of approver names.`)
  }
  return chain as string[]
}

/** The name of the session's directory, for the {project} placeholder. */
export function projectNameOf(cwd: string): string {
  return cwd.split(/[\\/]+/).filter(Boolean).pop() ?? ''
}

function lookup(context: Record<string, unknown>, path: string): unknown {
  return path.split('.').reduce<unknown>((node, key) => (asRecord(node) ?? {})[key], context)
}

function stringify(value: unknown): string {
  if (value === undefined || value === null) return ''
  return typeof value === 'string' ? value : JSON.stringify(value)
}

function asRecord(value: unknown): Record<string, unknown> | null {
  return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as Record<string, unknown>) : null
}
