import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { ActDocumentTemplateSettings } from './ActDocumentTemplateSettings';

const confirmMock = vi.fn();
vi.mock('../../hooks/useDialog', () => ({ useDialog: () => ({ confirm: confirmMock }) }));
vi.mock('../../hooks/useToast', () => ({ useToast: () => ({ success: vi.fn(), error: vi.fn() }) }));
vi.mock('./DocumentTemplateHistoryPanel', () => ({ DocumentTemplateHistoryPanel: () => <div>История акта</div> }));
vi.mock('../../services/api', () => ({
  apiService: {
    getActDocumentTemplateSettings: vi.fn(),
    updateActDocumentTemplateSettings: vi.fn(),
  },
}));

const fixture = {
  activeTemplateKey: 'act-standard',
  printWithSignatureAndSealByDefault: false,
  defaultTemplateHtml: '<html><body>{{act.number}}</body></html>',
  tokens: [{ key: 'act.number', category: 'Акт', description: 'Номер акта', isRepeating: false }],
  templates: [{ key: 'act-standard', name: 'Стандартный акт', description: 'Основной шаблон', html: '<html><body>{{act.number}}</body></html>' }],
};

describe('ActDocumentTemplateSettings', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    confirmMock.mockResolvedValue(true);
    vi.mocked(apiService.getActDocumentTemplateSettings).mockResolvedValue(fixture);
    vi.mocked(apiService.updateActDocumentTemplateSettings).mockImplementation(async (payload) => ({ ...fixture, ...payload }));
  });

  it('loads, previews and saves the act template', async () => {
    render(<ActDocumentTemplateSettings />);
    expect(await screen.findByText('Печатный шаблон акта')).toBeTruthy();
    expect(screen.getByTitle('Предпросмотр шаблона акта')).toBeTruthy();

    fireEvent.change(screen.getByDisplayValue('<html><body>{{act.number}}</body></html>'), {
      target: { value: '<html><body>Акт {{act.number}}</body></html>' },
    });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить' }));

    await waitFor(() => expect(apiService.updateActDocumentTemplateSettings).toHaveBeenCalledWith({
      activeTemplateKey: 'act-standard',
      printWithSignatureAndSealByDefault: false,
      templates: [{ key: 'act-standard', name: 'Стандартный акт', description: 'Основной шаблон', html: '<html><body>Акт {{act.number}}</body></html>' }],
    }));
  });
});
