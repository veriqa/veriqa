// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using Veriqa.Core.AuthServer.Configuration;
using Veriqa.Core.AuthServer.Configuration.Resolution;
using Veriqa.Core.AuthServer.Constants;
using Veriqa.Core.AuthServer.UI;
using Veriqa.Core.ChannelAdapter.Abstractions;
using Veriqa.Core.ChannelAdapter.MultiTenancy;
using Veriqa.Core.ChannelAdapter.Pipeline;
using Veriqa.Core.ChannelAdapter.UI;
using Veriqa.Core.Configuration;
using Veriqa.Core.Contracts;
using Veriqa.Core.TransactionEngine.Domain;

namespace Veriqa.Core.AuthServer.ClaimCompletion;

/// <summary>
/// Builds the model of the form of the web step of the claims completion (SPEC-041 L108) out of the fields
/// of the transaction (C90) and the effective catalog of the claims: what is still missing, how each field
/// is labelled and bounded, which account the sign-in was confirmed with, which actions the page offers and
/// whether the page warns that the hop link of the transaction was opened once more (L93).
/// </summary>
/// <remarks>
/// Scoped: it reads the channel adapters, whose lifetime a host may narrow to the request.
/// </remarks>
internal sealed class ClaimCompletionFormModelBuilder : IClaimCompletionFormModelBuilder
{
    /// <summary>
    /// Canonical resolver — the catalog of the claims is read through it in the context of the transaction.
    /// </summary>
    private readonly IConfigurationResolver _resolver;

    /// <summary>
    /// Registered channel adapters — the label a third-party channel declared for itself is read off them.
    /// </summary>
    private readonly IEnumerable<IChannelAdapter> _adapters;

    /// <summary>
    /// Localizer of the labels of the catalog.
    /// </summary>
    private readonly IConfirmationPromptLocalizer _localizer;

    /// <summary>
    /// Logger.
    /// </summary>
    private readonly ILogger<ClaimCompletionFormModelBuilder> _logger;

    /// <summary>
    /// Creates the builder.
    /// </summary>
    /// <param name="resolver">Canonical resolver of the effective setting values.</param>
    /// <param name="adapters">Registered channel adapters.</param>
    /// <param name="localizer">Localizer of the labels of the catalog.</param>
    /// <param name="logger">Logger.</param>
    public ClaimCompletionFormModelBuilder(
        IConfigurationResolver resolver,
        IEnumerable<IChannelAdapter> adapters,
        IConfirmationPromptLocalizer localizer,
        ILogger<ClaimCompletionFormModelBuilder> logger)
    {
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _adapters = adapters ?? throw new ArgumentNullException(nameof(adapters));
        _localizer = localizer ?? throw new ArgumentNullException(nameof(localizer));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask<ClaimCompletionFormModel> BuildAsync(
        Transaction transaction,
        WebStepSurface surface,
        string language,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentException.ThrowIfNullOrWhiteSpace(language);

        // The method takes the claims of the web step that have no outcome yet. Every one of them but the
        // email becomes a field, described by its row of the effective catalog; the email has a block of its
        // own. The account is shown on the page of the initiating device only; "Cancel sign-in" is offered on
        // every surface, whatever the fields. The warning of a reopened hop link goes on both surfaces.

        var open = OpenWebStepItems(transaction);

        var catalog = (await _resolver.ResolveAsync(
                ScopesClaimsConfigKeys.ScopeCatalog,
                TransactionResolutionContext.For(transaction),
                ConfigDimensionValues.None,
                cancellationToken))
            .Value ?? [];

        var fields = open
            .Where(item => !VeriqaClaimTypes.NameComparer.Equals(item.Claim, VeriqaClaimTypes.Email))
            .Select(item => BuildField(item, catalog, language))
            .ToList();

        var onInitiatingPage = surface is WebStepSurface.InitiatingPage;

        // The hop link of the transaction was opened once more from another browser (SPEC-041 L93): the
        // form warns on both surfaces. The mark is read as the page is built, so a reopening recorded while
        // the page is open shows on its next load.
        var notice = transaction.HopRepeatResolvedAt is null
            ? null
            : HopReopenedWarning.Markup(_localizer.ResolveOrBaseText(AuthPageStrings.HopReopened, language));

        return new ClaimCompletionFormModel
        {
            Language = language,
            Confirmation = string.Equals(transaction.Type, TransactionTypes.Confirmation, StringComparison.Ordinal),
            NoticeHtml = notice,
            Account = onInitiatingPage ? BuildAccount(transaction.ChannelIdentitySnapshot, language) : null,
            Fields = fields,
            SubmitPath = OidcEndpoints.AuthorizeCallbackClaims,
            CancelPath = OidcEndpoints.AuthorizeCallbackClaimsCancel
        };
    }

    /// <summary>
    /// The claims of the web step that have no outcome yet — what the form of the web step asks for.
    /// </summary>
    /// <param name="transaction">The transaction.</param>
    /// <returns>Open items of the web step; empty when the transaction is not in the claims completion.</returns>
    internal static IReadOnlyList<ClaimCompletionItem> OpenWebStepItems(Transaction transaction) =>
        transaction.ClaimCompletion?.OpenItems()
            .Where(item => item.Step is ClaimCompletionStep.WebStep)
            .ToList() ?? [];

    /// <summary>
    /// Describes the field of one missing claim.
    /// </summary>
    /// <remarks>
    /// A claim whose catalog row is gone or no longer describes a completion (the catalog changed while the
    /// transaction was open — the transaction fixes the levels, not the description) still gets a field: a
    /// text field labelled with the claim name and bounded by the shipped maximum length. A choice gets
    /// the values of its row with their captions localized.
    /// </remarks>
    /// <param name="item">The missing claim.</param>
    /// <param name="catalog">Effective catalog of the claims.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The field, with no value and no error.</returns>
    private ClaimCompletionFieldView BuildField(
        ClaimCompletionItem item,
        IReadOnlyList<ScopeDefinition> catalog,
        string language)
    {
        var required = item.Level is ClaimRequirementLevel.Required;
        var completion = ScopesClaimsCatalogRules.FindClaim(catalog, item.Claim)?.Completion;

        if (completion is null)
        {
            return new ClaimCompletionFieldView(
                item.Claim,
                item.Claim,
                required,
                ClaimCompletionFieldKind.Text,
                ClaimCompletionDefinition.DefaultMaxLength,
                Choices: [],
                Value: null,
                Error: null);
        }

        var (kind, maxLength) = ClaimCompletionFieldRules.ShapeOf(completion);

        // The label is a text setting (SPEC-012 CFG-241): the stated value is resolved as a Natural Key, and
        // a value no locale file carries is shown as it is stated, its context suffix stripped. The caption
        // of a value of a choice is the same kind of setting and is resolved the same way.
        IReadOnlyList<ClaimCompletionChoiceView> choices = kind is ClaimCompletionFieldKind.Choice
            ? [.. (completion.Options ?? []).Select(option => new ClaimCompletionChoiceView(option.Value, LocalizeChoice(option, language)))]
            : [];

        return new ClaimCompletionFieldView(
            item.Claim,
            _localizer.ResolveOrBaseText(completion.Label, language),
            required,
            kind,
            maxLength,
            choices,
            Value: null,
            Error: null);
    }

    /// <summary>
    /// The caption of a value of a choice in the language of the page.
    /// </summary>
    /// <param name="option">The value as the catalog row states it.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The localized caption.</returns>
    private string LocalizeChoice(ClaimCompletionOption option, string language) =>
        _localizer.ResolveOrBaseText(option.Label, language);

    /// <summary>
    /// Describes the channel account the sign-in was confirmed with.
    /// </summary>
    /// <param name="snapshot">Channel snapshot of the transaction.</param>
    /// <param name="language">Content language of the page.</param>
    /// <returns>The account; null when the transaction carries no snapshot.</returns>
    private ClaimCompletionAccountView? BuildAccount(ChannelIdentitySnapshot? snapshot, string language)
    {
        if (snapshot is null)
        {
            return null;
        }

        // The display name is shown in addition to the identifier only, and not at all when it repeats it
        // (the Email adapter states the address as both).
        var identifier = ClaimCompletionAccountIdentifier.Resolve(snapshot);
        var displayName = string.IsNullOrWhiteSpace(snapshot.DisplayName)
            || string.Equals(snapshot.DisplayName, identifier, StringComparison.OrdinalIgnoreCase)
                ? null
                : snapshot.DisplayName;

        return new ClaimCompletionAccountView(
            ChannelNames.NameChannel(snapshot.ChannelType, _adapters, _localizer, language, _logger),
            identifier,
            displayName);
    }
}
