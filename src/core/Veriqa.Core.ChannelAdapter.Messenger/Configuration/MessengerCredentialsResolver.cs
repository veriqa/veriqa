// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Common;

namespace Veriqa.Core.ChannelAdapter.Messenger.Configuration;

/// <summary>
/// Shared entry point for resolving the Messenger tenant-credential group (SPEC-003 §17.4, CA-162).
/// The tenant comes from the ambient context of the request; null is the default implicit tenant
/// (a self-hosted installation), for which the canonical resolver answers with the core level — the
/// <c>Veriqa:Channels:Messenger</c> section. It exists so that the resolver call is written once
/// rather than repeated at every consumer. Messenger has no long-lived platform client (HTTP goes
/// through <c>IHttpClientFactory</c>), so the credentials are resolved directly, per operation.
/// </summary>
internal static class MessengerCredentialsResolver
{
    /// <summary>
    /// Resolves the effective Messenger credentials of the current tenant.
    /// </summary>
    /// <param name="resolver">Canonical layer resolver.</param>
    /// <param name="logger">
    /// Logger of the calling path — it names the place of a credential read made outside a tenant scope.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The credential group, or a failure when no layer supplies credentials.</returns>
    public static ValueTask<Result<MessengerTenantCredentials>> ResolveAsync(
        IConfigurationResolver resolver,
        ILogger logger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resolver);

        var context = ChannelCredentialContext.ForCurrentTenant(ChannelTypes.Messenger);

        return resolver.ResolveCredentialsAsync(MessengerConfigKeys.Credentials, context, logger, cancellationToken);
    }
}
