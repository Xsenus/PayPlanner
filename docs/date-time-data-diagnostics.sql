-- Date/time data diagnostics for PayPlanner.
-- Read-only PostgreSQL checks. Do not run any UPDATE before reviewing the output.

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
    or btrim(us."Value") = ''
    or btrim(us."Value", '" ') not in (
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
where t."IsDeleted" = false
  and t."DueAt" is not null
  and (assignee_tz."Value" is null or btrim(assignee_tz."Value") = '')
order by t."DueAt" desc
limit 200;

-- 3. Active tasks around midnight in the assignee region.
-- These are the highest-risk rows for day-boundary shifts.
select
  t."Id",
  t."Title",
  t."Status",
  t."AssigneeUserId",
  t."AssigneeName",
  t."DueAt" as "DueAtUtc",
  btrim(coalesce(assignee_tz."Value", '"Europe/Moscow"'), '" ') as "AssigneeTimeZoneId",
  (
    t."DueAt" at time zone 'UTC' at time zone
    case btrim(coalesce(assignee_tz."Value", '"Europe/Moscow"'), '" ')
      when 'Europe/Samara' then 'Europe/Samara'
      when 'Asia/Yekaterinburg' then 'Asia/Yekaterinburg'
      when 'Asia/Omsk' then 'Asia/Omsk'
      when 'Asia/Novosibirsk' then 'Asia/Novosibirsk'
      when 'Asia/Krasnoyarsk' then 'Asia/Krasnoyarsk'
      when 'Asia/Irkutsk' then 'Asia/Irkutsk'
      when 'Asia/Yakutsk' then 'Asia/Yakutsk'
      when 'Asia/Vladivostok' then 'Asia/Vladivostok'
      when 'Asia/Magadan' then 'Asia/Magadan'
      when 'Asia/Kamchatka' then 'Asia/Kamchatka'
      else 'Europe/Moscow'
    end
  ) as "DueAtAssigneeLocal"
from "TaskJournalItems" t
left join "UserSettings" assignee_tz
  on assignee_tz."UserId" = t."AssigneeUserId"
  and assignee_tz."Key" = 'profile.timeZoneId'
where t."IsDeleted" = false
  and t."DueAt" is not null
  and t."Status" not in ('done', 'cancelled')
  and extract(hour from (
    t."DueAt" at time zone 'UTC' at time zone
    case btrim(coalesce(assignee_tz."Value", '"Europe/Moscow"'), '" ')
      when 'Europe/Samara' then 'Europe/Samara'
      when 'Asia/Yekaterinburg' then 'Asia/Yekaterinburg'
      when 'Asia/Omsk' then 'Asia/Omsk'
      when 'Asia/Novosibirsk' then 'Asia/Novosibirsk'
      when 'Asia/Krasnoyarsk' then 'Asia/Krasnoyarsk'
      when 'Asia/Irkutsk' then 'Asia/Irkutsk'
      when 'Asia/Yakutsk' then 'Asia/Yakutsk'
      when 'Asia/Vladivostok' then 'Asia/Vladivostok'
      when 'Asia/Magadan' then 'Asia/Magadan'
      when 'Asia/Kamchatka' then 'Asia/Kamchatka'
      else 'Europe/Moscow'
    end
  )) in (0, 1, 2, 22, 23)
order by t."DueAt" desc
limit 200;

-- 4. Tasks that may have been saved with the old fixed +07:00 rule.
-- This does not prove an error; it marks candidates for manual comparison
-- with the task history and external calendar event.
select
  t."Id",
  t."Title",
  t."Status",
  t."AssigneeName",
  t."DueAt" as "DueAtUtc",
  (t."DueAt" + interval '7 hours') as "OldFixedPlus7LocalCandidate",
  (t."DueAt" + interval '4 hours') as "SamaraLocalCandidate",
  (t."DueAt" + interval '3 hours') as "MoscowLocalCandidate"
from "TaskJournalItems" t
where t."IsDeleted" = false
  and t."DueAt" is not null
  and t."CreatedAt" < timestamp '2026-06-05 00:00:00'
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
  and extract(hour from c."NextHearingAt") in (0, 1, 2, 21, 22, 23)
order by c."NextHearingAt" desc
limit 200;

-- 6. External calendar links that should be compared with provider event times.
select
  'google' as "Provider",
  l."TaskJournalItemId",
  t."Title",
  t."DueAt",
  l."GoogleEventId" as "ExternalEventId",
  l."SyncStatus",
  l."UpdatedAt"
from "GoogleCalendarTaskLinks" l
join "TaskJournalItems" t on t."Id" = l."TaskJournalItemId"
where t."IsDeleted" = false
union all
select
  'yandex' as "Provider",
  l."TaskJournalItemId",
  t."Title",
  t."DueAt",
  l."YandexEventId" as "ExternalEventId",
  l."SyncStatus",
  l."UpdatedAt"
from "YandexCalendarTaskLinks" l
join "TaskJournalItems" t on t."Id" = l."TaskJournalItemId"
where t."IsDeleted" = false
order by "UpdatedAt" desc nulls last
limit 200;
