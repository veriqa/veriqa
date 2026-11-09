// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Domain;
using Veriqa.Core.TransactionEngine.Domain;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.ChannelAdapter.Pipeline;

/// <summary>
/// The tenant of the identity snapshot of one inbound event (SPEC-003 CA-164). The core states it, and the
/// value the adapter wrote is never used: the tenant of the event (the owner of the registration, otherwise the
/// tenant of the route or of the polling loop) when there is one; otherwise the tenant of the transaction the
/// event addresses, from the moment that transaction is read; otherwise none. An adapter that left the tenant
/// empty gets it set silently; one that wrote a value other than the chosen one gets one warning per event.
/// </summary>
/// <remarks>
/// One instance per event. Without a tenant of the event the choice is open until a handler takes the event
/// for the transaction it has read (<see cref="ForTransaction"/>, <see cref="Choose"/>) or the processing ends
/// without one (<see cref="Complete"/>), and the warning is written at that moment, with the tenant actually
/// chosen.
/// </remarks>
internal sealed class InboundSnapshotTenant
{
    /// <summary>
    /// Tenant of the event; null — the route without a tenant segment or the polling of the implicit tenant.
    /// </summary>
    private readonly string? _eventTenantId;

    /// <summary>
    /// Channel type the event arrived through.
    /// </summary>
    private readonly string _channelType;

    /// <summary>
    /// Logger the warning goes to.
    /// </summary>
    private readonly ILogger _logger;

    /// <summary>
    /// Whether the tenant in the snapshot handed in is the adapter's; false — the core has stated it already
    /// (a letter, whose snapshot carries the tenant of its transaction), and it is compared with nothing.
    /// </summary>
    private readonly bool _adapterStatesTenant;

    /// <summary>
    /// Tenant the adapter wrote into the snapshot; null — none.
    /// </summary>
    private string? _adapterTenantId;

    /// <summary>
    /// Whether the tenant is chosen, so the comparison with the adapter value is made once.
    /// </summary>
    private bool _settled;

    /// <summary>
    /// Creates the tenant of the snapshot of one event.
    /// </summary>
    /// <param name="eventTenantId">Tenant of the event; null — none.</param>
    /// <param name="channelType">Channel type the event arrived through.</param>
    /// <param name="logger">Logger the warning goes to.</param>
    /// <param name="adapterStatesTenant">Whether the tenant in the snapshot handed in is the adapter's; false —
    /// the core has stated it already, and no warning is written about it.</param>
    internal InboundSnapshotTenant(string? eventTenantId, string channelType, ILogger logger, bool adapterStatesTenant)
    {
        ArgumentException.ThrowIfNullOrEmpty(channelType);

        _eventTenantId = eventTenantId;
        _channelType = channelType;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _adapterStatesTenant = adapterStatesTenant;
    }

    /// <summary>
    /// Replaces the tenant the adapter wrote into the snapshot of the event with the tenant of the event — none
    /// when the event has no tenant, until a handler reads the transaction. Done before anything else looks at
    /// the event, the dropping of repeats included, whose key carries the tenant of the snapshot.
    /// </summary>
    /// <param name="inboundResult">Result the adapter produced.</param>
    /// <returns>The result with the tenant of the core in its snapshot; a result without a snapshot as it is.</returns>
    internal ChannelInboundResult TakeOver(ChannelInboundResult inboundResult)
    {
        ArgumentNullException.ThrowIfNull(inboundResult);

        ChannelInboundResult? taken = inboundResult switch
        {
            ChannelAuthStartResult start => start with { Identity = TakeOver(start.Identity) },
            ChannelAuthStartByCodeResult startByCode => startByCode with { Identity = TakeOver(startByCode.Identity) },
            ChannelAuthConfirmResult confirm => confirm with { Identity = TakeOver(confirm.Identity) },
            ChannelAuthDeclineResult decline => decline with { Identity = TakeOver(decline.Identity) },
            ChannelPhoneSharedResult phoneShared => phoneShared with { Identity = TakeOver(phoneShared.Identity) },
            ChannelPhoneStepActionResult phoneStepAction => phoneStepAction with { Identity = TakeOver(phoneStepAction.Identity) },
            ChannelMenuItemPressedResult menuItemPressed => menuItemPressed with { Identity = TakeOver(menuItemPressed.Identity) },
            ChannelUnaddressedResult unaddressed => unaddressed with { Identity = TakeOver(unaddressed.Identity) },
            _ => null
        };

        if (taken is not null)
        {
            if (_eventTenantId is not null)
            {
                Settle(_eventTenantId);
            }

            return taken;
        }

        // A result without a snapshot has no tenant to settle.
        _settled = true;

        if (inboundResult is not ChannelUnrelatedResult)
        {
            // A further intent added later passes as it is rather than bringing the runtime down here.
            _logger.LogWarning(
                "Unknown inbound result type — the tenant of its identity snapshot is not set by the core. ChannelType: {ChannelType}, ResultType: {ResultType}",
                _channelType,
                inboundResult.GetType().Name);
        }

        return inboundResult;
    }

    /// <summary>
    /// The snapshot of the event once the handler has read the transaction it addresses: with a tenant of the
    /// event the snapshot already carries it, and the transaction, compared with it, is of that tenant;
    /// without one the snapshot takes the tenant of the transaction for the rest of the processing.
    /// </summary>
    /// <param name="identity">Snapshot of the event as the handler holds it.</param>
    /// <param name="transaction">The transaction the event addresses.</param>
    /// <returns>The snapshot with the tenant of the core.</returns>
    internal ChannelIdentitySnapshot ForTransaction(ChannelIdentitySnapshot identity, Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(transaction);

        Choose(transaction);

        return WithTenant(identity, TenantFor(transaction));
    }

    /// <summary>
    /// The tenant the snapshot of the event takes for a transaction it addresses, without closing the choice:
    /// the tenant of the event when there is one, otherwise the tenant of the transaction. For a handler that
    /// still has to decide whether the event is taken for that transaction at all.
    /// </summary>
    /// <param name="transaction">The transaction the event addresses.</param>
    /// <returns>The tenant; null — none.</returns>
    internal string? TenantFor(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        return _eventTenantId ?? transaction.GetTenantId();
    }

    /// <summary>
    /// Closes the choice with the transaction the handler has taken the event for (<see cref="TenantFor"/>):
    /// an event a handler ignores and passes on as one naming no transaction keeps the choice open.
    /// </summary>
    /// <param name="transaction">The transaction the event is taken for.</param>
    internal void Choose(Transaction transaction) => Settle(TenantFor(transaction));

    /// <summary>
    /// Closes the choice of an event the processing ended without taking for a transaction: its snapshot has
    /// no tenant.
    /// </summary>
    internal void Complete() => Settle(chosenTenantId: null);

    /// <summary>
    /// The snapshot a channel that routes no event through the processor hands over for the transaction it has
    /// read (the mail of Email push): it takes the tenant of that transaction, and an adapter value that differs
    /// is reported once.
    /// </summary>
    /// <param name="identity">Snapshot the adapter produced.</param>
    /// <param name="transaction">The transaction the event addresses.</param>
    /// <param name="channelType">Channel type the event arrived through.</param>
    /// <param name="logger">Logger the warning goes to.</param>
    /// <returns>The snapshot with the tenant of the transaction.</returns>
    internal static ChannelIdentitySnapshot OfTransaction(
        ChannelIdentitySnapshot identity,
        Transaction transaction,
        string channelType,
        ILogger logger)
    {
        var snapshotTenant = new InboundSnapshotTenant(
            eventTenantId: null, channelType, logger, adapterStatesTenant: true);

        return snapshotTenant.ForTransaction(snapshotTenant.TakeOver(identity), transaction);
    }

    /// <summary>
    /// Keeps the tenant the adapter wrote for the comparison — none when the core stated it — and replaces it
    /// with the tenant of the event.
    /// </summary>
    /// <param name="identity">Snapshot the adapter produced.</param>
    /// <returns>The snapshot with the tenant of the event.</returns>
    private ChannelIdentitySnapshot TakeOver(ChannelIdentitySnapshot identity)
    {
        _adapterTenantId = _adapterStatesTenant ? identity.TenantId : null;

        return WithTenant(identity, _eventTenantId);
    }

    /// <summary>
    /// A copy of the snapshot with another tenant. The snapshot is a class of the contracts, not a record, so
    /// the copy names every member it has.
    /// </summary>
    /// <param name="identity">Snapshot to copy.</param>
    /// <param name="tenantId">Tenant of the copy; null — none.</param>
    /// <returns>The copy; the snapshot itself when it already carries that tenant.</returns>
    private static ChannelIdentitySnapshot WithTenant(ChannelIdentitySnapshot identity, string? tenantId) =>
        string.Equals(identity.TenantId, tenantId, StringComparison.Ordinal)
            ? identity
            : new ChannelIdentitySnapshot
            {
                TenantId = tenantId,
                ChannelType = identity.ChannelType,
                IsBot = identity.IsBot,
                ChannelUserId = identity.ChannelUserId,
                DisplayName = identity.DisplayName,
                FirstName = identity.FirstName,
                LastName = identity.LastName,
                Username = identity.Username,
                Email = identity.Email,
                EmailVerified = identity.EmailVerified,
                PhoneNumber = identity.PhoneNumber,
                PhoneNumberVerified = identity.PhoneNumberVerified,
                AvatarUrl = identity.AvatarUrl,
                Locale = identity.Locale,
                RawMetadata = identity.RawMetadata,
                AdditionalClaims = identity.AdditionalClaims,
                CapturedAt = identity.CapturedAt,
                AdapterVersion = identity.AdapterVersion
            };

    /// <summary>
    /// Fixes the chosen tenant and, the first time only, warns when the adapter wrote another one. An adapter
    /// that wrote none is not warned about. Compared ordinally, as the tenant of a transaction is.
    /// </summary>
    /// <param name="chosenTenantId">The tenant the core chose; null — none.</param>
    private void Settle(string? chosenTenantId)
    {
        if (_settled)
        {
            return;
        }

        _settled = true;

        if (_adapterTenantId is not null && !string.Equals(_adapterTenantId, chosenTenantId, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "The channel adapter wrote a tenant into the identity snapshot other than the one the core chose — the value of the adapter is not used. ChannelType: {ChannelType}, Tenant: {Tenant}, AdapterTenant: {AdapterTenant}",
                _channelType,
                chosenTenantId,
                _adapterTenantId);
        }
    }
}
