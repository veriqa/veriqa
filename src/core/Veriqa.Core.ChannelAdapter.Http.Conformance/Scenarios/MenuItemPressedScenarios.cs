// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.ObjectModel;

using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Composition;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Running;
using Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;
using Veriqa.Core.ChannelAdapter.MultiTenancy;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Scenarios;

/// <summary>
/// The protection of the <c>menu_item_pressed</c> event in the shipped wrapper (SPEC-042 C39, R60): a
/// signed envelope is accepted and read as the press with its value and the press identifier; the same
/// envelope without a signature, signed with a foreign secret, signed out of the signature window or on
/// the route of a tenant other than the owner of the registration is not accepted; an empty value and a
/// value over the limit are read as unrelated. The suite hands the envelopes to the working composition
/// itself — through <see cref="IChannelAdapter.ValidateWebhookAsync"/> and
/// <see cref="IChannelAdapter.ProcessInboundEventAsync"/>, the checks the webhook route of the core makes
/// — with no HTTP and no receiver: a refused envelope is no event of the adapter, and
/// <c>events.all-valid</c> never sees one. On the route of the core a refusal is the answer 403. The
/// outcome of these scenarios does not depend on the adapter; they run in every mode, only for a
/// registration that declares buttons, in the scope of the owner of the registration, or of the first
/// tenant of the run when it has none.
/// </summary>
internal static class MenuItemPressedScenarios
{
    /// <summary>
    /// A signed <c>menu_item_pressed</c> is accepted and read as the press, its value and its
    /// <c>webhook-id</c> as the press identifier.
    /// </summary>
    public const string Signed = "menu-item-pressed.signed";

    /// <summary>
    /// The envelope of <see cref="Signed"/> without the signature headers is not accepted.
    /// </summary>
    public const string Unsigned = "menu-item-pressed.unsigned";

    /// <summary>
    /// The envelope of <see cref="Signed"/> signed with a foreign secret is not accepted.
    /// </summary>
    public const string WrongSecret = "menu-item-pressed.wrong-secret";

    /// <summary>
    /// The envelope of <see cref="Signed"/> signed ten minutes in the past is not accepted.
    /// </summary>
    public const string TimestampPast = "menu-item-pressed.timestamp-past";

    /// <summary>
    /// The envelope of <see cref="Signed"/>, rightly signed, on the route of a tenant other than the
    /// owner of the registration is not accepted (SPEC-042 R60).
    /// </summary>
    public const string ForeignRoute = "menu-item-pressed.foreign-route";

    /// <summary>
    /// An empty value and a value one character over the limit are read as unrelated.
    /// </summary>
    public const string ValueBounds = "menu-item-pressed.value-bounds";

    /// <summary>
    /// The value of the pressed item in the envelopes of the scenarios — one for all of them, so the
    /// refused envelopes are the accepted one byte for byte.
    /// </summary>
    private const string MenuItemValue = "veriqa-conformance-menu-item";

    /// <summary>
    /// The character the value one character over the limit is made of.
    /// </summary>
    private const char OverLimitCharacter = 'x';

    /// <summary>
    /// What makes the synthetic tenant of <see cref="ForeignRoute"/> out of the owner: appended, it gives
    /// a tenant that is never the owner.
    /// </summary>
    private const string ForeignTenantSuffix = "-veriqa-conformance-foreign";

    /// <summary>
    /// Reason of <see cref="ForeignRoute"/> for a registration without an owner: it serves every route.
    /// </summary>
    private const string NoOwnerReason = "the registration has no owner";

    /// <summary>
    /// The query of every request: the webhook route of the core takes none from an adapter.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> NoQuery = ReadOnlyDictionary<string, string>.Empty;

    /// <summary>
    /// The scenarios, in the order they run: the accepted envelope first, then the refusals of that same
    /// envelope, then the bounds of the value. A failed <see cref="Signed"/> beside passed refusals tells
    /// that the wrapper accepts no such envelope at all.
    /// </summary>
    public static IReadOnlyList<ConformanceScenario> All { get; } =
    [
        new(Signed, ConformanceModes.Any, SignedIsReadAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons },
        new(Unsigned, ConformanceModes.Any, UnsignedIsRefusedAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons },
        new(WrongSecret, ConformanceModes.Any, WrongSecretIsRefusedAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons },
        new(TimestampPast, ConformanceModes.Any, PastTimestampIsRefusedAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons },
        new(ForeignRoute, ConformanceModes.Any, ForeignRouteIsRefusedAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons },
        new(ValueBounds, ConformanceModes.Any, ValuesOutOfBoundsAreUnrelatedAsync) { RequiredMessageKind = ChannelMessageKind.ActionButtons }
    ];

    /// <summary>
    /// <see cref="Signed"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> SignedIsReadAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var tenant = TenantOf(context);
        using var scope = ChannelTenantContext.BeginScope(tenant);

        var body = EnvelopeOf(context, MenuItemValue);
        var webhookId = ConformanceInboundEventIds.NewId();
        var request = RequestOf(body, context.Sender.SignatureHeadersOf(body, webhookId, TimeProvider.System.GetUtcNow()));
        var adapter = context.Compositions.Working.Adapter;

        if (!await AcceptedAsync(adapter, request, cancellationToken))
        {
            return ScenarioOutcome.Failed(
                "the wrapper did not accept a menu_item_pressed envelope signed with the transport secret of the registration on the route of "
                + TenantNames.Describe(tenant));
        }

        var read = await adapter.ProcessInboundEventAsync(request, cancellationToken);
        if (read.IsFailure)
        {
            return ScenarioOutcome.Failed("the wrapper did not read the menu_item_pressed envelope: " + read.Error.Message);
        }

        if (read.Value is not ChannelMenuItemPressedResult press)
        {
            return ScenarioOutcome.Failed("the wrapper read the menu_item_pressed envelope as " + read.Value.GetType().Name);
        }

        if (!string.Equals(press.Value, MenuItemValue, StringComparison.Ordinal))
        {
            return ScenarioOutcome.Failed("the wrapper read the press with a value other than the menu_item_value of the envelope");
        }

        return string.Equals(press.PressId, webhookId, StringComparison.Ordinal)
            ? ScenarioOutcome.Passed
            : ScenarioOutcome.Failed("the wrapper read the press with a press identifier other than the webhook-id of the envelope");
    }

    /// <summary>
    /// <see cref="Unsigned"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> UnsignedIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        RefusedAsync(
            context,
            TenantOf(context),
            static _ => new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            "without the signature headers",
            cancellationToken);

    /// <summary>
    /// <see cref="WrongSecret"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> WrongSecretIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        RefusedAsync(
            context,
            TenantOf(context),
            static body => OutOfWrapperSender.SignatureHeadersOf(
                body,
                ConformanceInboundEventIds.NewId(),
                TimeProvider.System.GetUtcNow(),
                [WrapperCompositions.NewForeignSecret()]),
            "signed with a secret the registration does not know",
            cancellationToken);

    /// <summary>
    /// <see cref="TimestampPast"/>.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> PastTimestampIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken) =>
        RefusedAsync(
            context,
            TenantOf(context),
            body => context.Sender.SignatureHeadersOf(
                body,
                ConformanceInboundEventIds.NewId(),
                TimeProvider.System.GetUtcNow() - WrapperCallScenarios.SignatureWindowOverrun),
            "signed ten minutes in the past",
            cancellationToken);

    /// <summary>
    /// <see cref="ForeignRoute"/>; applies when the registration has an owner.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static Task<ScenarioOutcome> ForeignRouteIsRefusedAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        if (context.Compositions.Working.OwnerTenantId is not { } owner)
        {
            return Task.FromResult(ScenarioOutcome.NotApplicable(NoOwnerReason));
        }

        return RefusedAsync(
            context,
            owner + ForeignTenantSuffix,
            body => context.Sender.SignatureHeadersOf(body, ConformanceInboundEventIds.NewId(), TimeProvider.System.GetUtcNow()),
            "signed with the transport secret of the registration",
            cancellationToken);
    }

    /// <summary>
    /// <see cref="ValueBounds"/>: an empty value, then a value one character over the limit, each in an
    /// envelope that is accepted.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> ValuesOutOfBoundsAreUnrelatedAsync(ConformanceContext context, CancellationToken cancellationToken)
    {
        var tenant = TenantOf(context);
        using var scope = ChannelTenantContext.BeginScope(tenant);

        var adapter = context.Compositions.Working.Adapter;
        (string Value, string Name)[] values =
        [
            (string.Empty, "an empty menu_item_value"),
            (new string(OverLimitCharacter, HttpChannelWire.MaxMenuItemValueLength + 1), "a menu_item_value over the limit")
        ];

        foreach (var (value, name) in values)
        {
            var body = EnvelopeOf(context, value);
            var request = RequestOf(body, context.Sender.SignatureHeadersOf(body, ConformanceInboundEventIds.NewId(), TimeProvider.System.GetUtcNow()));

            if (!await AcceptedAsync(adapter, request, cancellationToken))
            {
                return ScenarioOutcome.Failed(
                    "the wrapper did not accept a signed menu_item_pressed envelope with " + name + " on the route of "
                    + TenantNames.Describe(tenant));
            }

            var read = await adapter.ProcessInboundEventAsync(request, cancellationToken);
            if (read.IsFailure)
            {
                return ScenarioOutcome.Failed("the wrapper did not read the menu_item_pressed envelope with " + name + ": " + read.Error.Message);
            }

            if (read.Value is not ChannelUnrelatedResult)
            {
                return ScenarioOutcome.Failed(
                    "the wrapper read the menu_item_pressed envelope with " + name + " as " + read.Value.GetType().Name
                    + " instead of " + nameof(ChannelUnrelatedResult));
            }
        }

        return ScenarioOutcome.Passed;
    }

    /// <summary>
    /// An envelope of the scenarios the wrapper must not accept: the envelope of <see cref="Signed"/>
    /// with other headers, handed to the check of the wrapper on the route of a tenant.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="tenant">Tenant of the route.</param>
    /// <param name="headersOf">The headers of the body.</param>
    /// <param name="how">How the envelope is signed, as the reason of a failure names it.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The outcome.</returns>
    private static async Task<ScenarioOutcome> RefusedAsync(
        ConformanceContext context,
        string? tenant,
        Func<byte[], IReadOnlyDictionary<string, string>> headersOf,
        string how,
        CancellationToken cancellationToken)
    {
        using var scope = ChannelTenantContext.BeginScope(tenant);

        var body = EnvelopeOf(context, MenuItemValue);
        var request = RequestOf(body, headersOf(body));

        return await AcceptedAsync(context.Compositions.Working.Adapter, request, cancellationToken)
            ? ScenarioOutcome.Failed(
                "the wrapper accepted a menu_item_pressed envelope " + how + " on the route of " + TenantNames.Describe(tenant))
            : ScenarioOutcome.Passed;
    }

    /// <summary>
    /// Whether the check of the wrapper accepts a request: the refusal of the check — a failed result
    /// or <c>false</c> — is the answer 403 of the webhook route of the core.
    /// </summary>
    /// <param name="adapter">The wrapper.</param>
    /// <param name="request">The request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> — accepted.</returns>
    private static async Task<bool> AcceptedAsync(IChannelAdapter adapter, ChannelInboundRequest request, CancellationToken cancellationToken)
    {
        var validated = await adapter.ValidateWebhookAsync(request, cancellationToken);
        return !validated.IsFailure && validated.Value;
    }

    /// <summary>
    /// The tenant of the route the envelopes come on: the owner of the registration, or the first tenant
    /// of the run when it has none.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <returns>The tenant; <c>null</c> — the implicit one.</returns>
    private static string? TenantOf(ConformanceContext context) =>
        context.Compositions.Working.OwnerTenantId ?? context.Options.PrimaryTenant;

    /// <summary>
    /// The <c>menu_item_pressed</c> envelope of the user of the run.
    /// </summary>
    /// <param name="context">Context of the run.</param>
    /// <param name="value">The value of the pressed item.</param>
    /// <returns>The body.</returns>
    private static byte[] EnvelopeOf(ConformanceContext context, string value) =>
        WireBodies.MenuItemPressed(context.Options.ChannelUserId, value);

    /// <summary>
    /// The channel-neutral envelope of a request, as the webhook route of the core builds it.
    /// </summary>
    /// <param name="body">The body.</param>
    /// <param name="headers">The headers.</param>
    /// <returns>The request.</returns>
    private static ChannelInboundRequest RequestOf(byte[] body, IReadOnlyDictionary<string, string> headers) => new()
    {
        Method = HttpMethod.Post.Method,
        Body = body,
        Headers = headers,
        Query = NoQuery
    };
}
