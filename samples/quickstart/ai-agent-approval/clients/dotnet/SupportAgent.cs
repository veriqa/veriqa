// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;

namespace Veriqa.Sample.Quickstart.AgentApproval;

/// <summary>Arguments of one tool call, in the order they are logged.</summary>
public sealed class ToolArguments(params (string Name, string Value)[] pairs)
{
    /// <summary>The value of an argument.</summary>
    public string this[string name] => pairs.First(pair => pair.Name == name).Value;

    /// <inheritdoc />
    public override string ToString() => string.Join(", ", pairs.Select(pair => $"{pair.Name}: {pair.Value}"));
}

/// <summary>
/// The approval policy of a tool: which declared action type asks the person, and how the arguments
/// become its slot values. The person reads the slots, never text the agent wrote.
/// </summary>
public sealed record ApprovalPolicy(string ActionType, Func<ToolArguments, IReadOnlyDictionary<string, string>> Slots);

/// <summary>A tool the agent may call; <paramref name="Approval"/> null — it runs without asking.</summary>
public sealed record AgentTool(string Name, ApprovalPolicy? Approval, Func<ToolArguments, string> Execute);

/// <summary>A refund ticket as the page sends it.</summary>
public sealed record RefundTicket(string Order, string Amount, string Reason);

/// <summary>One line of the agent log.</summary>
public sealed record AgentLogEntry(string At, string Kind, string Text);

/// <summary>The approval that holds a run, as the page shows it.</summary>
public sealed record PendingApproval(string Tool, string Url, string? Qr);

/// <summary>What the page reads about a run.</summary>
public sealed record AgentRunSnapshot(string Id, IReadOnlyList<AgentLogEntry> Log, PendingApproval? Pending, string? Completion);

/// <summary>
/// One run of the agent over a ticket. Every step is driven by a request of the page: the run is
/// started by one and moved on by every read, so no background work is needed.
/// </summary>
public sealed class AgentRun(RefundTicket ticket)
{
    private readonly List<AgentLogEntry> _log = [];

    /// <summary>The run id.</summary>
    public string Id { get; } = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));

    /// <summary>The ticket the agent works on.</summary>
    public RefundTicket Ticket { get; } = ticket;

    /// <summary>Serialises the steps of the run: two reads at once must not answer one approval twice.</summary>
    public SemaphoreSlim Gate { get; } = new(1, 1);

    /// <summary>The held call and its transaction; null — nothing waits for a person.</summary>
    public (AgentTool Tool, ToolArguments Arguments, string TransactionId, PendingApproval View)? Waiting { get; set; }

    /// <summary>How the run ended; null while it goes on.</summary>
    public string? Completion { get; set; }

    /// <summary>Adds a log line stamped with the local time.</summary>
    public void Log(string kind, string text) =>
        _log.Add(new AgentLogEntry(DateTimeOffset.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture), kind, text));

    /// <summary>The state of the run for the page.</summary>
    public AgentRunSnapshot Snapshot() => new(Id, [.. _log], Waiting?.View, Completion);
}

/// <summary>
/// The support agent: three tools known in advance, as in any agent a team builds for one job. The
/// approval gate sits in the tool-invocation path, not in a prompt, so nothing the model writes can
/// skip it. The agent itself is scripted here — the point is the gate, not the model.
/// </summary>
public sealed class SupportAgent(VeriqaClient veriqa)
{
    private const string LookupOrder = "lookup_order";
    private const string RefundPayment = "refund_payment";
    private const string SendCustomerEmail = "send_customer_email";

    private readonly ConcurrentDictionary<string, (string Item, string Amount, string Customer)> _orders = new(StringComparer.Ordinal)
    {
        ["A-1042"] = ("Wireless headphones", "89.00 EUR", "anna@example.com"),
        ["A-1043"] = ("Standing desk", "420.00 EUR", "ben@example.com")
    };

    private readonly ConcurrentDictionary<string, bool> _refunded = new(StringComparer.Ordinal);

    private Dictionary<string, AgentTool>? _tools;

    // The tools, by name. Each one is plain code; the ones with consequences carry an approval policy.
    private Dictionary<string, AgentTool> Tools => _tools ??= new(StringComparer.Ordinal)
    {
        // Reading is harmless: no approval.
        [LookupOrder] = new(LookupOrder, null, args =>
            _orders.TryGetValue(args["order"], out var order)
                ? $"Order {args["order"]}: {order.Item}, paid {order.Amount}, customer {order.Customer}, "
                    + (_refunded.ContainsKey(args["order"]) ? "already refunded." : "not refunded.")
                : $"Order {args["order"]} does not exist."),

        // Moving money is not: a person approves every call, with the amount and the order in front of them.
        [RefundPayment] = new(
            RefundPayment,
            new ApprovalPolicy("approve-refund", args => new Dictionary<string, string>
            {
                ["amount"] = args["amount"],
                ["order"] = args["order"],
                ["reason"] = args["reason"]
            }),
            args =>
            {
                _refunded[args["order"]] = true;
                return $"Refunded {args["amount"]} for order {args["order"]}.";
            }),

        // Writing to the customer about a decision already taken: no approval in this sample.
        [SendCustomerEmail] = new(SendCustomerEmail, null, args =>
            $"E-mail sent to the customer of order {args["order"]}: \"{args["text"]}\""),
    };

    /// <summary>Starts a run: the agent reads the order and stops at the refund until a person answers.</summary>
    public async Task<AgentRun> StartAsync(RefundTicket ticket, CancellationToken cancellationToken)
    {
        var run = new AgentRun(ticket);
        run.Log("agent", $"Ticket: the customer asks to refund order {ticket.Order} — \"{ticket.Reason}\".");
        await StepAsync(run, async () =>
        {
            await InvokeAsync(run, LookupOrder, new(("order", ticket.Order)), cancellationToken);
            await InvokeAsync(
                run,
                RefundPayment,
                new(("order", ticket.Order), ("amount", ticket.Amount), ("reason", ticket.Reason)),
                cancellationToken);
        });
        return run;
    }

    /// <summary>
    /// Moves a run on: reads the approval that holds it once and, when a person has answered, lets the
    /// agent go on with its next tool.
    /// </summary>
    public async Task AdvanceAsync(AgentRun run, CancellationToken cancellationToken)
    {
        await run.Gate.WaitAsync(cancellationToken);
        try
        {
            if (run.Waiting is not { } waiting)
            {
                return;
            }

            await StepAsync(run, async () =>
            {
                var result = await veriqa.GetResultAsync(waiting.TransactionId, cancellationToken);
                if (result.Outcome == ConfirmationResult.Pending)
                {
                    return;
                }

                var executed = false;
                if (result.Outcome == ConfirmationResult.Confirmed)
                {
                    // Who approved goes into the agent's own log — the audit of the run.
                    var claims = await veriqa.ExchangeForClaimsAsync(waiting.TransactionId, cancellationToken);
                    run.Log("approval", $"Approved by {claims.GetProperty("sub").GetString()}.");
                    run.Log("result", waiting.Tool.Execute(waiting.Arguments));
                    executed = true;
                }
                else
                {
                    run.Log("result", $"Not executed: the approval ended as {result.Outcome}.");
                }

                run.Waiting = null;
                var text = executed
                    ? $"Your refund of {run.Ticket.Amount} is on its way."
                    : "We could not approve a refund right now; a colleague will contact you.";
                await InvokeAsync(run, SendCustomerEmail, new(("order", run.Ticket.Order), ("text", text)), cancellationToken);
                run.Completion = "Done.";
            });
        }
        finally
        {
            run.Gate.Release();
        }
    }

    // A failed call to Veriqa stops the run: an approval that cannot be asked or read is not a yes.
    private static async Task StepAsync(AgentRun run, Func<Task> step)
    {
        try
        {
            await step();
        }
        catch (VeriqaCallException refused)
        {
            run.Waiting = null;
            run.Completion = $"Stopped: {refused.Message}";
        }
        catch (HttpRequestException unreachable)
        {
            run.Waiting = null;
            run.Completion = $"Stopped: {unreachable.Message}";
        }
    }

    // Calls a tool. A tool with an approval policy does not run: the call is held, a confirmation is
    // created, and the run waits for the person.
    private async Task InvokeAsync(AgentRun run, string name, ToolArguments arguments, CancellationToken cancellationToken)
    {
        var tool = Tools[name];
        run.Log("call", $"{tool.Name}({arguments})");

        if (tool.Approval is null)
        {
            run.Log("result", tool.Execute(arguments));
            return;
        }

        run.Log("approval", $"{tool.Name} needs a person's approval — waiting.");
        var created = await veriqa.CreateAsync(tool.Approval.ActionType, tool.Approval.Slots(arguments), null, cancellationToken);
        run.Waiting = (tool, arguments, created.TransactionId,
            new PendingApproval(tool.Name, created.ChannelEntry.Url, created.ChannelEntry.Qr));
    }
}
