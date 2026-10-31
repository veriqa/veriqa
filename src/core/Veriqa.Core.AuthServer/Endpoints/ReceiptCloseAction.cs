// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What the receipt of an outcome (<see cref="TransactionConfirmPage.BuildReceipt"/>) builds the action under
/// its line with — the button that closes the tab (<see cref="OutcomeCloseAction"/>).
/// </summary>
/// <param name="ApplicationName">The name of the application the caption names — "Return to {app}"; null —
/// "Close".</param>
/// <param name="Texts">Text overrides stated by the levels of the transaction.</param>
/// <param name="CspNonce">CSP nonce of the request; null — the script goes out without the attribute.</param>
internal sealed record ReceiptCloseAction(string? ApplicationName, CorePageTexts Texts, string? CspNonce);
