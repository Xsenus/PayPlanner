import { describe, expect, it } from 'vitest';
import { normalizeCalendarSelection, resolveCalendarSelection } from './taskCalendarSelectionPreferences';

describe('calendar display preferences', () => {
  it('normalizes malformed or duplicate IDs without expanding an explicit empty selection', () => {
    const selection = normalizeCalendarSelection(JSON.parse('{"all":false,"userIds":[8,8,-1,"9",null],"roleIds":[2],"departmentIds":null}'));
    expect(selection).toEqual({ all: false, userIds: [8], roleIds: [2], departmentIds: [], departmentNames: [] });
    expect(resolveCalendarSelection(normalizeCalendarSelection({ all: false }), [{ id: 8, name: 'Юрист', email: '' }])).toEqual([]);
  });

  it('matches legacy named departments while excluding stale selected IDs', () => {
    const selection = normalizeCalendarSelection({ all: false, userIds: [99], departmentNames: [' ОФИС '] });
    expect(resolveCalendarSelection(selection, [
      { id: 8, name: 'Первый', email: '', department: 'Офис' },
      { id: 9, name: 'Второй', email: '', departmentId: 2, department: 'Филиал' },
    ])).toEqual([8]);
  });
});
