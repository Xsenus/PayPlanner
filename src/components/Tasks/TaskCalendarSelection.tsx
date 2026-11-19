import { useId } from 'react';
import type { TaskJournalAssignee } from '../../utils/taskJournalStore';
import { SystemCheckbox } from '../ui/SystemCheckbox';
import { resolveCalendarSelection, normalizeCalendarSelection, type CalendarSelection } from './taskCalendarSelectionPreferences';

export function TaskCalendarSelection({ selection, assignees, onChange, loading = false, error, onRetry }: {
  selection: CalendarSelection;
  assignees: TaskJournalAssignee[];
  onChange: (selection: CalendarSelection) => void;
  loading?: boolean;
  error?: string | null;
  onRetry?: () => void;
}) {
  const id = useId();
  const selected = new Set(resolveCalendarSelection(selection, assignees));
  const roles = new Map<number, string>();
  const departments = new Map<number, string>();
  const namedDepartments = new Map<string, string>();
  assignees.forEach((assignee) => {
    if (assignee.roleId) roles.set(assignee.roleId, assignee.roleName || `Роль ${assignee.roleId}`);
    if (assignee.departmentId) departments.set(assignee.departmentId, assignee.department || `Подразделение ${assignee.departmentId}`);
    else if (assignee.department?.trim()) namedDepartments.set(assignee.department.trim().toLocaleLowerCase('ru'), assignee.department.trim());
  });
  const changeGroup = (kind: 'roleIds' | 'departmentIds', groupId: number, checked: boolean) => {
    if (selection.all && !checked) {
      const remaining = assignees.filter((assignee) => (kind === 'roleIds' ? assignee.roleId : assignee.departmentId) !== groupId);
      onChange({ all: false, userIds: remaining.map((assignee) => assignee.id), roleIds: [], departmentIds: [], departmentNames: [] });
      return;
    }
    onChange({ ...selection, all: false, [kind]: checked
      ? [...selection[kind], groupId]
      : selection[kind].filter((value) => value !== groupId) });
  };

  const changeNamedDepartment = (name: string, checked: boolean) => {
    if (selection.all && !checked) {
      const remaining = assignees.filter((assignee) => Boolean(assignee.departmentId) || assignee.department?.trim().toLocaleLowerCase('ru') !== name);
      onChange({ all: false, userIds: remaining.map((assignee) => assignee.id), roleIds: [], departmentIds: [], departmentNames: [] });
      return;
    }
    onChange({ ...selection, all: false, departmentNames: checked
      ? [...selection.departmentNames, name]
      : selection.departmentNames.filter((value) => value !== name) });
  };

  return (
    <details className="mb-3 min-w-0 rounded-xl border border-slate-200 bg-white p-3" data-testid="task-calendar-selection">
      <summary className="cursor-pointer text-sm font-semibold text-slate-800">
        Календари сотрудников · {loading ? 'загрузка списка' : error ? 'список недоступен' : selection.all ? 'все' : `выбрано ${selected.size} из ${assignees.length}`}
      </summary>
      {loading ? <div role="status" className="mt-2 text-sm text-slate-500">Загружаем сотрудников…</div> : error ? (
        <div role="alert" className="mt-2 text-sm text-rose-700">
          Не удалось загрузить список сотрудников. Выбор календарей сохранён.
          <button type="button" onClick={onRetry} className="ml-2 underline">Повторить загрузку календарей</button>
        </div>
      ) : null}
      {!loading && !error ? <>
      {assignees.length === 0 ? <div className="mt-2 text-sm text-slate-500">Нет доступных сотрудников.</div> : null}
      <div className="mt-2 flex flex-wrap gap-2">
        <button type="button" className="rounded border border-slate-200 px-2 py-1 text-xs" onClick={() => onChange(normalizeCalendarSelection())}>Все календари</button>
        <button type="button" className="rounded border border-slate-200 px-2 py-1 text-xs" onClick={() => onChange({ all: false, userIds: [], roleIds: [], departmentIds: [], departmentNames: [] })}>Снять выбор календарей</button>
      </div>
      <div className="mt-3 grid max-h-64 gap-3 overflow-y-auto sm:grid-cols-3" aria-label="Выбор отображаемых календарей">
        <fieldset className="min-w-0 space-y-1">
          <legend className="mb-1 text-xs font-semibold text-slate-600">Сотрудники</legend>
          {assignees.map((assignee) => (
            <label key={assignee.id} className="flex items-start gap-2 break-words text-sm" htmlFor={`${id}-user-${assignee.id}`}>
              <SystemCheckbox id={`${id}-user-${assignee.id}`} checked={selected.has(assignee.id)} onChange={(event) => {
                const users = new Set(selected);
                if (event.target.checked) users.add(assignee.id);
                else users.delete(assignee.id);
                onChange({ all: false, userIds: Array.from(users), roleIds: [], departmentIds: [], departmentNames: [] });
              }} />
              <span className="min-w-0 break-words">{assignee.name}</span>
            </label>
          ))}
        </fieldset>
        <fieldset className="min-w-0 space-y-1">
          <legend className="mb-1 text-xs font-semibold text-slate-600">Роли</legend>
          {Array.from(roles).map(([roleId, name]) => (
            <label key={roleId} className="flex items-start gap-2 break-words text-sm" htmlFor={`${id}-role-${roleId}`}>
              <SystemCheckbox id={`${id}-role-${roleId}`} checked={selection.all || selection.roleIds.includes(roleId)} onChange={(event) => changeGroup('roleIds', roleId, event.target.checked)} />
              <span className="min-w-0 break-words">{name}</span>
            </label>
          ))}
        </fieldset>
        <fieldset className="min-w-0 space-y-1">
          <legend className="mb-1 text-xs font-semibold text-slate-600">Подразделения</legend>
          {Array.from(namedDepartments).map(([key, name], index) => (
            <label key={key} className="flex items-start gap-2 break-words text-sm" htmlFor={`${id}-named-department-${index}`}>
              <SystemCheckbox id={`${id}-named-department-${index}`} checked={selection.all || selection.departmentNames.includes(key)} onChange={(event) => changeNamedDepartment(key, event.target.checked)} />
              <span className="min-w-0 break-words">{name}</span>
            </label>
          ))}
          {Array.from(departments).map(([departmentId, name]) => (
            <label key={departmentId} className="flex items-start gap-2 break-words text-sm" htmlFor={`${id}-department-${departmentId}`}>
              <SystemCheckbox id={`${id}-department-${departmentId}`} checked={selection.all || selection.departmentIds.includes(departmentId)} onChange={(event) => changeGroup('departmentIds', departmentId, event.target.checked)} />
              <span className="min-w-0 break-words">{name}</span>
            </label>
          ))}
        </fieldset>
      </div>
      </> : null}
    </details>
  );
}
