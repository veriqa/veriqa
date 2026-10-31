// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.ChannelAdapter.Http.Conformance.Sending;

/// <summary>
/// The bodies the suite builds itself. A call body the sender posts is always one the wrapper produced,
/// with the change of its scenario (SPEC-042 R23): the suite does not build call envelopes. The envelopes it builds are
/// events — the wrapper never produces one: one the sender reflects onto the input of the adapter
/// (SPEC-042 E50), and the press of an item of the menu the suite hands to the wrapper itself
/// (SPEC-042 C39).
/// </summary>
internal static class WireBodies
{
    /// <summary>
    /// Member of an envelope carrying the version of the wire.
    /// </summary>
    private const string VersionMember = "version";

    /// <summary>
    /// Member of a call envelope carrying the operation.
    /// </summary>
    private const string OperationMember = "operation";

    /// <summary>
    /// Member of a call envelope carrying the data of the operation.
    /// </summary>
    private const string PayloadMember = "payload";

    /// <summary>
    /// Member of the payload of <c>send-prompt</c> carrying the structured context of the prompt.
    /// </summary>
    private const string PromptMember = "prompt";

    /// <summary>
    /// Member of the prompt carrying its variant.
    /// </summary>
    private const string VariantMember = "variant";

    /// <summary>
    /// Member of the payload of <c>deep-link</c> carrying the prefill text the core rendered.
    /// </summary>
    private const string RenderedMember = "rendered";

    /// <summary>
    /// Member of an event envelope naming the event.
    /// </summary>
    private const string EventMember = "event";

    /// <summary>
    /// Member of an event envelope carrying the version of the adapter.
    /// </summary>
    private const string AdapterVersionMember = "adapter_version";

    /// <summary>
    /// Member of an event and of a call payload carrying its transaction.
    /// </summary>
    private const string TransactionIdMember = "transaction_id";

    /// <summary>
    /// Member of <c>menu_item_pressed</c> carrying the value of the pressed item.
    /// </summary>
    private const string MenuItemValueMember = "menu_item_value";

    /// <summary>
    /// Member of an event carrying the identity of the user.
    /// </summary>
    private const string IdentityMember = "identity";

    /// <summary>
    /// Member of the identity carrying the user of the channel.
    /// </summary>
    private const string ChannelUserIdMember = "channel_user_id";

    /// <summary>
    /// Member of the identity carrying the display name.
    /// </summary>
    private const string DisplayNameMember = "display_name";

    /// <summary>
    /// Member of the identity telling a bot.
    /// </summary>
    private const string IsBotMember = "is_bot";

    /// <summary>
    /// Name of the field no version of the wire declares, added to probe that unknown fields are ignored.
    /// </summary>
    private const string UnknownFieldName = "x_veriqa_conformance_unknown_field";

    /// <summary>
    /// Value of the unknown field.
    /// </summary>
    private const string UnknownFieldValue = "to be ignored";

    /// <summary>
    /// An operation no version of the wire declares.
    /// </summary>
    private const string UnknownOperation = "x-veriqa-conformance-unknown-operation";

    /// <summary>
    /// A variant of the prompt no version of the wire declares.
    /// </summary>
    private const string UnknownVariant = "x_veriqa_conformance_unknown_variant";

    /// <summary>
    /// Version of the adapter an event of the suite claims — any semantic version; the account of a
    /// prompt of the suite states the same one.
    /// </summary>
    internal const string EventAdapterVersion = "1.0.0";

    /// <summary>
    /// Display name of the user of an event of the suite.
    /// </summary>
    private const string EventDisplayName = "Veriqa conformance suite";

    /// <summary>
    /// Minor part of the foreign major version.
    /// </summary>
    private const int ForeignMinorVersion = 0;

    /// <summary>
    /// Separator of the parts of a version of the wire.
    /// </summary>
    private const char VersionSeparator = '.';

    /// <summary>
    /// A version of the wire of the next major version — one the core does not speak.
    /// </summary>
    private static readonly string ForeignMajorVersion =
        (HttpChannelWire.MajorVersion + 1).ToString(CultureInfo.InvariantCulture)
        + VersionSeparator
        + ForeignMinorVersion.ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// The body with a field no version of the wire declares, on the top level of the envelope and in
    /// its payload.
    /// </summary>
    /// <param name="body">A call body the wrapper produced.</param>
    /// <returns>The mutated body.</returns>
    public static byte[] WithUnknownFields(ReadOnlyMemory<byte> body) => Mutate(body, static envelope =>
    {
        envelope[UnknownFieldName] = UnknownFieldValue;
        envelope[PayloadMember]!.AsObject()[UnknownFieldName] = UnknownFieldValue;
    });

    /// <summary>
    /// The body of a <c>send-prompt</c> whose prompt has a variant no version of the wire declares.
    /// </summary>
    /// <param name="body">A <c>send-prompt</c> body the wrapper produced.</param>
    /// <returns>The mutated body.</returns>
    public static byte[] WithUnknownVariant(ReadOnlyMemory<byte> body) => Mutate(body, static envelope =>
        envelope[PayloadMember]!.AsObject()[PromptMember]!.AsObject()[VariantMember] = UnknownVariant);

    /// <summary>
    /// The body of a <c>deep-link</c> without the prefill text — what the wrapper sends when no step of
    /// the template gives a text for the link — for a new transaction: an adapter that keeps the link of
    /// a request (SPEC-042 C37) cannot answer from the link of the source body and builds one without a
    /// text.
    /// </summary>
    /// <param name="body">A <c>deep-link</c> body the wrapper produced.</param>
    /// <returns>The mutated body.</returns>
    public static byte[] WithoutRenderedPrefill(ReadOnlyMemory<byte> body) => Mutate(body, static envelope =>
    {
        var payload = envelope[PayloadMember]!.AsObject();
        payload[TransactionIdMember] = TransactionId.NewId().ToString();
        payload[RenderedMember] = null;
    });

    /// <summary>
    /// The body with an operation no version of the wire declares.
    /// </summary>
    /// <param name="body">A call body the wrapper produced.</param>
    /// <returns>The mutated body.</returns>
    public static byte[] WithUnknownOperation(ReadOnlyMemory<byte> body) => Mutate(body, static envelope =>
        envelope[OperationMember] = UnknownOperation);

    /// <summary>
    /// The body in the next major version of the wire.
    /// </summary>
    /// <param name="body">A call body the wrapper produced.</param>
    /// <returns>The mutated body.</returns>
    public static byte[] WithForeignMajorVersion(ReadOnlyMemory<byte> body) => Mutate(body, static envelope =>
        envelope[VersionMember] = ForeignMajorVersion);

    /// <summary>
    /// A well-formed <c>auth_start</c> event envelope of a new transaction — what an adapter would post
    /// to the core, sent to the input of the adapter instead (SPEC-042 E50).
    /// </summary>
    /// <param name="channelUserId">The user of the channel of the event.</param>
    /// <returns>The body.</returns>
    public static byte[] ReflectedAuthStart(string channelUserId)
    {
        var envelope = new JsonObject
        {
            [VersionMember] = HttpChannelWire.Version,
            [EventMember] = HttpChannelWire.Events.AuthStart,
            [AdapterVersionMember] = EventAdapterVersion,
            [TransactionIdMember] = TransactionId.NewId().ToString(),
            [IdentityMember] = IdentityOf(channelUserId)
        };

        return Utf8Of(envelope);
    }

    /// <summary>
    /// A <c>menu_item_pressed</c> event envelope — what an adapter posts to the core when the user presses
    /// an item of the menu (SPEC-042 C39).
    /// </summary>
    /// <param name="channelUserId">The user of the channel who pressed the item.</param>
    /// <param name="menuItemValue">The value of the pressed item, as it is put on the wire.</param>
    /// <returns>The body.</returns>
    public static byte[] MenuItemPressed(string channelUserId, string menuItemValue)
    {
        var envelope = new JsonObject
        {
            [VersionMember] = HttpChannelWire.Version,
            [EventMember] = HttpChannelWire.Events.MenuItemPressed,
            [AdapterVersionMember] = EventAdapterVersion,
            [MenuItemValueMember] = menuItemValue,
            [IdentityMember] = IdentityOf(channelUserId)
        };

        return Utf8Of(envelope);
    }

    /// <summary>
    /// The identity of an event of the suite: the user of the channel, a human.
    /// </summary>
    /// <param name="channelUserId">The user of the channel.</param>
    /// <returns>The identity.</returns>
    private static JsonObject IdentityOf(string channelUserId) => new()
    {
        [ChannelUserIdMember] = channelUserId,
        [DisplayNameMember] = EventDisplayName,
        [IsBotMember] = false
    };

    /// <summary>
    /// Applies one change to a call body.
    /// </summary>
    /// <param name="body">A call body the wrapper produced — a JSON object.</param>
    /// <param name="change">The change.</param>
    /// <returns>The changed body.</returns>
    private static byte[] Mutate(ReadOnlyMemory<byte> body, Action<JsonObject> change)
    {
        var envelope = JsonNode.Parse(body.Span)!.AsObject();
        change(envelope);
        return Utf8Of(envelope);
    }

    /// <summary>
    /// The UTF-8 bytes of a JSON node.
    /// </summary>
    /// <param name="node">The node.</param>
    /// <returns>The bytes.</returns>
    private static byte[] Utf8Of(JsonNode node) =>
        Encoding.UTF8.GetBytes(node.ToJsonString());
}
