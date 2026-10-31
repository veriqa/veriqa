# Veriqa sample — .NET · inproc · approving an AI agent's action

Runnable sample of **human-in-the-loop approval**: a support agent handles a refund ticket with three
tools known in advance. Two of them run on their own; `refund_payment` moves money, so every call of it
waits until a person approves it in Telegram ("The support agent wants to refund 89.00 EUR for order
A-1042: The headphones arrived broken. Allow it?").

| Tool | Approval |
|---|---|
| `lookup_order` | none — it only reads |
| `refund_payment` | every call, action type `approve-refund` |
| `send_customer_email` | none in this sample |

The application is both the Veriqa issuer (inproc) and the backend that runs the agent and calls
Veriqa over the public HTTP API, as in the [confirmation sample](../confirmation/README.md).

## How the approval is enforced

`SupportAgent.cs` holds the pattern:

- **`ApprovalPolicy`** on a tool: which declared action type asks the person, and how the arguments of
  the call become its slot values. The person reads those values, never text the agent wrote.
- **`SupportAgent.InvokeAsync`** is the one path every tool call takes. A tool with a policy goes
  through **`ApprovalGate`**: it creates the confirmation, exposes its QR and link, and polls the
  result. The tool runs only on `confirmed`; `declined` or `expired` come back to the agent as the
  result of the call — the way a harness reports a refused call to the model.
- The person who approved (`sub` of the exchanged `id_token`) goes into the run log.

The agent follows a fixed plan instead of asking a model, so the sample needs no model and no API key.
With a real model the gate stays where it is: whatever the model decides to call passes through
`InvokeAsync`, so no model output can skip the approval.

In production the way in reaches the approver instead of the agent's screen — a link sent to the
on-call manager, say. The API returns it; delivering it is up to you.

## The configuration that makes it work

The client entry `support-agent` in `appsettings.json`:

- `AllowClientCredentials: true` — the backend may call the API (needs a `ClientSecret`);
- `AllowConfirmationTokenGrant: true` and `openid` in `AllowedScopes` — to record who approved;
- `MessageTemplates:approve-refund` — the action type: `Contract:Slots` (`amount`, `order`, `reason`)
  and `Templates`.

Beside the client entry, the host section of `appsettings.json` words what the user sees when the
transaction ends:

- `Veriqa:Channels:OutcomeNotice:DisplayIntent: "NewMessage"` — the receipt of the outcome arrives as
  a message of its own instead of replacing the question. The shipped value is `ReplacePrompt`; a
  confirmation is the case for the other one, because the question carries the wording of the action
  and replacing it would leave the conversation with an outcome and no record of what it answered;
- `Veriqa:MessageTemplates:outcome-receipt-confirmed` / `…-declined` — the receipts name the calling
  application through the server slot `{app}`, which for a confirmation is the `ClientId`.

Each tool that needs approval gets its own action type. The client secret for Development is in
`appsettings.Development.json`, on both sides (`Veriqa:OpenIddict:Clients` and `AgentApprovalSample`).
Never ship it.

## Prerequisites

- .NET SDK 10.0+
- A trusted development certificate — the backend calls `https://localhost:7340`:

  ```bash
  dotnet dev-certs https --trust
  ```

- A Telegram bot token from [@BotFather](https://t.me/BotFather).

## Running

```bash
dotnet user-secrets --project samples/dotnet/inproc/agent-approval set "Veriqa:Channels:Telegram:BotToken" "<token>"
dotnet user-secrets --project samples/dotnet/inproc/agent-approval set "Veriqa:Channels:Telegram:Enabled" "true"
dotnet run --project samples/dotnet/inproc/agent-approval
```

Open `https://localhost:7340` and press **Hand the ticket to the agent**. The log shows the lookup,
then the agent stops at `refund_payment` and the page shows a QR. Approve in Telegram: the refund runs
and the customer gets the "refund is on its way" e-mail. Run it again and decline: the refund is not
executed, and the agent tells the customer a colleague will contact them.

## Further reading

- Approving AI-agent actions — <https://veriqa.app/docs/scenarios/agent-approvals>
- Server-to-server confirmation API — <https://veriqa.app/docs/reference/confirmation-api>
