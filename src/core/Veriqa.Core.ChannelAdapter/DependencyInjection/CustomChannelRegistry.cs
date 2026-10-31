// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text.RegularExpressions;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Pipeline;

namespace Veriqa.Core.ChannelAdapter.DependencyInjection;

/// <summary>
/// Registry of the channels registered through <c>ChannelAdapterBuilder.AddChannel</c> — the single
/// path every channel takes, the ones shipped with the product included. It is the single place
/// where the channel-type contract is enforced (format, collisions with each other) and the source
/// of the webhook routes for <c>ChannelWebhookPipeline</c>.
/// Every violation fails fast at startup: an adapter whose type silently differs from what the code
/// matches would look registered and never receive a single event.
/// </summary>
/// <remarks>
/// The registry keeps no list of channel types the shipped channels claim in advance: they claim
/// their types here, like everyone else, and the "every channel type is registered exactly once"
/// check covers the shipped ones and the third-party ones with one rule. That rule covers the
/// webhook paths as well — a channel's generic path is derived from its type, so two channels can
/// only collide on a path by colliding on the type first.
/// </remarks>
internal sealed partial class CustomChannelRegistry
{
    /// <summary>
    /// Registrations in the order they were added.
    /// </summary>
    private readonly List<CustomChannelRegistration> _registrations = [];

    /// <summary>
    /// Registered channels (an immutable view over the added registrations).
    /// </summary>
    public IReadOnlyList<CustomChannelRegistration> Registrations { get; }

    /// <summary>
    /// Creates an empty registry.
    /// </summary>
    public CustomChannelRegistry()
    {
        Registrations = _registrations.AsReadOnly();
    }

    /// <summary>
    /// Validates and records a channel registration.
    /// </summary>
    /// <param name="channelType">Channel type declared by the adapter.</param>
    /// <param name="adapterType">Adapter type registered for this channel type.</param>
    /// <param name="options">Settings declared for this channel at registration.</param>
    /// <returns>The recorded registration.</returns>
    /// <exception cref="InvalidOperationException">
    /// The channel type violates the SPI format or collides with an already registered channel.
    /// </exception>
    public CustomChannelRegistration Register(string channelType, Type adapterType, ChannelRegistrationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Format first: everything below (path, ordinal matching) assumes a valid token.
        if (string.IsNullOrEmpty(channelType) || !ChannelTypeRegex().IsMatch(channelType))
        {
            throw new InvalidOperationException(
                $"Invalid channel type '{channelType}' registered via AddChannel. " +
                $"A channel type must match {CustomChannelConstants.ChannelTypePattern}: " +
                "a lowercase URL-safe token starting with a latin letter, up to 64 characters " +
                "(letters, digits and '-'). Examples: 'kakaotalk', 'acme-chat'.");
        }

        if (_registrations.Exists(registration =>
                string.Equals(registration.ChannelType, channelType, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                $"Channel type '{channelType}' is already registered via AddChannel. " +
                "Every channel type must be registered exactly once.");
        }

        var webhookPath = CustomChannelConstants.BuildWebhookPath(channelType);

        var result = new CustomChannelRegistration(channelType, adapterType, webhookPath, options);
        _registrations.Add(result);
        return result;
    }

    /// <summary>
    /// The texts one registered channel declared for the messages it sends on its own behalf.
    /// </summary>
    /// <remarks>
    /// It is what a driver of the pipeline that has no webhook route — a polling loop — reads instead
    /// of naming a constant of its own: the registration is the single place an installation states
    /// whether a channel answers at all, and a loop spelling the text itself would keep answering
    /// after the integrator switched the answer off.
    /// </remarks>
    /// <param name="channelType">Channel type whose registration is read.</param>
    /// <returns>The declared texts of that channel.</returns>
    /// <exception cref="InvalidOperationException">The channel was never registered.</exception>
    public ChannelReplyTexts ReplyTextsOf(string channelType)
    {
        foreach (var registration in _registrations)
        {
            if (!string.Equals(registration.ChannelType, channelType, StringComparison.Ordinal))
            {
                continue;
            }

            var options = registration.Options;
            return new ChannelReplyTexts(
                options.ErrorMessageText,
                options.StaleLinkReplyText,
                options.UnaddressedReplyText,
                options.UnmatchedCodeReplyText);
        }

        throw new InvalidOperationException(
            $"Channel type '{channelType}' was not registered via AddChannel, so it declares no reply " +
            "texts. A component that drives the pipeline for a channel is registered by the very call " +
            "that registers the channel itself.");
    }

    /// <summary>
    /// Cross-checks the recorded registrations against the adapters actually resolved from DI. Each
    /// registration must map one-to-one onto the adapter it was registered for: the channel type
    /// passed to <c>AddChannel</c> must be declared by <b>exactly one</b> adapter, and that adapter
    /// must be served by the registered adapter type. "Served by" is deliberately wider than a strict
    /// instance-of check — it also accepts an external wrapper the host layered over
    /// <see cref="IChannelAdapter"/> (an interface decorator or interface proxy — Scrutor
    /// <c>Decorate</c>, Castle) that forwards the wrapped adapter's channel type — while still
    /// rejecting a genuine type swap between native adapters (best-effort under a sweeping interface
    /// decorator, which hides the adapters' runtime types — see
    /// <see cref="IsServedByRegisteredAdapter"/>). A plain
    /// set-inclusion check is not enough — two channels declaring each other's type satisfy it, and
    /// the webhook of one channel then reaches the other adapter (including one the host registered
    /// with <c>mapWebhook: false</c>), while a channel type declared twice makes the sign-in window
    /// draw two identical buttons of which only the first ever completes a sign-in.
    /// Without the check a mismatch stays silent — the route is mapped and the sign-in button is
    /// rendered, but the pipeline never finds the adapter (it matches ordinally by
    /// <c>ChannelType</c>), so every valid webhook is answered 403 forever.
    /// </summary>
    /// <param name="adapters">All channel adapters registered in the container.</param>
    /// <exception cref="InvalidOperationException">
    /// A registered channel type is not declared by exactly one adapter, or is declared by an
    /// adapter other than the registered one.
    /// </exception>
    public void ValidateAgainstAdapters(IEnumerable<IChannelAdapter> adapters)
    {
        var adapterList = adapters.ToList();

        foreach (var registration in _registrations)
        {
            var declaring = adapterList
                .Where(adapter =>
                    string.Equals(adapter.ChannelType, registration.ChannelType, StringComparison.Ordinal))
                .ToList();

            if (declaring.Count is 0)
            {
                var declaredTypes = adapterList
                    .Select(adapter => adapter.ChannelType)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal);

                throw new InvalidOperationException(
                    $"Channel type '{registration.ChannelType}' was registered via AddChannel, but no " +
                    "registered adapter returns it from IChannelAdapter.ChannelType. The argument of " +
                    "AddChannel and the ChannelType of the adapter must be equal (ordinal comparison): " +
                    "the webhook pipeline and the sign-in window match the channel by the adapter's " +
                    $"ChannelType. Adapters currently declare: {string.Join(", ", declaredTypes)}.");
            }

            if (declaring.Count > 1)
            {
                ThrowMultipleDeclaringAdapters(registration, declaring);
            }

            if (!IsServedByRegisteredAdapter(registration, declaring[0]))
            {
                throw new InvalidOperationException(
                    $"Channel type '{registration.ChannelType}' was registered via AddChannel for adapter " +
                    $"'{registration.AdapterType.FullName}', but it is declared by adapter " +
                    $"'{declaring[0].GetType().FullName}', which is the adapter type registered for a " +
                    "different channel. Every AddChannel argument must match the ChannelType of the very " +
                    "adapter it registers — otherwise the webhooks of one channel are handled by another " +
                    "adapter, including one registered with mapWebhook: false.");
            }
        }
    }

    /// <summary>
    /// Whether the adapter that declares <paramref name="registration"/>'s channel type may legitimately
    /// serve it. True when the adapter is an instance of the registered type (a direct instance, a
    /// subclass, or a class proxy), or an external wrapper the host layered over
    /// <see cref="IChannelAdapter"/> (an interface decorator or interface proxy) that forwards the
    /// wrapped adapter's channel type. False for a genuine type swap between native adapters: the
    /// declaring adapter is in fact the adapter type registered for a <b>different</b> channel, so one
    /// channel's webhook would reach another adapter — the single mismatch the count checks in
    /// <see cref="ValidateAgainstAdapters"/> cannot see. A native external wrapper is told apart from a
    /// swap by exclusion: its runtime type is not the registered adapter type of any other registration,
    /// and its channel type already matched this registration, so routing is correct.
    /// <para>
    /// Swap detection is exact only while the adapters' runtime types are visible. When the host layers
    /// a <i>sweeping</i> interface decorator over <b>every</b> <see cref="IChannelAdapter"/> (for
    /// example Scrutor <c>Decorate&lt;IChannelAdapter, TMetrics&gt;()</c> or a Castle interface proxy on
    /// the whole set), every resolved adapter shares the decorator's single runtime type and the
    /// wrapped adapter types are hidden behind it. A swap concealed that way is type-identical to a
    /// legitimate sweeping decorator, so it cannot be told apart here and is <b>not</b> rejected —
    /// swap detection is best-effort in that configuration. It stays exact for native adapters and for
    /// per-adapter class proxies/subclasses, which keep their own runtime type.
    /// </para>
    /// </summary>
    /// <param name="registration">Registration whose channel type is being served.</param>
    /// <param name="declaring">The sole adapter declaring the registration's channel type.</param>
    /// <returns><c>true</c> if the declaring adapter legitimately serves the registration.</returns>
    private bool IsServedByRegisteredAdapter(CustomChannelRegistration registration, IChannelAdapter declaring)
    {
        if (registration.AdapterType.IsInstanceOfType(declaring))
        {
            return true;
        }

        return !_registrations.Exists(other =>
            !ReferenceEquals(other, registration) && other.AdapterType.IsInstanceOfType(declaring));
    }

    /// <summary>
    /// Fails a channel type declared by more than one adapter. When every declaring adapter is the same
    /// runtime type, two distinct causes are possible and get distinct remedies: one adapter class backs
    /// more than one <c>AddChannel</c> registration (each instance returns the class's single channel
    /// type), whose remedy is the factory overload with a distinct <c>ChannelType</c> per instance; or a
    /// single registration whose adapter type is resolved several times because an extra container
    /// registration was added outside <c>AddChannel</c> (a manual
    /// <c>AddSingleton&lt;IChannelAdapter, TAdapter&gt;</c>), whose remedy is to drop the duplicate. The
    /// two are told apart by how many registrations the declaring runtime type is an instance of
    /// (instance-of, not exact type equality — a per-adapter class proxy or subclass still counts against
    /// its registration). Otherwise it reports the distinct declaring types.
    /// </summary>
    /// <param name="registration">Registration whose channel type is declared more than once.</param>
    /// <param name="declaring">All adapters declaring the registration's channel type.</param>
    /// <exception cref="InvalidOperationException">Always thrown.</exception>
    private void ThrowMultipleDeclaringAdapters(
        CustomChannelRegistration registration,
        List<IChannelAdapter> declaring)
    {
        var declaringTypes = declaring
            .Select(adapter => adapter.GetType())
            .Distinct()
            .ToList();

        if (declaringTypes.Count is 1)
        {
            var declaringType = declaringTypes[0];
            var registrationsOfType =
                _registrations.Count(other => other.AdapterType.IsAssignableFrom(declaringType));

            if (registrationsOfType > 1)
            {
                throw new InvalidOperationException(
                    $"Channel type '{registration.ChannelType}' is declared by {declaring.Count} instances " +
                    $"of the single adapter '{declaringType.FullName}', which backs more than one AddChannel " +
                    "registration. Every instance returns the same ChannelType, so the other channels stay " +
                    "undeclared and this one is ambiguous. To back several channels with one adapter class, " +
                    "register each channel with the factory overload AddChannel(channelType, factory) and let " +
                    "each factory build the adapter with its own distinct ChannelType — a single fixed " +
                    "ChannelType cannot serve more than one channel.");
            }

            throw new InvalidOperationException(
                $"Channel type '{registration.ChannelType}' is declared by {declaring.Count} instances of " +
                $"the single adapter '{declaringType.FullName}', but it was registered through AddChannel " +
                "exactly once. The adapter type is registered in the container more than once — an extra " +
                "registration added outside AddChannel (such as a manual " +
                "AddSingleton<IChannelAdapter, TAdapter>). AddChannel already registers the adapter, so " +
                "remove the duplicate registration.");
        }

        throw new InvalidOperationException(
            $"Channel type '{registration.ChannelType}' is declared by more than one registered adapter " +
            $"({string.Join(", ", declaringTypes.Select(type => type.FullName).Order(StringComparer.Ordinal))}). " +
            "A channel type identifies exactly one adapter: the webhook pipeline routes every event to the " +
            "first match, while the sign-in window renders one button per adapter.");
    }

    /// <summary>
    /// Compiled matcher of the SPI channel-type contract.
    /// </summary>
    [GeneratedRegex(CustomChannelConstants.ChannelTypePattern)]
    private static partial Regex ChannelTypeRegex();
}
