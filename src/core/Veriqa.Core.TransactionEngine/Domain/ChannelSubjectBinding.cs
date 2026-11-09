// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Diagnostics.CodeAnalysis;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// The channel subject a transaction is bound to: the first sender whose action the transaction
/// accepted (SPEC-007 UI-018, UI-022; SPEC-039 N34). An action of any other subject is refused and
/// changes nothing; an action of this one is accepted again.
/// </summary>
/// <remarks>
/// A subject is the pair (<see cref="ChannelType"/>, <see cref="ChannelUserId"/>), compared ordinally —
/// the same pair a channel snapshot names. The binding is written by an ordinary
/// <c>ITransactionStore.UpdateAsync</c> with the concurrency token, so of two senders racing for an
/// unbound transaction exactly one is bound.
/// </remarks>
public sealed record ChannelSubjectBinding
{
    /// <summary>
    /// Channel type of the bound subject.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Identifier of the bound subject within its channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// true — the answer on the core confirmation page is accepted only from the browser that carries
    /// the answer mark issued when the channel identity was attached; false — from any browser.
    /// </summary>
    public bool AnswerBrowserBound { get; init; }

    /// <summary>
    /// Snapshot the bound subject entered the transaction with — the profile the channel read on the start.
    /// Null — the binding was written without one: by the attachment of the channel identity, whose snapshot
    /// is already on the transaction, over the size limit of a stored snapshot, or before the field existed.
    /// </summary>
    /// <remarks>
    /// Not a subject fact: the subject is <see cref="ChannelType"/> and <see cref="ChannelUserId"/>, and no
    /// comparison reads this property. It is the profile the decision in the channel is taken with
    /// (<see cref="WithStartProfile"/>), so that a press does not have to read the profile again.
    /// Additive field: a binding stored before it existed deserializes with null.
    /// </remarks>
    public ChannelIdentitySnapshot? StartIdentity { get; init; }

    /// <summary>
    /// Binding of the subject a channel snapshot names.
    /// </summary>
    /// <param name="identity">Channel snapshot naming the subject.</param>
    /// <param name="answerBrowserBound">Whether the answer is bound to the browser of the attachment.</param>
    /// <returns>The binding.</returns>
    internal static ChannelSubjectBinding Of(ChannelIdentitySnapshot identity, bool answerBrowserBound) => new()
    {
        ChannelType = identity.ChannelType,
        ChannelUserId = identity.ChannelUserId,
        AnswerBrowserBound = answerBrowserBound
    };

    /// <summary>
    /// Checks whether a channel snapshot names this subject.
    /// </summary>
    /// <param name="identity">Channel snapshot of a sender.</param>
    /// <returns>true when the channel type and the user identifier both match ordinally.</returns>
    internal bool IsSameSubject(ChannelIdentitySnapshot identity)
        => string.Equals(ChannelType, identity.ChannelType, StringComparison.Ordinal)
            && string.Equals(ChannelUserId, identity.ChannelUserId, StringComparison.Ordinal);

    /// <summary>
    /// The snapshot a decision in the channel is taken with: the profile of the start completed by the press.
    /// </summary>
    /// <remarks>
    /// A press carries what the channel delivers with it, which may be no profile at all — the adapter then
    /// names the user by the identifier. The start read the profile, so a profile value the start holds wins
    /// over the press, and the press fills only what the start left empty. An e-mail address and a phone
    /// number are taken together with their verification flag, from the same snapshot, so that a flag never
    /// states a value it was not given with. The fields that describe the event rather than the user —
    /// tenant, subject, bot flag, metadata, capture time, adapter version — always come from the press.
    /// Without a start snapshot, or when the press names another subject, the press is returned as it is: the
    /// decision then refuses it or goes as it did before the start snapshot was kept.
    /// </remarks>
    /// <param name="pressed">Snapshot of the press.</param>
    /// <returns>The snapshot the decision is taken with.</returns>
    internal ChannelIdentitySnapshot WithStartProfile(ChannelIdentitySnapshot pressed)
    {
        ArgumentNullException.ThrowIfNull(pressed);

        if (StartIdentity is not { } start || !IsSameSubject(pressed))
        {
            return pressed;
        }

        var emailSource = string.IsNullOrEmpty(start.Email) ? pressed : start;
        var phoneSource = string.IsNullOrEmpty(start.PhoneNumber) ? pressed : start;

        return new ChannelIdentitySnapshot
        {
            TenantId = pressed.TenantId,
            ChannelType = pressed.ChannelType,
            ChannelUserId = pressed.ChannelUserId,
            IsBot = pressed.IsBot,
            DisplayName = StartOrPressed(start.DisplayName, pressed.DisplayName),
            FirstName = StartOrPressed(start.FirstName, pressed.FirstName),
            LastName = StartOrPressed(start.LastName, pressed.LastName),
            Username = StartOrPressed(start.Username, pressed.Username),
            Email = emailSource.Email,
            EmailVerified = emailSource.EmailVerified,
            PhoneNumber = phoneSource.PhoneNumber,
            PhoneNumberVerified = phoneSource.PhoneNumberVerified,
            AvatarUrl = StartOrPressed(start.AvatarUrl, pressed.AvatarUrl),
            Locale = StartOrPressed(start.Locale, pressed.Locale),
            RawMetadata = pressed.RawMetadata,
            AdditionalClaims = start.AdditionalClaims is { Count: > 0 } ? start.AdditionalClaims : pressed.AdditionalClaims,
            CapturedAt = pressed.CapturedAt,
            AdapterVersion = pressed.AdapterVersion
        };
    }

    /// <summary>
    /// The value of the start when it has one, otherwise the value of the press.
    /// </summary>
    /// <param name="start">Value of the start snapshot.</param>
    /// <param name="pressed">Value of the press snapshot.</param>
    /// <returns>The value the merged snapshot takes.</returns>
    [return: NotNullIfNotNull(nameof(pressed))]
    private static string? StartOrPressed(string? start, string? pressed)
        => string.IsNullOrEmpty(start) ? pressed : start;
}
