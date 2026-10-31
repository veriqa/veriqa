---
name: costly-run
description: Demo of a costly run that starts only after a person approves its token estimate in Veriqa. Use when the user asks to start a costly or long agent run and wants approval of its estimated token spend first.
argument-hint: "<what to run> [input=<millions of input tokens>] [output=<millions of output tokens>]"
disable-model-invocation: true
---

# Costly run with an approved estimate

The user asked for: `$ARGUMENTS`

This run is expensive, so it may start only after a person approves its estimate in the messenger.
Follow the steps in order and do not skip the approval.

## 1. Estimate

Estimate the token spend of the whole run, in **millions of tokens**:

- if the arguments state `input=` and `output=`, use those numbers as they are;
- otherwise use the demo estimate: **1000** million input tokens (1 billion) and **50** million
  output tokens.

Give the run a short name, at most 64 characters, from what the user asked for.

## 2. Ask for approval

Call the tool `mcp__veriqa-approval__approve_run` exactly once, with:

- `run` — the name of the run;
- `input_mtok` — the input estimate, a plain number;
- `output_mtok` — the output estimate, a plain number.

Do not add the cost yourself: the plugin computes it from the estimate and the configured prices and
puts it into the question.

The run needs two approvers in turn, `developer` and then `manager`, each from their own enrolled
messenger account. The plugin shows the QR code of the approver who is due in its own pane and waits
for both answers within this one call: there is nothing to show and nothing to repeat.

## 3. Act on the answer

- The tool succeeds with a result that starts with `APPROVED` — start the run.
- The tool answers with an error (`NOT APPROVED` or any other) — **do not start the run**, not even
  a smaller version of it. Print the reason from the error and stop. Do not call the tool again,
  do not ask another way and do not work around the refusal.

## 4. The run

This is a demo: in place of the real work, say that the run would start now and repeat the approved
estimate, including `cost_usd` from the `Asked:` part of the tool result. A real command replaces
this step with its own work and keeps steps 1–3 as they are.
