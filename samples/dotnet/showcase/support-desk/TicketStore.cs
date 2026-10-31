// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace Veriqa.Sample.DotNet.Showcase.SupportDesk;

/// <summary>Where a ticket stands.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TicketStatus>))]
public enum TicketStatus
{
    /// <summary>Waiting for the support desk.</summary>
    [JsonStringEnumMemberName("open")]
    Open,

    /// <summary>Waiting for the customer.</summary>
    [JsonStringEnumMemberName("waiting")]
    Waiting,

    /// <summary>Closed.</summary>
    [JsonStringEnumMemberName("resolved")]
    Resolved
}

/// <summary>What the customer asked to do with a ticket.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TicketAction>))]
public enum TicketAction
{
    /// <summary>Close it.</summary>
    [JsonStringEnumMemberName(SampleActionNames.Resolve)]
    Resolve,

    /// <summary>Remove it from the list for good.</summary>
    [JsonStringEnumMemberName(SampleActionNames.Delete)]
    Delete
}

/// <summary>How the customer asked the desk to get in touch about a ticket.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ContactMethod>))]
public enum ContactMethod
{
    /// <summary>A callback to the confirmed phone number.</summary>
    [JsonStringEnumMemberName(SampleContactMethods.Callback)]
    Callback,

    /// <summary>An answer to the confirmed e-mail address.</summary>
    [JsonStringEnumMemberName(SampleContactMethods.Email)]
    Email
}

/// <summary>
/// A request to get in touch, as it stays with the ticket. The destination is part of the request,
/// not of the session: it is still here after a sign-out, when the session has no contacts left.
/// </summary>
/// <param name="Method">How the desk should get in touch.</param>
/// <param name="Destination">Where to, in the form the page shows: a phone number is kept masked.</param>
public sealed record TicketContactRequest(ContactMethod Method, string Destination);

/// <summary>What the backend decided about an operation once its confirmation ended.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ResolutionState>))]
public enum ResolutionState
{
    /// <summary>The confirmation has not ended yet.</summary>
    [JsonStringEnumMemberName("pending")]
    Pending,

    /// <summary>Confirmed by the customer who asked: the operation was applied.</summary>
    [JsonStringEnumMemberName("applied")]
    Applied,

    /// <summary>Declined, expired or failed: the ticket is untouched.</summary>
    [JsonStringEnumMemberName("refused")]
    Refused,

    /// <summary>Confirmed, but not by the customer who asked: the ticket is untouched.</summary>
    [JsonStringEnumMemberName("mismatched")]
    Mismatched
}

/// <summary>One ticket of the customer.</summary>
/// <param name="Id">Identifier shown to the customer and used in the URLs.</param>
/// <param name="Subject">One-line summary.</param>
/// <param name="Status">Where it stands.</param>
/// <param name="CreatedAt">When it was opened.</param>
/// <param name="LastMessage">Last line of the conversation.</param>
/// <param name="ContactRequest">How the customer asked to be contacted about it, or null.</param>
public sealed record Ticket(
    string Id,
    string Subject,
    TicketStatus Status,
    DateTimeOffset CreatedAt,
    string LastMessage,
    TicketContactRequest? ContactRequest);

/// <summary>An operation waiting for its confirmation, or already decided.</summary>
/// <param name="TransactionId">The confirmation transaction.</param>
/// <param name="TicketId">The ticket it acts on.</param>
/// <param name="Action">What it does to the ticket.</param>
/// <param name="State">What the backend did; <see cref="ResolutionState.Pending"/> until the confirmation ends.</param>
/// <param name="Outcome">Outcome of the confirmation: <c>pending</c> until it ends.</param>
public sealed record ResolutionView(
    string TransactionId,
    string TicketId,
    TicketAction Action,
    ResolutionState State,
    string Outcome);

/// <summary>
/// The tickets of the customer, and the operations in flight against them.
/// <para>
/// The list is not stored — it is SYNTHESISED from the <c>sub</c> claim by a pure function, so the
/// same person sees the same tickets on every visit without the sample carrying a database. What IS
/// stored is the difference the customer has made since: which tickets they closed, which they
/// deleted, and how they asked to be contacted. A restart of the process forgets that difference
/// and the list returns to its synthesised state, which is exactly what a sample should do.
/// </para>
/// </summary>
public sealed class TicketStore
{
    /// <summary>Outcome of a confirmation that has not ended yet.</summary>
    public const string PendingOutcome = "pending";

    /// <summary>Fewest tickets a customer is given.</summary>
    private const int MinTickets = 4;

    /// <summary>Most tickets a customer is given.</summary>
    private const int MaxTickets = 7;

    /// <summary>Highest ticket number the desk has ever issued.</summary>
    private const int HighestTicketNumber = 1042;

    /// <summary>
    /// How far below <see cref="HighestTicketNumber"/> one customer's newest ticket may sit. Without
    /// it every customer's newest ticket would carry the same number, which no real desk does.
    /// </summary>
    private const int NewestTicketNumberSpread = 300;

    /// <summary>Prefix of a ticket identifier.</summary>
    private const string TicketIdPrefix = "A-";

    /// <summary>
    /// The moment the synthesised dates are counted back from. A constant rather than the clock: a
    /// list built from the current time would change between two reads, and the whole point of the
    /// synthesis is that it does not.
    /// </summary>
    private static readonly DateTimeOffset DateAnchor = new(2026, 9, 15, 9, 0, 0, TimeSpan.Zero);

    /// <summary>Longest gap, in days, between the anchor and the oldest synthesised ticket.</summary>
    private const int MaxTicketAgeDays = 90;

    /// <summary>Largest step between the numbers of two neighbouring tickets of one customer.</summary>
    private const int MaxTicketNumberGap = 9;

    private static readonly string[] Subjects =
    [
        "Invoice mismatch on the September statement",
        "Password reset did not arrive",
        "Cannot change the billing address",
        "Duplicate charge for one order",
        "Delivery window needs moving",
        "Account name spelled wrong",
        "Subscription renews on the wrong date",
        "Refund still not visible",
        "Two-factor code never reaches me",
        "Export of the order history fails"
    ];

    private static readonly string[] LastMessages =
    [
        "Thanks — we have passed this to the billing team.",
        "Could you confirm the last four digits of the card?",
        "We reproduced it and a fix is on the way.",
        "The change is applied; please check on your side.",
        "We are waiting for the carrier to answer.",
        "Sorry about that — a correction is already queued."
    ];

    private static readonly TicketStatus[] Statuses =
    [
        TicketStatus.Open,
        TicketStatus.Open,
        TicketStatus.Waiting,
        TicketStatus.Resolved
    ];

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CustomerChanges> _changesBySubject = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Resolution> _resolutions = new(StringComparer.Ordinal);

    /// <summary>The tickets of this customer: the synthesised list with their own changes applied.</summary>
    /// <param name="subject">The <c>sub</c> claim of the customer.</param>
    public IReadOnlyList<Ticket> TicketsFor(string subject)
    {
        lock (_gate)
        {
            var changes = _changesBySubject.GetValueOrDefault(subject);

            return
            [
                .. Synthesize(subject)
                    .Where(ticket => changes?.Deleted.Contains(ticket.Id) is not true)
                    .Select(ticket => changes is null
                        ? ticket
                        : ticket with
                        {
                            Status = changes.Resolved.Contains(ticket.Id) ? TicketStatus.Resolved : ticket.Status,
                            ContactRequest = changes.ContactRequests.GetValueOrDefault(ticket.Id)
                        })
            ];
        }
    }

    /// <summary>
    /// A ticket of this customer by id, or null. A ticket belonging to somebody else simply does not
    /// exist here: the list is derived from the asking customer's own <c>sub</c>.
    /// </summary>
    /// <param name="subject">The <c>sub</c> claim of the customer.</param>
    /// <param name="ticketId">Identifier of the ticket.</param>
    public Ticket? Find(string subject, string ticketId) =>
        TicketsFor(subject).FirstOrDefault(ticket => ticket.Id == ticketId);

    /// <summary>
    /// Remembers how the customer asked to be contacted about a ticket, and where. Asking again
    /// replaces the earlier request: the last one wins.
    /// </summary>
    /// <param name="subject">The <c>sub</c> claim of the customer.</param>
    /// <param name="ticketId">The ticket the request is about.</param>
    /// <param name="request">The method and the destination, the latter as the page shows it.</param>
    public void RequestContact(string subject, string ticketId, TicketContactRequest request)
    {
        lock (_gate)
        {
            ChangesOf(subject).ContactRequests[ticketId] = request;
        }
    }

    /// <summary>Remembers which ticket a confirmation transaction acts on, for whom, and how.</summary>
    /// <param name="transactionId">The confirmation transaction.</param>
    /// <param name="ticketId">The ticket it acts on.</param>
    /// <param name="subject">The <c>sub</c> claim of the customer who started it.</param>
    /// <param name="action">What it does to the ticket.</param>
    public void StartResolution(string transactionId, string ticketId, string subject, TicketAction action)
    {
        lock (_gate)
        {
            _resolutions[transactionId] = new Resolution(transactionId, ticketId, subject, action, PendingOutcome, null);
        }
    }

    /// <summary>
    /// An operation of THIS customer by its transaction, or null — a transaction started by someone
    /// else is answered the same way as one that never existed.
    /// </summary>
    /// <param name="transactionId">The confirmation transaction.</param>
    /// <param name="subject">The <c>sub</c> claim of the customer asking.</param>
    public ResolutionView? FindResolution(string transactionId, string subject)
    {
        lock (_gate)
        {
            var resolution = _resolutions.GetValueOrDefault(transactionId);

            return resolution is null || !string.Equals(resolution.Subject, subject, StringComparison.Ordinal)
                ? null
                : View(resolution);
        }
    }

    /// <summary>
    /// Records the verdict of an operation once, and applies it to the ticket when the verdict says
    /// so. An operation already decided keeps its first verdict, so polling it again costs Veriqa
    /// nothing.
    /// </summary>
    /// <param name="transactionId">The confirmation transaction.</param>
    /// <param name="outcome">Outcome Veriqa reported.</param>
    /// <param name="state">What the backend decided to do about it.</param>
    /// <returns>The operation as the page sees it.</returns>
    public ResolutionView Conclude(string transactionId, string outcome, ResolutionState state)
    {
        lock (_gate)
        {
            var resolution = _resolutions[transactionId];
            if (resolution.State is not null)
            {
                return View(resolution);
            }

            if (state is ResolutionState.Applied)
            {
                var changes = ChangesOf(resolution.Subject);

                // Applying is idempotent: two operations may be confirmed for one ticket, and the
                // second then asks for an end state the first has already reached.
                if (resolution.Action is TicketAction.Delete)
                {
                    changes.Deleted.Add(resolution.TicketId);
                }
                else
                {
                    changes.Resolved.Add(resolution.TicketId);
                }
            }

            resolution = resolution with { Outcome = outcome, State = state };
            _resolutions[transactionId] = resolution;

            return View(resolution);
        }
    }

    /// <summary>The changes of one customer, created on their first change. Called under the gate.</summary>
    /// <param name="subject">The <c>sub</c> claim of the customer.</param>
    private CustomerChanges ChangesOf(string subject)
    {
        if (!_changesBySubject.TryGetValue(subject, out var changes))
        {
            changes = new CustomerChanges();
            _changesBySubject[subject] = changes;
        }

        return changes;
    }

    /// <summary>
    /// Builds the tickets of one customer — a pure function of their <c>sub</c> and of the constant
    /// tables above, with no clock and no state behind it. A real service reads this from its
    /// database; the synthesis exists so the sample has a believable list without shipping one.
    /// </summary>
    /// <param name="subject">The <c>sub</c> claim of the customer.</param>
    /// <returns>Their tickets, newest first.</returns>
    private static List<Ticket> Synthesize(string subject)
    {
        var random = new DeterministicSequence(StableHash(subject));
        var count = MinTickets + random.Next(MaxTickets - MinTickets + 1);
        var drafts = new List<Ticket>(count);

        for (var index = 0; index < count; index++)
        {
            drafts.Add(new Ticket(
                string.Empty,
                Subjects[random.Next(Subjects.Length)],
                Statuses[random.Next(Statuses.Length)],
                DateAnchor.AddDays(-random.Next(MaxTicketAgeDays)).AddHours(-random.Next(24)),
                LastMessages[random.Next(LastMessages.Length)],
                ContactRequest: null));
        }

        // Numbers are handed out after the sort, counting down from the newest ticket: they then read
        // the way a real desk numbers its tickets — the higher the number, the later it was opened.
        // Counting DOWN by a gap of at least one also makes the numbers unique by construction, which
        // drawing each one independently would not.
        var ordered = drafts.OrderByDescending(ticket => ticket.CreatedAt).ToList();
        var number = HighestTicketNumber - random.Next(NewestTicketNumberSpread);

        for (var index = 0; index < ordered.Count; index++)
        {
            ordered[index] = ordered[index] with
            {
                Id = TicketIdPrefix + number.ToString(CultureInfo.InvariantCulture)
            };

            number -= 1 + random.Next(MaxTicketNumberGap);
        }

        return ordered;
    }

    /// <summary>
    /// FNV-1a over the UTF-8 bytes of the subject. <c>string.GetHashCode</c> would not do: it is
    /// randomised per process, so the list would change on every restart — and the one promise this
    /// synthesis makes is that it does not.
    /// </summary>
    /// <param name="value">The value to hash.</param>
    private static uint StableHash(string value)
    {
        const uint OffsetBasis = 2166136261;
        const uint Prime = 16777619;

        var hash = OffsetBasis;
        foreach (var octet in Encoding.UTF8.GetBytes(value))
        {
            hash = (hash ^ octet) * Prime;
        }

        return hash;
    }

    /// <summary>The wire shape of an operation: a verdict not yet reached reads as pending.</summary>
    private static ResolutionView View(Resolution resolution) =>
        new(resolution.TransactionId,
            resolution.TicketId,
            resolution.Action,
            resolution.State ?? ResolutionState.Pending,
            resolution.Outcome);

    /// <summary>
    /// What one customer has changed since their list was synthesised. Sets rather than a rewritten
    /// list: the synthesis stays a pure function, and the difference is kept beside it.
    /// </summary>
    private sealed class CustomerChanges
    {
        /// <summary>Tickets the customer has closed.</summary>
        public HashSet<string> Resolved { get; } = new(StringComparer.Ordinal);

        /// <summary>Tickets the customer has deleted.</summary>
        public HashSet<string> Deleted { get; } = new(StringComparer.Ordinal);

        /// <summary>How the customer asked to be contacted, by ticket.</summary>
        public Dictionary<string, TicketContactRequest> ContactRequests { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>
    /// An operation as the store keeps it. <c>Subject</c> stays here and never reaches the page, and
    /// a null <c>State</c> is what "still waiting" looks like inside the store.
    /// </summary>
    private sealed record Resolution(
        string TransactionId,
        string TicketId,
        string Subject,
        TicketAction Action,
        string Outcome,
        ResolutionState? State);

    /// <summary>
    /// A tiny linear congruential generator, seeded from the customer's identifier. Deliberately not
    /// <see cref="Random"/>: the numbers have to be the same in every process and in every runtime
    /// version, and only an algorithm written out here guarantees that.
    /// </summary>
    /// <param name="seed">Seed of the sequence.</param>
    private sealed class DeterministicSequence(uint seed)
    {
        private const uint Multiplier = 1664525;
        private const uint Increment = 1013904223;

        private uint _state = seed;

        /// <summary>The next number in <c>[0; exclusiveUpperBound)</c>.</summary>
        /// <param name="exclusiveUpperBound">Upper bound, not included.</param>
        public int Next(int exclusiveUpperBound)
        {
            _state = (_state * Multiplier) + Increment;

            // The high bits of an LCG are the well-mixed ones; the low bits cycle far too visibly.
            return (int)((_state >> 16) % (uint)exclusiveUpperBound);
        }
    }
}
