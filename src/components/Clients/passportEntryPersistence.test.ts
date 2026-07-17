// @vitest-environment node

import { describe, expect, it } from 'vitest';

import {
  stripPassportEntryValidity,
  stripPassportEntryValidityList,
} from './passportEntryPersistence';

describe('passportEntryPersistence', () => {
  it('clears hidden validity dates before persistence', () => {
    expect(
      stripPassportEntryValidity({
        series: '36 19',
        validFrom: '2019-06-15',
        validTo: '2045-06-15',
      }),
    ).toEqual({
      series: '36 19',
      validFrom: '',
      validTo: '',
    });
  });

  it('clears validity dates for every passport entry in the saved payload', () => {
    expect(
      stripPassportEntryValidityList([
        {
          series: '36 19',
          validFrom: '2019-06-15',
          validTo: '2045-06-15',
        },
        {
          series: '45 01',
          validFrom: '2020-01-01',
          validTo: '',
        },
      ]),
    ).toEqual([
      {
        series: '36 19',
        validFrom: '',
        validTo: '',
      },
      {
        series: '45 01',
        validFrom: '',
        validTo: '',
      },
    ]);
  });

  it('keeps validity dates when the feature is turned back on', () => {
    expect(
      stripPassportEntryValidity(
        {
          series: '36 19',
          validFrom: '2019-06-15',
          validTo: '2045-06-15',
        },
        true,
      ),
    ).toEqual({
      series: '36 19',
      validFrom: '2019-06-15',
      validTo: '2045-06-15',
    });
  });
});
