# Quick Start Guide - PayPlanner Enhancements

## 🎯 What Was Built

A complete, **non-destructive** enhancement system for PayPlanner with:
- ✅ Unified expenses module (visible in multiple places, counted once)
- ✅ Employee directory with lifecycle and case assignments
- ✅ Client entity split (Companies + Persons)
- ✅ Partial payments with remainder tracking
- ✅ Enhanced RBAC and activity audit
- ✅ Feature flags for safe rollout

**CRITICAL:** All original data is preserved. Nothing was deleted or overwritten.

---

## 🚦 Deployment Checklist

### Phase 1: Backup & Safety (REQUIRED)
```bash
# 1. Create database backup
sqlite3 payplanner.db ".backup payplanner_backup_$(date +%Y%m%d_%H%M%S).db"

# 2. Verify backup works
sqlite3 payplanner_backup_*.db "SELECT COUNT(*) FROM Clients;"

# 3. Document current state
sqlite3 payplanner.db ".schema" > schema_before_migration.sql
```

### Phase 2: Apply Migrations (Staging First)
```bash
# On staging environment
cd /backend
dotnet ef database update

# Verify migrations applied
sqlite3 payplanner.db "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;"

# Expected new tables:
# - feature_flags
# - companies
# - persons
# - employees
# - case_employees
# - expenses
# - payment_schedule
```

### Phase 3: Run Backfill (DRY RUN)
```bash
# Via API (create admin endpoint or run via service)
# POST /api/admin/migrate/backfill?dryRun=true

# Check logs for:
# "DRY RUN: Would create X companies, Y persons"
```

### Phase 4: Verify Data Integrity
```sql
-- Run these validation queries
-- 1. Check original data intact
SELECT 'Original Clients', COUNT(*) FROM Clients
UNION ALL SELECT 'Original Payments', COUNT(*) FROM Payments
UNION ALL SELECT 'Original Cases', COUNT(*) FROM Cases;

-- 2. Check new tables empty (before backfill)
SELECT 'Companies', COUNT(*) FROM companies
UNION ALL SELECT 'Persons', COUNT(*) FROM persons
UNION ALL SELECT 'Employees', COUNT(*) FROM employees;

-- 3. Verify feature flags exist
SELECT flag_key, enabled FROM feature_flags;
```

### Phase 5: Run Actual Backfill
```bash
# POST /api/admin/migrate/backfill?dryRun=false

# Verify results
SELECT
  (SELECT COUNT(*) FROM Clients) as total_clients,
  (SELECT COUNT(*) FROM companies) as companies_created,
  (SELECT COUNT(*) FROM persons) as persons_created;
```

### Phase 6: Enable Features One by One
```bash
# Enable employees directory first (safest)
POST /api/feature-flags/employees_directory_enabled/enable

# Test thoroughly, then enable next
POST /api/feature-flags/expenses_module_enabled/enable

# Continue with others...
POST /api/feature-flags/clients_split_enabled/enable
POST /api/feature-flags/partial_payments_enabled/enable
```

---

## 🛠️ Feature Flags

All features are **disabled by default**. Enable via API:

| Flag | Purpose | Risk Level |
|------|---------|------------|
| `employees_directory_enabled` | Employee management | 🟢 Low |
| `expenses_module_enabled` | Unified expenses | 🟢 Low |
| `clients_split_enabled` | Companies/Persons split | 🟡 Medium |
| `partial_payments_enabled` | Payment scheduling | 🟡 Medium |
| `activity_logging_ui_clicks` | Granular UI logging | 🟡 Medium (high volume) |

### Enable a Feature
```bash
POST /api/feature-flags/{flag_key}/enable
```

### Disable a Feature (Instant Rollback)
```bash
POST /api/feature-flags/{flag_key}/disable
```

### Check Status
```bash
GET /api/feature-flags
```

---

## 📊 Key Concepts

### 1. Unified Expenses
**Problem:** Track expenses that relate to both employees and cases without duplication.

**Solution:**
- Single `expenses` table
- Each expense has ONE record
- Visible in employee view AND case view
- Aggregations use `DISTINCT expense.id` to prevent double-counting

**Views:**
- `vw_expenses_by_employee` - Employee-centric view
- `vw_expenses_by_case` - Case-centric view
- `vw_expenses_out_of_case` - Non-case expenses

### 2. Client Entity Split
**Problem:** Mix of companies and individuals in one table.

**Solution:**
- NEW: `companies` table (legal entities)
- NEW: `persons` table (individuals, optionally linked to companies)
- OLD: `Clients` table **preserved completely**
- Link via `legacy_client_id`

**Compatibility:**
- Use `vw_legacy_clients` view for backward compatibility
- Existing queries work unchanged
- New features use `companies`/`persons` tables

### 3. Partial Payments
**Problem:** Track remainder when payment < due amount.

**Solution:**
- `payment_schedule` table tracks planned amounts
- Status: Planned → Overdue → Paid
- Auto-apply new payments to oldest planned item
- `vw_receivables_status` shows current/overdue buckets

### 4. Employee Lifecycle
**Problem:** Track employees with hire/termination dates and case assignments.

**Solution:**
- `employees` table with status (active/terminated)
- `case_employees` links employees to cases with roles (lead/assistant/consultant)
- Reports: workload, expenses per employee, case margins

---

## 🔍 Validation Queries

### Check No Duplicates in Expense Aggregations
```sql
SELECT
  COUNT(*) as total_records,
  COUNT(DISTINCT id) as unique_ids,
  CASE
    WHEN COUNT(*) = COUNT(DISTINCT id) THEN '✅ PASS'
    ELSE '❌ FAIL - DUPLICATES'
  END as status
FROM vw_expenses_by_employee;
```

### Verify Original Data Intact
```sql
-- Should return exact same counts before and after migration
SELECT 'Clients' as table_name, COUNT(*) as count FROM Clients
UNION ALL SELECT 'Payments', COUNT(*) FROM Payments
UNION ALL SELECT 'Cases', COUNT(*) FROM Cases
UNION ALL SELECT 'DealTypes', COUNT(*) FROM DealTypes;
```

### Check Backfill Accuracy
```sql
SELECT
  c.Id as client_id,
  c.Name as client_name,
  c.Company as client_company,
  comp.id as company_id,
  comp.legal_name,
  p.id as person_id,
  p.first_name || ' ' || p.last_name as person_name
FROM Clients c
LEFT JOIN companies comp ON comp.legacy_client_id = c.Id
LEFT JOIN persons p ON p.legacy_client_id = c.Id
LIMIT 20;
```

### Verify Payment Schedule Logic
```sql
SELECT
  ps.*,
  c.CaseNumber,
  CASE
    WHEN ps.due_date < date('now') AND ps.status = 'Planned' THEN '⚠️ Should be Overdue'
    WHEN ps.paid_amount >= ps.scheduled_amount AND ps.status != 'Paid' THEN '⚠️ Should be Paid'
    ELSE '✅ OK'
  END as validation_status
FROM payment_schedule ps
INNER JOIN Cases c ON ps.case_id = c.Id;
```

---

## 🚨 Rollback Procedures

### Immediate Rollback (No Data Loss)
```bash
# 1. Disable all feature flags
POST /api/feature-flags/expenses_module_enabled/disable
POST /api/feature-flags/clients_split_enabled/disable
POST /api/feature-flags/partial_payments_enabled/disable
POST /api/feature-flags/employees_directory_enabled/disable

# 2. Application reverts to legacy code paths
# 3. New tables remain but are unused
# 4. Zero risk - all original data intact
```

### Full Rollback (If Absolutely Necessary)
```bash
# 1. Restore from backup
mv payplanner.db payplanner_with_new_features.db
cp payplanner_backup_YYYYMMDD_HHMMSS.db payplanner.db

# 2. Restart application
# 3. Verify original state

# 4. Optional: Remove new tables
sqlite3 payplanner.db "
DROP TABLE IF EXISTS payment_schedule;
DROP TABLE IF EXISTS case_employees;
DROP TABLE IF EXISTS expenses;
DROP TABLE IF EXISTS employees;
DROP TABLE IF EXISTS persons;
DROP TABLE IF EXISTS companies;
DROP TABLE IF EXISTS feature_flags;
"
```

---

## 📁 File Reference

### Models
- `/backend/Models/Company.cs` - Legal entity
- `/backend/Models/Person.cs` - Individual contact
- `/backend/Models/Employee.cs` - Staff member
- `/backend/Models/Expense.cs` - Unified expense
- `/backend/Models/PaymentSchedule.cs` - Partial payment tracking
- `/backend/Models/FeatureFlag.cs` - Runtime toggles

### Services
- `/backend/Services/FeatureFlagService.cs` - Flag management
- `/backend/Services/DataMigrationService.cs` - Non-destructive backfill
- `/backend/Services/ActivityLogService.cs` - Enhanced audit logging

### Migrations
- `/backend/Migrations/20251003000000_AddFeatureFlags.cs`
- `/backend/Migrations/20251003010000_AddCompaniesAndPersons.cs`
- `/backend/Migrations/20251003020000_AddEmployeesDirectory.cs`
- `/backend/Migrations/20251003030000_AddExpensesModule.cs`
- `/backend/Migrations/20251003040000_AddPartialPayments.cs`

### Documentation
- `/MIGRATION.md` - Detailed migration procedures
- `/IMPLEMENTATION_SUMMARY.md` - Complete feature overview
- `/QUICK_START.md` - This file

---

## 💡 Best Practices

1. **Always Test on Staging First**
   - Run all migrations on staging
   - Enable features one by one
   - Verify each feature before moving to next

2. **Monitor Performance**
   - Check query execution times
   - Verify indexes are used (`EXPLAIN QUERY PLAN`)
   - Watch for N+1 queries in views

3. **Gradual Feature Rollout**
   - Enable for small user group first
   - Monitor for issues 24-48 hours
   - Roll out to all users if stable

4. **Regular Backups**
   - Before each migration
   - Before enabling new features
   - Weekly automated backups

5. **Data Integrity Checks**
   - Run validation queries daily
   - Alert on unexpected counts
   - Audit logs for anomalies

---

## ❓ FAQ

**Q: Will this affect my existing data?**
A: No. All existing tables are completely preserved. New features use separate tables.

**Q: What if I need to roll back?**
A: Simply disable the feature flags. The app reverts to legacy behavior instantly.

**Q: Do I need to migrate all clients to companies/persons?**
A: No. You can use both systems in parallel. The `vw_legacy_clients` view provides compatibility.

**Q: Can I enable just one feature?**
A: Yes. Each feature has its own flag and can be enabled independently.

**Q: What if the backfill creates wrong data?**
A: Run in DRY RUN mode first. The backfill doesn't modify original data, only creates new records.

**Q: How do I test without affecting production?**
A: Create a staging environment with a copy of production database. Test there first.

---

## 📞 Support

### Common Issues

1. **Migration fails with "table already exists"**
   - Solution: Migrations are idempotent. Check if already applied: `SELECT * FROM __EFMigrationsHistory`

2. **Feature flag not taking effect**
   - Solution: Cache refresh (5-minute lifetime). Clear: `POST /api/feature-flags/cache/clear`

3. **Expenses showing duplicate totals**
   - Solution: Verify views use `DISTINCT id`. Run validation query above.

4. **Backfill created too many/few records**
   - Solution: Check logic in `DataMigrationService`. Adjust company/person detection rules.

---

## ✅ Success Criteria

Before considering deployment complete:

- [ ] All migrations applied successfully
- [ ] Feature flags table populated
- [ ] Backfill DRY RUN completed without errors
- [ ] Validation queries show no duplicates
- [ ] Original data counts unchanged
- [ ] Compatibility views accessible
- [ ] At least one feature tested end-to-end
- [ ] Rollback procedure tested
- [ ] Documentation reviewed by team

---

**Ready to deploy? Follow the checklist above and enable features gradually. All original data is safe!**
