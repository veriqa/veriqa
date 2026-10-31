// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Text;

namespace Veriqa.Core.Configuration;

/// <summary>
/// Address of a value inside the record of one level, parsed once at declaration time and turned into
/// a concrete path once per step of the fallback chain (SPEC-012 §10.6, CFG-236/CFG-238).
/// <para>
/// The grammar is deliberately tiny — segments separated by <see cref="ConfigNode.PathSeparator"/>,
/// <c>{dimension}</c> substituted with the value the step is asked about, and <c>[...]</c> a group
/// dropped AS A WHOLE when the step does not address the dimension inside it:
/// </para>
/// <code>
/// Veriqa:MessageTemplates:{kind}:[ByType:{transaction_type}]:[BySurface:{surface}]
/// </code>
/// <para>
/// Nothing else is reserved. A character with a special meaning of its own would be a character some
/// deployment cannot spell in an environment variable, and an address half of the configuration
/// sources cannot serve is not an address (the same reason <see cref="ConfigNode"/> reserves nothing
/// but its separator).
/// </para>
/// <para>
/// A group is DROPPED, not emptied: a step that does not address the transaction type reads
/// <c>Veriqa:MessageTemplates:login</c> and not <c>Veriqa:MessageTemplates:login::</c>. That is what
/// makes one address serve every step of the ladder instead of one address per step.
/// </para>
/// <para>
/// What the grammar deliberately does NOT express is a step reading a DIFFERENT leaf than its
/// neighbour (the map <c>LogoUrlByTheme</c> against the plain <c>LogoUrl</c>): dropping a group
/// leaves no second name behind, and inventing an alternative address per step would grow the grammar
/// for a case the parse hook of the key already covers.
/// </para>
/// </summary>
internal sealed class ConfigAddressTemplate
{
    /// <summary>
    /// Opening character of a group dropped as a whole.
    /// </summary>
    private const char GroupStart = '[';

    /// <summary>
    /// Closing character of a group dropped as a whole.
    /// </summary>
    private const char GroupEnd = ']';

    /// <summary>
    /// Opening character of a dimension substitution.
    /// </summary>
    private const char SubstitutionStart = '{';

    /// <summary>
    /// Closing character of a dimension substitution.
    /// </summary>
    private const char SubstitutionEnd = '}';

    /// <summary>
    /// Separator of the parts of a key NAME — what the derived address turns into a path separator.
    /// </summary>
    private const char NameSeparator = '.';

    /// <summary>
    /// Parts of the address in the order they are joined in.
    /// </summary>
    private readonly IReadOnlyList<Part> _parts;

    /// <summary>
    /// Creates the parsed address.
    /// </summary>
    /// <param name="text">Address as it is written.</param>
    /// <param name="parts">Parts of the address.</param>
    private ConfigAddressTemplate(string text, IReadOnlyList<Part> parts)
    {
        Text = text;
        _parts = parts;
        MinimumSegments = parts.Where(part => !part.Optional).Sum(part => part.SegmentCount);
        RootSegment = RootSegmentOf(parts);
        SectionText = SectionTextOf(text, parts);
        EnumerableDimension = EnumerableDimensionOf(parts);
    }

    /// <summary>
    /// Address as it is written — with the substitutions and the groups still in it.
    /// </summary>
    public string Text { get; }

    /// <summary>
    /// The address WITHOUT its last segment — the section the value lives in, as the address writes it.
    /// It is what names the level in a report that speaks about the setting rather than about one of
    /// its values: "the level states nothing for this setting anywhere under section X". The last
    /// segment is dropped because it names the VALUE, and a report that quoted it would claim the level
    /// states nothing at an address it might well state something at through another step of the chain.
    /// <para>
    /// It is the address as it is WRITTEN, substitutions and groups included: a section stated per step
    /// of the chain has no single spelling, and inventing one would name a section that is not there.
    /// What is dropped is a SEGMENT of the parsed address rather than the characters after the last
    /// separator of its text — a group is one unit of the address and goes whole (see
    /// <see cref="SectionTextOf"/>).
    /// </para>
    /// </summary>
    public string SectionText { get; }

    /// <summary>
    /// Number of segments EVERY resolved path of this address has: the segments outside the optional
    /// groups. It answers the one question that has to be asked before a path is read at the core
    /// level, where the node stands over a section of the host configuration rather than over a record
    /// (see <see cref="PathConfigKeyRegistrar"/>).
    /// </summary>
    public int MinimumSegments { get; }

    /// <summary>
    /// FIRST segment of every path this address resolves to, when that segment is a plain literal —
    /// null when the address opens with an optional group or with a substitution, and the first segment
    /// therefore depends on the step of the chain.
    /// <para>
    /// It is the one segment that says WHAT the address is written against: at the core level it names
    /// a section of the application configuration of the host, and above the core level the address is
    /// relative to the record of its own level (SPEC-012 §10.1). Comparing the two is what lets the
    /// startup check tell an address that names the host's store from one that does not
    /// (<see cref="ConfigResolutionStartupDiagnosticsService"/>).
    /// </para>
    /// </summary>
    public string? RootSegment { get; }

    /// <summary>
    /// Address derived from the NAME of a key: the dots of the name become path separators, so
    /// <c>AuthPage.Title</c> is read at <c>AuthPage:Title</c>. This is what a level costs when the
    /// configuration is spelled the way the key is named — nothing.
    /// </summary>
    /// <param name="keyName">Setting name.</param>
    /// <returns>Address derived from the name.</returns>
    public static string DerivedFrom(string keyName) => keyName.Replace(NameSeparator, ConfigNode.PathSeparator);

    /// <summary>
    /// Parses an address against the dimensions the key declares. Every defect of the address is
    /// refused HERE — while the declaration is being built — rather than at the first resolution that
    /// silently found nothing at a path nobody could have written.
    /// </summary>
    /// <param name="keyName">Setting name (for the message).</param>
    /// <param name="text">Address as it is written.</param>
    /// <param name="dimensions">Names of the dimensions the key declares.</param>
    /// <returns>Parsed address.</returns>
    /// <exception cref="ArgumentException">The address is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">The address is malformed or names an undeclared dimension.</exception>
    public static ConfigAddressTemplate Parse(string keyName, string text, IReadOnlySet<string> dimensions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var parts = new List<Part>();
        var plain = new StringBuilder();
        var index = 0;

        while (index < text.Length)
        {
            var character = text[index];

            if (character == GroupEnd)
            {
                throw Defect(keyName, text, $"a '{GroupEnd}' closes a group that was never opened");
            }

            if (character != GroupStart)
            {
                plain.Append(character);
                index++;

                continue;
            }

            // A group covers WHOLE segments: it starts a segment and it ends one. A group in the
            // middle of a segment could not be dropped without leaving half a segment behind.
            if (index > 0 && text[index - 1] != ConfigNode.PathSeparator)
            {
                throw Defect(keyName, text, "a group must start a segment of its own");
            }

            var end = text.IndexOf(GroupEnd, index + 1);

            if (end < 0)
            {
                throw Defect(keyName, text, "a group is left unclosed");
            }

            var nested = text.IndexOf(GroupStart, index + 1);

            if (nested >= 0 && nested < end)
            {
                throw Defect(keyName, text, "groups are not nested");
            }

            if (end + 1 < text.Length && text[end + 1] != ConfigNode.PathSeparator)
            {
                throw Defect(keyName, text, "a group must end a segment of its own");
            }

            Flush(parts, plain, beforeGroup: true, keyName, text, dimensions);

            var group = ParsePart(keyName, text, text[(index + 1)..end], optional: true, dimensions);

            if (group.Dimensions.Count == 0)
            {
                throw Defect(
                    keyName,
                    text,
                    "a group addresses no dimension: nothing would ever decide whether it is dropped");
            }

            parts.Add(group);
            index = end + 1;

            // The separator that follows a group belongs to the group: it disappears together with it.
            if (index < text.Length)
            {
                index++;
            }
        }

        Flush(parts, plain, beforeGroup: false, keyName, text, dimensions);

        if (parts.All(part => part.Optional))
        {
            throw Defect(keyName, text, "the address has no segment outside its groups, so it can resolve to nothing");
        }

        return new ConfigAddressTemplate(text, parts);
    }

    /// <summary>
    /// The one dimension the address substitutes OUTSIDE its groups, together with the section whose
    /// child keys are the values of that dimension a record states: for
    /// <c>Veriqa:Channels:{channel}:Member</c> — the dimension <c>channel</c> and the section
    /// <c>Veriqa:Channels</c>. It is what lets a reader of a record ENUMERATE the points such an
    /// address exists at, where <see cref="Resolve"/> can only be asked about a point already known.
    /// <para>
    /// Null when the address has no such substitution, has more than one, or does not let the values be
    /// read off the keys of one section: a substitution that shares its segment with literal text, one
    /// that opens the address, or one standing behind a group.
    /// </para>
    /// </summary>
    public (string Section, string Dimension)? EnumerableDimension { get; }

    /// <summary>
    /// Builds the concrete path for ONE step of the fallback chain: the substitutions take the values
    /// the step is asked about, and a group whose dimensions the step does not address is dropped
    /// whole.
    /// </summary>
    /// <param name="point">Point of the chain handed in by the resolver.</param>
    /// <returns>
    /// Path of this step, or null when a substitution OUTSIDE the groups has no value at this
    /// point — the step then addresses nothing and the chain moves on, exactly as an unstated value
    /// does (SPEC-012 §10.3).
    /// </returns>
    /// <exception cref="ArgumentException">
    /// A dimension value carries the path separator: which segment of the path it belongs to would be
    /// undecidable.
    /// </exception>
    public string? Resolve(ConfigDimensionValues point)
    {
        var path = new StringBuilder();

        foreach (var part in _parts)
        {
            if (part.Optional && !AddressesAll(point, part.Dimensions))
            {
                continue;
            }

            if (path.Length > 0)
            {
                path.Append(ConfigNode.PathSeparator);
            }

            foreach (var token in part.Tokens)
            {
                if (token.Dimension is null)
                {
                    path.Append(token.Literal);

                    continue;
                }

                var value = point.ValueOf(token.Dimension);

                if (string.IsNullOrWhiteSpace(value))
                {
                    return null;
                }

                if (value.Contains(ConfigNode.PathSeparator))
                {
                    throw new ArgumentException(
                        $"The value of dimension '{token.Dimension}' carries the path separator " +
                        $"'{ConfigNode.PathSeparator}', so the segment of the address '{Text}' it stands in would " +
                        "be ambiguous.",
                        nameof(point));
                }

                path.Append(value);
            }
        }

        return path.ToString();
    }

    /// <summary>
    /// Whether the step addresses every dimension of a group — the question that decides whether the
    /// group is kept. A dimension present with a blank value counts as unaddressed: the caller asked
    /// about a dimension whose value it does not know, and a segment built out of a blank is not an
    /// address.
    /// </summary>
    /// <param name="point">Point of the chain.</param>
    /// <param name="dimensions">Dimensions the group substitutes.</param>
    /// <returns>true when every one of them carries a value at this point.</returns>
    private static bool AddressesAll(ConfigDimensionValues point, IReadOnlyList<string> dimensions)
    {
        foreach (var dimension in dimensions)
        {
            if (string.IsNullOrWhiteSpace(point.ValueOf(dimension)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Builds the text of the section the value lives in: the address WITHOUT its last segment, taken
    /// off the PARSED parts. A group is one unit of the address — dropped whole, exactly as a step of
    /// the chain drops it — so an address ending in a group loses the group and not the characters
    /// behind the last separator of its text, which would leave an unclosed bracket in a line an
    /// operator reads. A part outside the groups loses its last segment and keeps the ones in front
    /// of it.
    /// </summary>
    /// <param name="text">Address as it is written.</param>
    /// <param name="parts">Parts of the address.</param>
    /// <returns>Text of the section.</returns>
    private static string SectionTextOf(string text, IReadOnlyList<Part> parts)
    {
        var kept = new List<string>(parts.Count);

        foreach (var part in parts.Take(parts.Count - 1))
        {
            kept.Add(TextOf(part));
        }

        var last = parts[^1];

        if (!last.Optional && last.SegmentCount > 1)
        {
            var body = TextOf(last);

            kept.Add(body[..body.LastIndexOf(ConfigNode.PathSeparator)]);
        }

        // An address of a single unit has no section in front of its value, and naming the empty string
        // would name no section at all: the address itself stands for it, which is the answer an address
        // without a separator has always been given.
        return kept.Count == 0 ? text : string.Join(ConfigNode.PathSeparator, kept);
    }

    /// <summary>
    /// Writes one part back the way the address spells it — the substitutions in their braces and a
    /// group in its brackets. Joining the parts with the path separator reproduces the address itself,
    /// which is what lets the section be built out of the parsed parts and still read as written.
    /// </summary>
    /// <param name="part">Part of the address.</param>
    /// <returns>Text of the part.</returns>
    private static string TextOf(Part part)
    {
        var text = new StringBuilder();

        if (part.Optional)
        {
            text.Append(GroupStart);
        }

        foreach (var token in part.Tokens)
        {
            if (token.Dimension is null)
            {
                text.Append(token.Literal);

                continue;
            }

            text.Append(SubstitutionStart).Append(token.Dimension).Append(SubstitutionEnd);
        }

        if (part.Optional)
        {
            text.Append(GroupEnd);
        }

        return text.ToString();
    }

    /// <summary>
    /// Takes the first segment of the address when it is a plain literal (see
    /// <see cref="RootSegment"/>). A segment that a substitution stands in — whole or in part — is not
    /// one: what it reads is decided by the step of the chain, and a check made over it would be a
    /// check over one of its possible values.
    /// </summary>
    /// <param name="parts">Parts of the address.</param>
    /// <returns>First segment, or null when it is not a plain literal.</returns>
    private static string? RootSegmentOf(IReadOnlyList<Part> parts)
    {
        if (parts.Count == 0 || parts[0].Optional || parts[0].Tokens.Count == 0)
        {
            return null;
        }

        var first = parts[0].Tokens[0];

        if (first.Dimension is not null)
        {
            return null;
        }

        var end = first.Literal.IndexOf(ConfigNode.PathSeparator);

        if (end >= 0)
        {
            return first.Literal[..end];
        }

        // The literal reaches the end of the token without a separator, so the segment continues into
        // whatever follows: it is the whole first segment only when nothing follows it.
        return parts[0].Tokens.Count == 1 ? first.Literal : null;
    }

    /// <summary>
    /// Turns the text accumulated outside the groups into a mandatory part.
    /// </summary>
    /// <param name="parts">Parts collected so far.</param>
    /// <param name="plain">Text accumulated outside the groups.</param>
    /// <param name="beforeGroup">
    /// The text is being flushed because a group starts here: the separator in front of the group
    /// belongs to the group, so it is not a segment boundary of this part.
    /// </param>
    /// <param name="keyName">Setting name (for the message).</param>
    /// <param name="text">Address as it is written (for the message).</param>
    /// <param name="dimensions">Names of the dimensions the key declares.</param>
    private static void Flush(
        List<Part> parts,
        StringBuilder plain,
        bool beforeGroup,
        string keyName,
        string text,
        IReadOnlySet<string> dimensions)
    {
        if (beforeGroup && plain.Length > 0 && plain[^1] == ConfigNode.PathSeparator)
        {
            plain.Length--;
        }

        if (plain.Length == 0)
        {
            return;
        }

        parts.Add(ParsePart(keyName, text, plain.ToString(), optional: false, dimensions));
        plain.Clear();
    }

    /// <summary>
    /// Parses one part of the address — the text of a group or the text between two of them — into the
    /// literals and substitutions it is built of.
    /// </summary>
    /// <param name="keyName">Setting name (for the message).</param>
    /// <param name="text">Address as it is written (for the message).</param>
    /// <param name="body">Text of the part.</param>
    /// <param name="optional">The part is a group and is dropped when its dimensions are unaddressed.</param>
    /// <param name="dimensions">Names of the dimensions the key declares.</param>
    /// <returns>Parsed part.</returns>
    private static Part ParsePart(
        string keyName,
        string text,
        string body,
        bool optional,
        IReadOnlySet<string> dimensions)
    {
        var segments = body.Split(ConfigNode.PathSeparator);

        foreach (var segment in segments)
        {
            if (segment.Length == 0)
            {
                throw Defect(keyName, text, "the address has an empty segment");
            }
        }

        var tokens = new List<Token>();
        var substituted = new List<string>();
        var literal = new StringBuilder();
        var index = 0;

        while (index < body.Length)
        {
            var character = body[index];

            if (character == SubstitutionEnd)
            {
                throw Defect(keyName, text, $"a '{SubstitutionEnd}' closes a substitution that was never opened");
            }

            if (character != SubstitutionStart)
            {
                literal.Append(character);
                index++;

                continue;
            }

            var end = body.IndexOf(SubstitutionEnd, index + 1);

            if (end < 0)
            {
                throw Defect(keyName, text, "a substitution is left unclosed");
            }

            var name = body[(index + 1)..end];

            if (!dimensions.Contains(name))
            {
                throw Defect(
                    keyName,
                    text,
                    $"the address substitutes dimension '{name}', which the key does not declare");
            }

            if (literal.Length > 0)
            {
                tokens.Add(new Token(literal.ToString(), Dimension: null));
                literal.Clear();
            }

            tokens.Add(new Token(Literal: string.Empty, name));
            substituted.Add(name);
            index = end + 1;
        }

        if (literal.Length > 0)
        {
            tokens.Add(new Token(literal.ToString(), Dimension: null));
        }

        return new Part(tokens, optional, substituted, segments.Length);
    }

    /// <summary>
    /// Refusal of a malformed address, naming the key and the address the author wrote.
    /// </summary>
    /// <param name="keyName">Setting name.</param>
    /// <param name="text">Address as it is written.</param>
    /// <param name="reason">What is wrong with it.</param>
    /// <returns>Exception to raise.</returns>
    private static InvalidOperationException Defect(string keyName, string text, string reason) =>
        new($"Configuration key '{keyName}' declares the address '{text}': {reason}.");

    /// <summary>
    /// Finds the dimension a reader of a record can enumerate the values of
    /// (<see cref="EnumerableDimension"/>).
    /// </summary>
    /// <param name="parts">Parts of the address.</param>
    /// <returns>The section and the dimension, or null.</returns>
    private static (string Section, string Dimension)? EnumerableDimensionOf(IReadOnlyList<Part> parts)
    {
        if (parts.Where(part => !part.Optional).Sum(part => part.Dimensions.Count) != 1 || parts[0].Optional)
        {
            return null;
        }

        var tokens = parts[0].Tokens;
        var section = new StringBuilder();

        for (var index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Dimension is not { } dimension)
            {
                section.Append(tokens[index].Literal);

                continue;
            }

            // The substitution has to be a segment of its own — the key of a child of the section in
            // front of it — or the values could not be read off the keys of that section.
            var opensItsSegment = section.Length > 1 && section[^1] == ConfigNode.PathSeparator;
            var closesItsSegment = index == tokens.Count - 1
                || tokens[index + 1].Literal.StartsWith(ConfigNode.PathSeparator);

            return opensItsSegment && closesItsSegment
                ? (section.ToString(0, section.Length - 1), dimension)
                : null;
        }

        return null;
    }

    /// <summary>
    /// Piece of an address: either a literal chunk or one dimension substitution.
    /// </summary>
    /// <param name="Literal">Literal text; empty for a substitution.</param>
    /// <param name="Dimension">Dimension whose value stands here; null for a literal.</param>
    private readonly record struct Token(string Literal, string? Dimension);

    /// <summary>
    /// Part of an address: a group (dropped as a whole) or the text between two groups.
    /// </summary>
    /// <param name="Tokens">Pieces the part is built of.</param>
    /// <param name="Optional">The part is a group.</param>
    /// <param name="Dimensions">Dimensions the part substitutes.</param>
    /// <param name="SegmentCount">Number of segments the part contributes to the path.</param>
    private sealed record Part(
        IReadOnlyList<Token> Tokens,
        bool Optional,
        IReadOnlyList<string> Dimensions,
        int SegmentCount);
}
