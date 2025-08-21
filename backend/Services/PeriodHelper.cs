namespace PayPlanner.Api.Services
{
    /// <summary>
    /// Хелпер для расчёта периодов.
    /// Приоритет входных параметров: (from,to) > period > this-month.
    /// Все границы возвращаются как [начало дня, конец дня], включительно.
    /// </summary>
    public static class PeriodHelper
    {
        private static DateTime EndOfDay(DateTime d) => d.Date.AddDays(1).AddTicks(-1);

        /// <summary>Определить "сегодня" с учётом таймзоны (если задана), иначе UTC.</summary>
        private static DateTime GetToday(string? tzId, DateTime? nowUtc)
        {
            var now = nowUtc ?? DateTime.UtcNow;
            if (string.IsNullOrWhiteSpace(tzId))
                return now.Date;

            try
            {
                var tz = TimeZoneInfo.FindSystemTimeZoneById(tzId);
                return TimeZoneInfo.ConvertTimeFromUtc(now, tz).Date;
            }
            catch
            {
                // На случай некорректного tzId — откатываемся к UTC
                return now.Date;
            }
        }

        /// <summary>Границы месяца относительно указанной даты.</summary>
        private static (DateTime from, DateTime to) Month(DateTime d, int monthShift)
        {
            var first = new DateTime(d.Year, d.Month, 1).AddMonths(monthShift);
            var lastInclusive = first.AddMonths(1).AddTicks(-1);
            return (first, lastInclusive);
        }

        /// <summary>Границы квартала относительно указанной даты.</summary>
        private static (DateTime from, DateTime to) Quarter(DateTime d, int quarterShift)
        {
            int qStartMonth = ((d.Month - 1) / 3) * 3 + 1;
            var start = new DateTime(d.Year, qStartMonth, 1).AddMonths(quarterShift * 3);
            var end = start.AddMonths(3).AddTicks(-1);
            return (start, end);
        }

        private static DateTime StartOfDay(DateTime d) => d.Date;

        /// <summary>
        /// Рассчитать период (см. XML-doc для поддерживаемых пресетов).
        /// </summary>
        public static (DateTime from, DateTime to) Resolve(
            DateTime? from, DateTime? to, string? period,
            DayOfWeek weekStart = DayOfWeek.Monday, string? tzId = null, DateTime? nowUtc = null)
        {
            // 1) Обе границы: нормализуем дни, переставляем при необходимости, затем применяем Start/EndOfDay.
            if (from.HasValue && to.HasValue)
            {
                var fd = from.Value.Date;
                var td = to.Value.Date;
                var startDay = fd <= td ? fd : td;
                var endDay = fd <= td ? td : fd;
                return (StartOfDay(startDay), EndOfDay(endDay));
            }

            // 2) Одна граница → однодневный период [SOD, EOD]
            if (from.HasValue ^ to.HasValue)
            {
                var d = (from ?? to)!.Value.Date;
                return (StartOfDay(d), EndOfDay(d));
            }

            // 3) Пресеты
            var today = GetToday(tzId, nowUtc);
            var key = (period ?? "this-month").Trim().ToLowerInvariant();

            switch (key)
            {
                // День
                case "today":
                    return (StartOfDay(today), EndOfDay(today));
                case "yesterday":
                    {
                        var d = today.AddDays(-1);
                        return (StartOfDay(d), EndOfDay(d));
                    }

                // Скользящие окна
                case "last-7d":
                    return (StartOfDay(today.AddDays(-6)), EndOfDay(today));
                case "last-30d":
                    return (StartOfDay(today.AddDays(-29)), EndOfDay(today));

                // Недели
                case "this-week":
                    {
                        var sow = StartOfWeek(today, weekStart);
                        var eow = sow.AddDays(6);
                        return (StartOfDay(sow), EndOfDay(eow));
                    }
                case "last-week":
                    {
                        var sow = StartOfWeek(today, weekStart).AddDays(-7);
                        var eow = sow.AddDays(6);
                        return (StartOfDay(sow), EndOfDay(eow));
                    }

                // Месяцы
                case "this-month":
                    return Month(today, 0);
                case "last-month":
                case "previous-month":
                    return Month(today, -1);

                // Кварталы
                case "this-quarter":
                    return Quarter(today, 0);
                case "last-quarter":
                    return Quarter(today, -1);
                case "qtd":
                    {
                        var (qs, _) = Quarter(today, 0);
                        return (qs, EndOfDay(today));
                    }

                // Год
                case "this-year":
                    {
                        var start = new DateTime(today.Year, 1, 1);
                        var endExclusiveNextYear = new DateTime(today.Year + 1, 1, 1);
                        return (start, endExclusiveNextYear.AddTicks(-1));
                    }
                case "ytd":
                    return (new DateTime(today.Year, 1, 1), EndOfDay(today));

                // По умолчанию
                default:
                    return Month(today, 0);
            }
        }

        /// <summary>Начало недели для указанной даты с заданным днём старта недели.</summary>
        public static DateTime StartOfWeek(DateTime date, DayOfWeek startOfWeek = DayOfWeek.Monday)
        {
            int diff = (7 + ((int)date.DayOfWeek - (int)startOfWeek)) % 7;
            return date.AddDays(-diff).Date;
        }
    }
}
