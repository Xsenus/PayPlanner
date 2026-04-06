using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using PayPlanner.Api.Data;
using PayPlanner.Api.Models;
using PayPlanner.Api.Models.Responses;

namespace PayPlanner.Api.Services;

public sealed partial class DatabaseImportService
{
    private async Task ImportRolesAsync(
        PaymentContext source,
        PaymentContext target,
        DatabaseImportTableResultDto table,
        ImportMaps maps,
        CancellationToken cancellationToken)
    {
        var sourceItems = await LoadSourceRolesAsync(source, cancellationToken);
        table.SourceRows = sourceItems.Count;

        var targetItems = await target.Roles.ToListAsync(cancellationToken);
        var existingByKey = targetItems
            .Where(item => !string.IsNullOrWhiteSpace(item.Name))
            .GroupBy(item => Key(item.Name), StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

        var inserted = new List<(int OldId, Role Entity)>();

        foreach (var sourceItem in sourceItems.OrderBy(item => item.Id))
        {
            var key = Key(sourceItem.Name);
            if (string.IsNullOrWhiteSpace(key))
            {
                table.Conflicts += 1;
                continue;
            }

            if (existingByKey.TryGetValue(key, out var existing))
            {
                CopyRole(sourceItem, existing);
                maps.Roles[sourceItem.Id] = existing.Id;
                table.Updated += 1;
                continue;
            }

            var created = new Role();
            CopyRole(sourceItem, created);
            target.Roles.Add(created);
            existingByKey[key] = created;
            inserted.Add((sourceItem.Id, created));
            table.Inserted += 1;
        }

        if (table.Inserted > 0 || table.Updated > 0)
        {
            await SaveImportedChangesAsync(target, cancellationToken);
        }

        foreach (var item in inserted)
        {
            maps.Roles[item.OldId] = item.Entity.Id;
        }
    }

    private static async Task<List<Role>> LoadSourceRolesAsync(
        PaymentContext source,
        CancellationToken cancellationToken)
    {
        await source.Database.OpenConnectionAsync(cancellationToken);

        var connection = source.Database.GetDbConnection();
        var columns = await GetSqliteTableColumnsAsync(connection, "Roles", cancellationToken);
        if (columns.Count == 0)
        {
            return [];
        }

        var selectParts = new List<string>
        {
            "\"Id\"",
            "\"Name\"",
            columns.Contains("Description") ? "\"Description\"" : "NULL AS \"Description\"",
            columns.Contains("CreatedAt") ? "\"CreatedAt\"" : "NULL AS \"CreatedAt\""
        };

        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {string.Join(", ", selectParts)} FROM \"Roles\" ORDER BY \"Id\"";

        var roles = new List<Role>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            roles.Add(new Role
            {
                Id = reader.GetInt32(0),
                Name = reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                Description = reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                CreatedAt = ReadSqliteDateTime(reader, 3) ?? DateTime.UtcNow
            });
        }

        return roles;
    }

    private static async Task<HashSet<string>> GetSqliteTableColumnsAsync(
        DbConnection connection,
        string tableName,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{tableName}\")";

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (!reader.IsDBNull(1))
            {
                columns.Add(reader.GetString(1));
            }
        }

        return columns;
    }

    private static DateTime? ReadSqliteDateTime(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetValue(ordinal) switch
        {
            DateTime dateTime => NormalizeDateTime(dateTime),
            string text when DateTime.TryParse(text, out var parsed) => NormalizeDateTime(parsed),
            _ => null
        };
    }
}
