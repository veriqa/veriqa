// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Constants;

namespace Veriqa.Core.AuthServer.UI;

/// <summary>
/// Where a page of the core loads the SignalR client library from — one rule for every page that
/// connects to the hub (the sign-in window and the page of the claims completion).
/// </summary>
internal static class SignalRClientLocation
{
    /// <summary>
    /// Picks the address of the library: the configured override
    /// (<see cref="AuthPageDesignOptions.SignalRClientPath"/>) as it is — the value belongs to the operator
    /// — or, when none is stated, the asset bundled with the package
    /// (<see cref="SignalRConstants.SignalRClientPath"/>), which is root-relative and therefore gets the
    /// path base of the application.
    /// </summary>
    /// <param name="configuredPath">The effective override; null, empty or whitespace — none.</param>
    /// <param name="pathBase">Path base the application is hosted under (empty — the site root).</param>
    /// <returns>The address to put into the <c>src</c> attribute, not yet HTML-encoded.</returns>
    public static string Resolve(string? configuredPath, string pathBase) =>
        !string.IsNullOrWhiteSpace(configuredPath)
            ? configuredPath
            : $"{pathBase}{SignalRConstants.SignalRClientPath}";
}
