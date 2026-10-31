// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MIT

namespace Veriqa.Core.ChannelAdapter.Constants;

/// <summary>
/// Ownership markers Veriqa puts into the payload of its own messenger buttons and deep links
/// (SPEC-003 §21.3, CA-194). They are the same for every channel: a router that has to tell a
/// Veriqa update from the integrator's own bot matches against these values, so they are contract,
/// not a per-adapter convention. A payload outside this registry is somebody else's event.
/// </summary>
public static class CallbackDataPrefixes
{
    /// <summary>
    /// Vendor namespace of the markers. Every button payload starts with it, which is what makes
    /// a Veriqa press distinguishable from a press on the integrator's own button — plain English
    /// words are not. Build your own marker from this prefix instead of repeating its value.
    /// </summary>
    public const string Vendor = "vq_";

    /// <summary>
    /// Operation confirmation prefix.
    /// </summary>
    public const string Confirm = Vendor + "confirm_";

    /// <summary>
    /// Operation decline prefix.
    /// </summary>
    public const string Decline = Vendor + "decline_";

    /// <summary>
    /// Authentication start prefix, used by deep links. Deliberately outside the vendor namespace:
    /// it travels inside the pre-filled text of a WhatsApp deep link, whose length is capped by the
    /// QR scannability budget (SPEC-003 §10.4.4). What narrows it instead is the value that follows
    /// it — a 43-character Base62 transaction identifier.
    /// </summary>
    public const string Auth = "auth_";

    /// <summary>
    /// Prefix of the button that skips the phone number in the phone step of claim completion
    /// (SPEC-041 L73); the transaction identifier follows it.
    /// </summary>
    public const string PhoneSkip = Vendor + "phone_skip_";

    /// <summary>
    /// Prefix of the button that cancels the sign-in in the phone step of claim completion
    /// (SPEC-041 L73); the transaction identifier follows it.
    /// </summary>
    public const string PhoneCancel = Vendor + "phone_cancel_";

    /// <summary>
    /// Prefix of a callback item of the post-sign-in menu (SPEC-012 §4.9); the value the operator
    /// configured for the item follows it. No transaction identifier travels in this payload: the
    /// press is handed to the host, not to a sign-in.
    /// </summary>
    public const string MenuItem = Vendor + "menu_";

    /// <summary>
    /// Every marker of this registry, in the order a scan tries them.
    /// </summary>
    private static readonly string[] Markers = [Confirm, Decline, Auth, PhoneSkip, PhoneCancel, MenuItem];

    /// <summary>
    /// Whether a raw inbound body carries any ownership marker of this registry. It is the scan the
    /// default <c>IChannelAdapter.OwnsInboundEvent</c> runs, published so that an adapter overriding
    /// that member with a check of its own wire format keeps the marker check too — a default
    /// interface body cannot be called from the implementing class.
    /// </summary>
    /// <remarks>
    /// An ordinal byte-wise substring search: the markers are ASCII by construction, so their UTF-8
    /// encoding is byte-identical to their characters and the body needs no decoding — which keeps
    /// the check allocation-free on a path a router walks for every single update. Being a substring
    /// search, it answers true for a marker inside any field, a user's own text included.
    /// </remarks>
    /// <param name="utf8Body">Raw body bytes.</param>
    /// <returns>true — at least one marker occurs in the body.</returns>
    public static bool ContainsAnyMarker(ReadOnlySpan<byte> utf8Body)
    {
        foreach (var marker in Markers)
        {
            if (ContainsMarker(utf8Body, marker))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether the body contains one ASCII marker, compared byte by byte.
    /// </summary>
    /// <param name="utf8Body">Raw body bytes.</param>
    /// <param name="marker">ASCII marker.</param>
    /// <returns>true — the marker occurs in the body.</returns>
    private static bool ContainsMarker(ReadOnlySpan<byte> utf8Body, string marker)
    {
        Span<byte> markerBytes = stackalloc byte[marker.Length];
        for (var i = 0; i < marker.Length; i++)
        {
            markerBytes[i] = (byte)marker[i];
        }

        return utf8Body.IndexOf(markerBytes) >= 0;
    }
}
