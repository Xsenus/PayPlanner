-- Financial cash access acceptance checks for PayPlanner.
-- Read-only PostgreSQL script. It must not change data.
-- Run on the target database after deploying the cash/payment access hardening.

-- 1. Roles and permissions for financial sections.
-- Expected for the reported issue:
--   - users who should not see money must have payments.CanView = false;
--   - cash reports also require reports access and payments access;
--   - delete/export permissions should be granted only intentionally.
select
  r."Id" as "RoleId",
  r."Name" as "RoleName",
  rp."Section",
  rp."CanView",
  rp."CanCreate",
  rp."CanEdit",
  rp."CanDelete",
  rp."CanExport"
from "Roles" r
left join "RolePermissions" rp on rp."RoleId" = r."Id"
where rp."Section" in ('payments', 'reports', 'accounts', 'acts', 'contracts')
order by r."Name", rp."Section";

-- 2. Roles that can still view payments or cash-related reports.
-- Review every row: these roles can potentially see payment/cash data.
select
  r."Id" as "RoleId",
  r."Name" as "RoleName",
  max(case when rp."Section" = 'payments' then rp."CanView"::int else 0 end)::boolean as "CanViewPayments",
  max(case when rp."Section" = 'payments' then rp."CanCreate"::int else 0 end)::boolean as "CanCreatePayments",
  max(case when rp."Section" = 'payments' then rp."CanEdit"::int else 0 end)::boolean as "CanEditPayments",
  max(case when rp."Section" = 'payments' then rp."CanDelete"::int else 0 end)::boolean as "CanDeletePayments",
  max(case when rp."Section" = 'payments' then rp."CanExport"::int else 0 end)::boolean as "CanExportPayments",
  max(case when rp."Section" = 'reports' then rp."CanView"::int else 0 end)::boolean as "CanViewReports",
  max(case when rp."Section" = 'reports' then rp."CanExport"::int else 0 end)::boolean as "CanExportReports"
from "Roles" r
left join "RolePermissions" rp on rp."RoleId" = r."Id"
where rp."Section" in ('payments', 'reports')
group by r."Id", r."Name"
having
  max(case when rp."Section" = 'payments' then rp."CanView"::int else 0 end) = 1
  or max(case when rp."Section" = 'reports' then rp."CanView"::int else 0 end) = 1
order by r."Name";

-- 3. Users whose roles can view payments.
-- Use this to confirm that secretaries/lawyers are not attached to such roles.
select
  u."Id" as "UserId",
  u."FullName",
  u."Email",
  r."Name" as "RoleName",
  rp."CanView" as "CanViewPayments",
  rp."CanCreate" as "CanCreatePayments",
  rp."CanEdit" as "CanEditPayments",
  rp."CanDelete" as "CanDeletePayments",
  rp."CanExport" as "CanExportPayments"
from "Users" u
join "Roles" r on r."Id" = u."RoleId"
join "RolePermissions" rp on rp."RoleId" = r."Id" and rp."Section" = 'payments'
where u."IsActive" = true
  and rp."CanView" = true
order by r."Name", u."FullName", u."Email";

-- 4. Cash order source summary.
-- This answers "where did these operations come from?" for the cash section.
select
  count(*) as "TotalCashOrders",
  count(*) filter (where co."PaymentId" is not null) as "FromPayments",
  count(*) filter (where co."MoneyOperationId" is not null) as "FromMoneyOperations",
  count(*) filter (
    where co."PaymentId" is null
      and co."MoneyOperationId" is null
      and (
        coalesce(co."Basis", '') ilike '%инвентар%'
        or coalesce(co."Comment", '') ilike '%инвентар%'
      )
  ) as "FromInventoryText",
  count(*) filter (where co."PaymentId" is null and co."MoneyOperationId" is null) as "WithoutDirectSourceLink",
  sum(
    case
      when co."Status" = 'Posted' and co."Type" = 'Incoming' then co."Amount"
      when co."Status" = 'Posted' and co."Type" = 'Outgoing' then -co."Amount"
      else 0
    end
  ) as "PostedCashBalance"
from "CashOrders" co
where co."IsDeleted" = false;

-- 5. Latest cash orders with source, payment, invoice and audit data.
-- These rows are suitable for explaining the screenshot to the customer.
select
  co."Number" as "CashOrderNumber",
  co."Date" as "CashOrderDate",
  co."Type",
  co."Status",
  co."Amount",
  case
    when co."PaymentId" is not null then 'payment'
    when co."MoneyOperationId" is not null then 'money_operation'
    when coalesce(co."Basis", '') ilike '%инвентар%'
      or coalesce(co."Comment", '') ilike '%инвентар%' then 'inventory'
    else 'manual_or_legacy'
  end as "SourceType",
  co."PaymentId",
  p."InvoiceId",
  i."Number" as "InvoiceNumber",
  co."MoneyOperationId",
  co."CreatedByUserName",
  co."UpdatedByUserName",
  co."Basis"
from "CashOrders" co
left join "Payments" p on p."Id" = co."PaymentId"
left join "Invoices" i on i."Id" = p."InvoiceId"
where co."IsDeleted" = false
order by co."Date" desc, co."Id" desc
limit 50;

-- 6. Cash orders without direct source links.
-- Review manually: these are manual, legacy, or inventory-created documents.
select
  co."Id",
  co."Number",
  co."Date",
  co."Type",
  co."Status",
  co."Amount",
  co."CreatedByUserName",
  co."Basis",
  co."Comment"
from "CashOrders" co
where co."IsDeleted" = false
  and co."PaymentId" is null
  and co."MoneyOperationId" is null
order by co."Date" desc, co."Id" desc
limit 100;

-- 7. Direct check for the three local roles from the reported scenario.
-- If role names differ on the target database, use sections 1-3 instead.
select
  r."Name" as "RoleName",
  rp."Section",
  rp."CanView",
  rp."CanCreate",
  rp."CanEdit",
  rp."CanDelete",
  rp."CanExport"
from "Roles" r
join "RolePermissions" rp on rp."RoleId" = r."Id"
where r."Name" in ('Секретарь', 'Юрист', 'Бухгалтер')
  and rp."Section" in ('payments', 'reports', 'accounts', 'acts', 'contracts')
order by r."Name", rp."Section";
