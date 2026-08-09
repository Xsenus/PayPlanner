import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { DocumentTemplateHistoryPanel } from './DocumentTemplateHistoryPanel';

const confirmMock = vi.fn();

vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: confirmMock }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn() }) }));
vi.mock('../../services/api', () => ({
  apiService: {
    getInvoiceDocumentTemplateHistory: vi.fn(),
    getContractDocumentTemplateHistory: vi.fn(),
    getInvoiceDocumentTemplateVersion: vi.fn(),
    getContractDocumentTemplateVersion: vi.fn(),
    restoreInvoiceDocumentTemplateVersion: vi.fn(),
    restoreContractDocumentTemplateVersion: vi.fn(),
  },
}));

const settings = {
  activeTemplateKey: 'invoice-standard',
  printWithSignatureAndSealByDefault: false,
  defaultTemplateHtml: '<html><body>default</body></html>',
  tokens: [],
  templates: [{ key: 'invoice-standard', name: 'Стандартный счет', html: '<html><body>Версия 1</body></html>' }],
};

describe('DocumentTemplateHistoryPanel', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(apiService.getInvoiceDocumentTemplateHistory).mockResolvedValue([{
      id: 11,
      revision: 1,
      documentType: 'invoice',
      activeTemplateKey: 'invoice-standard',
      templateNames: ['Стандартный счет'],
      createdByUserName: 'Администратор',
      createdAt: '2026-08-09T08:00:00Z',
    }]);
    vi.mocked(apiService.getInvoiceDocumentTemplateVersion).mockResolvedValue(settings);
    vi.mocked(apiService.restoreInvoiceDocumentTemplateVersion).mockResolvedValue(settings);
    confirmMock.mockResolvedValue(true);
  });

  it('shows, previews and restores a saved invoice template version', async () => {
    const onRestored = vi.fn();
    render(<DocumentTemplateHistoryPanel kind="invoice" refreshKey={0} onRestored={onRestored} />);

    fireEvent.click(await screen.findByRole('button', { name: /История версий/ }));
    expect(await screen.findByText('Версия №1')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Просмотреть' }));
    expect(await screen.findByTitle('Версия 1: Стандартный счет')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Восстановить' }));
    await waitFor(() => expect(onRestored).toHaveBeenCalledWith(settings));
    expect(apiService.restoreInvoiceDocumentTemplateVersion).toHaveBeenCalledWith(11);
  });
});
