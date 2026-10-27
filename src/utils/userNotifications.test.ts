import { describe, expect, it } from 'vitest';

import { isTaskJournalRealtimeChange } from './userNotifications';

describe('isTaskJournalRealtimeChange', () => {
  it('accepts realtime task and case events that can change the journal', () => {
    expect(isTaskJournalRealtimeChange({
      source: 'realtime',
      reason: 'created',
      notificationType: 'taskChanged',
      objectType: 'task',
      taskJournalItemId: 42,
    })).toBe(true);
    expect(isTaskJournalRealtimeChange({
      source: 'realtime',
      reason: 'visibility-resume',
      notificationType: 'caseInactivityControl',
      objectType: 'case',
      clientCaseId: 17,
    })).toBe(true);
    expect(isTaskJournalRealtimeChange({
      source: 'realtime',
      reason: 'reconnected',
    })).toBe(true);
    expect(isTaskJournalRealtimeChange({ source: 'realtime', reason: 'user-role-changed' })).toBe(true);
  });

  it('ignores inbox actions and unrelated realtime notifications', () => {
    expect(isTaskJournalRealtimeChange({
      source: 'inbox',
      reason: 'item-updated',
      notificationType: 'taskChanged',
      objectType: 'task',
    })).toBe(false);
    expect(isTaskJournalRealtimeChange({
      source: 'realtime',
      reason: 'created',
      notificationType: 'invoiceChanged',
      objectType: 'invoice',
    })).toBe(false);
    expect(isTaskJournalRealtimeChange({
      source: 'realtime',
      reason: 'toast-read',
      notificationType: 'taskAssigned',
      objectType: 'task',
    })).toBe(false);
  });
});
