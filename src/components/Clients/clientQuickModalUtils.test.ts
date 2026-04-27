import { describe, expect, it } from 'vitest';
import type { Client, LegalEntityDadataDetails } from '../../types';
import {
  applyLegalEntityDadataDetails,
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
      ...parseQuickClientModalFormData(),
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
      ...parseQuickClientModalFormData(),
      lastName: 'Петров',
      firstName: 'Петр',
      middleName: 'Петрович',
      phone: '+7 999 000-00-00',
      comment: 'Нужен договор',
    });
  });

  it('fills legal client fields from Dadata and builds a linked client payload', () => {
    const details: LegalEntityDadataDetails = {
      shortName: 'OOO Romashka',
      fullName: 'Obshchestvo s ogranichennoy otvetstvennostyu Romashka',
      inn: '7707083893',
      kpp: '770701001',
      ogrn: '1027700132195',
      address: 'Moscow, Test street, 1',
      director: 'Ivanov Ivan Ivanovich',
      country: 'Russia',
      opfShort: 'OOO',
      taxSystem: 'USN',
      okved: '62.01',
      okpo: '12345678',
      okato: '45286585000',
      oktmo: '45382000000',
      okogu: '4210014',
      okfs: '16',
      phones: ['+7 495 000-00-00'],
      emails: ['info@example.test'],
    };

    const formData = applyLegalEntityDadataDetails(parseQuickClientModalFormData(), details);
    const payload = buildQuickClientPayload(formData, undefined, 42);

    expect(formData.kind).toBe('legal');
    expect(formData.legalInn).toBe('7707083893');
    expect(formData.legalName).toBe('OOO Romashka');
    expect(formData.legalOkved).toBe('62.01');
    expect(payload.name).toBe('OOO Romashka');
    expect(payload.company).toBe('OOO Romashka');
    expect(payload.phone).toBe('+7 495 000-00-00');
    expect(payload.legalEntityId).toBe(42);

    const profile = JSON.parse(payload.notes.slice(CLIENT_PROFILE_NOTES_PREFIX.length)) as {
      kind: string;
      inn: string;
      firstName: string;
      phones: string[];
    };
    expect(profile.kind).toBe('legal');
    expect(profile.inn).toBe('7707083893');
    expect(profile.firstName).toBe('OOO Romashka');
    expect(profile.phones).toEqual(['+7 495 000-00-00']);
  });

  it('treats legacy Name requirement as first-name-only and keeps notes compatibility', () => {
    const requiredKeys = new Set(['Name', 'Notes', 'Phone']);
    const isRequired = (key: string) => requiredKeys.has(key);

    expect(isQuickClientModalFieldRequired('LastName', isRequired)).toBe(false);
    expect(isQuickClientModalFieldRequired('FirstName', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('MiddleName', isRequired)).toBe(false);
    expect(isQuickClientModalFieldRequired('Phone', isRequired)).toBe(true);
    expect(isQuickClientModalFieldRequired('Comment', isRequired)).toBe(true);
  });
});
