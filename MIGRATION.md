# Migration Guide - PayPlanner Enhancement

## Overview
This document describes additive-only migrations for expenses, employees, client splitting, partial payments, and enhanced RBAC. All changes are non-destructive and backward-compatible.

## Prerequisites
- **CRITICAL**: Create full database backup before proceeding
- Test all migrations on staging environment first
- All new features are behind feature flags (disabled by default)

## Backup Instructions
```bash
# SQLite backup
sqlite3 payplanner.db ".backup payplanner_backup_$(date +%Y%m%d_%H%M%S).db"

# Verify backup
sqlite3 payplanner_backup_*.db "SELECT COUNT(*) FROM Clients;"
```

## Migration Sequence

### Phase 1: New Tables (Additive Only)
1. `companies` - Legal entities split from clients
2. `persons` - Individual contacts split from clients
3. `employees` - Staff directory with lifecycle tracking
4. `expenses` - Unified expense tracking
5. `case_employees` - Employee assignment to cases
6. `payment_schedule` - Partial payment remainder tracking
7. `feature_flags` - Runtime feature toggles

### Phase 2: Backfill Scripts (Non-Destructive)
1. Map existing `Clients.Company` → `companies` table
2. Map existing `Clients.Name` → `persons` table
3. Link persons to companies based on data patterns
4. **NO DATA DELETION** - Original `Clients` table remains intact

### Phase 3: Views (Read-Only)
1. `vw_expenses_by_employee` - Deduplicated expense aggregations
2. `vw_expenses_by_case` - Case-specific expense views
3. `vw_receivables_status` - Payment schedule summary
4. `vw_legacy_clients` - Compatibility view for existing queries

## Feature Flags

| Flag | Default | Description |
|------|---------|-------------|
| `expenses_module_enabled` | false | Enable new expenses functionality |
| `clients_split_enabled` | false | Enable companies/persons split |
| `partial_payments_enabled` | false | Enable payment schedule tracking |
| `activity_logging_ui_clicks` | false | Log granular UI interactions |
| `employees_directory_enabled` | false | Enable employee management |

## Rollback Strategy

### Immediate Rollback (No Data Loss)
1. Disable all feature flags via configuration
2. Application reverts to legacy code paths
3. New tables remain but are unused

### Full Rollback (If Needed)
```sql
-- Drop new tables (only if absolutely necessary)
DROP TABLE IF EXISTS payment_schedule;
DROP TABLE IF EXISTS case_employees;
DROP TABLE IF EXISTS expenses;
DROP TABLE IF EXISTS employees;
DROP TABLE IF EXISTS persons;
DROP TABLE IF EXISTS companies;
DROP TABLE IF EXISTS feature_flags;

-- Drop views
DROP VIEW IF EXISTS vw_expenses_by_employee;
DROP VIEW IF EXISTS vw_expenses_by_case;
DROP VIEW IF EXISTS vw_receivables_status;
DROP VIEW IF EXISTS vw_legacy_clients;

-- Restore from backup
.restore payplanner_backup_YYYYMMDD_HHMMSS.db
```

## Migration Files

1. `20251003000000_AddCompaniesAndPersons.cs` - Client entity split
2. `20251003010000_AddEmployeesDirectory.cs` - Employee management
3. `20251003020000_AddExpensesModule.cs` - Unified expenses
4. `20251003030000_AddPartialPayments.cs` - Payment scheduling
5. `20251003040000_AddFeatureFlags.cs` - Runtime toggles
6. `20251003050000_AddBackfillScripts.cs` - Data migration helpers

## Validation Queries

### Verify Data Integrity
```sql
-- Ensure no data loss in Clients table
SELECT COUNT(*) as original_count FROM Clients;

-- Check backfill accuracy
SELECT COUNT(*) as companies_count FROM companies;
SELECT COUNT(*) as persons_count FROM persons;

-- Verify expense deduplication
SELECT
  COUNT(*) as total_records,
  COUNT(DISTINCT id) as unique_expenses
FROM vw_expenses_by_employee;
```

## Performance Considerations

### New Indexes
- `expenses(employee_id, date)` - Employee expense queries
- `expenses(case_id, date)` - Case expense queries
- `payment_schedule(status, due_date)` - Overdue payment detection
- `persons(company_id)` - Company-person lookups

### Query Plan Testing
```sql
EXPLAIN QUERY PLAN
SELECT * FROM vw_expenses_by_employee WHERE employee_id = 1;
```

## Support & Troubleshooting

### Common Issues
1. **Duplicate expenses in reports**: Check aggregation uses `DISTINCT expense.id`
2. **Missing legacy clients**: Verify `vw_legacy_clients` view is active
3. **Feature flag not working**: Check configuration reload

### Health Checks
```bash
# Verify migrations applied
sqlite3 payplanner.db "SELECT name FROM sqlite_master WHERE type='table';"

# Check feature flags status
curl http://localhost:5000/api/feature-flags
```

## Timeline
- **Day 1**: Apply migrations on staging, run validation
- **Day 2-3**: User acceptance testing with flags enabled
- **Day 4**: Production migration during maintenance window
- **Day 5+**: Monitor and adjust; rollback if critical issues

## Sign-Off Required
- [ ] Database backup verified
- [ ] Staging migration successful
- [ ] All validation queries passed
- [ ] Rollback procedure tested
- [ ] Team trained on new features
