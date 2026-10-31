// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.ChannelAdapter.Http.Configuration;
using Veriqa.Core.ChannelAdapter.Http.Constants;

namespace Veriqa.Core.ChannelAdapter.Http.Outbound;

/// <summary>
/// The deep links a registration may hand to the sign-in page (SPEC-042 L45). The rule lists what is
/// admitted, not what is refused: the link is shown by the sign-in page and handed to the relying party
/// in <c>channel_entries</c>, so a scheme nobody admitted (<c>javascript</c>, <c>data</c>,
/// <c>intent</c> …) must not reach either.
/// </summary>
internal static class HttpChannelDeepLinkRule
{
    /// <summary>
    /// Whether a deep link returned by the external adapter is admitted: an absolute URI no longer than
    /// the limit, with the scheme <c>https</c> or a scheme of the registration, compared without regard
    /// to case.
    /// </summary>
    /// <param name="url">The link as the adapter returned it.</param>
    /// <param name="registration">The registration whose adapter returned it.</param>
    /// <returns><c>true</c> — the link is admitted.</returns>
    public static bool IsAdmitted(string url, HttpChannelRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(url);
        ArgumentNullException.ThrowIfNull(registration);

        if (url.Length > HttpChannelConstants.MaxDeepLinkLength
            || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            return false;
        }

        // A path alone parses as an absolute file URI on some platforms; the scheme check refuses it
        // with every other scheme nobody admitted.
        return string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
            || registration.DeepLinkSchemes.Contains(uri.Scheme);
    }
}
