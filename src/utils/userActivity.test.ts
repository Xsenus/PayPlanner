import { describe, expect, it } from 'vitest';

import {
  buildUserActivityDateRange,
  formatUserActivityDescription,
  formatUserActivityFilterOption,
  getUserActivityActionLabel,
  getUserActivityCategoryLabel,
  getUserActivitySectionLabel,
  getUserActivitySearchAliases,
} from './userActivity';

describe('userActivity presentation', () => {
  it('shows readable labels while retaining diagnostic codes in filters', () => {
    expect(getUserActivityCategoryLabel('NotificationInbox')).toBe('Уведомления');
    expect(getUserActivityActionLabel('GetAll')).toBe('Просмотр списка');
    expect(getUserActivityActionLabel('DownloadDocument')).toBe('Скачивание');
    expect(getUserActivitySectionLabel('userActivity')).toBe('Контроль пользователей');
    expect(formatUserActivityFilterOption('Clients', 'category')).toBe('Клиенты — Clients');
  });

  it('replaces generated controller descriptions with a readable summary', () => {
    expect(formatUserActivityDescription({
      category: 'Clients',
      action: 'GetById',
      description: 'Clients.GetById | Id=42 | HTTP 200',
      objectId: '42',
    })).toBe('Просмотр карточки: Клиенты · объект №42');
  });

  it('preserves descriptions entered by the application', () => {
    expect(formatUserActivityDescription({
      category: 'navigation',
      action: 'open_tab',
      description: 'Открыт раздел: Контроль пользователей',
      objectId: null,
    })).toBe('Открыт раздел: Контроль пользователей');
  });

  it('converts visible date-time boundaries from the profile timezone to UTC', () => {
    expect(buildUserActivityDateRange(
      '2026-08-16T00:00',
      '2026-08-16T23:59',
      'Asia/Novosibirsk',
    )).toEqual({
      fromUtc: '2026-08-15T17:00:00.000Z',
      toUtc: '2026-08-16T16:59:00.000Z',
    });
  });

  it('expands a search by visible Russian labels', () => {
    expect(getUserActivitySearchAliases('клиент', {
      categories: ['Clients', 'NotificationInbox'],
      actions: ['GetAll'],
      sections: ['userActivity'],
    })).toEqual(['Clients']);

    expect(getUserActivitySearchAliases('просмотр списка', {
      actions: ['GetAll', 'Update'],
    })).toEqual(['GetAll']);
  });
});
