// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// The lines a page that follows a transaction without taking part in it ends on, worded for that
/// transaction.
/// </summary>
/// <param name="Confirmed">Line of a confirmed outcome.</param>
/// <param name="Expired">Line of a transaction that ran out of time or is no longer stored.</param>
/// <param name="Declined">Line of a transaction the USER declined.</param>
/// <param name="Error">Line of a failure that is not the user's refusal; null — the page's own.</param>
internal readonly record struct TransactionOutcomeLines(
    string Confirmed,
    string Expired,
    string Declined,
    string? Error);
