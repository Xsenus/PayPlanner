import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { CourtSyncHealthDialog } from './CourtSyncHealthDialog';

vi.mock('../../services/api', () => ({
  apiService: {
    getCourtSyncHealth: vi.fn(),
    runCaseCourtSync: vi.fn(),
    updateCourtSyncCaseNumber: vi.fn(),
    setCourtSyncMonitoring: vi.fn(),
  },
}));
vi.mock('../../hooks/useUserTimeZone', () => ({ useUserTimeZone: () => 'Asia/Novosibirsk' }));

const failedCase = {
  caseId: 42, title: 'Дело с ошибкой', caseNumber: '2-100/2026', providerName: 'ГАС Правосудие',
  courtUrl: 'https://example.sudrf.ru/case/42', lastAttemptAt: '2026-09-22T08:00:00Z',
  lastSuccessAt: '2026-09-20T08:00:00Z', failureStartedAt: '2026-09-21T08:00:00Z',
  lastError: 'Дело не найдено', monitoringEnabled: true,
};

describe('CourtSyncHealthDialog', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCourtSyncHealth).mockReset();
    vi.mocked(apiService.runCaseCourtSync).mockReset();
    vi.mocked(apiService.updateCourtSyncCaseNumber).mockReset();
    vi.mocked(apiService.setCourtSyncMonitoring).mockReset();
    vi.mocked(apiService.getCourtSyncHealth).mockImplementation(async (status) => status === 'failed'
      ? { items: [failedCase], total: 1, page: 1, pageSize: 50 }
      : { items: [{ ...failedCase, monitoringEnabled: false }], total: 1, page: 1, pageSize: 50 });
    vi.mocked(apiService.runCaseCourtSync).mockResolvedValue({ clientCaseId: 42, status: 'external-sync-completed', hasCourtBinding: true, message: 'Источник проверен', checkedAt: '2026-09-23T08:00:00Z' });
    vi.mocked(apiService.setCourtSyncMonitoring).mockResolvedValue({ caseId: 42, monitoringEnabled: false });
    vi.mocked(apiService.updateCourtSyncCaseNumber).mockResolvedValue({ caseId: 42, courtCaseNumber: '2-101/2026', courtUrl: failedCase.courtUrl });
  });

  it('shows the failure reason and requires confirmation before stopping monitoring', async () => {
    const onChanged = vi.fn();
    render(<CourtSyncHealthDialog open onClose={vi.fn()} onOpenCase={vi.fn()} onChanged={onChanged} />);
    expect(await screen.findByText('Причина: Дело не найдено')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Снять с мониторинга' }));
    expect(apiService.setCourtSyncMonitoring).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить' }));
    await waitFor(() => expect(apiService.setCourtSyncMonitoring).toHaveBeenCalledWith(42, false));
    await waitFor(() => expect(onChanged).toHaveBeenCalledOnce());
  });

  it('can fix the number and immediately retry the court source', async () => {
    render(<CourtSyncHealthDialog open onClose={vi.fn()} onOpenCase={vi.fn()} onChanged={vi.fn()} />);
    fireEvent.click(await screen.findByRole('button', { name: 'Указать новый номер' }));
    fireEvent.change(screen.getByLabelText('Новый номер дела'), { target: { value: '2-101/2026' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить и проверить' }));
    await waitFor(() => expect(apiService.updateCourtSyncCaseNumber).toHaveBeenCalledWith(42, '2-101/2026', failedCase.courtUrl));
    await waitFor(() => expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(42));
  });

  it('shows paused cases and supports resuming monitoring', async () => {
    render(<CourtSyncHealthDialog open onClose={vi.fn()} onOpenCase={vi.fn()} onChanged={vi.fn()} />);
    fireEvent.click(screen.getByRole('button', { name: 'Без мониторинга' }));
    fireEvent.click(await screen.findByRole('button', { name: 'Возобновить мониторинг' }));
    await waitFor(() => expect(apiService.setCourtSyncMonitoring).toHaveBeenCalledWith(42, true));
    await waitFor(() => expect(apiService.runCaseCourtSync).toHaveBeenCalledWith(42));
  });
});
