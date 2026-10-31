// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What the "sign-in session expired" page of the callback is rendered with, resolved in ONE pass over
/// the context of the transaction the page concerns: its branding, the overrides of its texts and the
/// address its action leads to (SPEC-007 UI-101, UI-109; SPEC-012 CFG-254). They travel together
/// because some branches hand the page a result resolved earlier, at a point where the transaction is
/// no longer in hand — and a branding that arrived without the address would quietly turn a known
/// application into "Close".
/// </summary>
/// <param name="Branding">Effective branding of the page.</param>
/// <param name="Texts">Text overrides of the page; <see cref="CorePageTexts.None"/> where there is no
/// context to resolve them against.</param>
/// <param name="RestartUrl">Admissible login-initiation address of the transaction's application
/// (<see cref="InitiateLoginUriRule"/>); null — there is none, and the page offers to close the
/// window.</param>
internal sealed record CallbackExpiredPageSetup(CorePageBranding Branding, CorePageTexts Texts, string? RestartUrl);
