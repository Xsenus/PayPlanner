import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CourtSyncJournalSettings } from './CourtSyncJournalSettings';

const apiMock = vi.hoisted(() => ({
  getCourtSyncJournal: vi.fn(),
  getCourtSyncProviders: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

describe('CourtSyncJournalSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    apiMock.getCourtSyncProviders.mockResolvedValue([
      {
        key: 'sudrf',
        label: 'ГАС Правосудие / sudrf.ru',
        sourceType: 'sudrf',
        schemaVersion: 1,
        capabilities: [],
        status: 'active',
        isAvailable: true,
        featureFlagEnabled: true,
        supportsManualRun: true,
        supportsBackgroundSync: true,
        supportedHosts: ['sudrf.ru'],
        message: '',
      },
    ]);
    apiMock.getCourtSyncJournal.mockResolvedValue([
      {
        id: '101-1',
        logId: 1,
        clientCaseId: 101,
        clientCaseTitle: 'Земцов - суд',
        caseNumber: 'CRM-1',
        ownerName: 'Земцов Виктор Александрович',
        courtName: 'Арбитражный суд Самарской области',
        courtCaseNumber: 'А55-12345/2026',
        courtUrl: 'https://example.sudrf.ru/case',
        providerKey: 'sudrf',
        providerLabel: 'ГАС Правосудие / sudrf.ru',
        status: 'external-sync-completed',
        hasError: false,
        message: 'Судебный источник обновлен: создано заседаний: 1.',
        occurredAt: '2026-05-18T10:30:00Z',
        ageDays: 2,
        actorName: 'Администратор',
      },
      {
        id: '102-1',
        logId: 1,
        clientCaseId: 102,
        clientCaseTitle: 'Проверка ошибки',
        caseNumber: 'CRM-2',
        ownerName: 'Иванов Иван Иванович',
        courtName: 'Мировой суд',
        courtCaseNumber: '2-100/2026',
        courtUrl: '',
        providerKey: 'unbound',
        providerLabel: 'Без привязки',
        status: 'external-sync-error',
        hasError: true,
        message: 'Проверка судебного источника не выполнена: источник временно недоступен.',
        occurredAt: '2026-05-18T11:30:00Z',
        ageDays: 33,
        actorName: 'Юрист',
      },
    ]);
  });

  it('renders court sync journal rows and counters', async () => {
    render(<CourtSyncJournalSettings />);

    expect(await screen.findByText('Журнал судебной синхронизации')).toBeTruthy();
    expect(screen.getByText('Земцов - суд')).toBeTruthy();
    expect(screen.getByText('Проверка ошибки')).toBeTruthy();
    expect(screen.getByText('А55-12345/2026')).toBeTruthy();
    expect(screen.getAllByText('ГАС Правосудие / sudrf.ru').length).toBeGreaterThan(0);
    expect(screen.getByText('2 дня назад')).toBeTruthy();
    expect(screen.getAllByText('Ошибка').length).toBeGreaterThan(0);
    expect(screen.getAllByRole('link', { name: /Открыть дело/i })[0].getAttribute('href')).toBe('/cases/101');
  });

  it('applies status, provider, error, age and search filters to the request', async () => {
    render(<CourtSyncJournalSettings />);
    await screen.findByText('Земцов - суд');

    fireEvent.change(screen.getByLabelText('Статус судебной синхронизации'), {
      target: { value: 'external-sync-error' },
    });
    await waitFor(() => expect(apiMock.getCourtSyncProviders).toHaveBeenCalledTimes(1));
    fireEvent.change(screen.getByLabelText('Судебный источник'), {
      target: { value: 'sudrf' },
    });
    fireEvent.change(screen.getByLabelText('Наличие ошибки'), {
      target: { value: 'with' },
    });
    fireEvent.change(screen.getByLabelText('Давность судебной синхронизации'), {
      target: { value: 'week' },
    });
    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, суду, номеру или сообщению'), {
      target: { value: 'А55' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() => expect(apiMock.getCourtSyncJournal).toHaveBeenLastCalledWith({
      take: 50,
      status: 'external-sync-error',
      search: 'А55',
      provider: 'sudrf',
      hasError: true,
      maxAgeDays: 7,
      minAgeDays: undefined,
    }));
  });
});
