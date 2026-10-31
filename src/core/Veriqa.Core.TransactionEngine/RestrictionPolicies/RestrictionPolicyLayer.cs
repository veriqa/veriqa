// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.Configuration;

namespace Veriqa.Core.TransactionEngine.RestrictionPolicies;

/// <summary>
/// One element of a <see cref="RestrictionPolicySet"/>: the policy a configuration level stated,
/// marked with that level when the level was read. The mark is what the audit record names as the
/// source of a refusal (RP-075).
/// </summary>
/// <param name="Level">Configuration level the policy was read at.</param>
/// <param name="Policy">
/// The policy of the level. Null marks a refusing element: the level states a policy that could not
/// be read, and the element refuses every sender instead of letting the level's restriction vanish.
/// </param>
internal sealed record RestrictionPolicyLayer(ConfigLevel Level, ChannelRestrictionPolicyOptions? Policy);
