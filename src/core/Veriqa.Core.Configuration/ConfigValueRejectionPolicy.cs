// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.Configuration;

/// <summary>
/// What a setting does about a value outside its domain (SPEC-012 §10.6, CFG-240). It is a property
/// of the KEY, declared by its owner next to the domain, because the answer differs by setting rather
/// than by deployment: for one setting a quiet substitution is the safe outcome, for another it is
/// the dangerous one, and only the owner of the setting knows which.
/// </summary>
public enum ConfigValueRejectionPolicy
{
    /// <summary>
    /// The step of the chain that stated the value is left unset and the resolution goes on — to the
    /// next step of the same level, and then to the level below (CFG-240, the behaviour of every
    /// setting before the policy existed). A broken entry of one owner does not stop the host, and the
    /// operator is told about it once per configuration snapshot rather than once per request.
    /// <para>
    /// At the CORE level there is no level below: once the chain of the level is exhausted, the level
    /// answers as over a record stating nothing — with the value its owner declared for it, where one
    /// was declared — rather than leaving the consumer with the default of the type.
    /// </para>
    /// </summary>
    SkipStep = 0,

    /// <summary>
    /// The setting refuses to be served the value of the level below in place of one it does not admit.
    /// It is the choice for a setting where a quiet substitution is worse than the loss: the deployment
    /// learns about the broken value instead of serving a request with somebody else's value in its
    /// place.
    /// <para>
    /// WHAT that costs is decided by the LEVEL the inadmissible value lies at (SPEC-012 §4.1 CFG-246),
    /// because what there is to give up differs by level:
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Above the core level</b> — the RECORD holding the value (the entry of one OIDC client, one
    /// <c>ui_config</c> record) is left out of the effective configuration as a whole, exactly as a
    /// record that did not bind is. Its neighbouring records are untouched, and the host starts: a
    /// broken entry of one owner taking the host down is what <see cref="SkipStep"/> exists to prevent.
    /// The price is named rather than hidden — every OTHER setting of that record is resolved from the
    /// level below as well, so this is not a promise that the setting will not be substituted, but a
    /// promise that a substitution is loud and wholesale rather than quiet and piecemeal.
    /// </description></item>
    /// <item><description>
    /// <b>At the core level</b> — there is no record to give up and no level below to cede to, so at
    /// START the host does not start (an operator fixes the configuration; no value has ever been read
    /// successfully, so there is nothing to fall back to), and on a RELOAD the level answers with the
    /// last value it was successfully read with and the host goes on serving. A reload that brings an
    /// inadmissible value where the core level stated nothing before takes nothing away, and the step is
    /// dropped by the default of CFG-240. The last value is kept by the resolution, not by the walk: a
    /// level no resolution has read since start has none, and only a value the DOMAIN refused is
    /// answered with it — a value that cannot be read into the type of the setting leaves the step
    /// unset on the resolution path, by the same default of CFG-240.
    /// </description></item>
    /// </list>
    /// <para>
    /// Both outcomes are enforced at the SNAPSHOT boundary — the walk of the configuration at start and
    /// on each reload — so they cover the levels a catalog is registered for
    /// (<see cref="IConfigSnapshotCatalog"/>). A level whose source does not enumerate its records is
    /// outside that walk and is named as such at start; a value stated there is dropped by the
    /// resolution path exactly as <see cref="SkipStep"/> is.
    /// </para>
    /// </summary>
    FailStart = 1
}
