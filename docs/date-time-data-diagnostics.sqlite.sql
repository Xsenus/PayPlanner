-- Date/time data diagnostics for local PayPlanner SQLite databases.
-- Read-only checks. Do not run any UPDATE before reviewing the output.

-- 1. User profile time zones: empty or unexpected values.
select
  us."UserId",
  u."FullName",
  u."Email",
  us."Value" as "TimeZoneId"
from "UserSettings" us
left join "Users" u on u."Id" = us."UserId"
where us."Key" = 'profile.timeZoneId'
  and (
    us."Value" is null
    or trim(us."Value") = ''
    or trim(us."Value", '" ') not in (
      'Europe/Moscow',
      'Europe/Samara',
      'Asia/Yekaterinburg',
      'Asia/Omsk',
      'Asia/Novosibirsk',
      'Asia/Krasnoyarsk',
      'Asia/Irkutsk',
      'Asia/Yakutsk',
      'Asia/Vladivostok',
      'Asia/Magadan',
      'Asia/Kamchatka'
    )
  )
order by u."FullName", u."Email";

-- 2. Tasks that have a due time but no assignee profile time zone.
select
  t."Id",
  t."Title",
  t."AssigneeUserId",
  t."AssigneeName",
  t."DueAt",
  assignee_tz."Value" as "AssigneeTimeZoneId"
from "TaskJournalItems" t
left join "UserSettings" assignee_tz
  on assignee_tz."UserId" = t."AssigneeUserId"
  and assignee_tz."Key" = 'profile.timeZoneId'
where coalesce(t."IsDeleted", 0) = 0
  and t."DueAt" is not null
  and (assignee_tz."Value" is null or trim(assignee_tz."Value") = '')
order by t."DueAt" desc
limit 200;

-- 3. Active tasks around midnight in UTC.
-- SQLite cannot reliably apply named IANA time zones without extensions,
-- so this query marks UTC boundary candidates for manual profile-timezone review.
select
  t."Id",
  t."Title",
  t."Status",
  t."AssigneeUserId",
  t."AssigneeName",
  t."DueAt" as "DueAtUtc",
  trim(coalesce(assignee_tz."Value", '"Europe/Moscow"'), '" ') as "AssigneeTimeZoneId"
from "TaskJournalItems" t
left join "UserSettings" assignee_tz
  on assignee_tz."UserId" = t."AssigneeUserId"
  and assignee_tz."Key" = 'profile.timeZoneId'
where coalesce(t."IsDeleted", 0) = 0
  and t."DueAt" is not null
  and t."Status" not in ('done', 'cancelled')
  and cast(strftime('%H', replace(replace(t."DueAt", 'T', ' '), 'Z', '')) as integer) in (0, 1, 2, 21, 22, 23)
order by t."DueAt" desc
limit 200;

-- 4. Tasks that may have been saved with the old fixed +07:00 rule.
-- This does not prove an error. It marks candidates for manual comparison
-- with the task history and external calendar event.
select
  t."Id",
  t."Title",
  t."Status",
  t."AssigneeName",
  t."DueAt" as "DueAtUtc",
  datetime(replace(replace(t."DueAt", 'T', ' '), 'Z', ''), '+7 hours') as "OldFixedPlus7LocalCandidate",
  datetime(replace(replace(t."DueAt", 'T', ' '), 'Z', ''), '+4 hours') as "SamaraLocalCandidate",
  datetime(replace(replace(t."DueAt", 'T', ' '), 'Z', ''), '+3 hours') as "MoscowLocalCandidate"
from "TaskJournalItems" t
where coalesce(t."IsDeleted", 0) = 0
  and t."DueAt" is not null
  and t."CreatedAt" < '2026-06-05 00:00:00'
order by t."DueAt" desc
limit 200;

-- 5. Court hearings with exact time near day boundaries.
select
  c."Id",
  c."Title",
  c."NextHearingAt" as "NextHearingAtUtc",
  c."ResponsibleUserId",
  responsible_tz."Value" as "ResponsibleTimeZoneId"
from "ClientCases" c
left join "UserSettings" responsible_tz
  on responsible_tz."UserId" = c."ResponsibleUserId"
  and responsible_tz."Key" = 'profile.timeZoneId'
where c."NextHearingAt" is not null
  and cast(strftime('%H', replace(replace(c."NextHearingAt", 'T', ' '), 'Z', '')) as integer) in (0, 1, 2, 21, 22, 23)
order by c."NextHearingAt" desc
limit 200;

-- 6. External calendar link table availability.
-- Older local SQLite databases can be behind the current calendar-sync schema.
-- When both tables are present, use the PostgreSQL diagnostic on the migrated
-- server copy or adapt the provider queries from docs/date-time-data-diagnostics.sql.
select
  'GoogleCalendarTaskLinks' as "TableName",
  exists(select 1 from sqlite_master where type = 'table' and name = 'GoogleCalendarTaskLinks') as "Exists"
union all
select
  'YandexCalendarTaskLinks' as "TableName",
  exists(select 1 from sqlite_master where type = 'table' and name = 'YandexCalendarTaskLinks') as "Exists";
