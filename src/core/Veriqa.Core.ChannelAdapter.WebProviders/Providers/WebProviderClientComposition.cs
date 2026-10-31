// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Collections.Frozen;
using System.Reflection;

using Microsoft.AspNetCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using OpenIddict.Client;
using OpenIddict.Client.AspNetCore;
using OpenIddict.Server;

using Veriqa.Core.ChannelAdapter.WebProviders.Configuration;
using Veriqa.Core.ChannelAdapter.WebProviders.Constants;

namespace Veriqa.Core.ChannelAdapter.WebProviders.Providers;

/// <summary>
/// Adds the OpenIddict client the web provider channels sign in through to the same
/// <c>AddOpenIddict()</c> the server of the host is registered on. What it adds is additive: the
/// registrations of the channels, the address of return of each, and the few settings of the client an
/// interactive sign-in needs. A host that registers a client of its own keeps every other setting of
/// it. The state of a sign-in lands in the token store the server already uses, so the schema does not
/// change and the existing pruning of tokens clears it.
/// </summary>
internal static class WebProviderClientComposition
{
    /// <summary>
    /// How far before the answer of OpenIddict to an error at the address of return the pass-through of
    /// that error to the route of a channel runs.
    /// </summary>
    private const int CallbackErrorPassthroughOrderOffset = 500;

    /// <summary>
    /// <see cref="Typed{TBuilder}"/>, closed over the builder of each provider at composition.
    /// </summary>
    private static readonly MethodInfo TypedConfigure = typeof(WebProviderClientComposition)
        .GetMethod(nameof(Typed), BindingFlags.NonPublic | BindingFlags.Static)!;

    /// <summary>
    /// Registers the OpenIddict client with one registration per enabled channel.
    /// <list type="bullet">
    /// <item>the authorization code flow — the flow every web provider signs in with;</item>
    /// <item>the address of return of every channel, added to the redirection endpoints, and the
    /// pass-through mode of that endpoint: the routes of the channel take the request over once
    /// OpenIddict has validated it;</item>
    /// <item>an error at the address of return of a channel — the provider's own, a cancellation by the
    /// user, a state that did not pass — passed on to the route of the channel as well, which answers it
    /// with the page of the channel. This handler acts on those addresses alone: the error answer of every
    /// other endpoint of the client stays as the host has it;</item>
    /// <item>the HTTP transport and the provider definitions of the package;</item>
    /// <item>the signing and encryption keys of the server of the same host: the client protects the
    /// state of a sign-in with them, and no key material of its own is introduced.</item>
    /// </list>
    /// </summary>
    /// <param name="services">Service collection.</param>
    /// <param name="registrations">The enabled channels.</param>
    public static void AddClient(IServiceCollection services, IReadOnlyList<WebProviderRegistration> registrations)
    {
        services.AddOpenIddict()
            .AddClient(client =>
            {
                client.AllowAuthorizationCodeFlow();

                // Added one by one rather than set: setting the endpoints would replace those of a client
                // the host registered itself. The relative form is resolved against the base address of
                // the request, path base included, the way the endpoints of the server are.
                client.Configure(options =>
                {
                    foreach (var registration in registrations)
                    {
                        options.RedirectionEndpointUris.Add(
                            new Uri(WebProviderRoutes.CallbackPath(registration.ChannelType), UriKind.Relative));
                    }
                });

                client.UseAspNetCore()
                      .EnableRedirectionEndpointPassthrough();

                AddCallbackErrorPassthrough(client, registrations);

                client.UseSystemNetHttp();

                var providers = client.UseWebProviders();

                foreach (var registration in registrations)
                {
                    AddRegistration(providers, registration);
                }
            });

        services.AddOptions<OpenIddictClientOptions>()
            .Configure<IOptions<OpenIddictServerOptions>>(static (client, server) =>
            {
                client.SigningCredentials.AddRange(server.Value.SigningCredentials);
                client.EncryptionCredentials.AddRange(server.Value.EncryptionCredentials);
            });
    }

    /// <summary>
    /// Passes an error at the address of return of a channel on to the route of the channel, the way the
    /// error pass-through mode of OpenIddict does — but for those addresses only, since that mode is a
    /// setting of the whole client. The handler runs before the redirection endpoint answers the error:
    /// that answer — the status code pages or the local plain-text error of OpenIddict — is written by
    /// the event OpenIddict raises from that step, so a handler ordered after it never takes effect.
    /// </summary>
    /// <param name="client">Builder of the client.</param>
    /// <param name="registrations">The enabled channels.</param>
    private static void AddCallbackErrorPassthrough(
        OpenIddictClientBuilder client,
        IReadOnlyList<WebProviderRegistration> registrations)
    {
        var callbackPaths = registrations
            .Select(registration => WebProviderRoutes.RoutePath(registration.ChannelType, WebProviderRoutes.CallbackSegment))
            .ToFrozenSet(StringComparer.OrdinalIgnoreCase);

        client.AddEventHandler<OpenIddictClientEvents.ProcessErrorContext>(handler => handler
            .UseInlineHandler(context =>
            {
                if (context.EndpointType is OpenIddictClientEndpointType.Redirection
                    && context.Transaction.GetHttpRequest() is { } request
                    && request.Path.Value is { } path
                    && callbackPaths.Contains(path))
                {
                    context.SkipRequest();
                }

                return ValueTask.CompletedTask;
            })
            .SetOrder(OpenIddictClientHandlers.Authentication.ApplyRedirectionResponse<OpenIddictClientEvents.ProcessErrorContext>.Descriptor.Order - CallbackErrorPassthroughOrderOffset)
            .SetType(OpenIddictClientHandlerType.Custom));
    }

    /// <summary>
    /// Adds the registration of one channel through the add method of its provider. The method takes a
    /// delegate over the builder type of that provider, so the delegate is built for that type here.
    /// </summary>
    /// <param name="providers">The provider definitions of the client.</param>
    /// <param name="registration">The channel.</param>
    private static void AddRegistration(OpenIddictClientWebIntegrationBuilder providers, WebProviderRegistration registration)
    {
        Action<object> configure = builder => Configure(builder, registration);

        var typed = TypedConfigure
            .MakeGenericMethod(registration.Provider.BuilderType)
            .Invoke(null, new object?[] { configure });

        registration.Provider.AddMethod.Invoke(
            providers,
            BindingFlags.DoNotWrapExceptions,
            binder: null,
            new[] { typed },
            culture: null);
    }

    /// <summary>
    /// Wraps a delegate over any builder into one over the builder type a provider's add method takes.
    /// </summary>
    /// <typeparam name="TBuilder">Builder type of the provider.</typeparam>
    /// <param name="configure">Configuration of the builder.</param>
    /// <returns>The delegate the add method takes.</returns>
    private static Action<TBuilder> Typed<TBuilder>(Action<object> configure)
        where TBuilder : notnull =>
        builder => configure(builder);

    /// <summary>
    /// States the registration of one channel on the builder of its provider. The registration
    /// identifier and the provider name are the channel type: several channels on one provider — two
    /// clients of Google — are told apart by them, and the authentication scheme OpenIddict forwards per
    /// provider name stays one per channel. The settings of the provider's own and the signing key are
    /// applied through the setters of the builder, which is what a provider definition validates.
    /// </summary>
    /// <param name="builder">Builder of the provider.</param>
    /// <param name="registration">The channel.</param>
    private static void Configure(object builder, WebProviderRegistration registration)
    {
        var target = (OpenIddictClientRegistration)registration.Provider.RegistrationProperty.GetValue(builder)!;

        target.RegistrationId = registration.ChannelType;
        target.ProviderName = registration.ChannelType;
        target.ClientId = registration.ClientId;
        target.ClientSecret = registration.ClientSecret;
        target.RedirectUri = registration.RedirectUri;
        target.Scopes.UnionWith(registration.Scopes);

        foreach (var setting in registration.Settings)
        {
            setting.Setter.Invoke(
                builder,
                BindingFlags.DoNotWrapExceptions,
                binder: null,
                new object?[] { setting.Value },
                culture: null);
        }

        if (registration.SigningKey is not null && registration.Provider.SigningKeySetter is { } signingKeySetter)
        {
            signingKeySetter.Invoke(
                builder,
                BindingFlags.DoNotWrapExceptions,
                binder: null,
                new object?[] { registration.SigningKey },
                culture: null);
        }
    }
}
