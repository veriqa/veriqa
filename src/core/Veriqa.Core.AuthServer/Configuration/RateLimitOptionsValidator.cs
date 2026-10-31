// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.AuthServer.Configuration;

/// <summary>
/// Validator for rate-limiting options.
/// Ensures that all limits and periods are positive.
/// </summary>
public sealed class RateLimitOptionsValidator : IValidateOptions<RateLimitOptions>
{
    /// <summary>
    /// Validates the rate-limiting options.
    /// </summary>
    /// <param name="name">Name of the options instance.</param>
    /// <param name="options">Options instance to validate.</param>
    /// <returns>Validation result.</returns>
    public ValidateOptionsResult Validate(string? name, RateLimitOptions options)
    {
        // Verify that all limits and windows are positive
        var failures = new List<string>();

        if (options.AuthorizePermitLimit <= 0)
        {
            failures.Add("AuthorizePermitLimit must be greater than 0.");
        }

        if (options.AuthorizeWindowSeconds <= 0)
        {
            failures.Add("AuthorizeWindowSeconds must be greater than 0.");
        }

        if (options.SignalRPermitLimit <= 0)
        {
            failures.Add("SignalRPermitLimit must be greater than 0.");
        }

        if (options.SignalRWindowSeconds <= 0)
        {
            failures.Add("SignalRWindowSeconds must be greater than 0.");
        }

        if (options.WebhookPermitLimit <= 0)
        {
            failures.Add("WebhookPermitLimit must be greater than 0.");
        }

        if (options.WebhookWindowSeconds <= 0)
        {
            failures.Add("WebhookWindowSeconds must be greater than 0.");
        }

        if (options.TokenPermitLimit <= 0)
        {
            failures.Add("TokenPermitLimit must be greater than 0.");
        }

        if (options.TokenWindowSeconds <= 0)
        {
            failures.Add("TokenWindowSeconds must be greater than 0.");
        }

        if (options.CallbackPermitLimit <= 0)
        {
            failures.Add("CallbackPermitLimit must be greater than 0.");
        }

        if (options.CallbackWindowSeconds <= 0)
        {
            failures.Add("CallbackWindowSeconds must be greater than 0.");
        }

        if (options.PollingPermitLimit <= 0)
        {
            failures.Add("PollingPermitLimit must be greater than 0.");
        }

        if (options.PollingWindowSeconds <= 0)
        {
            failures.Add("PollingWindowSeconds must be greater than 0.");
        }

        if (options.UserAuthPermitLimit <= 0)
        {
            failures.Add("UserAuthPermitLimit must be greater than 0.");
        }

        if (options.UserAuthWindowSeconds <= 0)
        {
            failures.Add("UserAuthWindowSeconds must be greater than 0.");
        }

        if (options.UserAuthQueueLimit < 0)
        {
            failures.Add("UserAuthQueueLimit must not be negative.");
        }

        if (options.UserAuthMaxPartitions <= 0)
        {
            failures.Add("UserAuthMaxPartitions must be greater than 0.");
        }

        if (options.EmailStartPermitLimit <= 0)
        {
            failures.Add("EmailStartPermitLimit must be greater than 0.");
        }

        if (options.EmailStartWindowSeconds <= 0)
        {
            failures.Add("EmailStartWindowSeconds must be greater than 0.");
        }

        if (options.ConfirmationCreatePermitLimit <= 0)
        {
            failures.Add("ConfirmationCreatePermitLimit must be greater than 0.");
        }

        if (options.ConfirmationCreateWindowSeconds <= 0)
        {
            failures.Add("ConfirmationCreateWindowSeconds must be greater than 0.");
        }

        if (options.ConfirmationResultPermitLimit <= 0)
        {
            failures.Add("ConfirmationResultPermitLimit must be greater than 0.");
        }

        if (options.ConfirmationResultWindowSeconds <= 0)
        {
            failures.Add("ConfirmationResultWindowSeconds must be greater than 0.");
        }

        if (options.ConfirmationPagesPermitLimit <= 0)
        {
            failures.Add("ConfirmationPagesPermitLimit must be greater than 0.");
        }

        if (options.ConfirmationPagesWindowSeconds <= 0)
        {
            failures.Add("ConfirmationPagesWindowSeconds must be greater than 0.");
        }

        if (options.HopResolvePermitLimit <= 0)
        {
            failures.Add("HopResolvePermitLimit must be greater than 0.");
        }

        if (options.HopResolveWindowSeconds <= 0)
        {
            failures.Add("HopResolveWindowSeconds must be greater than 0.");
        }

        if (options.HopSignalPermitLimit <= 0)
        {
            failures.Add("HopSignalPermitLimit must be greater than 0.");
        }

        if (options.HopSignalWindowSeconds <= 0)
        {
            failures.Add("HopSignalWindowSeconds must be greater than 0.");
        }

        if (failures.Count > 0)
        {
            return ValidateOptionsResult.Fail(failures);
        }

        return ValidateOptionsResult.Success;
    }
}
