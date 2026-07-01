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
      isActive: true,
      isApproved: true,
      createdAt: '2026-06-01T00:00:00Z',
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

  it('shows contractor task, client, case and payment as regular links', async () => {
    render(
      <UserDetailPage
        userId={10}
        onBack={vi.fn()}
        onSaved={vi.fn()}
        canOpenContractorPaymentLinks
      />,
    );

    fireEvent.click(await screen.findByText('Расчеты'));

    const taskLink = await screen.findByRole('link', {
      name: 'ВИНОКУРОВА - ПОДГОТОВКА УТОЧНЕННОГО ИСКОВОГО ЗАЯВЛЕНИЯ',
    });
    const clientLink = screen.getByRole('link', { name: 'Винокурова Нина Тариеловна' });
    const caseLink = screen.getByRole('link', { name: 'ВИНОКУРОВА - РАЗДЕЛ ИМУЩЕСТВА' });
    const paymentLink = screen.getByRole('link', { name: 'Платеж #2069' });

    expect(taskLink.getAttribute('href')).toBe('/tasks/77');
    expect(taskLink.getAttribute('target')).toBe('_blank');
    expect(clientLink.getAttribute('href')).toBe('/clients/71');
    expect(clientLink.getAttribute('target')).toBe('_blank');
    expect(caseLink.getAttribute('href')).toBe('/cases/501');
    expect(caseLink.getAttribute('target')).toBe('_blank');
    expect(paymentLink.getAttribute('href')).toBe('/payments/2069');
    expect(paymentLink.getAttribute('target')).toBe('_blank');
  });

  it('explains why a contractor payment is absent from the calendar before task completion', async () => {
    vi.mocked(authService.getUserContractorBalance).mockResolvedValue({
      owedToMe: 3500,
      owedByMe: 0,
      paidToMe: 0,
      paidByMe: 0,
      queue: [
        {
          taskId: 78,
          taskTitle: 'МЕРКУЛОВ - ПОДГОТОВКА ИСКА О РАЗДЕЛЕ КВАРТИРЫ',
          paymentId: null,
          direction: 'toMe',
          assigneeName: 'Гордик Сергей',
          authorName: 'Администратор',
          clientId: 72,
          clientName: 'Меркулов Матвей Сергеевич',
          clientCaseId: 502,
          clientCaseTitle: 'МЕРКУЛОВ - РАЗДЕЛ КВАРТИРА КАЛИНИНГРАД',
          amount: 3500,
          paidAmount: 0,
          outstandingAmount: 3500,
          paymentStatus: 'WaitingForResult',
          taskStatus: 'inProgress',
          paymentDueDate: null,
          paidDate: null,
        },
      ],
      paid: [],
    });

    render(
      <UserDetailPage
        userId={10}
        onBack={vi.fn()}
        onSaved={vi.fn()}
        canOpenContractorPaymentLinks
      />,
    );

    fireEvent.click(await screen.findByText('Расчеты'));

    expect(await screen.findByText('Платеж появится в календаре после завершения задачи')).toBeTruthy();
    expect(screen.queryByRole('link', { name: /Платеж #/ })).toBeNull();
  });
});
