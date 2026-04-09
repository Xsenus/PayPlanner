using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;

namespace PayPlanner.Api.Extensions;

public sealed record ObjectAuditSnapshot(
    int? CreatedByUserId,
    string? CreatedByUserName,
    DateTime? UpdatedAt,
    int? UpdatedByUserId,
    string? UpdatedByUserName,
    bool IsDeleted,
    DateTime? DeletedAt,
    int? DeletedByUserId,
    string? DeletedByUserName);

public static class AuditMetadataExtensions
{
    public static ObjectAuditSnapshot? ReadAuditSnapshot<TEntity>(this PaymentContext db, TEntity entity)
        where TEntity : class
    {
        var entry = db.Entry(entity);
        if (entry.Metadata.FindProperty("CreatedByUserId") is null)
        {
            return null;
        }

        return new ObjectAuditSnapshot(
            entry.Property<int?>("CreatedByUserId").CurrentValue,
            entry.Property<string?>("CreatedByUserName").CurrentValue,
            entry.Property<DateTime?>("UpdatedAt").CurrentValue,
            entry.Property<int?>("UpdatedByUserId").CurrentValue,
            entry.Property<string?>("UpdatedByUserName").CurrentValue,
            entry.Property<bool>("IsDeleted").CurrentValue,
            entry.Property<DateTime?>("DeletedAt").CurrentValue,
            entry.Property<int?>("DeletedByUserId").CurrentValue,
            entry.Property<string?>("DeletedByUserName").CurrentValue);
    }
}
