// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.ChannelAdapter.Http.Outbound;
using Veriqa.Core.ChannelAdapter.Http.Wire;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Common;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Inbound;

/// <summary>
/// Reads the event envelope an external adapter posts to the webhook route of its channel into the
/// result the pipeline consumes (SPEC-042 C39). The signature is already verified when this runs;
/// what the wire does not carry is taken from the receipt, never from the body: the channel type from
/// the registration, the tenant from the route, the capture moment from the moment of receipt, the
/// adapter version from the envelope. Neither the body nor any value of the identity reaches the log,
/// and neither does the text of a message.
/// </summary>
internal sealed class HttpChannelInboundEventReader
{
    /// <summary>
    /// Message of the failure of an envelope that does not parse (SPEC-042 E52).
    /// </summary>
    private const string EnvelopeNotParsedMessage = "The inbound event envelope does not parse.";

    /// <summary>
    /// Message of the failure of a <c>phone_shared</c> event whose number is not in the format of the
    /// wire. It names the field and the format, never the value.
    /// </summary>
    private const string PhoneNumberNotE164Message =
        "The phone_number of the phone_shared event is not in the E.164 format with the leading plus.";

    /// <summary>
    /// Reason of a body that is the JSON literal <c>null</c> instead of an object.
    /// </summary>
    private const string NullEnvelopeReason = "the body is the JSON literal null, not an envelope object";

    /// <summary>
    /// Reason of a wire version that is not <c>major.minor</c>.
    /// </summary>
    private const string MalformedVersionReason = "the version is not in the major.minor form";

    /// <summary>
    /// Reason of an envelope of a major version the core does not speak (SPEC-042 L42).
    /// </summary>
    private const string ForeignMajorVersionReason = "the major version of the envelope is not the one the core speaks";

    /// <summary>
    /// Reason of <c>raw_metadata</c> that is not a JSON object.
    /// </summary>
    private const string RawMetadataNotObjectReason = "identity.raw_metadata is not a JSON object";

    /// <summary>
    /// Reason of a value of <c>additional_claims</c> that is not a string.
    /// </summary>
    private const string ClaimValueNotStringReason = "a value of identity.additional_claims is not a string";

    /// <summary>
    /// Reasons of the checks the serializer cannot express, which this reader raises as a
    /// <see cref="JsonException"/> itself: the only exception messages the log may carry as they are.
    /// </summary>
    private static readonly FrozenSet<string> OwnCheckReasons = new[]
    {
        NullEnvelopeReason,
        RawMetadataNotObjectReason,
        ClaimValueNotStringReason,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Channel type of the registration: the channel of every identity this reader builds.
    /// </summary>
    private readonly string _channelType;

    /// <summary>
    /// Proof of the phone number the registration declares: the ceiling of what an event may state
    /// about a number (SPEC-042 C39).
    /// </summary>
    private readonly PhoneNumberProof _phoneNumberProof;

    /// <summary>
    /// Logger of the adapter.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Creates the reader of one registration.
    /// </summary>
    /// <param name="channelType">Channel type of the registration.</param>
    /// <param name="phoneNumberProof">Proof of the phone number the registration declares.</param>
    /// <param name="logger">Logger of the adapter.</param>
    public HttpChannelInboundEventReader(string channelType, PhoneNumberProof phoneNumberProof, ILogger logger)
    {
        _channelType = channelType ?? throw new ArgumentNullException(nameof(channelType));
        _phoneNumberProof = phoneNumberProof;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Reads one inbound event.
    /// </summary>
    /// <param name="request">The inbound request whose signature is verified.</param>
    /// <param name="receivedAt">Moment of the receipt — the capture moment of the identity.</param>
    /// <returns>
    /// The result of the event by the table of SPEC-042 C39; an unknown event, an unknown action of the
    /// phone step, a value of a pressed item of the menu that is empty or over the limit and a
    /// transaction identifier that does not parse give an unrelated result. A failure — the envelope does not
    /// parse, or the number of <c>phone_shared</c> is not in the E.164 format (SPEC-042 E52): the event
    /// is not processed, the pipeline still answers 200.
    /// </returns>
    public Result<ChannelInboundResult> Read(ChannelInboundRequest request, DateTimeOffset receivedAt)
    {
        ArgumentNullException.ThrowIfNull(request);

        request.Headers.TryGetValue(HttpChannelWire.WebhookIdHeader, out var webhookId);

        // Every parse error of the wire surfaces as a JsonException — the ones of the serializer
        // (invalid JSON, a missing or null required field, a value of a wrong type) and the ones of
        // the checks below it cannot express. The body is never logged, so only the messages of those
        // checks — constants without values — become the reason; a message of the serializer may quote
        // the body and is replaced by where the reading stopped.
        try
        {
            return ReadEnvelope(request.Body.Span, webhookId, receivedAt);
        }
        catch (JsonException ex)
        {
            return NotParsed(OwnCheckReasons.Contains(ex.Message) ? ex.Message : WireParseFailure.ReasonOf(ex), webhookId);
        }
    }

    /// <summary>
    /// Reads the head of the envelope, checks its major version and dispatches by the event type.
    /// </summary>
    /// <param name="body">Raw body bytes.</param>
    /// <param name="webhookId">Identifier of the event, for the log.</param>
    /// <param name="receivedAt">Moment of the receipt.</param>
    /// <returns>The result of the event, or a failure of an envelope that does not parse.</returns>
    /// <exception cref="JsonException">The envelope does not parse.</exception>
    private Result<ChannelInboundResult> ReadEnvelope(ReadOnlySpan<byte> body, string? webhookId, DateTimeOffset receivedAt)
    {
        var head = Deserialize(body, HttpChannelWireJsonContext.Default.WireEventHead);

        if (!WireVersion.TryGetMajor(head.Version, out var major))
        {
            return NotParsed(MalformedVersionReason, webhookId);
        }

        // An envelope of another major version is not read further: its fields may mean something
        // else (SPEC-042 L42, E52).
        if (major != HttpChannelWire.MajorVersion)
        {
            return NotParsed(ForeignMajorVersionReason, webhookId);
        }

        var receipt = new Receipt(head.AdapterVersion, receivedAt, webhookId);

        switch (head.Event)
        {
            case HttpChannelWire.Events.AuthStart:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireAuthStartEvent);
                    return ForTransaction(head.Event, fields.TransactionId, webhookId, transactionId =>
                        new ChannelAuthStartResult
                        {
                            TransactionId = transactionId,
                            Identity = ToSnapshot(fields.Identity, receipt)
                        });
                }

            case HttpChannelWire.Events.AuthStartByCode:
                {
                    // The code is handed on as the user sent it: what can be a code, and whether this
                    // registration declared the start by one, is decided by the pipeline (SPEC-003
                    // CA-218), so an empty or non-digit value is a start the core answers, not a wire
                    // error. Neither the code nor anything derived from it is logged.
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireAuthStartByCodeEvent);
                    return Result<ChannelInboundResult>.Success(new ChannelAuthStartByCodeResult
                    {
                        Code = fields.Code,
                        Identity = ToSnapshot(fields.Identity, receipt)
                    });
                }

            case HttpChannelWire.Events.AuthConfirm:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireAuthAnswerEvent);
                    return ForTransaction(head.Event, fields.TransactionId, webhookId, transactionId =>
                        new ChannelAuthConfirmResult
                        {
                            TransactionId = transactionId,
                            Identity = ToSnapshot(fields.Identity, receipt),
                            InboundToken = ToInboundToken(fields.InboundToken)
                        });
                }

            case HttpChannelWire.Events.AuthDecline:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireAuthAnswerEvent);
                    return ForTransaction(head.Event, fields.TransactionId, webhookId, transactionId =>
                        new ChannelAuthDeclineResult
                        {
                            TransactionId = transactionId,
                            Identity = ToSnapshot(fields.Identity, receipt),
                            InboundToken = ToInboundToken(fields.InboundToken)
                        });
                }

            case HttpChannelWire.Events.PhoneShared:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WirePhoneSharedEvent);

                    // The format of the number is the adapter's to keep (SPEC-003 CA-009): a number that
                    // is not already E.164 is neither repaired here nor handed on, where it would leave
                    // the phone step waiting in silence.
                    if (!E164PhoneNumber.IsCanonical(fields.PhoneNumber))
                    {
                        return PhoneNumberNotAccepted(webhookId);
                    }

                    return Result<ChannelInboundResult>.Success(new ChannelPhoneSharedResult
                    {
                        PhoneNumber = fields.PhoneNumber,
                        Identity = ToSnapshot(fields.Identity, receipt)
                    });
                }

            case HttpChannelWire.Events.PhoneStepAction:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WirePhoneStepActionEvent);

                    // A newer minor version may add actions; the core reads one it does not know the
                    // way it reads an unknown event (SPEC-042 L42). Whether the action fits the step of
                    // the transaction is decided further down the pipeline, not here.
                    if (!HttpChannelWireMapping.TryPhoneStepAction(fields.Action, out var action))
                    {
                        _logger.LogWarning(
                            "HTTP channel {ChannelType} posted {Event} with an unknown action; read as unrelated. WebhookId: {WebhookId}",
                            _channelType,
                            head.Event,
                            webhookId);
                        return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
                    }

                    return ForTransaction(head.Event, fields.TransactionId, webhookId, transactionId =>
                        new ChannelPhoneStepActionResult
                        {
                            TransactionId = transactionId,
                            Identity = ToSnapshot(fields.Identity, receipt),
                            Action = action
                        });
                }

            case HttpChannelWire.Events.MenuItemPressed:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireMenuItemPressedEvent);

                    // The value reaches the handler of the host as it is. An empty value names no item,
                    // as the marker alone names none on a built-in channel; the bound keeps a value as
                    // long as the body itself away from the handler. Neither the value nor its length
                    // is logged.
                    if (fields.MenuItemValue.Length is 0 or > HttpChannelWire.MaxMenuItemValueLength)
                    {
                        _logger.LogWarning(
                            "HTTP channel {ChannelType} posted {Event} with an empty value or a value over {MaxLength} characters; read as unrelated. WebhookId: {WebhookId}, ChannelUserFingerprint: {ChannelUserFingerprint}",
                            _channelType,
                            head.Event,
                            HttpChannelWire.MaxMenuItemValueLength,
                            webhookId,
                            LogMasking.Fingerprint(fields.Identity.ChannelUserId));
                        return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
                    }

                    // The delivery identifier is the identifier of the press: a repeated delivery of the
                    // same event carries the same one, and the host deduplicates by it — the core does not.
                    return Result<ChannelInboundResult>.Success(new ChannelMenuItemPressedResult
                    {
                        Identity = ToSnapshot(fields.Identity, receipt),
                        Value = fields.MenuItemValue,
                        PressId = webhookId
                    });
                }

            case HttpChannelWire.Events.Unaddressed:
                {
                    var fields = Deserialize(body, HttpChannelWireJsonContext.Default.WireUnaddressedEvent);
                    return Result<ChannelInboundResult>.Success(new ChannelUnaddressedResult
                    {
                        Identity = ToSnapshot(fields.Identity, receipt),
                        Text = fields.Text
                    });
                }

            case HttpChannelWire.Events.Unrelated:
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());

            default:
                // A newer minor version may add event types; the core reads one it does not know as
                // unrelated and says so (SPEC-042 L42).
                _logger.LogWarning(
                    "HTTP channel {ChannelType} posted an event of an unknown type {Event}; read as unrelated. WebhookId: {WebhookId}",
                    _channelType,
                    head.Event,
                    webhookId);
                return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }
    }

    /// <summary>
    /// Builds the result of an event that names a transaction. An identifier that does not parse is
    /// not an identifier of this core: the event is read as unrelated, as every channel reads it.
    /// </summary>
    /// <param name="eventType">Type of the event, for the log.</param>
    /// <param name="rawTransactionId">Identifier as the envelope spells it.</param>
    /// <param name="webhookId">Identifier of the event, for the log.</param>
    /// <param name="build">Builds the result over the parsed identifier.</param>
    /// <returns>The result of the event.</returns>
    private Result<ChannelInboundResult> ForTransaction(
        string eventType,
        string rawTransactionId,
        string? webhookId,
        Func<TransactionId, ChannelInboundResult> build)
    {
        if (!TransactionId.TryParse(rawTransactionId, out var transactionId))
        {
            _logger.LogWarning(
                "HTTP channel {ChannelType} posted {Event} with a transaction identifier that does not parse; read as unrelated. WebhookId: {WebhookId}",
                _channelType,
                eventType,
                webhookId);
            return Result<ChannelInboundResult>.Success(new ChannelUnrelatedResult());
        }

        return Result<ChannelInboundResult>.Success(build(transactionId));
    }

    /// <summary>
    /// Builds the identity snapshot of an event. The identity is carried as the adapter reports it —
    /// the operator trusts the external adapter of the registration (SPEC-042 R19); the rules of
    /// claim ownership apply further down the pipeline. Three things are the wrapper's own: the vendor
    /// claims are dropped, metadata over the size limit is dropped, and a proof of the phone number
    /// above the one the registration declares is lowered.
    /// </summary>
    /// <param name="identity">Identity as the envelope carries it.</param>
    /// <param name="receipt">What the receipt of the event states.</param>
    /// <returns>The identity snapshot.</returns>
    /// <exception cref="JsonException">A value of the identity is of a wrong JSON type.</exception>
    private ChannelIdentitySnapshot ToSnapshot(WireIdentity identity, Receipt receipt) =>
        new()
        {
            // The route names the tenant: the pipeline opened this scope for the request, and the
            // signature binds the event to the registration serving that route (SPEC-042 C39).
            TenantId = ChannelTenantContext.CurrentTenantId,
            ChannelType = _channelType,
            ChannelUserId = identity.ChannelUserId,
            IsBot = identity.IsBot,
            DisplayName = identity.DisplayName,
            FirstName = identity.FirstName,
            LastName = identity.LastName,
            Username = identity.Username,
            Email = identity.Email,
            EmailVerified = identity.EmailVerified,
            PhoneNumber = identity.PhoneNumber,
            PhoneNumberVerified = WithinDeclaredProof(identity.PhoneNumberVerified, receipt.WebhookId),
            AvatarUrl = identity.AvatarUrl,
            Locale = identity.Locale,
            RawMetadata = BoundedRawMetadata(identity, receipt.WebhookId),
            AdditionalClaims = WithoutVendorClaims(identity.AdditionalClaims, receipt.WebhookId),
            CapturedAt = receipt.ReceivedAt,
            AdapterVersion = receipt.AdapterVersion
        };

    /// <summary>
    /// Agrees the proof an event states about its phone number with the proof the registration
    /// declares (SPEC-042 C39). The registration is the ceiling, the event is the statement about this
    /// very number: <c>true</c> is taken only from a registration that declares the number proven, and
    /// is lowered to <c>false</c> otherwise — the operator declared no proof, so none is issued.
    /// <c>false</c> and no statement stay as they are: the wrapper never raises the proof of a number
    /// the adapter said nothing about.
    /// </summary>
    /// <param name="stated">The proof as the envelope states it; <c>null</c> — no statement.</param>
    /// <param name="webhookId">Identifier of the event, for the log.</param>
    /// <returns>The proof the identity snapshot carries.</returns>
    private bool? WithinDeclaredProof(bool? stated, string? webhookId)
    {
        if (stated is not true || _phoneNumberProof is PhoneNumberProof.Proven)
        {
            return stated;
        }

        _logger.LogWarning(
            "HTTP channel {ChannelType} posted an identity that states its phone number proven, while the registration does not declare the number proven; the number is taken as not proven. WebhookId: {WebhookId}",
            _channelType,
            webhookId);

        return false;
    }

    /// <summary>
    /// Takes the metadata of an identity within the limit every channel shares (CA-005). Metadata over
    /// the limit is dropped and the event is processed: the field is optional, and refusing the whole
    /// event over it would cost the user the sign-in.
    /// </summary>
    /// <param name="identity">Identity as the envelope carries it.</param>
    /// <param name="webhookId">Identifier of the event, for the log.</param>
    /// <returns>The metadata; <c>null</c> — absent or over the limit.</returns>
    /// <exception cref="JsonException">The metadata is not a JSON object.</exception>
    private JsonElement? BoundedRawMetadata(WireIdentity identity, string? webhookId)
    {
        // An explicit JSON null is an absent field, whichever way the serializer surfaces it.
        if (identity.RawMetadata is not { } metadata || metadata.ValueKind is JsonValueKind.Null)
        {
            return null;
        }

        if (metadata.ValueKind is not JsonValueKind.Object)
        {
            throw new JsonException(RawMetadataNotObjectReason);
        }

        // The size is the size of the value as the adapter sent it, in UTF-8.
        var size = Encoding.UTF8.GetByteCount(metadata.GetRawText());
        if (size > ChannelAdapterLimits.MaxRawMetadataSize)
        {
            _logger.LogWarning(
                "HTTP channel {ChannelType} posted raw metadata of {Size} bytes, over the limit of {Limit}; the metadata is dropped, the event is processed. WebhookId: {WebhookId}, ChannelUserFingerprint: {ChannelUserFingerprint}",
                _channelType,
                size,
                ChannelAdapterLimits.MaxRawMetadataSize,
                webhookId,
                LogMasking.Fingerprint(identity.ChannelUserId));
            return null;
        }

        return metadata;
    }

    /// <summary>
    /// Takes the additional claims of an identity without the vendor claims: those are the core's own
    /// product claims, and an external adapter issuing them would pass itself off before the relying
    /// party as the channel that issues them (SPEC-042 C39).
    /// </summary>
    /// <param name="claims">Claims as the envelope carries them.</param>
    /// <param name="webhookId">Identifier of the event, for the log.</param>
    /// <returns>The claims kept; <c>null</c> — none, so no claim is stated that was never issued.</returns>
    /// <exception cref="JsonException">A value of the claims is not a string.</exception>
    private IReadOnlyDictionary<string, string>? WithoutVendorClaims(
        IReadOnlyDictionary<string, string>? claims,
        string? webhookId)
    {
        if (claims is null)
        {
            return null;
        }

        var kept = new Dictionary<string, string>(claims.Count, StringComparer.Ordinal);

        foreach (var (name, value) in claims)
        {
            // The serializer does not hold the values of a dictionary to their annotation, so a JSON
            // null reaches here as a null string.
            if (value is null)
            {
                throw new JsonException(ClaimValueNotStringReason);
            }

            if (VeriqaClaimTypes.VendorClaims.Contains(name))
            {
                _logger.LogWarning(
                    "HTTP channel {ChannelType} posted the vendor claim {ClaimName}; the claim is dropped. WebhookId: {WebhookId}",
                    _channelType,
                    name,
                    webhookId);
                continue;
            }

            kept.Add(name, value);
        }

        return kept.Count is 0 ? null : kept;
    }

    /// <summary>
    /// Turns the token of an interaction into its value: an absent, empty or blank token is no token.
    /// </summary>
    /// <param name="token">Token as the envelope carries it.</param>
    /// <returns>The token; <c>null</c> — none.</returns>
    private static ChannelInboundToken? ToInboundToken(string? token) =>
        string.IsNullOrWhiteSpace(token) ? null : new ChannelInboundToken(token);

    /// <summary>
    /// Deserializes the body into a wire type, refusing the JSON literal <c>null</c>.
    /// </summary>
    /// <typeparam name="T">Wire type.</typeparam>
    /// <param name="body">Raw body bytes.</param>
    /// <param name="typeInfo">Metadata of the type from the context of the wire.</param>
    /// <returns>The deserialized value.</returns>
    /// <exception cref="JsonException">The body does not parse into the type.</exception>
    private static T Deserialize<T>(ReadOnlySpan<byte> body, JsonTypeInfo<T> typeInfo)
        where T : class =>
        JsonSerializer.Deserialize(body, typeInfo) ?? throw new JsonException(NullEnvelopeReason);

    /// <summary>
    /// The failure of an envelope that does not parse (SPEC-042 E52): a warning with the reason and the
    /// identifier of the event, never the body.
    /// </summary>
    /// <param name="reason">Why the envelope does not parse.</param>
    /// <param name="webhookId">Identifier of the event.</param>
    /// <returns>A failed result.</returns>
    private Result<ChannelInboundResult> NotParsed(string reason, string? webhookId)
    {
        _logger.LogWarning(
            "HTTP channel {ChannelType} posted an event envelope that does not parse; the event is not processed. Reason: {Reason}. WebhookId: {WebhookId}",
            _channelType,
            reason,
            webhookId);

        return NotProcessed(EnvelopeNotParsedMessage);
    }

    /// <summary>
    /// The failure of a <c>phone_shared</c> event whose number is not in the E.164 format: a warning
    /// with the channel and the identifier of the event, never the number.
    /// </summary>
    /// <param name="webhookId">Identifier of the event.</param>
    /// <returns>A failed result.</returns>
    private Result<ChannelInboundResult> PhoneNumberNotAccepted(string? webhookId)
    {
        _logger.LogWarning(
            "HTTP channel {ChannelType} posted phone_shared with a phone number that is not in the E.164 format with the leading plus; the event is not processed, the number is not taken. WebhookId: {WebhookId}",
            _channelType,
            webhookId);

        return NotProcessed(PhoneNumberNotE164Message);
    }

    /// <summary>
    /// The one failure of the reader (SPEC-042 E52): the event is not processed, whatever the reason —
    /// the route does not tell the reasons apart, the warning written before it does.
    /// </summary>
    /// <param name="message">Message of the failure; it names no value of the event.</param>
    /// <returns>A failed result.</returns>
    private static Result<ChannelInboundResult> NotProcessed(string message) =>
        Result<ChannelInboundResult>.Failure(ChannelAdapterErrorCodes.UnsupportedEventType, message);

    /// <summary>
    /// What the receipt of an event states about its identity, as opposed to what the wire carries.
    /// </summary>
    /// <param name="AdapterVersion">Version of the adapter from the envelope.</param>
    /// <param name="ReceivedAt">Moment of the receipt.</param>
    /// <param name="WebhookId">Identifier of the event, for the log.</param>
    private readonly record struct Receipt(string AdapterVersion, DateTimeOffset ReceivedAt, string? WebhookId);
}
