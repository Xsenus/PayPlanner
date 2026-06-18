import { describe, expect, it } from 'vitest';

import {
  DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB,
  parseUserNotificationDoNotDisturb,
  serializeUserNotificationDoNotDisturb,
} from './userNotificationDoNotDisturb';

describe('userNotificationDoNotDisturb', () => {
  it('returns defaults for empty or broken values', () => {
    expect(parseUserNotificationDoNotDisturb(null)).toEqual(DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB);
    expect(parseUserNotificationDoNotDisturb('{broken')).toEqual(DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB);
  });

  it('normalizes time, date and work days', () => {
    const parsed = parseUserNotificationDoNotDisturb(JSON.stringify({
      enabled: true,
      workStart: '8:00',
      workEnd: '19:30',
      muteWeekends: false,
      vacationUntil: '2026-07-01',
      allowCritical: false,
      workDays: [1, 1, 6, 9, 'bad'],
    }));

    expect(parsed).toEqual({
      enabled: true,
      workStart: '09:00',
      workEnd: '19:30',
      muteWeekends: false,
      vacationUntil: '2026-07-01',
      allowCritical: false,
      workDays: [1, 6],
    });
  });

  it('serializes normalized settings', () => {
    expect(JSON.parse(serializeUserNotificationDoNotDisturb({
      enabled: true,
      workStart: '10:00',
      workEnd: '18:99',
      vacationUntil: 'bad-date',
    }))).toEqual({
      ...DEFAULT_USER_NOTIFICATION_DO_NOT_DISTURB,
      enabled: true,
      workStart: '10:00',
      vacationUntil: null,
    });
  });
});
