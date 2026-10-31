// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

namespace Veriqa.Core.ChannelAdapter.Email.Constants;

/// <summary>
/// Field names of the JSON of the Pull start and of the entry of the code of a sign-in mail that the pages
/// of the core read: the start answers whether the mail carries a code, the entry of the code takes the code
/// and answers where the page goes next. The answers name every field explicitly, so that the naming policy
/// of the host's JSON options does not rename what the scripts of the pages read.
/// </summary>
internal static class EmailPullJsonFields
{
    /// <summary>
    /// Every answer: whether the request was accepted.
    /// </summary>
    public const string Success = "success";

    /// <summary>
    /// A refused request: the text of the refusal, localized.
    /// </summary>
    public const string Error = "error";

    /// <summary>
    /// Answer of the start: the address the mail went to, masked.
    /// </summary>
    public const string MaskedEmail = "maskedEmail";

    /// <summary>
    /// Answer of the start: seconds before the next mail is accepted; zero — no interval.
    /// </summary>
    public const string RetryAfterSeconds = "retryAfterSeconds";

    /// <summary>
    /// Answer of the start: the mail carries a code, and the page asks for it.
    /// </summary>
    public const string CodeExpected = "code_expected";

    /// <summary>
    /// Body of the entry of the code: the code as the user typed it.
    /// </summary>
    public const string Code = "code";

    /// <summary>
    /// Answer of an accepted code: where the page goes by itself; null — the page stays and follows the
    /// status of the transaction.
    /// </summary>
    public const string NextUrl = "next_url";

    /// <summary>
    /// Answer of an accepted code: the line the page of the link shows for the same outcome; stated only
    /// when the page stays.
    /// </summary>
    public const string Message = "message";

    /// <summary>
    /// Answer of a wrong code: attempts left of the code.
    /// </summary>
    public const string AttemptsLeft = "attempts_left";
}
