import { describe, expect, it } from 'vitest';

import { createEmptyClientRelative, normalizeClientRelative } from './clientRelativeUtils';

describe('clientRelativeUtils', () => {
  it('creates a relative with an empty phone field by default', () => {
    expect(createEmptyClientRelative()).toEqual({
      relation: '',
      fullName: '',
      birthYear: '',
      phone: '',
    });
  });

  it('adds an empty phone field when normalizing legacy relative data', () => {
    expect(
      normalizeClientRelative({
        relation: 'Мать',
        fullName: 'Иванова Мария Петровна',
        birthYear: '1964',
      }),
    ).toEqual({
      relation: 'Мать',
      fullName: 'Иванова Мария Петровна',
      birthYear: '1964',
      phone: '',
    });
  });
});
