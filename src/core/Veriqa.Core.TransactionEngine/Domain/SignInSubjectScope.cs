// Copyright (c) Dmitrii Erusov
// SPDX-License-Identifier: MPL-2.0

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Veriqa.Core.TransactionEngine.Services;

namespace Veriqa.Core.TransactionEngine.Domain;

/// <summary>
/// What makes two sign-ins "the same sign-in started again": the tenant, the application, and the
/// channel subject the sign-in is bound to. A sign-in that gets its subject closes the earlier
/// sign-ins of the same scope that still wait for an answer.
/// </summary>
/// <remarks>
/// Every member is compared ordinally, and the record equality is the equality of the scope. Only a
/// sign-in has a scope: a confirmation transaction is created by the application for an operation of
/// its own, and a new operation does not replace an earlier one.
/// </remarks>
public sealed record SignInSubjectScope
{
    /// <summary>
    /// Tenant of the sign-in; null — no tenant is stated.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    /// Application the sign-in was started for (<c>client_id</c>).
    /// </summary>
    public required string ApplicationId { get; init; }

    /// <summary>
    /// Channel type of the bound subject.
    /// </summary>
    public required string ChannelType { get; init; }

    /// <summary>
    /// Identifier of the bound subject within its channel.
    /// </summary>
    public required string ChannelUserId { get; init; }

    /// <summary>
    /// Reads the scope of a transaction.
    /// </summary>
    /// <remarks>
    /// The bound subject is read the way the transaction itself reads it: the subject binding, or the
    /// subject of the channel snapshot when no binding was written. A tenant stated as an empty or a
    /// blank string is no tenant.
    /// </remarks>
    /// <param name="transaction">Transaction being read.</param>
    /// <returns>
    /// The scope; null — the transaction is not a sign-in, names no application, or is bound to no
    /// channel subject yet.
    /// </returns>
    public static SignInSubjectScope? Of(Transaction transaction)
    {
        ArgumentNullException.ThrowIfNull(transaction);

        if (!string.Equals(transaction.Type, TransactionTypes.Login, StringComparison.Ordinal))
        {
            return null;
        }

        var applicationId = transaction.GetApplicationId();
        if (string.IsNullOrWhiteSpace(applicationId))
        {
            return null;
        }

        var channelType = transaction.SubjectBinding?.ChannelType ?? transaction.ChannelIdentitySnapshot?.ChannelType;
        var channelUserId = transaction.SubjectBinding?.ChannelUserId ?? transaction.ChannelIdentitySnapshot?.ChannelUserId;
        if (channelType is null || channelUserId is null)
        {
            return null;
        }

        var tenantId = transaction.GetTenantId();

        return new SignInSubjectScope
        {
            TenantId = string.IsNullOrWhiteSpace(tenantId) ? null : tenantId,
            ApplicationId = applicationId,
            ChannelType = channelType,
            ChannelUserId = channelUserId
        };
    }

    /// <summary>
    /// Produces the stable fingerprint of the scope a store indexes sign-ins by: SHA-256 over the
    /// length-prefixed UTF-8 form of the four members in the order of their declaration, as 64
    /// lowercase hexadecimal characters.
    /// </summary>
    /// <remarks>
    /// The length prefixes keep ("a", "bc") and ("ab", "c") apart. An absent tenant is written as an
    /// empty string — <see cref="Of"/> never produces an empty one, so the two cannot collide.
    /// </remarks>
    /// <returns>The fingerprint.</returns>
    public string ToFingerprint()
    {
        byte[][] fields =
        [
            Encoding.UTF8.GetBytes(TenantId ?? string.Empty),
            Encoding.UTF8.GetBytes(ApplicationId),
            Encoding.UTF8.GetBytes(ChannelType),
            Encoding.UTF8.GetBytes(ChannelUserId)
        ];

        var headerLength = fields.Length * sizeof(int);
        var buffer = new byte[headerLength + fields.Sum(field => field.Length)];
        var offset = headerLength;

        for (var index = 0; index < fields.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(buffer.AsSpan(index * sizeof(int), sizeof(int)), fields[index].Length);
            fields[index].CopyTo(buffer, offset);
            offset += fields[index].Length;
        }

        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(buffer, hash);

        return Convert.ToHexStringLower(hash);
    }
}
