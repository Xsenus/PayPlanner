// @vitest-environment node

import { describe, expect, it } from 'vitest';

import { chooseBestDisplayAddress, composeAddressFromParts } from './addressEntryUtils';

describe('addressEntryUtils', () => {
  it('keeps the fullest suggestion instead of replacing it with a shorter cleaned address', () => {
    const result = chooseBestDisplayAddress(
      {
        postalCode: '446200',
        region: 'Самарская обл',
        city: 'г Новокуйбышевск',
      },
      '446200, Самарская обл, г Новокуйбышевск',
      '446200, Самарская обл, г Новокуйбышевск, ул Миронова, д 9, кв 1',
    );

    expect(result).toBe('446200, Самарская обл, г Новокуйбышевск, ул Миронова, д 9, кв 1');
  });

  it('prefers composed structured address over a shorthand raw query', () => {
    const result = chooseBestDisplayAddress(
      {
        postalCode: '633208',
        region: 'Новосибирская обл',
        city: 'г Искитим',
        street: 'Южный мкр',
        house: '37',
        flat: '19',
      },
      'искитим южный 37 19',
    );

    expect(result).toBe('633208, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19');
  });

  it('composes readable full address from structured parts without duplicate fragments', () => {
    expect(
      composeAddressFromParts({
        postalCode: '633208',
        country: 'Россия',
        region: 'Новосибирская обл',
        district: 'Искитимский р-н',
        city: 'г Искитим',
        settlement: 'Южный мкр',
        street: 'Южный мкр',
        house: '37',
        flat: '19',
      }),
    ).toBe('633208, Россия, Новосибирская обл, Искитимский р-н, г Искитим, Южный мкр, д 37, кв 19');
  });

  it('prefers the most complete address when country, district and settlement are available', () => {
    const result = chooseBestDisplayAddress(
      {
        postalCode: '633208',
        country: 'Россия',
        region: 'Новосибирская обл',
        district: 'Искитимский р-н',
        city: 'г Искитим',
        settlement: 'Южный мкр',
        street: 'Южный мкр',
        house: '37',
        flat: '19',
      },
      'Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
      '633208, Россия, Новосибирская обл, г Искитим, Южный мкр, д 37, кв 19',
    );

    expect(result).toBe('633208, Россия, Новосибирская обл, Искитимский р-н, г Искитим, Южный мкр, д 37, кв 19');
  });
});
