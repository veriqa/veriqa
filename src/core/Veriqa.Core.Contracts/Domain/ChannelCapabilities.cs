// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Collections.Frozen;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Domain;

/// <summary>
/// The facts a channel declares about itself (SPEC-003 §6.1, SPEC-012 §4.4.1).
/// One declared object instead of a scattering of interface members: a new fact is a new property
/// with a safe default, so an already compiled adapter keeps compiling and running. Adding a new
/// mandatory interface member for a new fact is forbidden.
/// </summary>
public sealed record ChannelCapabilities
{
    /// <summary>
    /// Post-completion actions of a channel that declared none: nothing is done after a sign-in.
    /// </summary>
    private static readonly FrozenSet<CompletionBehaviorAction> NoCompletionBehaviorActions =
        new[] { CompletionBehaviorAction.None }.ToFrozenSet();

    /// <summary>
    /// The channel can ask for and receive a confirmation inside itself — the single confirmation
    /// fact a channel owns (CFG-048). How it does so is the channel's own business (inline buttons,
    /// a web view inside the messenger, biometrics); the core neither knows nor asks.
    /// </summary>
    /// <remarks>
    /// The channel never names a surface of the core: whether a confirmation is needed at all is the
    /// core's policy, and the page that asks the question when the channel cannot is the core's own.
    /// The default (<c>false</c>) means "cannot" — the safe value: an already compiled adapter that
    /// knows nothing about this fact will not be handed a confirmation it cannot display, and the
    /// core routes the question to its own surface instead (SPEC-012 §4.4.2).
    /// </remarks>
    public bool SupportsInChannelConfirmation { get; init; }

    /// <summary>
    /// Content kinds the channel accepts in <c>SendMessageAsync</c>/<c>SendConfirmationPromptAsync</c>.
    /// A message whose kind is outside this set never reaches the adapter. Must be a genuinely
    /// immutable collection (<c>FrozenSet</c>).
    /// </summary>
    public required IReadOnlySet<ChannelMessageKind> SupportedMessageKinds { get; init; }

    /// <summary>
    /// The channel can update a message it sent earlier, addressed by <see cref="ChannelMessageRef"/>.
    /// The core stores the prompt reference only for such channels.
    /// </summary>
    public bool SupportsMessageUpdate { get; init; }

    /// <summary>
    /// The channel shows the user the status of a transaction (<c>ReportOutcomeAsync</c>): its
    /// terminal outcome and the non-terminal state of a claims completion.
    /// The core never calls that method on a channel that has not declared this.
    /// </summary>
    public bool DeliversOutcomeNotice { get; init; }

    /// <summary>
    /// The channel can supply the recipient's locale in <c>ChannelIdentitySnapshot.Locale</c>
    /// (SPEC-017 §7.2). false — consumers go straight to the transaction fallback of the
    /// recipient-locale chain.
    /// </summary>
    public bool ProvidesRecipientLocale { get; init; }

    /// <summary>
    /// The channel renders the emoji a messenger-worded outcome receipt carries, so a receipt shown
    /// inside it is addressed to the messenger in-channel surface rather than to the plain one.
    /// Which of the two in-channel surfaces a channel carries is the rule of the CHANNEL and not of
    /// the render point (SPEC-036 TPL-123), and this is where a channel states it.
    /// </summary>
    /// <remarks>
    /// <c>true</c> — the messenger surface; <c>false</c> or <c>null</c> (the channel states nothing)
    /// — the plain one. The core keeps no list of channel types for this: the built-in messengers
    /// declare <c>true</c> like any third-party adapter would.
    /// </remarks>
    public bool? RendersEmoji { get; init; }

    /// <summary>
    /// How the channel gives the phone number of its user and whether it proves the number belongs
    /// to the sender (SPEC-041 C36).
    /// </summary>
    /// <remarks>
    /// The default (<see cref="PhoneNumberCapability.None"/>) means "gives no phone number" — the safe
    /// value: an already compiled adapter that knows nothing about this fact is never offered to a
    /// sign-in that requires a phone number.
    /// </remarks>
    public PhoneNumberCapability PhoneNumber { get; init; } = PhoneNumberCapability.None;

    /// <summary>
    /// Post-completion actions the channel can render (SPEC-012 §4.9). An action asked for by the
    /// deployment and absent from this set is never handed to the channel: the core applies
    /// <see cref="DefaultCompletionBehaviorAction"/> instead and reports the replacement. Must be a
    /// genuinely immutable collection (<c>FrozenSet</c>).
    /// </summary>
    /// <remarks>
    /// The default — <see cref="CompletionBehaviorAction.None"/> alone — is the safe value: an already
    /// compiled adapter that knows nothing about this fact is never asked to do anything after a
    /// sign-in is completed.
    /// </remarks>
    public IReadOnlySet<CompletionBehaviorAction> SupportedCompletionBehaviorActions { get; init; } =
        NoCompletionBehaviorActions;

    /// <summary>
    /// Action applied when the one asked for is outside <see cref="SupportedCompletionBehaviorActions"/>.
    /// Default — <see cref="CompletionBehaviorAction.None"/>.
    /// </summary>
    public CompletionBehaviorAction DefaultCompletionBehaviorAction { get; init; } =
        CompletionBehaviorAction.None;

    /// <summary>
    /// The channel's sign-in can be started by a short code the core shows on its pages and the user
    /// sends to the bot. <c>null</c> — the sign-in is started by the link alone.
    /// </summary>
    /// <remarks>
    /// The default (<c>null</c>) is the safe value: an already compiled adapter that knows nothing
    /// about this fact is shown and started exactly as before. A channel that declares it hands a
    /// received code to the core as <see cref="ChannelAuthStartByCodeResult"/>.
    /// </remarks>
    public ChannelCodeStart? CodeStart { get; init; }
}
