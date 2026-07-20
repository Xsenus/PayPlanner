import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import { ContractModal } from './ContractModal';

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

vi.mock('../../services/api', () => ({
  apiService: {
    getNextContractNumber: getNextContractNumberMock,
    getContractRulesSettings: getContractRulesSettingsMock,
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
});
