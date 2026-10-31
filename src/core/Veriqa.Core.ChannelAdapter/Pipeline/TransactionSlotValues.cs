// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.MessageTemplates;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The one place the slot values of a transaction are assembled (SPEC-036 TPL-124): the caller values
/// the contract of a message admits, then the server values the core states about the transaction —
/// the application, the displayable fields of the initiator context, the masked identity of the channel
/// account, the moment of the request and the moment of the outcome. The
/// question of a confirmation, the outcome receipt, the sign-in prompt of a messenger and the sign-in
/// mail ask the same question of the same transaction, so which value fills which server slot is
/// decided here once.
/// <para>
/// A best-effort value that is absent is left out of the map rather than passed as an empty value: the
/// absence is what makes the template ladder degrade onto a shorter variant (TPL-031), while an empty
/// string would keep the fullest one and print a gap.
/// </para>
/// </summary>
internal static class TransactionSlotValues
{
    /// <summary>
    /// The sanitizer the fields of <see cref="InitiatorContextFieldTable"/> read their values through.
    /// </summary>
    private static readonly Func<string?, string?> Sanitize =
        static value => MessageValueSanitizer.Sanitize(value);

    /// <summary>
    /// Builds the slot values of a message reporting a transaction.
    /// </summary>
    /// <remarks>
    /// Everything the render point knows is supplied, and nothing decides here whether it reaches the
    /// text: <see cref="MessageTextRenderer"/> walks the CONTRACT rather than the values, so a value of a
    /// slot the contract does not declare changes no text.
    /// <para>
    /// A caller value is taken only for a slot the contract declares with the CALLER source, and it is
    /// READ into the type that slot declares (<see cref="StoredSlotValue.Read"/>): the snapshot stores the
    /// canonical textual form of the declared type, while the render takes typed values. The server
    /// values are written last, so a value of the calling party never fills a slot the core states.
    /// </para>
    /// </remarks>
    /// <param name="source">What the render point knows about the transaction; null — nothing, and no
    /// transaction value is stated at all.</param>
    /// <param name="contract">Contract of the message being rendered.</param>
    /// <param name="outcomeMoment">Moment the transaction ended; null when the render point states
    /// none.</param>
    /// <param name="surface">Surface the message is shown on (<see cref="MessageSurfaces"/>,
    /// <see cref="OutcomeReceiptSurfaces"/>); it decides whether the identity of the channel account may
    /// be stated at all (<see cref="WithholdsChannelIdentity"/>).</param>
    /// <returns>Slot name → value.</returns>
    public static Dictionary<string, object?> Of(
        TransactionSlotSource? source,
        MessageContract contract,
        DateTimeOffset? outcomeMoment,
        string surface)
    {
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(surface);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal);

        if (source?.CallerSlotValues is { } callerSlotValues)
        {
            foreach (var (name, value) in callerSlotValues)
            {
                if (contract.FindSlot(name) is { IsCaller: true } slot)
                {
                    values[name] = StoredSlotValue.Read(slot, value);
                }
            }
        }

        if (source is not null)
        {
            values[SlotNames.App] = ApplicationOf(source);
            AddBrandName(values, source.BrandName);

            if (source.InitiatorContext is { } initiator)
            {
                foreach (var field in InitiatorContextFieldTable.Rows)
                {
                    Add(values, field.SlotName, field.SnapshotValue(initiator, Sanitize));
                }
            }

            if (!WithholdsChannelIdentity(source.TransactionType, surface))
            {
                AddChannelIdentity(values, source.ChannelIdentity);
            }

            AddMoment(values, SlotNames.RequestedAt, source.RequestedAt);
        }

        AddMoment(values, SlotNames.OutcomeAt, outcomeMoment);

        return values;
    }

    /// <summary>
    /// Builds the slot values of the initiator details a sign-in message shows — the details the display
    /// decision already let through (SPEC-017 ICC-081).
    /// </summary>
    /// <param name="details">Confirmation context carrying the initiator details.</param>
    /// <returns>Slot name → value, for the fields that have one.</returns>
    public static Dictionary<string, object?> Of(DetailedConfirmationPromptContext details)
    {
        ArgumentNullException.ThrowIfNull(details);

        var values = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [SlotNames.App] = ApplicationNameOf(details.ClientApplicationName),
        };

        foreach (var field in InitiatorContextFieldTable.Rows)
        {
            Add(values, field.SlotName, field.PromptValue(details, Sanitize));
        }

        AddChannelIdentity(values, details.ChannelIdentity);
        AddChannelName(values, details.ChannelName);
        AddBotName(values, details.BotName);
        AddBrandName(values, details.BrandName);
        AddMoment(values, SlotNames.RequestedAt, details.RequestedAt);

        return values;
    }

    /// <summary>
    /// Adds the name of the channel a question is asked in — sanitized, and no value when it is absent or
    /// nothing is left of it. The one assembly of the value for both branches of the question: the sign-in
    /// prompt and the subject of a confirmation.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="channelName">Name of the channel; null — the channel is not known.</param>
    public static void AddChannelName(Dictionary<string, object?> values, string? channelName)
    {
        ArgumentNullException.ThrowIfNull(values);

        Add(values, SlotNames.ChannelName, Sanitize(channelName));
    }

    /// <summary>
    /// Adds the name of the chat of the channel a question is asked in — sanitized, and no value when it is
    /// absent or nothing is left of it. The one assembly of the value for both branches of the question, as
    /// <see cref="AddChannelName"/> is.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="botName">Name of the chat; null — the channel states none.</param>
    public static void AddBotName(Dictionary<string, object?> values, string? botName)
    {
        ArgumentNullException.ThrowIfNull(values);

        Add(values, SlotNames.BotName, Sanitize(botName));
    }

    /// <summary>
    /// Adds the brand name the levels of the transaction state — a configured text the renderer translates
    /// into the language of the recipient; no value when it is absent or nothing is left of it. The one
    /// assembly of the value for every text: the questions, the mails, the receipts and the pages.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="brandName">Brand name as configured; null — no level states one.</param>
    public static void AddBrandName(Dictionary<string, object?> values, string? brandName)
    {
        ArgumentNullException.ThrowIfNull(values);

        if (Sanitize(brandName) is { } naturalKey)
        {
            values[SlotNames.BrandName] = new NaturalKeySlotValue(naturalKey);
        }
    }

    /// <summary>
    /// Whether the identity of the channel account is withheld from a text: a confirmation transaction
    /// (<c>Type = confirmation</c>) whose text is shown anywhere but inside the channel — a page of the core
    /// (the interaction page, the pages of a hop, the question page and its receipt), the confirmation page
    /// of the Email adapter included. The pages are not told apart: any surface other than an in-channel one
    /// is a page, an unknown surface among them. The channel name and the name of its chat are not withheld.
    /// </summary>
    /// <remarks>
    /// Two grounds, and they are not the same kind:
    /// <list type="bullet">
    /// <item><description>a technical one: a page of a confirmation is not bound to a browser by a mark,
    /// any holder of the <c>session_id</c> opens it, so a page has no one to name the account to — the
    /// technical reason the "sign-in and confirmation are one user flow" rule requires of any difference
    /// between the two;</description></item>
    /// <item><description>for the Email page with <c>AnswerBrowserBound</c>, which IS bound to a browser,
    /// it is a decision of the human of 2026-10-26, not a technical reason.</description></item>
    /// </list>
    /// A sign-in states the account on every surface. Rolling the exception back is removing this member
    /// and its call, nothing else.
    /// </remarks>
    /// <param name="transactionType">Type of the transaction; null when the render point does not know it.</param>
    /// <param name="surface">Surface the text is shown on.</param>
    /// <returns><c>true</c> — the masked identity slots get no value.</returns>
    private static bool WithholdsChannelIdentity(string? transactionType, string surface) =>
        string.Equals(transactionType, TransactionTypes.Confirmation, StringComparison.Ordinal)
        && surface is not (MessageSurfaces.InChannel
            or OutcomeReceiptSurfaces.InChannelMessenger
            or OutcomeReceiptSurfaces.InChannelPlain);

    /// <summary>
    /// Adds the masked identity of a channel account — the email address, the phone number, the username
    /// and the display name of its snapshot, each masked by its rule (<see cref="ChannelIdentityMask"/>)
    /// and sanitized. A field that is blank, or that its mask leaves nothing of, gets no value, so the
    /// ladder degrades instead of printing a gap.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="snapshot">Channel snapshot of the account; null — no account, and no value.</param>
    private static void AddChannelIdentity(Dictionary<string, object?> values, ChannelIdentitySnapshot? snapshot)
    {
        if (snapshot is null)
        {
            return;
        }

        Add(values, SlotNames.EmailMasked, Sanitize(ChannelIdentityMask.MaskEmailOrNull(snapshot.Email)));
        Add(values, SlotNames.PhoneMasked, Sanitize(ChannelIdentityMask.MaskPhoneNumber(snapshot.PhoneNumber)));
        Add(values, SlotNames.UsernameMasked, Sanitize(ChannelIdentityMask.MaskName(snapshot.Username)));
        Add(values, SlotNames.DisplayNameMasked, Sanitize(ChannelIdentityMask.MaskName(snapshot.DisplayName)));
    }

    /// <summary>
    /// The value of the application slot, one rule for every type of the transaction: the name the
    /// initiator context carries, through the display decision it already went through; without one —
    /// a confirmation collects none (SPEC-039 N32) — the display name the transaction took at its
    /// creation, and the attribution when it took none (a transaction created before the name was kept,
    /// the attribution of an event).
    /// </summary>
    /// <remarks>
    /// Always there, an empty string when the source states no value: the prompt has always put the
    /// application slot in that way, and the minimal variant of a wording may rest on it.
    /// </remarks>
    /// <param name="source">What the render point knows about the transaction.</param>
    /// <returns>The value of the application slot.</returns>
    private static string ApplicationOf(TransactionSlotSource source)
    {
        return source.InitiatorContext is { } initiator
            ? ApplicationNameOf(initiator.ClientApplicationName)
            : ApplicationNameOf(source.ApplicationName ?? source.ApplicationId);
    }

    /// <summary>
    /// The application name of an initiator context, sanitized; an empty string when it is blank.
    /// </summary>
    /// <param name="name">Application name the initiator context carries.</param>
    /// <returns>The value of the application slot.</returns>
    private static string ApplicationNameOf(string? name) =>
        MessageValueSanitizer.Sanitize(name) ?? string.Empty;

    /// <summary>
    /// Adds a moment when there is one.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="name">Slot name.</param>
    /// <param name="moment">The moment, or null when the render point states none.</param>
    private static void AddMoment(Dictionary<string, object?> values, string name, DateTimeOffset? moment)
    {
        if (moment is not null)
        {
            values[name] = moment.Value;
        }
    }

    /// <summary>
    /// Adds a best-effort value when there is one.
    /// </summary>
    /// <param name="values">Accumulator of the slot values.</param>
    /// <param name="name">Slot name.</param>
    /// <param name="value">Sanitized value, or null when the field is absent.</param>
    private static void Add(Dictionary<string, object?> values, string name, string? value)
    {
        if (value is not null)
        {
            values[name] = value;
        }
    }
}
