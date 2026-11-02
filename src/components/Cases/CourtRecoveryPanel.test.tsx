import { documentCopyTestSession } from '../../test/documentCopySession';
import { afterEach } from 'vitest';
vi.mock('../../contexts/AuthContext', () => ({ useAuth: () => ({ user: { id: 7, role: { id: 4 } } }) }));
let session: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { session = documentCopyTestSession(); });
afterEach(() => session.restore());
import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { apiService } from '../../services/api';
import type { CourtRecoveryOverview } from '../../types';
import { CourtRecoveryPanel } from './CourtRecoveryPanel';

vi.mock('../../services/api', () => ({
  apiService: {
    getCourtRecoveries: vi.fn(),
    setCourtRecoveryPayment: vi.fn(),
    removeCourtRecoveryPayment: vi.fn(),
  },
}));

const overview: CourtRecoveryOverview = {
  awardedAmount: 100_000,
  receivedAmount: 0,
  remainingAmount: 100_000,
  linkedPayments: [],
  availablePayments: [{
    paymentId: 7,
    date: '2026-09-24T00:00:00',
    description: 'Поступление по исполнительному листу',
    paidAmount: 25_000,
    allocatedAmount: null,
    effectiveAmount: 0,
    warning: null,
  }],
  hasMoreAvailablePayments: false,
  hasCompletePaymentVisibility: true,
};

describe('CourtRecoveryPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue(overview);
    vi.mocked(apiService.setCourtRecoveryPayment).mockResolvedValue(undefined);
    vi.mocked(apiService.removeCourtRecoveryPayment).mockResolvedValue(undefined);
  });

  it('links only a chosen part of an existing paid payment', async () => {
    render(<CourtRecoveryPanel caseId={3} canEdit />);
    await screen.findByRole('combobox', { name: 'Платёж для взыскания' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Платёж для взыскания' }), { target: { value: '7' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма платежа в счёт взыскания' }), { target: { value: '12000,50' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связь' }));
    await waitFor(() => expect(apiService.setCourtRecoveryPayment).toHaveBeenCalledWith(3, 7, 12000.5));
    expect(apiService.getCourtRecoveries).toHaveBeenCalledTimes(2);
  });

  it('does not allow invalid fractional amounts or editing in read-only mode', async () => {
    const { rerender } = render(<CourtRecoveryPanel caseId={3} canEdit />);
    await screen.findByRole('combobox', { name: 'Платёж для взыскания' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Платёж для взыскания' }), { target: { value: '7' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма платежа в счёт взыскания' }), { target: { value: '1,234' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связь' }));
    expect((await screen.findByRole('alert')).textContent).toContain('с точностью до копеек');
    expect(apiService.setCourtRecoveryPayment).not.toHaveBeenCalled();
    fireEvent.change(screen.getByRole('textbox', { name: 'Сумма платежа в счёт взыскания' }), { target: { value: '25000,01' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить связь' }));
    expect((await screen.findByRole('alert')).textContent).toContain('не может превышать оплаченную часть');
    expect(apiService.setCourtRecoveryPayment).not.toHaveBeenCalled();
    rerender(<CourtRecoveryPanel caseId={3} canEdit={false} />);
    expect(screen.queryByRole('button', { name: 'Сохранить связь' })).toBeNull();
  });

  it('shows a warning when a linked payment no longer confirms the recorded amount', async () => {
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({
      ...overview,
      linkedPayments: [{ ...overview.availablePayments[0], allocatedAmount: 25_000, warning: 'Оплаченная сумма платежа уменьшилась;', effectiveAmount: 0 }],
      availablePayments: [],
    });
    render(<CourtRecoveryPanel caseId={3} canEdit={false} />);
    expect((await screen.findByRole('alert')).textContent).toContain('Оплаченная сумма платежа уменьшилась');
    expect(screen.getByText(/Подтверждено платежами:/)).toBeTruthy();
  });

  it('does not present partial payment visibility as a complete recovery total', async () => {
    vi.mocked(apiService.getCourtRecoveries).mockResolvedValue({
      ...overview,
      receivedAmount: null,
      remainingAmount: null,
      hasCompletePaymentVisibility: false,
    });
    render(<CourtRecoveryPanel caseId={3} canEdit={false} />);
    expect(await screen.findByText(/Показаны только доступные вам платежи/)).toBeTruthy();
    expect(screen.getByText(/Подтверждено платежами:/).textContent).toContain('—');
    expect(screen.getByText(/Осталось:/).textContent).toContain('—');
  });
});
