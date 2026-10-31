// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Options;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Start-up validation of the restriction policies section (RP-082): a policy of a broken form stops
/// the host instead of being found by the events it refuses.
/// </summary>
/// <remarks>
/// Every policy of the section is checked, a disabled one included: a policy is switched on by one
/// member, and a rule that was never valid must not wait for that moment to be noticed. The check
/// itself is <see cref="RestrictionPolicyValidation"/> — the one the resolver keys ask of a level.
/// </remarks>
internal sealed class RestrictionPoliciesOptionsValidator : IValidateOptions<RestrictionPoliciesOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, RestrictionPoliciesOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();

        failures.AddRange(RestrictionPolicyValidation.Validate(options.Common, RestrictionPoliciesOptions.CommonAddress));

        if (options.ByChannel is { } byChannel)
        {
            foreach (var (channelType, policy) in byChannel)
            {
                failures.AddRange(RestrictionPolicyValidation.Validate(
                    policy,
                    RestrictionPoliciesOptions.ChannelAddress(channelType)));
            }
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
