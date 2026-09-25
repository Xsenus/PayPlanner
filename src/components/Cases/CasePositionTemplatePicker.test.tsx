import { fireEvent, render, screen, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { apiService } from '../../services/api';
import { CasePositionTemplatePicker } from './CasePositionTemplatePicker';

vi.mock('../../services/api', () => ({
  apiService: {
    getCasePositionTemplates: vi.fn(),
    createCasePositionTemplate: vi.fn(),
    updateCasePositionTemplate: vi.fn(),
    archiveCasePositionTemplate: vi.fn(),
  },
}));

const shared = {
  id: 1, title: 'Возражения', text: 'Проверить доводы и доказательства.',
  scope: 'shared' as const, updatedAt: '2026-09-25T00:00:00Z', canManage: false,
};

describe('CasePositionTemplatePicker', () => {
  beforeEach(() => {
    vi.mocked(apiService.getCasePositionTemplates).mockReset();
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [shared], canManageShared: false });
    vi.mocked(apiService.createCasePositionTemplate).mockReset();
    vi.mocked(apiService.updateCasePositionTemplate).mockReset();
    vi.mocked(apiService.archiveCasePositionTemplate).mockReset();
  });

  it('inserts a copy after existing case text without changing the saved case automatically', async () => {
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text="Моя позиция" onChange={onChange} />);

    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));

    expect(onChange).toHaveBeenCalledWith('Моя позиция\n\nПроверить доводы и доказательства.');
    expect(apiService.updateCasePositionTemplate).not.toHaveBeenCalled();
  });

  it('creates a personal template while hiding shared management from a non-admin', async () => {
    vi.mocked(apiService.createCasePositionTemplate).mockResolvedValue({
      id: 2, title: 'Моя заготовка', text: 'Текст дела', scope: 'personal',
      updatedAt: '2026-09-25T00:00:00Z', canManage: true,
    });
    render(<CasePositionTemplatePicker text="Текст дела" onChange={vi.fn()} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });

    fireEvent.click(screen.getByRole('button', { name: 'Новый шаблон' }));
    expect(screen.queryByRole('option', { name: 'Общий' })).toBeNull();
    fireEvent.change(screen.getByRole('textbox', { name: 'Название шаблона позиции' }), { target: { value: 'Моя заготовка' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));

    await waitFor(() => expect(apiService.createCasePositionTemplate).toHaveBeenCalledWith({
      title: 'Моя заготовка', text: 'Текст дела', scope: 'personal',
    }));
    expect(await screen.findByRole('option', { name: 'Личный · Моя заготовка' })).toBeTruthy();
  });

  it('keeps the draft unchanged when insertion would exceed the case field limit', async () => {
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text={'x'.repeat(3999)} onChange={onChange} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Вставить в позицию' }));
    expect(onChange).not.toHaveBeenCalled();
    expect(screen.getByRole('alert').textContent).toContain('4000 символов');
  });

  it('lets an administrator create a shared template', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({ items: [], canManageShared: true });
    vi.mocked(apiService.createCasePositionTemplate).mockResolvedValue({
      id: 3, title: 'Общая позиция', text: 'Текст', scope: 'shared',
      updatedAt: '2026-09-25T00:00:00Z', canManage: true,
    });
    render(<CasePositionTemplatePicker text="Текст" onChange={vi.fn()} />);
    await waitFor(() => expect(apiService.getCasePositionTemplates).toHaveBeenCalled());
    fireEvent.click(screen.getByRole('button', { name: 'Новый шаблон' }));
    fireEvent.change(screen.getByRole('combobox', { name: 'Доступность шаблона позиции' }), { target: { value: 'shared' } });
    fireEvent.change(screen.getByRole('textbox', { name: 'Название шаблона позиции' }), { target: { value: 'Общая позиция' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));
    await waitFor(() => expect(apiService.createCasePositionTemplate).toHaveBeenCalledWith({
      title: 'Общая позиция', text: 'Текст', scope: 'shared',
    }));
  });

  it('updates and archives only a manageable template after confirmation', async () => {
    vi.mocked(apiService.getCasePositionTemplates).mockResolvedValue({
      items: [{ ...shared, canManage: true }], canManageShared: true,
    });
    vi.mocked(apiService.updateCasePositionTemplate).mockResolvedValue({
      ...shared, text: 'Исправленная позиция', canManage: true,
    });
    vi.mocked(apiService.archiveCasePositionTemplate).mockResolvedValue(undefined);
    const onChange = vi.fn();
    render(<CasePositionTemplatePicker text="Позиция конкретного дела" onChange={onChange} />);
    await screen.findByRole('option', { name: 'Общий · Возражения' });
    fireEvent.change(screen.getByRole('combobox', { name: 'Шаблон позиции' }), { target: { value: '1' } });
    fireEvent.click(screen.getByRole('button', { name: 'Изменить шаблон' }));
    fireEvent.change(screen.getByRole('textbox', { name: 'Текст шаблона позиции' }), { target: { value: 'Исправленная позиция' } });
    fireEvent.click(screen.getByRole('button', { name: 'Сохранить шаблон' }));
    await waitFor(() => expect(apiService.updateCasePositionTemplate).toHaveBeenCalledWith(1, {
      title: 'Возражения', text: 'Исправленная позиция', scope: 'shared',
    }));
    expect(onChange).not.toHaveBeenCalled();

    fireEvent.click(screen.getByRole('button', { name: 'В архив' }));
    expect(apiService.archiveCasePositionTemplate).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole('button', { name: 'Подтвердить' }));
    await waitFor(() => expect(apiService.archiveCasePositionTemplate).toHaveBeenCalledWith(1));
    expect(screen.queryByRole('option', { name: 'Общий · Возражения' })).toBeNull();
    expect(onChange).not.toHaveBeenCalled();
  });
});
