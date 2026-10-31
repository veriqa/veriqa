// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

using Veriqa.Core.ChannelAdapter.Constants;
using Veriqa.Core.TransactionEngine.Constants;

namespace Veriqa.ServiceDefaults;

/// <summary>
/// Extension methods for wiring up common Aspire service defaults.
/// Includes OpenTelemetry, health checks, service discovery and HTTP resilience.
/// </summary>
public static class Extensions
{
    /// <summary>
    /// Registers common Aspire service defaults: telemetry, health checks,
    /// service discovery and HTTP resilience.
    /// </summary>
    /// <param name="builder">Host application builder.</param>
    /// <returns>The host application builder for chaining.</returns>
    public static IHostApplicationBuilder AddServiceDefaults(this IHostApplicationBuilder builder)
    {
        // Wire up service discovery
        builder.Services.AddServiceDiscovery();

        // Configure OpenTelemetry
        ConfigureOpenTelemetry(builder);

        // Register the standard health checks
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        // Configure default HTTP clients with service discovery and resilience
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            // Wire up the standard resilience handler
            http.AddStandardResilienceHandler();

            // Wire up service discovery for all HTTP clients
            http.AddServiceDiscovery();
        });

        return builder;
    }

    /// <summary>
    /// Configuration key with the list of additional trusted proxies (IP addresses).
    /// </summary>
    private const string KnownProxiesConfigKey = "ForwardedHeaders:KnownProxies";

    /// <summary>
    /// Configuration key with the list of additional trusted networks (CIDR, e.g. "10.0.0.0/8").
    /// </summary>
    private const string KnownIPNetworksConfigKey = "ForwardedHeaders:KnownIPNetworks";

    /// <summary>
    /// Configures ForwardedHeaders for running behind TLS termination / proxy / tunnel:
    /// the application trusts the X-Forwarded-For and X-Forwarded-Proto headers
    /// <b>only from trusted sources</b>.
    /// </summary>
    /// <remarks>
    /// The ASP.NET Core default trusted sources (loopback) are preserved — the tunnel connector
    /// (e.g. cloudflared) forwards traffic through localhost and is covered by them.
    /// Unconditional XFF trust (clearing KnownProxies/KnownIPNetworks) is deliberately NOT used:
    /// otherwise an external client with a forged X-Forwarded-For could spoof its IP
    /// (bypassing per-IP rate limiting, false addresses in logs). For topologies with non-loopback
    /// proxies the trusted sources are extended via configuration:
    /// <c>ForwardedHeaders:KnownProxies</c> (IP addresses) and
    /// <c>ForwardedHeaders:KnownIPNetworks</c> (CIDR ranges).
    /// A single configuration point eliminates drift between hosts consuming ServiceDefaults.
    /// </remarks>
    /// <param name="services">Service collection.</param>
    /// <param name="configuration">Application configuration (source of additional trusted proxies/networks).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection ConfigureForwardedHeadersForProxy(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            // Additional trusted proxies/networks from configuration (an invalid value is
            // a configuration error; a FormatException at startup is preferable to silent ignore)
            foreach (var proxy in configuration.GetSection(KnownProxiesConfigKey).Get<string[]>() ?? [])
            {
                options.KnownProxies.Add(IPAddress.Parse(proxy));
            }

            foreach (var network in configuration.GetSection(KnownIPNetworksConfigKey).Get<string[]>() ?? [])
            {
                // System.Net.IPNetwork is fully qualified: the name conflicts with the obsolete
                // Microsoft.AspNetCore.HttpOverrides.IPNetwork from the same namespace scope
                options.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(network));
            }
        });

        return services;
    }

    /// <summary>
    /// Registers the standard application endpoints: health checks.
    /// </summary>
    /// <param name="app">Web application instance.</param>
    /// <returns>The web application instance for chaining.</returns>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        // Health endpoints are exempted from rate limiting: the orchestrator's liveness/readiness
        // probes must not receive 429 (otherwise a live service is wrongly marked unhealthy).
        // DisableRateLimiting removes both the global limiter and endpoint policies;
        // for hosts without a rate limiter it is a safe no-op.

        // Aggregate endpoint — /health: runs every registered check, with no filter by tag, so its
        // status is the worst status among all of them. Readiness is a separate endpoint,
        // /health/ready, filtered by the readiness tag and mapped by the host, not here.
        app.MapHealthChecks("/health")
            .DisableRateLimiting();

        // Liveness endpoint — /alive, checks only probes tagged "live"
        app.MapHealthChecks("/alive", new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains("live")
        })
            .DisableRateLimiting();

        return app;
    }

    /// <summary>
    /// Configures OpenTelemetry: logging, metrics and tracing.
    /// </summary>
    /// <param name="builder">Host application builder.</param>
    private static void ConfigureOpenTelemetry(IHostApplicationBuilder builder)
    {
        // Wire up logs through OpenTelemetry
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        // Configure metrics and tracing
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics =>
            {
                // ASP.NET Core metrics
                metrics.AddAspNetCoreInstrumentation();

                // HTTP client metrics
                metrics.AddHttpClientInstrumentation();

                // .NET runtime metrics
                metrics.AddRuntimeInstrumentation();

                // Channel adapter metrics: the component owns the instruments, the host subscribes.
                // The name is the shared constant, not a wildcard — a wildcard cannot be checked
                // against the meter it is supposed to match.
                metrics.AddMeter(ChannelTelemetry.MeterName);

                // Transaction lifecycle metrics: same arrangement, second component.
                metrics.AddMeter(TransactionTelemetry.MeterName);
            })
            .WithTracing(tracing =>
            {
                // ASP.NET Core tracing
                tracing.AddAspNetCoreInstrumentation();

                // HTTP client tracing
                tracing.AddHttpClientInstrumentation();

                // A secret in a URL path must not reach the exporter added below
                tracing.AddProcessor(new SecretUrlPathRedactionProcessor());

                // Spans of the product's own components: each component owns its activity source and
                // subscribes to nothing, so the host adds them here. By the shared constants, not by
                // a wildcard — a wildcard cannot be checked against the source it is meant to match.
                tracing.AddSource(ChannelTelemetry.ActivitySourceName);
                tracing.AddSource(TransactionTelemetry.ActivitySourceName);
            });

        // Export telemetry via OTLP if an endpoint is configured
        AddOpenTelemetryExporters(builder);
    }

    /// <summary>
    /// Wires up the OTLP exporter for sending telemetry if the environment variable is set.
    /// </summary>
    /// <param name="builder">Host application builder.</param>
    private static void AddOpenTelemetryExporters(IHostApplicationBuilder builder)
    {
        // Check for the OTLP endpoint via the environment variable
        var useOtlpExporter = !string.IsNullOrWhiteSpace(
            builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]);

        if (useOtlpExporter)
        {
            // Wire up the OTLP exporter for metrics and tracing
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }
    }
}
