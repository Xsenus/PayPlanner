import { documentCopyTestSession } from '../../test/documentCopySession';
import { fireEvent, render, screen, waitFor, within } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi, afterEach } from 'vitest';
let sharedTimezoneTestSession: ReturnType<typeof documentCopyTestSession>;
beforeEach(() => { sharedTimezoneTestSession = documentCopyTestSession(); });
afterEach(() => sharedTimezoneTestSession.restore());


import { DialogProvider } from '../../contexts/DialogContext';
import { apiService } from '../../services/api';
import { CaseDocumentTemplateSettings } from './CaseDocumentTemplateSettings';

const toastMock = {
  success: vi.fn(),
  error: vi.fn(),
};

function renderSettings() {
  return render(
    <DialogProvider>
      <CaseDocumentTemplateSettings />
    </DialogProvider>,
  );
}

vi.mock('../../hooks/useToast', () => ({
  useToast: () => toastMock,
}));

vi.mock('../../services/api', () => ({
  apiService: {
    getCaseDocumentTemplates: vi.fn(),
    getCaseDocumentTemplateTokens: vi.fn(),
    createCaseDocumentTemplate: vi.fn(),
    updateCaseDocumentTemplate: vi.fn(),
    renderCaseDocumentTemplate: vi.fn(),
    getCaseDocumentTemplateHistory: vi.fn(),
    getCaseDocumentTemplateVersion: vi.fn(),
    restoreCaseDocumentTemplateVersion: vi.fn(),
    getUserSetting: vi.fn(),
  },
}));

describe('CaseDocumentTemplateSettings', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCaseDocumentTemplates).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplateTokens).mockReset();
    vi.mocked(apiService.createCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.updateCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.renderCaseDocumentTemplate).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplateHistory).mockReset();
    vi.mocked(apiService.getCaseDocumentTemplateVersion).mockReset();
    vi.mocked(apiService.restoreCaseDocumentTemplateVersion).mockReset();
    vi.mocked(apiService.getUserSetting).mockReset();
    vi.mocked(apiService.getUserSetting).mockResolvedValue({ key: 'profile.timeZoneId', value: 'Europe/Samara' });
    vi.mocked(apiService.getCaseDocumentTemplateHistory).mockResolvedValue([]);
    toastMock.success.mockReset();
    toastMock.error.mockReset();
  });

  it('loads placeholders, saves an existing template and renders a preview', async () => {
    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([
      {
        key: 'claim',
        label: 'Claim template',
        documentType: 'Claim',
        heading: 'Claim template',
        caseTypes: ['Bankruptcy'],
        description: 'Base claim template.',
        content: '<p>{{case.title}}</p>',
        version: 1,
        isActive: true,
        updatedAt: '2026-05-20T22:30:00Z',
      },
    ]);
    vi.mocked(apiService.getCaseDocumentTemplateTokens).mockResolvedValue([
      {
        key: 'case.title',
        category: 'Case',
        description: 'Case title.',
        placeholder: '{{case.title}}',
      },
      {
        key: 'client.name',
        category: 'Client',
        description: 'Client name.',
        placeholder: '{{client.name}}',
      },
    ]);
    vi.mocked(apiService.updateCaseDocumentTemplate).mockResolvedValue({
      key: 'claim',
      label: 'Claim template',
      documentType: 'Claim',
      heading: 'Claim template',
      caseTypes: ['Bankruptcy', 'Contract'],
      description: 'Base claim template.',
      content: '<p>{{case.title}}</p><p>{{client.name}}</p>',
      version: 2,
      isActive: true,
      updatedAt: null,
    });
    vi.mocked(apiService.renderCaseDocumentTemplate).mockResolvedValue({
      templateKey: 'claim',
      templateLabel: 'Claim template',
      documentType: 'Claim',
      fileName: 'claim.html',
      html: '<html><body>Rendered claim</body></html>',
    });

    renderSettings();

    expect(await screen.findByText('Claim template')).toBeTruthy();
    expect(screen.getByText('{{case.title}}')).toBeTruthy();
    expect(screen.getByText('v1')).toBeTruthy();
    expect(screen.getByText('Версия 1')).toBeTruthy();
    expect(await screen.findByText('Обновлено: 21.05.2026')).toBeTruthy();

    const editor = screen.getByDisplayValue('<p>{{case.title}}</p>') as HTMLTextAreaElement;
    fireEvent.click(screen.getAllByRole('button', { name: 'Вставить' })[0]);
    await waitFor(() => {
      expect(editor.value).toBe('<p>{{case.title}}</p>\n{{case.title}}');
    });

    fireEvent.change(editor, { target: { value: '<p>{{case.title}}</p><p>{{client.name}}</p>' } });
    fireEvent.change(screen.getByLabelText(/Типы дел/), { target: { value: 'Bankruptcy, Contract, Bankruptcy' } });
    fireEvent.click(screen.getByRole('button', { name: /Сохранить/i }));

    await waitFor(() => {
      expect(apiService.updateCaseDocumentTemplate).toHaveBeenCalledWith('claim', expect.objectContaining({
        caseTypes: ['Bankruptcy', 'Contract'],
        content: '<p>{{case.title}}</p><p>{{client.name}}</p>',
        documentType: 'Claim',
        isActive: true,
      }));
    });

    fireEvent.change(screen.getByLabelText('ID дела'), { target: { value: '15' } });
    fireEvent.click(screen.getByRole('button', { name: /Предпросмотр/i }));

    await waitFor(() => {
      expect(apiService.renderCaseDocumentTemplate).toHaveBeenCalledWith('claim', {
        clientCaseId: 15,
        documentType: 'Claim',
      });
    });
    expect(await screen.findByTitle('Предпросмотр шаблона документа дела')).toBeTruthy();
  });

  it('previews and restores a saved library version', async () => {
    const currentTemplate = {
      key: 'claim',
      label: 'Текущий иск',
      documentType: 'Иск',
      heading: 'Текущий иск',
      caseTypes: [],
      description: 'Текущая версия.',
      content: '<p>Текущий HTML</p>',
      version: 2,
      isActive: true,
      updatedAt: '2026-08-09T10:00:00Z',
    };
    const restoredTemplate = {
      ...currentTemplate,
      label: 'Исходный иск',
      description: 'Исходная версия.',
      content: '<p>Исходный HTML</p>',
      version: 1,
    };
    const history = [{
      id: 41,
      revision: 1,
      documentType: 'case-document',
      activeTemplateKey: '',
      templateNames: ['Исходный иск'],
      createdByUserId: 3,
      createdByUserName: 'Администратор',
      createdAt: '2026-08-09T09:00:00Z',
      restoredFromVersionId: null,
    }];

    vi.mocked(apiService.getCaseDocumentTemplates).mockResolvedValue([currentTemplate]);
    vi.mocked(apiService.getCaseDocumentTemplateTokens).mockResolvedValue([]);
    vi.mocked(apiService.getCaseDocumentTemplateHistory).mockResolvedValue(history);
    vi.mocked(apiService.getCaseDocumentTemplateVersion).mockResolvedValue([restoredTemplate]);
    vi.mocked(apiService.restoreCaseDocumentTemplateVersion).mockResolvedValue([restoredTemplate]);
    renderSettings();

    fireEvent.click(await screen.findByRole('button', { name: /История версий/ }));
    expect(await screen.findByText('Версия №1')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Просмотреть' }));
    expect(await screen.findByText('<p>Исходный HTML</p>')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Восстановить' }));
    const dialog = await screen.findByRole('dialog', { name: 'Восстановить версию №1' });
    expect(within(dialog).getByText(/Текущая библиотека шаблонов/)).toBeTruthy();
    fireEvent.click(within(dialog).getByRole('button', { name: 'Восстановить' }));
    await waitFor(() => {
      expect(apiService.restoreCaseDocumentTemplateVersion).toHaveBeenCalledWith(41);
    });
    expect((await screen.findAllByText('Исходный иск')).length).toBeGreaterThanOrEqual(2);
    expect(toastMock.success).toHaveBeenCalledWith('Версия №1 восстановлена.', 'История шаблонов');
  });
});
