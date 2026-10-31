// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using Veriqa.Sample.PseudoChannels.Adapter;

namespace Veriqa.Sample.PseudoChannels.Host;

/// <summary>
/// String constants of the host: authentication schemes, routes, form fields and the facts about the
/// two providers. Everything the adapter shares with the host stays in
/// <see cref="PseudoChannelConstants"/>; this class holds what belongs to the web side alone.
/// </summary>
internal static class PseudoHostConstants
{
    /// <summary>
    /// Content type of every page the sample renders.
    /// </summary>
    public const string HtmlContentType = "text/html; charset=utf-8";

    /// <summary>
    /// Content type of the relay webhook body.
    /// </summary>
    public const string JsonContentType = "application/json";

    /// <summary>
    /// Authentication schemes of the host.
    /// </summary>
    public static class Schemes
    {
        /// <summary>
        /// Short-lived cookie the provider handlers sign in to: the proof that THIS browser signed
        /// in at the provider for THIS transaction. It is not the scheme the desktop page reads, so
        /// a sign-in at the provider alone signs nobody in to the sample.
        /// </summary>
        public const string ProviderCookie = "PseudoChannels.Provider";

        /// <summary>
        /// Name of the cookie of <see cref="ProviderCookie"/>.
        /// </summary>
        public const string ProviderCookieName = "pseudo_provider";
    }

    /// <summary>
    /// Entries the sample keeps in the protected state of a provider sign-in.
    /// </summary>
    public static class StateItems
    {
        /// <summary>
        /// Veriqa transaction the sign-in at the provider belongs to.
        /// </summary>
        public const string Transaction = "pseudo.tx";

        /// <summary>
        /// Channel type the sign-in was started for.
        /// </summary>
        public const string ChannelType = "pseudo.channel";
    }

    /// <summary>
    /// Routes of the host.
    /// </summary>
    public static class Routes
    {
        /// <summary>
        /// Desktop page: the sign-in link, or the claims of the signed-in user.
        /// </summary>
        public const string Home = "/";

        /// <summary>
        /// Starts the desktop sign-in against the embedded issuer.
        /// </summary>
        public const string SignIn = "/signin";

        /// <summary>
        /// Drops the desktop session.
        /// </summary>
        public const string SignOut = "/signout";

        /// <summary>
        /// Where the desktop lands when the sign-in was declined or failed.
        /// </summary>
        public const string SignInFailed = "/signin-failed";

        /// <summary>
        /// Return address of the desktop relying party; listed in the client's AllowedRedirectUris.
        /// </summary>
        public const string DesktopCallback = "/signin-oidc";

        /// <summary>
        /// Last path segment of the provider's return address.
        /// </summary>
        public const string CallbackSegment = "callback";

        /// <summary>
        /// Last path segment of the phone page that asks the question.
        /// </summary>
        public const string ConfirmSegment = "confirm";

        /// <summary>
        /// Last path segment of the address the answer is posted to.
        /// </summary>
        public const string AnswerSegment = "answer";

        /// <summary>
        /// Builds an address of the sample for one pseudo-channel.
        /// </summary>
        /// <param name="channelType">Channel type.</param>
        /// <param name="segment">Last path segment.</param>
        /// <returns>Rooted path.</returns>
        public static string ForChannel(string channelType, string segment)
            => $"/{PseudoChannelConstants.PathPrefix}/{channelType}/{segment}";
    }

    /// <summary>
    /// The answer form of the phone page.
    /// </summary>
    public static class AnswerForm
    {
        /// <summary>
        /// Name of the field both buttons submit.
        /// </summary>
        public const string Field = "answer";

        /// <summary>
        /// Value of the "yes" button.
        /// </summary>
        public const string Yes = "yes";

        /// <summary>
        /// Value of the "no" button.
        /// </summary>
        public const string No = "no";
    }

    /// <summary>
    /// Facts about Google as an OpenID provider.
    /// </summary>
    public static class Google
    {
        /// <summary>
        /// Issuer; the handler reads the discovery document under it.
        /// </summary>
        public const string Authority = "https://accounts.google.com";

        /// <summary>
        /// Makes Google show the account chooser even when one account is already signed in.
        /// </summary>
        public const string Prompt = "select_account";
    }

    /// <summary>
    /// Facts about Sign in with Apple as an OpenID provider.
    /// </summary>
    public static class Apple
    {
        /// <summary>
        /// Issuer; the handler reads the discovery document under it. Also the audience of the
        /// client secret.
        /// </summary>
        public const string Authority = "https://appleid.apple.com";

        /// <summary>
        /// How long a client secret made for one token request stays valid. Apple accepts up to six
        /// months; a secret made per request needs minutes.
        /// </summary>
        public static readonly TimeSpan ClientSecretLifetime = TimeSpan.FromMinutes(5);
    }
}
