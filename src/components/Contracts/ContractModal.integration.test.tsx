import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ContractModal } from './ContractModal';
import { apiService } from '../../services/api';

const { profileTimeZoneIdRef, getNextContractNumberMock, getContractRulesSettingsMock } = vi.hoisted(() => ({
  profileTimeZoneIdRef: { current: 'Europe/Samara' },
  getNextContractNumberMock: vi.fn(),
  getContractRulesSettingsMock: vi.fn(),
}));

vi.mock('../../hooks/useTranslation', () => ({
  useTranslation: () => ({ t: () => undefined }),
}));

vi.mock('../../hooks/useUserTimeZone', () => ({
  useUserTimeZone: () => profileTimeZoneIdRef.current,
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({ error: vi.fn(), success: vi.fn(), info: vi.fn() }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getNextContractNumber: getNextContractNumberMock,
    getContractRulesSettings: getContractRulesSettingsMock,
    getOwnCompanyOptions: vi.fn().mockResolvedValue([]),
  },
}));

vi.mock('../Clients/ClientStatusBadge', () => ({
  ClientStatusBadge: () => null,
}));

vi.mock('../ui/CounterpartyPickerModal', () => ({
  CounterpartyPickerModal: () => null,
}));

describe('ContractModal', () => {
  beforeEach(() => {
    vi.useFakeTimers({ shouldAdvanceTime: true });
    vi.setSystemTime(new Date('2026-06-04T22:30:00.000Z'));
    profileTimeZoneIdRef.current = 'Europe/Samara';
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValue([]);
    getNextContractNumberMock.mockResolvedValue({
      number: 'Д-1',
      autoGenerateWhenEmpty: true,
      allowManualNumberEdit: true,
    });
    getContractRulesSettingsMock.mockResolvedValue({
      autoGenerateWhenEmpty: true,
      allowManualNumberEdit: true,
    });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.clearAllMocks();
  });

  it('сохраняет введённую дату при смене часового пояса открытой формы', async () => {
    const props = {
      open: true,
      mode: 'create' as const,
      contract: null,
      onClose: vi.fn(),
      onSubmit: vi.fn().mockResolvedValue(undefined),
      submitting: false,
    };
    const { rerender } = render(<ContractModal {...props} />);
    const dateInput = screen.getByLabelText('Дата договора') as HTMLInputElement;

    expect(dateInput.value).toBe('2026-06-05');
    fireEvent.change(dateInput, { target: { value: '2026-06-10' } });

    profileTimeZoneIdRef.current = 'America/New_York';
    rerender(<ContractModal {...props} />);

    await waitFor(() => expect(dateInput.value).toBe('2026-06-10'));
  });

  it('передаёт выбранного продавца вместе с новым договором без отдельного сохранения', async () => {
    vi.mocked(apiService.getOwnCompanyOptions).mockResolvedValue([
      { id: 41, shortName: 'Своя компания', isDefault: true, branches: [{ id: 42, name: 'Офис', kind: 'Office' }] },
    ]);
    const onSubmit = vi.fn().mockResolvedValue(undefined);
    render(<ContractModal open mode="create" contract={null} onClose={vi.fn()} onSubmit={onSubmit} submitting={false}
      defaultClients={[{ id: 51, name: 'Контрагент' }]} />);

    fireEvent.change(await screen.findByLabelText('Подразделение-продавец'), { target: { value: '42' } });
    fireEvent.change(screen.getByLabelText('Сумма договора'), { target: { value: '25000' } });
    fireEvent.click(screen.getByRole('button', { name: 'Создать' }));

    await waitFor(() => expect(onSubmit).toHaveBeenCalledTimes(1));
    expect(onSubmit.mock.calls[0]?.[0]).toMatchObject({ sellerCompanyId: 41, sellerBranchId: 42, amount: 25000, clients: [{ clientId: 51 }] });
  });
});
