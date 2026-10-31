// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using OpenTelemetry;

namespace Veriqa.ServiceDefaults;

/// <summary>
/// Removes secrets carried in a URL path from HTTP spans before an exporter reads them. The runtime and
/// the OpenTelemetry instrumentation redact the query string and user info only, so a path stays as sent:
/// the Telegram Bot API takes the bot token as a path segment, and a hop URL carries its token the same way.
/// </summary>
internal sealed partial class SecretUrlPathRedactionProcessor : BaseProcessor<Activity>
{
    private const string UrlFullTag = "url.full";
    private const string UrlPathTag = "url.path";
    private const string HttpRouteTag = "http.route";
    private const string HttpStatusCodeTag = "http.response.status_code";

    /// <summary>End of a route parameter whose value is a token, such as <c>{hop_token}</c>.</summary>
    private const string TokenRouteParameterEnd = "token}";

    private const string BotTokenPattern = "(?<!/)/bot[^/?#]*:[^/?#]*";
    private const string BotTokenMask = "/bot***";

    private const string LastPathSegmentPattern = "[^/]+/?$";
    private const string LastPathSegmentMask = "***";

    /// <inheritdoc />
    public override void OnEnd(Activity data)
    {
        if (data.Kind == ActivityKind.Client)
        {
            RedactBotToken(data);
        }
        else if (data.Kind == ActivityKind.Server)
        {
            RedactTokenRouteValue(data);
        }
    }

    // The pattern needs the colon that Telegram.Bot requires in a token, so "/bots/1" or "/v2/bot/message" of
    // another service keeps its address. The lookbehind leaves a host named "bot" alone in "http://bot:8081".
    private static void RedactBotToken(Activity activity)
    {
        if (activity.GetTagItem(UrlFullTag) is string url && BotTokenSegment().IsMatch(url))
        {
            activity.SetTag(UrlFullTag, BotTokenSegment().Replace(url, BotTokenMask));
        }
    }

    // The route template names the parameter, so the configurable base path of a hop URL needs no setting
    // here. An error without a template can still come from a token route, for example a 405 to a HEAD sent to
    // a hop URL, so its last segment goes: a token parameter ends its route. A 404 keeps its path, as the one
    // error where the address is the thing to diagnose. A success without a template keeps it too: OpenIddict
    // and static files answer that way, and their addresses are what a dashboard groups by.
    private static void RedactTokenRouteValue(Activity activity)
    {
        if (activity.GetTagItem(HttpRouteTag) is string route)
        {
            if (route.Contains(TokenRouteParameterEnd, StringComparison.OrdinalIgnoreCase))
            {
                activity.SetTag(UrlPathTag, route);
            }
        }
        else if (activity.GetTagItem(HttpStatusCodeTag)
                is >= StatusCodes.Status400BadRequest and not StatusCodes.Status404NotFound
            && activity.GetTagItem(UrlPathTag) is string path)
        {
            activity.SetTag(UrlPathTag, LastPathSegment().Replace(path, LastPathSegmentMask));
        }
    }

    [GeneratedRegex(BotTokenPattern, RegexOptions.CultureInvariant)]
    private static partial Regex BotTokenSegment();

    [GeneratedRegex(LastPathSegmentPattern, RegexOptions.CultureInvariant)]
    private static partial Regex LastPathSegment();
}
