import { describe, expect, it } from 'vitest';
import type { Client } from '../../types';
import {
  buildQuickClientPayload,
  CLIENT_PROFILE_NOTES_PREFIX,
  isQuickClientModalFieldRequired,
  parseQuickClientModalFormData,
} from './clientQuickModalUtils';

function createClient(overrides?: Partial<Client>): Client {
  return {
    id: 1,
    name: '',
    email: '',
    phone: '',
    company: '',
    address: '',
    notes: '',
    createdAt: new Date().toISOString(),
    isActive: true,
    ...overrides,
  };
}

describe('clientQuickModalUtils', () => {
  it('builds a client payload with structured notes for quick creation', () => {
    const payload = buildQuickClientPayload({
      lastName: 'Иванов',
      firstName: 'Иван',
      middleName: 'Иванович',
      phone: '+7 999 000-00-00',
      comment: 'Перезвонить после 18:00',
    });

    expect(payload.name).toBe('Иванов Иван Иванович');
    expect(payload.phone).toBe('+7 999 000-00-00');
    expect(payload.email).toBe('');
    expect(payload.notes.startsWith(CLIENT_PROFILE_NOTES_PREFIX)).toBe(true);

    const profile = JSON.parse(payload.notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)) as {
      lastName: string;
      firstName: string;
      middleName: string;
      phones: string[];
      comment: string;
    };

    expect(profile.lastName).toBe('Иванов');
    expect(profile.firstName).toBe('Иван');
    expect(profile.middleName).toBe('Иванович');
    expect(profile.phones).toEqual(['+7 999 000-00-00']);
    expect(profile.comment).toBe('Перезвонить после 18:00');
  });

  it('parses structured quick-create data from an existing client profile', () => {
    const client = createClient({
      name: 'Неиспользуемое имя',
      phone: '+7 999 000-00-00',
      notes: `${CLIENT_PROFILE_NOTES_PREFIX}${JSON.stringify({
        lastName: 'Петров',
        firstName: 'Петр',
        middleName: 'Петрович',
        phones: ['+7 999 000-00-00'],
        comment: 'Нужен договор',
      })}`,
    });

    expect(parseQuickClientModalFormData(client)).toEqual({
      lastName: 'Петров',
      firstName: 'Петр',
      middleName: 'Петрович',
      phone: '+7 999 000-00-00',
      comment: 'Нужен договор',
    });
  });

  it('treats legacy Name and Notes requirements as quick modal fio and comment requirements', () => {
    const requiredKeys = new Set(['Name', 'Notes', 'Phone']);
    const isRequired = (key: string) => requiredKeys.has(key);

    expect(isQuickClientModalFieldRequired('LastName', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('FirstName', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('MiddleName', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('Phone', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('Comment', isRequired)).toBe(true);
  });
});
