// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Microsoft.Extensions.Logging;

using Veriqa.Core.Configuration;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// Evaluates restriction policies. Stateless and pure: the decision follows from the snapshot and the
/// set handed in, with no configuration or storage access and no network call (RP-043).
/// </summary>
/// <remarks>
/// Elements of the set are evaluated in order and all of them have to admit the sender; inside an
/// element the rules combine by the element's own mode. Whatever the shape validation should have
/// rejected but did not — an unknown mode, operator or missing-claim behaviour — refuses rather than
/// admits: a policy that cannot be understood must not open the perimeter (RP-052).
/// </remarks>
internal sealed class RestrictionPolicyEvaluator(
    IRestrictionClaimReader claimReader,
    ILogger<RestrictionPolicyEvaluator> logger) : IRestrictionPolicyEvaluator
{
    /// <summary>
    /// Leading character of an E.164 number, which a country code in the configuration is stated
    /// without (RP-026).
    /// </summary>
    private const char E164Prefix = '+';

    /// <summary>
    /// How one rule ended.
    /// </summary>
    private enum RuleOutcome
    {
        /// <summary>
        /// The rule admits the sender.
        /// </summary>
        Passed,

        /// <summary>
        /// The rule refuses the sender.
        /// </summary>
        Failed,

        /// <summary>
        /// The rule takes no part in the evaluation: its claim is absent and it asked to be skipped.
        /// </summary>
        Skipped
    }

    /// <summary>
    /// Outcome of one rule, with the generalized reason when the rule failed.
    /// </summary>
    /// <param name="Outcome">How the rule ended.</param>
    /// <param name="Category">
    /// Reason of the failure — one of <see cref="RestrictionDenyCategories"/>; null unless the rule
    /// failed.
    /// </param>
    private readonly record struct RuleResult(RuleOutcome Outcome, string? Category)
    {
        /// <summary>
        /// The rule admits the sender.
        /// </summary>
        public static RuleResult Passed { get; } = new(RuleOutcome.Passed, Category: null);

        /// <summary>
        /// The rule takes no part in the evaluation.
        /// </summary>
        public static RuleResult Skipped { get; } = new(RuleOutcome.Skipped, Category: null);

        /// <summary>
        /// The rule refuses the sender.
        /// </summary>
        /// <param name="category">Reason of the failure.</param>
        /// <returns>Failed outcome carrying the reason.</returns>
        public static RuleResult Failed(string category) => new(RuleOutcome.Failed, category);
    }

    /// <inheritdoc />
    public RestrictionPolicyDecision Evaluate(ChannelIdentitySnapshot identity, RestrictionPolicySet? policies)
    {
        ArgumentNullException.ThrowIfNull(identity);

        if (policies is null)
        {
            return RestrictionPolicyDecision.Allowed;
        }

        foreach (var layer in policies.Layers)
        {
            var decision = EvaluateLayer(identity, layer);
            if (decision.IsDenied)
            {
                // The first refusal ends the evaluation (RP-046): the elements after it are not read.
                return decision;
            }
        }

        return RestrictionPolicyDecision.Allowed;
    }

    /// <summary>
    /// Evaluates one element of the set under its own mode.
    /// </summary>
    private RestrictionPolicyDecision EvaluateLayer(ChannelIdentitySnapshot identity, RestrictionPolicyLayer layer)
    {
        // A refusing element comes first: its level states a policy that could not be read, and it
        // must never be mistaken for a disabled one.
        if (layer.Policy is null)
        {
            return new RestrictionPolicyDecision
            {
                IsDenied = true,
                Level = layer.Level,
                IsUnreadablePolicy = true
            };
        }

        var policy = layer.Policy;
        if (!policy.Enabled || policy.Rules.Count is 0)
        {
            return RestrictionPolicyDecision.Allowed;
        }

        switch (policy.Mode)
        {
            case RestrictionPolicyModes.AllMustPass:
                return EvaluateAllMustPass(identity, policy, layer.Level);

            case RestrictionPolicyModes.AnyMayPass:
                return EvaluateAnyMayPass(identity, policy, layer.Level);

            default:
                logger.LogWarning(
                    "Restriction policy states an unknown mode; the sender is refused. Mode: {Mode}, Level: {Level}",
                    policy.Mode,
                    layer.Level);

                return Denied(RestrictionDenyCategories.MissingClaim, policy.Rules[0].Field, layer.Level);
        }
    }

    /// <summary>
    /// Every rule has to pass: the first one that fails refuses, and the rules after it are not read.
    /// </summary>
    private RestrictionPolicyDecision EvaluateAllMustPass(
        ChannelIdentitySnapshot identity,
        ChannelRestrictionPolicyOptions policy,
        ConfigLevel level)
    {
        foreach (var rule in policy.Rules)
        {
            var (outcome, category) = EvaluateRule(identity, rule, level);
            if (outcome is RuleOutcome.Failed)
            {
                return Denied(category, rule.Field, level);
            }
        }

        return RestrictionPolicyDecision.Allowed;
    }

    /// <summary>
    /// One passing rule is enough. With none passing, the refusal names the FIRST rule that failed, so
    /// the decision depends on the order of the rules alone; an element whose rules were all skipped
    /// demands nothing and admits.
    /// </summary>
    private RestrictionPolicyDecision EvaluateAnyMayPass(
        ChannelIdentitySnapshot identity,
        ChannelRestrictionPolicyOptions policy,
        ConfigLevel level)
    {
        RestrictionPolicyDecision? firstFailure = null;

        foreach (var rule in policy.Rules)
        {
            var (outcome, category) = EvaluateRule(identity, rule, level);
            if (outcome is RuleOutcome.Passed)
            {
                return RestrictionPolicyDecision.Allowed;
            }

            if (outcome is RuleOutcome.Failed)
            {
                firstFailure ??= Denied(category, rule.Field, level);
            }
        }

        return firstFailure ?? RestrictionPolicyDecision.Allowed;
    }

    /// <summary>
    /// Evaluates one rule. The category is stated only for a failed rule and tells a missing claim
    /// from a claim outside the allow-list and from a claim on the deny-list (RP-053).
    /// </summary>
    private RuleResult EvaluateRule(
        ChannelIdentitySnapshot identity,
        RestrictionRuleOptions rule,
        ConfigLevel level)
    {
        var claim = claimReader.Read(identity, rule.Field);

        switch (rule.Operator)
        {
            // The presence operators decide on presence alone and never consult the missing-claim
            // behaviour (RP-051).
            case RestrictionOperators.IsPresent:
                return claim is not null
                    ? RuleResult.Passed
                    : RuleResult.Failed(RestrictionDenyCategories.MissingClaim);

            case RestrictionOperators.IsAbsent:
                return claim is null
                    ? RuleResult.Passed
                    : RuleResult.Failed(RestrictionDenyCategories.InDenyList);

            case RestrictionOperators.InList:
            case RestrictionOperators.Equals:
                if (claim is null)
                {
                    return ApplyOnMissing(rule, level);
                }

                return MatchesAnyValue(rule, claim)
                    ? RuleResult.Passed
                    : RuleResult.Failed(RestrictionDenyCategories.NotInAllowList);

            case RestrictionOperators.NotInList:
            case RestrictionOperators.NotEquals:
                if (claim is null)
                {
                    return ApplyOnMissing(rule, level);
                }

                return MatchesAnyValue(rule, claim)
                    ? RuleResult.Failed(RestrictionDenyCategories.InDenyList)
                    : RuleResult.Passed;

            default:
                logger.LogWarning(
                    "Restriction policy rule states an unknown operator; the rule fails. Field: {Field}, Operator: {Operator}, Level: {Level}",
                    rule.Field,
                    rule.Operator,
                    level);

                return RuleResult.Failed(RestrictionDenyCategories.MissingClaim);
        }
    }

    /// <summary>
    /// Outcome of a comparing rule whose claim is absent (SPEC-018 §7).
    /// </summary>
    private RuleResult ApplyOnMissing(RestrictionRuleOptions rule, ConfigLevel level)
    {
        switch (rule.OnMissing)
        {
            case RestrictionOnMissing.Deny:
                return RuleResult.Failed(RestrictionDenyCategories.MissingClaim);

            case RestrictionOnMissing.Allow:
                return RuleResult.Passed;

            case RestrictionOnMissing.Skip:
                return RuleResult.Skipped;

            default:
                logger.LogWarning(
                    "Restriction policy rule states an unknown missing-claim behaviour; the rule fails. Field: {Field}, OnMissing: {OnMissing}, Level: {Level}",
                    rule.Field,
                    rule.OnMissing,
                    level);

                return RuleResult.Failed(RestrictionDenyCategories.MissingClaim);
        }
    }

    /// <summary>
    /// Whether the claim matches at least one value of the rule. The list operators and the equality
    /// operators differ only in how many values the rule carries, so they share this one test.
    /// </summary>
    private static bool MatchesAnyValue(RestrictionRuleOptions rule, string claim)
    {
        var matches = SelectComparison(rule.Field);

        foreach (var value in rule.Values)
        {
            if (matches(claim, value))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// The comparison of a field — the one place it is chosen. Operators call what this returns and
    /// compare nothing themselves.
    /// </summary>
    private static Func<string, string, bool> SelectComparison(string field) => field switch
    {
        RestrictionPolicyFields.PhoneCountryCode => HasCountryCode,
        _ => EqualsIgnoringCase
    };

    /// <summary>
    /// Case-insensitive equality — the comparison of every field but the country code (RP-022).
    /// </summary>
    private static bool EqualsIgnoringCase(string claim, string value) =>
        string.Equals(claim, value, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether an E.164 number starts with a country code (RP-026). Country codes are prefix-free —
    /// none begins another — so the prefix test is an exact comparison of codes and needs no table
    /// of them. An empty code is the code of no country and matches nothing.
    /// </summary>
    private static bool HasCountryCode(string phoneNumber, string countryCode) =>
        !string.IsNullOrEmpty(countryCode)
        && phoneNumber.Length > 0
        && phoneNumber[0] is E164Prefix
        && phoneNumber.AsSpan(1).StartsWith(countryCode, StringComparison.Ordinal);

    /// <summary>
    /// A refusal by a rule.
    /// </summary>
    private static RestrictionPolicyDecision Denied(string? category, string field, ConfigLevel level) => new()
    {
        IsDenied = true,
        Category = category,
        Field = field,
        Level = level
    };
}
