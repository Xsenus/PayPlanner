import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';

import { UserDetailPage } from './UserDetailPage';
import { authService } from '../../services/authService';
import { apiService } from '../../services/api';

const { toastMock } = vi.hoisted(() => ({
  toastMock: {
    error: vi.fn(),
    success: vi.fn(),
  },
}));

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { name: 'admin' } },
  }),
}));

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/authService', () => ({
  authService: {
    getRoles: vi.fn(),
    getUser: vi.fn(),
    getUserContractorBalance: vi.fn(),
    updateUser: vi.fn(),
  },
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getMainCompanySettings: vi.fn(),
    getDict: vi.fn(),
    getUserSettingForUser: vi.fn(),
  },
}));

vi.mock('../ui/ObjectAuditModal', () => ({
  ObjectAuditLauncher: () => null,
}));

describe('UserDetailPage', () => {
  beforeEach(() => {
    vi.mocked(authService.getRoles).mockResolvedValue([{ id: 1, name: 'admin', description: 'Admin' }]);
    vi.mocked(authService.getUser).mockResolvedValue({
      id: 10,
      email: 'sergey@example.test',
      fullName: 'Гордик Сергей',
      role: { id: 1, name: 'admin', description: 'Admin' },
      roleId: 1,
      isActive: true,
      isApproved: true,
      isEmployee: false,
      isExternalContractor: true,
      externalIdentities: [],
    });
    vi.mocked(authService.getUserContractorBalance).mockResolvedValue({
      owedToMe: 2500,
      owedByMe: 0,
      paidToMe: 0,
      paidByMe: 0,
      queue: [
        {
          taskId: 77,
          taskTitle: 'ВИНОКУРОВА - ПОДГОТОВКА УТОЧНЕННОГО ИСКОВОГО ЗАЯВЛЕНИЯ',
          paymentId: 2069,
          direction: 'toMe',
          assigneeName: 'Гордик Сергей',
          authorName: 'Администратор',
          clientId: 71,
          clientName: 'Винокурова Нина Тариеловна',
          clientCaseId: 501,
          clientCaseTitle: 'ВИНОКУРОВА - РАЗДЕЛ ИМУЩЕСТВА',
          amount: 2500,
          paidAmount: 0,
          outstandingAmount: 2500,
          paymentStatus: 'Pending',
          taskStatus: 'done',
          paymentDueDate: '2026-06-30',
          paidDate: null,
        },
      ],
      paid: [],
    });
    vi.mocked(apiService.getMainCompanySettings).mockResolvedValue({ companies: [] });
    vi.mocked(apiService.getDict).mockResolvedValue([]);
    vi.mocked(apiService.getUserSettingForUser).mockResolvedValue(null);
  });

  it('opens contractor payment through the provided payment handler', async () => {
    const onOpenPayment = vi.fn();

    render(
      <UserDetailPage
        userId={10}
        onBack={vi.fn()}
        onSaved={vi.fn()}
        onOpenPayment={onOpenPayment}
      />,
    );

    fireEvent.click(await screen.findByText('Расчеты'));
    const paymentButton = await screen.findByRole('button', { name: 'Платеж #2069' });
    fireEvent.click(paymentButton);

    await waitFor(() => expect(onOpenPayment).toHaveBeenCalledWith(2069));
  });
});
