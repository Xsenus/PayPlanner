import { describe, expect, it } from 'vitest';
import {
  isUserNotificationTypeEnabled,
  parseUserNotificationTypePreferences,
  serializeUserNotificationTypePreferences,
  toggleUserNotificationTypePreference,
} from './userNotificationPreferences';

describe('userNotificationPreferences', () => {
  it('normalizes disabled types against known notification types', () => {
    const preferences = parseUserNotificationTypePreferences(
      '{"disabledTypes":["taskChanged","unknown","taskChanged"]}',
      ['taskChanged', 'taskAssigned'],
    );

    expect(preferences.disabledTypes).toEqual(['taskChanged']);
    expect(isUserNotificationTypeEnabled(preferences, 'taskChanged')).toBe(false);
    expect(isUserNotificationTypeEnabled(preferences, 'taskAssigned')).toBe(true);
  });

  it('toggles and serializes type preferences', () => {
    const disabled = toggleUserNotificationTypePreference({ disabledTypes: [] }, 'taskComment', false);
    const enabled = toggleUserNotificationTypePreference(disabled, 'taskComment', true);

    expect(disabled.disabledTypes).toEqual(['taskComment']);
    expect(enabled.disabledTypes).toEqual([]);
    expect(serializeUserNotificationTypePreferences(disabled, ['taskComment'])).toBe('{"disabledTypes":["taskComment"]}');
  });
});
