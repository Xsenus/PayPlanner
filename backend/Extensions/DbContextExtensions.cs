using System;
using System.Data;
using Microsoft.EntityFrameworkCore;

namespace PayPlanner.Api.Extensions;

public static class DbContextExtensions
{
    public static async Task<bool> TableExistsAsync(this DbContext context, string tableName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(tableName))
        {
            return false;
        }

        var connection = context.Database.GetDbConnection();
        var wasOpen = connection.State == ConnectionState.Open;

        if (!wasOpen)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var restrictions = new string?[] { null, null, tableName, null };
            using var schema = connection.GetSchema("Tables", restrictions);
            if (schema is null)
            {
                return false;
            }

            foreach (DataRow row in schema.Rows)
            {
                var name = row["TABLE_NAME"]?.ToString();
                if (string.Equals(name, tableName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            if (!wasOpen)
            {
                await connection.CloseAsync();
            }
        }
    }
}
