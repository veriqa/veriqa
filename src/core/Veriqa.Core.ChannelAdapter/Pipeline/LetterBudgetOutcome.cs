// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// What the budget of the confirmation operation answered one pass of an event through the gate — a
/// press of a button, or one pass of a letter, which is charged once however many times it arrives.
/// </summary>
internal enum LetterBudgetOutcome
{
    /// <summary>
    /// The event goes on: the budget let it through now, or let the same letter through on its first pass.
    /// </summary>
    Allowed,

    /// <summary>
    /// The budget is exhausted; the transaction has been moved to Failed.
    /// </summary>
    Exhausted,

    /// <summary>
    /// Another pass of the same letter holds the budget and has not been let through — it is still in
    /// progress, or the budget refused it. This pass writes nothing and takes nothing, and is answered as a
    /// write lost to a parallel one.
    /// </summary>
    InProgress
}
