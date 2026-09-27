export type ReportGroupId = 'payments' | 'tasks' | 'legal' | 'analytics';

export interface ReportSectionAccess {
  payments: boolean;
  tasks: boolean;
  cases: boolean;
  accounts: boolean;
  acts: boolean;
  contracts: boolean;
}

export function canViewCaseFinanceReport(access: ReportSectionAccess): boolean {
  return access.cases && access.accounts && access.payments;
}

export function buildVisibleReportGroups(access: ReportSectionAccess): ReportGroupId[] {
  const groups: ReportGroupId[] = [];

  if (access.payments) {
    groups.push('payments');
  }

  if (access.tasks) {
    groups.push('tasks');
  }

  if (access.cases) {
    groups.push('legal');
  }

  if (access.payments || access.accounts || access.acts || access.contracts) {
    groups.push('analytics');
  }

  return groups;
}

export function isReportGroupEnabled(
  activeGroup: ReportGroupId,
  targetGroup: ReportGroupId,
  visibleGroups: readonly ReportGroupId[],
): boolean {
  return activeGroup === targetGroup && visibleGroups.includes(targetGroup);
}
