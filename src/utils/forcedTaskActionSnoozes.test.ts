import { beforeEach, describe, expect, it, vi } from 'vitest';
import {
  FORCED_TASK_SNOOZE_OPTIONS,
  FORCED_TASK_SNOOZE_STORAGE_KEY,
  FORCED_TASK_SNOOZES_EVENT,
  getForcedTaskApprovalSnoozeKey,
  getForcedTaskDeadlineChangeSnoozeKey,
  getForcedTaskNotificationSnoozeKey,
  getForcedTaskSnoozeStorageKey,
  isForcedTaskActionSnoozed,
  readForcedTaskSnoozes,
  shareForcedTaskSnooze,
  snoozeForcedTaskActions,
} from './forcedTaskActionSnoozes';

describe('forced task action snoozes', () => {
  const now = 1_800_000_000_000;

  beforeEach(() => window.localStorage.clear());

  it('reports a failed browser storage write without announcing a saved snooze', () => {
    const event = vi.fn();
    window.addEventListener(FORCED_TASK_SNOOZES_EVENT, event);
    const write = vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('storage unavailable'); });
    try {
      expect(snoozeForcedTaskActions(7, ['31'], 10, now)).toBe(false);
      expect(event).not.toHaveBeenCalled();
    } finally {
      write.mockRestore();
      window.removeEventListener(FORCED_TASK_SNOOZES_EVENT, event);
    }
    expect(snoozeForcedTaskActions(7, ['31'], 10, now)).toBe(true);
  });

  it('never overwrites earlier snoozes when the browser storage cannot be read', () => {
    snoozeForcedTaskActions(7, ['31'], 30, now);
    const storageKey = getForcedTaskSnoozeStorageKey(7)!;
    const previous = window.localStorage.getItem(storageKey);
    const read = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('storage read unavailable'); });
    const write = vi.spyOn(Storage.prototype, 'setItem');
    try {
      expect(snoozeForcedTaskActions(7, ['32'], 10, now)).toBe(false);
      expect(shareForcedTaskSnooze(7, '31', 'task-approval:101', now)).toBe(false);
      expect(write).not.toHaveBeenCalled();
    } finally { read.mockRestore(); write.mockRestore(); }
    expect(window.localStorage.getItem(storageKey)).toBe(previous);
    expect(readForcedTaskSnoozes(7, now)).toEqual({ '31': now + 1_800_000 });
  });

  it.each(FORCED_TASK_SNOOZE_OPTIONS)('expires $label exactly at its boundary for notification and task actions', ({ minutes }) => {
    expect(FORCED_TASK_SNOOZE_OPTIONS.map(option => option.minutes)).toEqual([10, 30, 60, 1440]);
    const keys = [getForcedTaskNotificationSnoozeKey(31), getForcedTaskApprovalSnoozeKey(101), getForcedTaskDeadlineChangeSnoozeKey(101, 902)];
    const until = now + minutes * 60_000;
    snoozeForcedTaskActions(7, keys, minutes, now);

    expect(readForcedTaskSnoozes(7, now)).toEqual(Object.fromEntries(keys.map(key => [key, until])));
    for (const key of keys) {
      expect(isForcedTaskActionSnoozed(7, key, until - 1)).toBe(true);
      expect(isForcedTaskActionSnoozed(8, key, until - 1)).toBe(false);
    }
    for (const key of keys) {
      expect(isForcedTaskActionSnoozed(7, key, until)).toBe(false);
    }
    expect(readForcedTaskSnoozes(7, until)).toEqual({});
  });

  it('retains a user snooze across reads without adopting the ownerless v1 record', () => {
    const legacy = JSON.stringify({ '31': now + 86_400_000, 'task-approval:101': now + 86_400_000 });
    window.localStorage.setItem(FORCED_TASK_SNOOZE_STORAGE_KEY, legacy);
    expect(readForcedTaskSnoozes(7, now)).toEqual({});
    expect(readForcedTaskSnoozes(8, now)).toEqual({});

    snoozeForcedTaskActions(7, ['31'], 10, now);
    expect(readForcedTaskSnoozes(7, now + 1)).toEqual({ '31': now + 600_000 });
    expect(readForcedTaskSnoozes(8, now + 1)).toEqual({});
    expect(window.localStorage.getItem(FORCED_TASK_SNOOZE_STORAGE_KEY)).toBe(legacy);
  });

  it('does not persist or share snoozes without a valid signed-in identity', () => {
    for (const userId of [null, undefined, 0, -1, 1.5, NaN, Infinity, Number.MAX_SAFE_INTEGER + 1]) {
      expect(getForcedTaskSnoozeStorageKey(userId)).toBeNull();
      snoozeForcedTaskActions(userId, ['31'], 10, now);
      expect(readForcedTaskSnoozes(userId, now)).toEqual({});
      expect(shareForcedTaskSnooze(userId, '31', 'task-approval:101', now)).toBe(false);
    }
    expect(window.localStorage.length).toBe(0);
  });

  it('shares notification and journal keys only within their owner and never shortens an active snooze', () => {
    snoozeForcedTaskActions(7, ['31'], 30, now);
    expect(shareForcedTaskSnooze(8, '31', 'task-approval:101', now)).toBe(false);
    expect(shareForcedTaskSnooze(7, '31', 'task-approval:101', now)).toBe(true);
    expect(readForcedTaskSnoozes(7, now)['task-approval:101']).toBe(now + 1_800_000);
    snoozeForcedTaskActions(7, ['task-approval:101'], 60, now);
    expect(shareForcedTaskSnooze(7, '31', 'task-approval:101', now)).toBe(false);
    expect(readForcedTaskSnoozes(7, now)['task-approval:101']).toBe(now + 3_600_000);
    expect(readForcedTaskSnoozes(8, now)).toEqual({});
  });

  it('includes the owner in change events and leaves another owner untouched during expiry cleanup', () => {
    const onChanged = vi.fn();
    window.addEventListener(FORCED_TASK_SNOOZES_EVENT, onChanged);
    try {
      snoozeForcedTaskActions(8, ['31'], 60, now);
      const other = window.localStorage.getItem(getForcedTaskSnoozeStorageKey(8)!);
      snoozeForcedTaskActions(7, ['31'], 10, now);
      expect(onChanged.mock.calls.map(([event]) => (event as CustomEvent).detail)).toEqual([{ userId: 8 }, { userId: 7 }]);
      expect(readForcedTaskSnoozes(7, now + 600_000)).toEqual({});
      expect(window.localStorage.getItem(getForcedTaskSnoozeStorageKey(8)!)).toBe(other);
    } finally {
      window.removeEventListener(FORCED_TASK_SNOOZES_EVENT, onChanged);
    }
  });

  it('ignores malformed storage and nonfinite or expired values', () => {
    const storageKey = getForcedTaskSnoozeStorageKey(7)!;
    for (const invalid of ['broken', 'null', '[]']) {
      window.localStorage.setItem(storageKey, invalid);
      expect(readForcedTaskSnoozes(7, now)).toEqual({});
    }
    window.localStorage.setItem(storageKey, `{"expired":${now},"infinite":1e999,"text":"later","active":${now + 1}}`);
    expect(readForcedTaskSnoozes(7, now)).toEqual({ active: now + 1 });
  });
});
