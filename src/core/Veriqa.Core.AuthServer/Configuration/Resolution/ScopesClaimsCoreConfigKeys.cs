// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.AuthServer.Configuration.Resolution;

/// <summary>
/// Registrar of the core level of <see cref="ScopesClaimsConfigKeys.ScopeCatalog"/>: a getter over the
/// catalog of the bound <c>Veriqa:ScopesClaims</c> section.
/// </summary>
/// <remarks>
/// The level is read off the bound section rather than by path because the catalog is judged as bound: the
/// startup validation reads the text the deployment wrote and applies the catalog rules to the catalog the
/// root options bind from that text, so the resolver serves the catalog the start accepted. It is the
/// section bound on its own, not the root: a resolution of this key would otherwise validate the whole
/// root. <see cref="IOptions{TOptions}"/> is the snapshot taken at startup — the same one the OpenIddict
/// registration of scopes follows, once per process.
/// </remarks>
internal sealed class ScopesClaimsCoreConfigKeys : IRegisterConfigKeys
{
    /// <summary>
    /// The claims catalog section the core catalog is read from.
    /// </summary>
    private readonly IOptions<ScopesClaimsOptions> _options;

    /// <summary>
    /// Creates the registrar of the core catalog getter.
    /// </summary>
    /// <param name="options">The claims catalog section.</param>
    public ScopesClaimsCoreConfigKeys(IOptions<ScopesClaimsOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <inheritdoc />
    /// <remarks>
    /// The key is declared by its catalog (<see cref="ScopesClaimsConfigKeys.Catalog"/>); this registrar
    /// only states how its core level is read.
    /// </remarks>
    public void DeclareKeys(IConfigKeyDeclarations keys)
    {
    }

    /// <inheritdoc />
    public void Register(IConfigBindings bindings, IConfigCoreBindings coreBindings)
    {
        coreBindings.RegisterCore(
            ScopesClaimsConfigKeys.ScopeCatalog,
            () => _options.Value.Scopes);
    }
}
