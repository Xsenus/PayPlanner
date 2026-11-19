import type { TaskJournalAssignee } from '../../utils/taskJournalStore';

export type CalendarSelection = {
  all: boolean;
  userIds: number[];
  roleIds: number[];
  departmentIds: number[];
  departmentNames: string[];
};

export function normalizeCalendarSelection(value?: Partial<CalendarSelection> | null): CalendarSelection {
  const ids = (items: unknown) => Array.isArray(items)
    ? Array.from(new Set(items.filter((id): id is number => Number.isSafeInteger(id) && id > 0)))
    : [];
  return {
    all: value?.all !== false,
    userIds: ids(value?.userIds),
    roleIds: ids(value?.roleIds),
    departmentIds: ids(value?.departmentIds),
    departmentNames: Array.isArray(value?.departmentNames)
      ? Array.from(new Set(value.departmentNames.filter((name): name is string => typeof name === 'string' && Boolean(name.trim())).map((name) => name.trim().toLocaleLowerCase('ru'))))
      : [],
  };
}

export function resolveCalendarSelection(selection: CalendarSelection, assignees: TaskJournalAssignee[]): number[] {
  const users = new Set(selection.userIds);
  const roles = new Set(selection.roleIds);
  const departments = new Set(selection.departmentIds);
  const departmentNames = new Set(selection.departmentNames);
  return assignees.filter((assignee) => selection.all || users.has(assignee.id)
    || (assignee.roleId != null && roles.has(assignee.roleId))
    || (assignee.departmentId != null && departments.has(assignee.departmentId))
    || (!assignee.departmentId && departmentNames.has(assignee.department?.trim().toLocaleLowerCase('ru') || '')))
    .map((assignee) => assignee.id);
}
