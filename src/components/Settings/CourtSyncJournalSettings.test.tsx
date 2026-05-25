import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { CourtSyncJournalSettings } from './CourtSyncJournalSettings';

const apiMock = vi.hoisted(() => ({
  getCourtSyncJournal: vi.fn(),
}));

vi.mock('../../services/api', () => ({
  apiService: apiMock,
}));

describe('CourtSyncJournalSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
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
        status: 'external-sync-completed',
        message: 'Судебный источник обновлен: создано заседаний: 1.',
        occurredAt: '2026-05-18T10:30:00Z',
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
        status: 'external-sync-error',
        message: 'Проверка судебного источника не выполнена: источник временно недоступен.',
        occurredAt: '2026-05-18T11:30:00Z',
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
    expect(screen.getAllByText('Ошибка').length).toBeGreaterThan(0);
    expect(screen.getAllByRole('link', { name: /Открыть дело/i })[0].getAttribute('href')).toBe('/cases/101');
  });

  it('applies status and search filters to the request', async () => {
    render(<CourtSyncJournalSettings />);
    await screen.findByText('Земцов - суд');

    fireEvent.change(screen.getByRole('combobox', { name: 'Статус судебной синхронизации' }), {
      target: { value: 'external-sync-error' },
    });
    fireEvent.change(screen.getByPlaceholderText('Поиск по делу, суду, номеру или сообщению'), {
      target: { value: 'А55' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Найти' }));

    await waitFor(() => expect(apiMock.getCourtSyncJournal).toHaveBeenLastCalledWith({
      take: 50,
      status: 'external-sync-error',
      search: 'А55',
    }));
  });
});
