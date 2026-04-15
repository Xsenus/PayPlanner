import { describe, expect, it } from 'vitest';

import { stripAddressEntryValidity, stripAddressEntryValidityList } from './addressEntryPersistence';

describe('addressEntryPersistence', () => {
  it('clears hidden validity dates before persistence', () => {
    expect(
      stripAddressEntryValidity({
        type: 'registration',
        validFrom: '2024-01-01',
        validTo: '2024-12-31',
      }),
    ).toEqual({
      type: 'registration',
      validFrom: '',
      validTo: '',
    });
  });

  it('clears validity dates for every address entry in the saved payload', () => {
    expect(
      stripAddressEntryValidityList([
        {
          type: 'registration',
          validFrom: '2024-01-01',
          validTo: '2024-12-31',
        },
        {
          type: 'actual',
          validFrom: '2025-01-01',
          validTo: '',
        },
      ]),
    ).toEqual([
      {
        type: 'registration',
        validFrom: '',
        validTo: '',
      },
      {
        type: 'actual',
        validFrom: '',
        validTo: '',
      },
    ]);
  });

  it('keeps validity dates when the feature is turned back on', () => {
    expect(
      stripAddressEntryValidity(
        {
          type: 'registration',
          validFrom: '2024-01-01',
          validTo: '2024-12-31',
        },
        true,
      ),
    ).toEqual({
      type: 'registration',
      validFrom: '2024-01-01',
      validTo: '2024-12-31',
    });
  });
});
