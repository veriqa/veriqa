// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;

/// <summary>
/// The clock of the composition "shifted clock": the wall time of the system moved by an offset a
/// scenario sets, so the wrapper signs its calls with a timestamp outside the window of the adapter.
/// Only the wall time moves; the timestamps and the timers the timeouts run on stay those of the system.
/// </summary>
internal sealed class ShiftedTimeProvider : TimeProvider
{
    /// <summary>
    /// The offset, in ticks; read and written atomically.
    /// </summary>
    private long _offsetTicks;

    /// <summary>
    /// The system wall time moved by the current offset.
    /// </summary>
    /// <returns>The moment.</returns>
    public override DateTimeOffset GetUtcNow() => TimeProvider.System.GetUtcNow().AddTicks(Interlocked.Read(ref _offsetTicks));

    /// <summary>
    /// Moves the clock by an offset until the scope is disposed; then the clock is the system's again.
    /// </summary>
    /// <param name="offset">The offset.</param>
    /// <returns>The scope.</returns>
    public IDisposable Shift(TimeSpan offset)
    {
        Interlocked.Exchange(ref _offsetTicks, offset.Ticks);
        return new ShiftScope(this);
    }

    /// <summary>
    /// Brings the clock back to the system's.
    /// </summary>
    /// <param name="clock">The clock.</param>
    private sealed class ShiftScope(ShiftedTimeProvider clock) : IDisposable
    {
        /// <summary>
        /// Resets the offset.
        /// </summary>
        public void Dispose() => Interlocked.Exchange(ref clock._offsetTicks, 0);
    }
}
