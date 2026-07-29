namespace PayPlanner.Api.Services.LegalIntegrations;

public static class CourtSyncNormalization
{
    public static class Instances
    {
        public const string First = "first";
        public const string Appeal = "appeal";
        public const string Cassation = "cassation";
        public const string Supreme = "supreme";
    }

    public static class EventTypes
    {
        public const string Claim = "claim";
        public const string Hearing = "hearing";
        public const string Decision = "decision";
        public const string Postponement = "postponement";
        public const string Suspension = "suspension";
        public const string Movement = "movement";
    }

    public static class MovementStatuses
    {
        public const string Scheduled = "scheduled";
        public const string Held = "held";
        public const string Postponed = "postponed";
        public const string Cancelled = "cancelled";
        public const string Completed = "completed";
        public const string Suspended = "suspended";
    }

    public static class PartySides
    {
        public const string Claimant = "claimant";
        public const string Respondent = "respondent";
        public const string ThirdParty = "thirdParty";
        public const string Applicant = "applicant";
        public const string Other = "other";
    }

    public static class DocumentTypes
    {
        public const string CourtAct = "Судебный акт";
    }

    public static string NormalizeInstance(string? value)
        => NormalizeCode(value) switch
        {
            Instances.Appeal => Instances.Appeal,
            Instances.Cassation => Instances.Cassation,
            Instances.Supreme or "supervision" => Instances.Supreme,
            _ => Instances.First,
        };

    public static string GetInstanceLabel(string? value)
        => NormalizeInstance(value) switch
        {
            Instances.Appeal => "Апелляция",
            Instances.Cassation => "Кассация",
            Instances.Supreme => "Верховный суд",
            _ => "Первая инстанция",
        };

    public static string NormalizeEventType(string? value)
        => NormalizeCode(value) switch
        {
            EventTypes.Claim => EventTypes.Claim,
            EventTypes.Decision => EventTypes.Decision,
            EventTypes.Postponement => EventTypes.Postponement,
            EventTypes.Suspension => EventTypes.Suspension,
            EventTypes.Movement => EventTypes.Movement,
            _ => EventTypes.Hearing,
        };

    public static string GetEventTypeLabel(string? value)
        => NormalizeEventType(value) switch
        {
            EventTypes.Claim => "Подача иска",
            EventTypes.Decision => "Судебное решение",
            EventTypes.Postponement => "Перенос заседания",
            EventTypes.Suspension => "Приостановление",
            EventTypes.Movement => "Движение дела",
            _ => "Судебное заседание",
        };

    public static string NormalizeMovementStatus(string? value)
        => NormalizeCode(value) switch
        {
            MovementStatuses.Held => MovementStatuses.Held,
            MovementStatuses.Postponed => MovementStatuses.Postponed,
            MovementStatuses.Cancelled => MovementStatuses.Cancelled,
            MovementStatuses.Completed => MovementStatuses.Completed,
            MovementStatuses.Suspended => MovementStatuses.Suspended,
            _ => MovementStatuses.Scheduled,
        };

    public static string GetMovementStatusLabel(string? value)
        => NormalizeMovementStatus(value) switch
        {
            MovementStatuses.Held => "Проведено",
            MovementStatuses.Postponed => "Отложено",
            MovementStatuses.Cancelled => "Отменено",
            MovementStatuses.Completed => "Завершено",
            MovementStatuses.Suspended => "Приостановлено",
            _ => "Назначено",
        };

    public static string ResolveMovementStatusFromResult(string? result)
    {
        if (Contains(result, "отлож"))
        {
            return MovementStatuses.Postponed;
        }

        if (Contains(result, "отмен") || Contains(result, "снят"))
        {
            return MovementStatuses.Cancelled;
        }

        if (Contains(result, "рассмотр") || Contains(result, "решен") || Contains(result, "вынес"))
        {
            return MovementStatuses.Completed;
        }

        return string.IsNullOrWhiteSpace(result)
            ? MovementStatuses.Scheduled
            : MovementStatuses.Held;
    }

    public static string ResolvePartySideFromRole(string? role)
    {
        if (Contains(role, "ОТВЕТЧИК") || Contains(role, "ДОЛЖНИК"))
        {
            return PartySides.Respondent;
        }

        if (Contains(role, "ТРЕТ") || Contains(role, "ЗАИНТЕРЕС"))
        {
            return PartySides.ThirdParty;
        }

        if (Contains(role, "ЗАЯВИТЕЛ"))
        {
            return PartySides.Applicant;
        }

        if (Contains(role, "ИСТЕЦ") || Contains(role, "ВЗЫСКАТЕЛ"))
        {
            return PartySides.Claimant;
        }

        return PartySides.Other;
    }

    public static string GetPartySideLabel(string? value)
        => value switch
        {
            PartySides.Claimant => "Истец",
            PartySides.Respondent => "Ответчик",
            PartySides.ThirdParty => "Третье лицо",
            PartySides.Applicant => "Заявитель",
            _ => "Иная сторона",
        };

    private static string NormalizeCode(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;

    private static bool Contains(string? value, string fragment)
        => value?.Contains(fragment, StringComparison.OrdinalIgnoreCase) == true;
}
