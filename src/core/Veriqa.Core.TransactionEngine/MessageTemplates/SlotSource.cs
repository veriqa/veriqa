// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.TransactionEngine.MessageTemplates;

/// <summary>
/// Where a slot value comes from (SPEC-036 §4.1). Server sources are collected by the server itself
/// and are best-effort at render time (their absence degrades the template variant, TPL-024); the
/// caller source carries values passed by the RP, which are fail-fast validated at transaction
/// creation (§4.4, §5.1); the localized source takes the value from the localization of a Natural Key
/// the contract names.
/// <para>
/// The numeric values are part of the surface: a source is spelled by name in configuration and
/// serialized by value, so an existing member is never renumbered — a new one is appended.
/// </para>
/// </summary>
public enum SlotSource
{
    /// <summary>
    /// Server-collected initiator context (SPEC-017): the sign-in prompt fields
    /// <c>app</c>/<c>browser</c>/<c>os</c>/<c>region</c>.
    /// </summary>
    ServerInitiatorContext = 0,

    /// <summary>
    /// Server-computed system fields (e.g. mail client name, link expiry, masked address).
    /// </summary>
    ServerSystem = 1,

    /// <summary>
    /// Server-resolved identity claims (TPL-053): the claims of the identity a completed sign-in
    /// resolved — e.g. the recipient's display name in the welcome message sent after it.
    /// </summary>
    ServerIdentityClaims = 2,

    /// <summary>
    /// Values passed by the calling party (RP). Only caller slots may be <c>required</c>; the transport
    /// for these values arrives with track A (TASK-078, §4.4).
    /// </summary>
    Caller = 3,

    /// <summary>
    /// A localized string: the value is the translation of the Natural Key the declaration names
    /// (<see cref="SlotDeclaration.NaturalKey"/>), resolved into the recipient's language with the same
    /// degradation onto the base text every other Natural Key has.
    /// <para>
    /// It is what lets ONE language-independent text carry visible strings that are translated: the
    /// markup is written once as the text of the variant, and the phrases inside it stay Natural Keys.
    /// The value comes neither from the caller nor from the state of the transaction, so it is always
    /// there — a slot of this source is <c>guaranteed</c> by construction and can never be
    /// <c>required</c> (that attribute belongs to the caller alone, TPL-022).
    /// </para>
    /// </summary>
    LocalizedText = 4
}
