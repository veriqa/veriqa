// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// The one check of the FORM of a restriction policy (RP-082). The start-up validation of the section
/// and the reading of a level by the resolver keys both ask it, so a policy the host started with and
/// a policy a reload or a client entry brought later are judged by the same rules.
/// </summary>
/// <remarks>
/// A message names the code of the configuration error, the address of the member and what is wrong
/// with it — never the text the member states: the messages travel to the log, and the lists of a
/// policy do not belong there (RP-090).
/// </remarks>
internal static class RestrictionPolicyValidation
{
    /// <summary>
    /// Checks one policy.
    /// </summary>
    /// <param name="policy">Policy as it was read.</param>
    /// <param name="sectionPath">Address of the policy, as the messages name it.</param>
    /// <returns>Messages about the problems found; an empty list — the policy is valid.</returns>
    public static IReadOnlyList<string> Validate(ChannelRestrictionPolicyOptions? policy, string sectionPath)
    {
        var problems = new List<string>();

        if (policy is null)
        {
            problems.Add(Problem(sectionPath, "states no policy"));

            return problems.AsReadOnly();
        }

        if (policy.Mode is null || !RestrictionPolicyModes.All.Contains(policy.Mode))
        {
            problems.Add(Problem(Member(sectionPath, nameof(policy.Mode)), "is not a known mode"));
        }

        var rulesPath = Member(sectionPath, nameof(policy.Rules));

        if (policy.Rules is null)
        {
            problems.Add(Problem(rulesPath, "states no list of rules"));

            return problems.AsReadOnly();
        }

        for (var index = 0; index < policy.Rules.Count; index++)
        {
            ValidateRule(policy.Rules[index], Member(rulesPath, index), problems);
        }

        return problems.AsReadOnly();
    }

    /// <summary>
    /// Checks one rule of a policy.
    /// </summary>
    /// <param name="rule">Rule as it was read; null for an element the configuration left empty.</param>
    /// <param name="rulePath">Address of the rule.</param>
    /// <param name="problems">Messages collected so far.</param>
    private static void ValidateRule(RestrictionRuleOptions? rule, string rulePath, List<string> problems)
    {
        if (rule is null)
        {
            problems.Add(Problem(rulePath, "states no rule"));

            return;
        }

        if (!RestrictionPolicyFields.IsKnown(rule.Field))
        {
            problems.Add(Problem(Member(rulePath, nameof(rule.Field)), "is not a known field"));
        }

        if (rule.Operator is null || !RestrictionOperators.All.Contains(rule.Operator))
        {
            problems.Add(Problem(Member(rulePath, nameof(rule.Operator)), "is not a known operator"));
        }

        if (rule.OnMissing is null || !RestrictionOnMissing.All.Contains(rule.OnMissing))
        {
            problems.Add(Problem(Member(rulePath, nameof(rule.OnMissing)), "is not a known missing-claim mode"));
        }

        ValidateValues(rule, Member(rulePath, nameof(rule.Values)), problems);
    }

    /// <summary>
    /// Checks the values of a rule against its operator and its field.
    /// </summary>
    /// <param name="rule">Rule being checked.</param>
    /// <param name="valuesPath">Address of the values of the rule.</param>
    /// <param name="problems">Messages collected so far.</param>
    private static void ValidateValues(RestrictionRuleOptions rule, string valuesPath, List<string> problems)
    {
        if (rule.Values is null)
        {
            problems.Add(Problem(valuesPath, "states no list of values"));

            return;
        }

        var comparesOne = rule.Operator is RestrictionOperators.Equals or RestrictionOperators.NotEquals;
        var comparesMany = rule.Operator is RestrictionOperators.InList or RestrictionOperators.NotInList;

        if ((comparesOne || comparesMany) && rule.Values.Count == 0)
        {
            problems.Add(Problem(valuesPath, "is empty while the operator compares the claim with a value"));
        }

        if (comparesOne && rule.Values.Count > 1)
        {
            problems.Add(Problem(valuesPath, "states several values while the operator compares the claim with one"));
        }

        // The country code is compared as a prefix of the number, "+" followed by the value (RP-026):
        // a value written with its own "+" or with anything but digits would never match a number.
        var isCountryCode = string.Equals(rule.Field, RestrictionPolicyFields.PhoneCountryCode, StringComparison.Ordinal);

        // A boolean field is read as one of two values: any other value would never match the claim,
        // and a rule excluding it would admit every sender.
        var isBoolean = rule.Field is RestrictionPolicyFields.IsBot or RestrictionPolicyFields.EmailVerified;

        for (var index = 0; index < rule.Values.Count; index++)
        {
            var value = rule.Values[index];
            var valuePath = Member(valuesPath, index);

            if (value is null)
            {
                problems.Add(Problem(valuePath, "states no value"));
            }
            else if (isCountryCode && !IsDigitsOnly(value))
            {
                problems.Add(Problem(valuePath, "is not a country code of digits alone, written without the leading plus"));
            }
            else if (isBoolean && !IsBooleanValue(value))
            {
                problems.Add(Problem(valuePath, "is not one of the two values a boolean field is compared with"));
            }
        }
    }

    /// <summary>
    /// Whether a text is one or more ASCII digits and nothing else.
    /// </summary>
    /// <param name="value">Text to check.</param>
    /// <returns><see langword="true"/> for a non-empty run of digits.</returns>
    private static bool IsDigitsOnly(string value) =>
        value.Length > 0 && value.AsSpan().IndexOfAnyExceptInRange('0', '9') < 0;

    /// <summary>
    /// Whether a text is one of the two values a boolean field is read as, in any letter case — the
    /// case the comparison of claim values ignores as well.
    /// </summary>
    /// <param name="value">Text to check.</param>
    /// <returns><see langword="true"/> for either value of a boolean field.</returns>
    private static bool IsBooleanValue(string value) =>
        string.Equals(value, RestrictionClaimValues.True, StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, RestrictionClaimValues.False, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Address of a member under an address.
    /// </summary>
    /// <param name="path">Address of the owner.</param>
    /// <param name="member">Name of the member.</param>
    /// <returns>Address of the member.</returns>
    private static string Member(string path, string member) =>
        string.Concat(path, ConfigNode.PathSeparator.ToString(), member);

    /// <summary>
    /// Address of an element of a list under an address.
    /// </summary>
    /// <param name="path">Address of the list.</param>
    /// <param name="index">Position of the element.</param>
    /// <returns>Address of the element.</returns>
    private static string Member(string path, int index) =>
        Member(path, index.ToString(CultureInfo.InvariantCulture));

    /// <summary>
    /// One message: the code of the configuration error, the address and what is wrong at it.
    /// </summary>
    /// <param name="path">Address the problem was found at.</param>
    /// <param name="what">What is wrong, without the text the member states.</param>
    /// <returns>Message.</returns>
    private static string Problem(string path, string what) =>
        $"{RestrictionPolicyResultCodes.Misconfigured}: '{path}' {what}.";
}
