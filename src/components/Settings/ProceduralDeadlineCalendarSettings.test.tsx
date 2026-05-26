import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import { ProceduralDeadlineCalendarSettings } from './ProceduralDeadlineCalendarSettings';

const toastMock = {
  success: vi.fn(),
  error: vi.fn(),
};

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getProceduralDeadlineCalendarSettings: vi.fn(),
    updateProceduralDeadlineCalendarSettings: vi.fn(),
  },
}));

describe('ProceduralDeadlineCalendarSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getProceduralDeadlineCalendarSettings).mockReset();
    vi.mocked(apiService.updateProceduralDeadlineCalendarSettings).mockReset();
    toastMock.success.mockReset();
    toastMock.error.mockReset();
  });

  it('loads, edits and saves production calendar overrides', async () => {
    vi.mocked(apiService.getProceduralDeadlineCalendarSettings).mockResolvedValue({
      workingDates: [{ date: '2026-05-02', note: 'Рабочая суббота' }],
      nonWorkingDates: [{ date: '2026-05-04', note: 'Перенос выходного' }],
      updatedAt: '2026-05-26T10:00:00Z',
    });
    vi.mocked(apiService.updateProceduralDeadlineCalendarSettings).mockImplementation(async (payload) => ({
      ...payload,
      updatedAt: '2026-05-26T11:00:00Z',
    }));

    render(<ProceduralDeadlineCalendarSettings />);

    expect(await screen.findByText('Рабочая суббота')).toBeTruthy();
    expect(screen.getByText('Перенос выходного')).toBeTruthy();

    fireEvent.change(screen.getByLabelText('Тип исключения календаря'), { target: { value: 'nonWorking' } });
    fireEvent.change(screen.getByLabelText('Дата исключения календаря'), { target: { value: '2026-06-12' } });
    fireEvent.change(screen.getByLabelText('Комментарий исключения календаря'), { target: { value: 'День России' } });
    fireEvent.click(screen.getByRole('button', { name: 'Добавить' }));

    expect(await screen.findByText('День России')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Сохранить календарь' }));

    await waitFor(() => {
      expect(apiService.updateProceduralDeadlineCalendarSettings).toHaveBeenCalledWith({
        workingDates: [{ date: '2026-05-02', note: 'Рабочая суббота' }],
        nonWorkingDates: [
          { date: '2026-05-04', note: 'Перенос выходного' },
          { date: '2026-06-12', note: 'День России' },
        ],
        updatedAt: '2026-05-26T10:00:00Z',
      });
      expect(toastMock.success).toHaveBeenCalledWith('Производственный календарь сохранен.', 'Настройки');
    });
  });
});
