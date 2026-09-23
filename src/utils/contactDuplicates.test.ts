import { describe, expect, it } from 'vitest';
import type { ContactDirectoryItemDto } from '../services/api';
import { findContactDuplicateGroups, normalizeContactPhone } from './contactDuplicates';

function contact(id: number, sourceType: ContactDirectoryItemDto['sourceType'], phone: string, email = '', contactType: ContactDirectoryItemDto['contactType'] = 'Person'): ContactDirectoryItemDto {
  return {
    sourceType,
    sourceId: id,
    category: sourceType === 'legal' ? 'organizations' : sourceType === 'client' ? 'clients' : 'external',
    contactType,
    roles: ['Client'],
    fullName: `Контакт ${id}`,
    phones: phone ? [phone] : [],
    emails: email ? [email] : [],
    hasFullProfile: sourceType !== 'external',
    isActive: true,
    syncToPhonebook: true,
  };
}

describe('contact duplicate discovery', () => {
  it('normalizes Russian phone prefixes and connects candidates across all directory sources', () => {
    const groups = findContactDuplicateGroups([
      contact(1, 'client', '8 (919) 810-63-43'),
      contact(2, 'external', '+7 919 810 63 43', 'name@example.org'),
      contact(3, 'client', '', 'NAME@example.org'),
      contact(4, 'legal', '8 (919) 810-63-43', '', 'Organization'),
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].contacts.map((item) => item.sourceId)).toEqual([1, 2, 3]);
    expect(groups[0].matches).toContain('телефон 79198106343');
  });

  it('ignores placeholder numbers, inactive contacts and groups larger than the safety cap', () => {
    const tooCommon = Array.from({ length: 21 }, (_, index) => contact(index + 1, 'external', '79990000001'));
    const groups = findContactDuplicateGroups([
      ...tooCommon,
      contact(99, 'client', '77777777777'),
      { ...contact(100, 'client', '79991112233'), isActive: false },
      contact(101, 'client', '79991112233'),
    ]);

    expect(groups).toEqual([]);
    expect(normalizeContactPhone('8 (919) 810-63-43')).toBe('79198106343');
  });

  it('does not suggest a linked quick card as a duplicate of its own full profile', () => {
    const groups = findContactDuplicateGroups([
      contact(1, 'client', '79991112233'),
      { ...contact(2, 'external', '79991112233'), linkedClientId: 1, hasFullProfile: true },
      contact(3, 'client', '89991112233'),
    ]);

    expect(groups).toHaveLength(1);
    expect(groups[0].contacts.map((item) => item.sourceId)).toEqual([1, 3]);
  });
});
