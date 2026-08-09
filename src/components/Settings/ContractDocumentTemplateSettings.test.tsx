import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ContractDocumentTemplateSettings } from './ContractDocumentTemplateSettings';
import { apiService } from '../../services/api';

vi.mock('../../hooks/useToast', () => ({
  useToast: () => ({
    success: vi.fn(),
    error: vi.fn(),
  }),
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getContractDocumentTemplateSettings: vi.fn(),
    updateContractDocumentTemplateSettings: vi.fn(),
    getContractDocumentTemplateHistory: vi.fn(),
    getContractDocumentTemplateVersion: vi.fn(),
    restoreContractDocumentTemplateVersion: vi.fn(),
  },
}));

vi.mock('../../hooks/useDialog', () => ({
  useDialog: () => ({ confirm: vi.fn() }),
}));

const settingsFixture = {
  activeTemplateKey: 'contract-standard',
  printWithSignatureAndSealByDefault: false,
  defaultTemplateHtml: '<html><body>Договор {{contract.number}}</body></html>',
  tokens: [
    {
      key: 'contract.number',
      category: 'Договор',
      description: 'Номер договора',
      isRepeating: false,
    },
  ],
  templates: [
    {
      key: 'contract-standard',
      name: 'Стандартный договор',
      description: 'Базовый шаблон',
      html: '<html><body>Договор {{contract.number}}</body></html>',
    },
  ],
};

describe('ContractDocumentTemplateSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getContractDocumentTemplateSettings).mockReset();
    vi.mocked(apiService.updateContractDocumentTemplateSettings).mockReset();
    vi.mocked(apiService.getContractDocumentTemplateHistory).mockResolvedValue([]);
    vi.mocked(apiService.getContractDocumentTemplateSettings).mockResolvedValue(settingsFixture);
    vi.mocked(apiService.updateContractDocumentTemplateSettings).mockImplementation(async (payload) => ({
      ...settingsFixture,
      ...payload,
      tokens: settingsFixture.tokens,
      defaultTemplateHtml: settingsFixture.defaultTemplateHtml,
    }));
  });

  it('loads and saves the contract print template', async () => {
    render(<ContractDocumentTemplateSettings />);

    expect(await screen.findByText('Печатный шаблон договора')).toBeTruthy();
    expect(screen.getByText('{{contract.number}}')).toBeTruthy();

    const htmlEditor = await screen.findByDisplayValue('<html><body>Договор {{contract.number}}</body></html>');
    fireEvent.change(htmlEditor, {
      target: { value: '<html><body>Новый {{contract.number}}</body></html>' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));

    await waitFor(() => {
      expect(apiService.updateContractDocumentTemplateSettings).toHaveBeenCalledWith({
        activeTemplateKey: 'contract-standard',
        printWithSignatureAndSealByDefault: false,
        templates: [
          {
            key: 'contract-standard',
            name: 'Стандартный договор',
            description: 'Базовый шаблон',
            html: '<html><body>Новый {{contract.number}}</body></html>',
          },
        ],
      });
    });
  });
});
