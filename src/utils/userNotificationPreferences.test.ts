import { describe, expect, it } from 'vitest';
import {
  isUserNotificationTypeEnabled,
  isUserNotificationChannelEnabled,
  parseUserNotificationTypePreferences,
  serializeUserNotificationTypePreferences,
  toggleUserNotificationChannelPreference,
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
    const disabled = toggleUserNotificationTypePreference({ disabledTypes: [], disabledChannels: {} }, 'taskComment', false);
    const enabled = toggleUserNotificationTypePreference(disabled, 'taskComment', true);

    expect(disabled.disabledTypes).toEqual(['taskComment']);
    expect(enabled.disabledTypes).toEqual([]);
    expect(serializeUserNotificationTypePreferences(disabled, ['taskComment'])).toBe('{"disabledTypes":["taskComment"],"disabledChannels":{}}');
  });

  it('normalizes and toggles disabled channels by type', () => {
    const preferences = parseUserNotificationTypePreferences(
      '{"disabledChannels":{"taskAssigned":["email","toast","forcedModal","webPush","sms","unknown","email"],"unknown":["inApp"]}}',
      ['taskAssigned'],
    );

    expect(preferences.disabledChannels).toEqual({ taskAssigned: ['email', 'toast', 'forcedModal', 'webPush', 'sms'] });
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'email')).toBe(false);
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'toast')).toBe(false);
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'forcedModal')).toBe(false);
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'webPush')).toBe(false);
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'sms')).toBe(false);
    expect(isUserNotificationChannelEnabled(preferences, 'taskAssigned', 'telegram')).toBe(true);

    const enabled = toggleUserNotificationChannelPreference(preferences, 'taskAssigned', 'sms', true);
    const disabled = toggleUserNotificationChannelPreference(enabled, 'taskAssigned', 'telegram', false);

    expect(enabled.disabledChannels).toEqual({ taskAssigned: ['email', 'toast', 'forcedModal', 'webPush'] });
    expect(disabled.disabledChannels).toEqual({ taskAssigned: ['email', 'toast', 'forcedModal', 'webPush', 'telegram'] });
  });
});
