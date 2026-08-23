using Npgsql;

namespace PayPlanner.Api.Services;

public static class FinancialConcurrencyGuard
{
    public const string ConflictMessage =
        "Баланс или платеж уже изменен другим пользователем. Обновите данные и повторите операцию.";

    public static bool IsSerializationFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is PostgresException postgresException
                && postgresException.SqlState == PostgresErrorCodes.SerializationFailure)
            {
                return true;
            }
        }

        return false;
    }
}
