// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.MultiTenancy;

namespace Veriqa.Core.AuthServer.UI.Services;

/// <summary>
/// The one reading of the owner of a channel registration on the paths that build the entries into
/// the channels for a tenant and create transactions with them (SPEC-042 R60).
/// </summary>
internal static class ChannelRegistrationOwnership
{
    /// <summary>
    /// Whether the registration of a channel belongs to a tenant other than the given one: its
    /// declaration names an owner and the owner is not that tenant, compared ordinally. The implicit
    /// tenant (<c>null</c>) owns no registration. A channel whose declaration names no owner — the
    /// registration of the operator and every channel shipped with the product — belongs to no other
    /// tenant.
    /// </summary>
    /// <param name="declarations">Core-level declarations of the channels of the host.</param>
    /// <param name="channelType">Channel type.</param>
    /// <param name="tenantId">Tenant the entry or the transaction is built for; null — the implicit one.</param>
    /// <returns><c>true</c> — the channel is not this tenant's to use.</returns>
    public static bool IsOwnedByAnotherTenant(
        IEnumerable<CoreChannelDeclaration> declarations,
        string channelType,
        string? tenantId)
    {
        foreach (var declaration in declarations)
        {
            if (string.Equals(declaration.ChannelType, channelType, StringComparison.Ordinal)
                && declaration.OwnerTenantId is { } owner
                && !string.Equals(owner, tenantId, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
