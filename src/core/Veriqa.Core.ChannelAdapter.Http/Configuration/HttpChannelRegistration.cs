// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

using Veriqa.Core.ChannelAdapter.DependencyInjection;
using Veriqa.Core.ChannelAdapter.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Configuration;

/// <summary>
/// One ENABLED HTTP channel registration as it was read and checked at start (SPEC-042 C30). The
/// values are fixed for the life of the host (SPEC-042 R5); the one value resolved per call — the
/// opaque settings string, for the tenant of the call — is deliberately not here.
/// <para>
/// The model is built in one place only, <see cref="HttpChannelRegistrationReader"/>, and every
/// member it carries has passed the start-up checks of that reader.
/// </para>
/// </summary>
internal sealed record HttpChannelRegistration
{
    /// <summary>
    /// Channel type — the name of the channel section, and the <c>ChannelType</c> of the adapter
    /// built over this registration (SPEC-042 R1).
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Absolute URL of the single entry of the external adapter; <c>https</c>, or <c>http</c> only
    /// when the operator admitted it explicitly (SPEC-042 E55).
    /// </summary>
    public required Uri BaseUrl { get; init; }

    /// <summary>
    /// Transport secrets in the <c>whsec_</c> serialization, the current one first and the previous
    /// one — while a rotation is in progress — second (SPEC-042 C31). Every value has been decoded
    /// successfully at start.
    /// </summary>
    public required IReadOnlyList<string> TransportSecrets { get; init; }

    /// <summary>
    /// Deep-link schemes admitted beyond <c>https</c>, compared without regard to case (SPEC-042 L45).
    /// </summary>
    public required IReadOnlySet<string> DeepLinkSchemes { get; init; }

    /// <summary>
    /// Tenant that owns the registration and is the only one it serves (SPEC-042 R60), compared
    /// ordinally and as stated; <c>null</c> — the registration of the operator, serving every tenant
    /// and the implicit one.
    /// </summary>
    public string? Tenant { get; init; }

    /// <summary>
    /// Channel facts declared by the operator, unstated ones at their safe defaults (SPEC-042 R8).
    /// </summary>
    public required ChannelCapabilities Capabilities { get; init; }

    /// <summary>
    /// Name of the channel on the sign-in button; <c>null</c> — not stated.
    /// </summary>
    public string? DisplayName { get; init; }

    /// <summary>
    /// Inner markup of the channel glyph; <c>null</c> — no icon.
    /// </summary>
    public string? IconSvgPath { get; init; }

    /// <summary>
    /// Settings of the channel registration: the texts of the registration and no verification
    /// handshake of a platform (SPEC-042 R4, R6).
    /// </summary>
    public required ChannelRegistrationOptions RegistrationOptions { get; init; }

    /// <summary>
    /// Timeout of one attempt of an outbound call (SPEC-042 L44).
    /// </summary>
    public required TimeSpan OperationTimeout { get; init; }

    /// <summary>
    /// Timeout of the deep-link call (SPEC-042 L44).
    /// </summary>
    public required TimeSpan DeepLinkTimeout { get; init; }

    /// <summary>
    /// Budget of all attempts of one outbound call with the pauses between them (SPEC-042 L44).
    /// </summary>
    public required TimeSpan RetryBudget { get; init; }

    /// <summary>
    /// Whether the external adapter is reached over plain <c>http</c> — admitted by the operator, and
    /// warned about at start (SPEC-042 E55).
    /// </summary>
    public bool UsesInsecureHttp =>
        string.Equals(BaseUrl.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a transport-secret rotation is in progress — the previous secret is still stated and
    /// still accepted (SPEC-042 C31, E55).
    /// </summary>
    public bool IsRotatingTransportSecret => TransportSecrets.Count > 1;

    /// <summary>
    /// Prints the registration by its channel type alone. The members generated for a record would
    /// print every property, and this record carries the transport secrets: a registration that
    /// reaches a log line or an exception message must not carry them along (SPEC-042 N58).
    /// </summary>
    /// <param name="builder">Builder of the printed form.</param>
    /// <returns>Always <c>true</c>: the channel type was printed.</returns>
    private bool PrintMembers(StringBuilder builder)
    {
        builder.Append(nameof(ChannelType));
        builder.Append(" = ");
        builder.Append(ChannelType);
        return true;
    }
}
