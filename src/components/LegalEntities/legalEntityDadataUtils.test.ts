// @vitest-environment node

import { describe, expect, it } from 'vitest';

import {
  dedupeLegalEntitySuggestions,
  getPreferredLegalEntityAddress,
  resolveInitialLegalEntityDadataQuery,
} from './legalEntityDadataUtils';

describe('legalEntityDadataUtils', () => {
  it('prefers unrestricted address when it exists', () => {
    expect(
      getPreferredLegalEntityAddress(
        'Самарская обл, г Новокуйбышевск',
        '446200, Россия, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9',
      ),
    ).toBe('446200, Россия, Самарская область, г. Новокуйбышевск, ул. Миронова, д. 9');
  });

  it('deduplicates repeated suggestions by INN', () => {
    const deduped = dedupeLegalEntitySuggestions([
      {
        shortName: 'ИП Кожеваткин Андрей Владимирович',
        fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
        inn: '633009620345',
        ogrn: '317631300153603',
        address: '446200, Россия, Самарская область, г. Новокуйбышевск',
      },
      {
        shortName: 'ИП Кожеваткин Андрей Владимирович',
        fullName: 'Индивидуальный предприниматель Кожеваткин Андрей Владимирович',
        inn: '633009620345',
        ogrn: '307631300200020',
        address: '446200, Россия, Самарская область, г. Новокуйбышевск',
      },
      {
        shortName: 'ООО Пример',
        fullName: 'Общество с ограниченной ответственностью Пример',
        inn: '7707083893',
        kpp: '770701001',
        ogrn: '1027700132195',
        address: '109012, Россия, г. Москва, ул. Ильинка, д. 9',
      },
    ]);

    expect(deduped).toHaveLength(2);
    expect(deduped[0].inn).toBe('633009620345');
    expect(deduped[1].inn).toBe('7707083893');
  });

  it('builds initial dadata query from saved inn', () => {
    expect(resolveInitialLegalEntityDadataQuery(' 633009620345 ')).toBe('633009620345');
    expect(resolveInitialLegalEntityDadataQuery(null)).toBe('');
  });
});
