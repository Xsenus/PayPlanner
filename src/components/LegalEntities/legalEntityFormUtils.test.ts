import { describe, expect, it } from 'vitest';

import {
  buildLegalEntityAddressKey,
  dedupeLegalEntityAddressInputs,
  upsertLegalEntityAddressEntry,
} from './legalEntityFormUtils';

describe('legalEntityFormUtils', () => {
  it('matches equivalent addresses with spacing and punctuation differences', () => {
    const firstKey = buildLegalEntityAddressKey({
      type: 'legal',
      fullAddress: 'Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9',
      postalCode: '446200',
      city: 'г Новокуйбышевск',
      street: 'ул Миронова',
      house: '9',
    });

    const secondKey = buildLegalEntityAddressKey({
      type: 'legal',
      fullAddress: '  Самарская обл г Новокуйбышевск ул Миронова дом 9 ',
      postalCode: '446200',
      city: 'Новокуйбышевск',
      street: 'Миронова',
      house: '9',
    });

    expect(firstKey).toBe(secondKey);
  });

  it('upserts an existing address instead of appending a duplicate', () => {
    const result = upsertLegalEntityAddressEntry(
      [
        {
          type: 'legal',
          additionalTypes: [],
          fullAddress: 'Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9',
          postalCode: '',
          region: '',
          district: '',
          city: '',
          settlement: '',
          street: '',
          house: '',
          flat: '',
          validFrom: '',
          validTo: '',
        },
      ],
      {
        type: 'legal',
        additionalTypes: [],
        fullAddress: '446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9',
        postalCode: '446200',
        region: 'Самарская обл.',
        district: '',
        city: 'г. Новокуйбышевск',
        settlement: '',
        street: 'ул. Миронова',
        house: '9',
        flat: '',
        validFrom: '',
        validTo: '',
      },
    );

    expect(result).toHaveLength(1);
    expect(result[0].postalCode).toBe('446200');
    expect(result[0].city).toContain('Новокуйбышевск');
  });

  it('deduplicates payload addresses and keeps a single primary record', () => {
    const deduped = dedupeLegalEntityAddressInputs([
      {
        type: 3,
        value: 'Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9',
        unrestrictedValue: '446200, Самарская обл., г. Новокуйбышевск, ул. Миронова, д. 9',
        postalCode: '446200',
        cityWithType: 'г Новокуйбышевск',
        streetWithType: 'ул Миронова',
        house: '9',
        isPrimary: true,
      },
      {
        type: 3,
        value: '446200 Самарская обл г Новокуйбышевск ул Миронова дом 9',
        unrestrictedValue: '446200 Самарская обл г Новокуйбышевск ул Миронова дом 9',
        postalCode: '446200',
        cityWithType: 'Новокуйбышевск',
        streetWithType: 'Миронова',
        house: '9',
        isPrimary: false,
      },
    ]);

    expect(deduped).toHaveLength(1);
    expect(deduped[0].isPrimary).toBe(true);
  });
});
