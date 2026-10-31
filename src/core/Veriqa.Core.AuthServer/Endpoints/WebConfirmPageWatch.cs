// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.UI;

namespace Veriqa.Core.AuthServer.Endpoints;

/// <summary>
/// What the question page of the core needs to watch the status of its transaction and, while the deadline
/// still decides it, to count it down (SPEC-007 UI-123).
/// </summary>
/// <param name="RemainingMs">Milliseconds left of the transaction's lifetime when the page is built
/// (<see cref="PageCountdown.RemainingMs"/>); null — the deadline no longer decides the transaction (a sign-in
/// in <c>Completed</c>), and the page watches without a countdown.</param>
/// <param name="RenderedState">Name of the state the transaction was in when the page was built, as the
/// status names it.</param>
/// <param name="SignalRClientPath">Address of the SignalR client library, as
/// <see cref="SignalRClientLocation"/> picked it for the transaction.</param>
internal sealed record WebConfirmPageWatch(long? RemainingMs, string RenderedState, string SignalRClientPath);
