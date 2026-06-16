-- Диагностика дублей календарей Google Calendar.
-- Подходит для SQLite и PostgreSQL: запрос ничего не удаляет и нужен для проверки VPS/локальной БД.

select
  c."ConnectionId",
  gc."UserId" as "ConnectionUserId",
  cu."FullName" as "ConnectionUserName",
  c."OwnerUserId",
  ou."FullName" as "OwnerUserName",
  c."Id" as "CalendarRowId",
  c."GoogleCalendarId",
  c."Name",
  c."LastError",
  c."CreatedAt",
  c."UpdatedAt"
from "GoogleCalendarSyncCalendars" c
join "GoogleCalendarConnections" gc on gc."Id" = c."ConnectionId"
left join "Users" cu on cu."Id" = gc."UserId"
left join "Users" ou on ou."Id" = c."OwnerUserId"
where exists (
  select 1
  from "GoogleCalendarSyncCalendars" d
  where d."ConnectionId" = c."ConnectionId"
    and coalesce(d."OwnerUserId", -1) = coalesce(c."OwnerUserId", -1)
  group by d."ConnectionId", coalesce(d."OwnerUserId", -1)
  having count(*) > 1
)
order by c."ConnectionId", coalesce(c."OwnerUserId", -1), c."Id";
