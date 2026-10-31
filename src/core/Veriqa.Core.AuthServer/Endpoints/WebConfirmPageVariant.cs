// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What of the question page of the core (<see cref="WebConfirmPage"/>, SPEC-007 UI-123) depends on the type
/// of the transaction. Built by <see cref="WebConfirmPage.ForSignIn"/> and
/// <see cref="WebConfirmPage.ForConfirmation"/> only; the title is the page's own, picked by
/// <see cref="IsSignIn"/>, and the question comes worded for both types by the one method that words it
/// (<see cref="WebConfirmPage.ResolveQuestionAsync"/>).
/// </summary>
/// <param name="IsSignIn">Whether the page asks about a sign-in: it then takes the title of a sign-in.
/// Otherwise it asks about a confirmation.</param>
/// <param name="Question">The question, already worded and localized, as plain text: the confirmation prompt
/// of a sign-in, the subject of a confirmation.</param>
/// <param name="ConfirmPath">Root-relative route of "Yes".</param>
/// <param name="DeclinePath">Root-relative route of "No".</param>
/// <param name="LeavePath">Root-relative address the watcher takes the browser to once the status no longer
/// matches the page: the callback of a sign-in, the own address of the question of a confirmation.</param>
internal sealed record WebConfirmPageVariant(
    bool IsSignIn,
    string Question,
    string ConfirmPath,
    string DeclinePath,
    string LeavePath);
