import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { MainCompanySettings } from './MainCompanySettings';

const mocks = vi.hoisted(() => ({
  get: vi.fn(), put: vi.fn(), refresh: vi.fn(), error: vi.fn(), success: vi.fn(),
}));
vi.mock('../../contexts/TabContext', () => ({ useTabNavigation: () => ({ setActiveTab: vi.fn() }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ error: mocks.error, success: mocks.success }) }));
vi.mock('../../hooks/useLegalEntities', () => ({ useLegalEntities: () => ({ legalEntities: [], error: null, refresh: mocks.refresh }) }));
vi.mock('../../services/api', () => ({ apiService: { getMainCompanySettings: mocks.get, updateMainCompanySettings: mocks.put } }));

describe('MainCompanySettings snapshot timing', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mocks.get.mockResolvedValue({ companies: [], defaultCompanyId: null });
    mocks.put.mockResolvedValue({ companies: [], defaultCompanyId: null, sellerSnapshotTiming: 'on-create' });
    mocks.refresh.mockResolvedValue(undefined);
  });

  it('keeps first-file for an old response and makes a timing-only change saveable', async () => {
    render(<MainCompanySettings />);
    const select = (await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе')) as HTMLSelectElement;
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    expect(select.value).toBe('first-file');
    expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(true);

    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(mocks.put).toHaveBeenCalledWith({ companyIds: [], defaultCompanyId: null, sellerSnapshotTiming: 'on-create' }));
    await waitFor(() => expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(true));
    const notice = screen.getByText(/Уже созданные документы и их снимки не меняются/);
    expect(notice.isConnected).toBe(true);
    for (let element: HTMLElement | null = notice; element; element = element.parentElement) {
      const style = window.getComputedStyle(element);
      expect(element.hidden || style.display === 'none' || ['hidden', 'collapse'].includes(style.visibility) || style.opacity === '0').toBe(false);
    }
  });

  it('preserves a rejected choice for an explicit retry and never reports it saved', async () => {
    mocks.put.mockRejectedValue(new Error('Нет права сохранять настройку'));
    render(<MainCompanySettings />);
    const select = (await screen.findByLabelText('Когда фиксировать реквизиты и изображения компании в новом документе')) as HTMLSelectElement;
    await waitFor(() => expect(select.matches(':disabled')).toBe(false));
    fireEvent.change(select, { target: { value: 'on-create' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(mocks.error).toHaveBeenCalled());
    expect(select.value).toBe('on-create');
    expect(screen.getByRole('button', { name: 'Сохранить' }).matches(':disabled')).toBe(false);
    expect(mocks.put).toHaveBeenCalledTimes(1);
    expect(mocks.success).not.toHaveBeenCalled();
  });
});
