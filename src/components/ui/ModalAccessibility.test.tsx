import userEvent from '@testing-library/user-event';
import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { DialogProvider } from '../../contexts/DialogContext';
import { useDialog } from '../../hooks/useDialog';
import { ConfirmDialog } from './ConfirmDialog';
import { TextPromptModal } from './TextPromptModal';

function PromptLauncher({ onResolved }: { onResolved: (value: string | null) => void }) {
  const { prompt } = useDialog();

  return (
    <button
      type="button"
      onClick={async () => {
        const value = await prompt({
          title: 'Название представления',
          label: 'Название',
          defaultValue: 'Мой фильтр',
        });
        onResolved(value);
      }}
    >
      Открыть ввод
    </button>
  );
}

function ConfirmLauncher({ onResolved }: { onResolved: (value: boolean) => void }) {
  const { confirm } = useDialog();

  return (
    <button
      type="button"
      onClick={async () => {
        const value = await confirm({
          title: 'Удалить заметку',
          description: 'Заметка будет удалена с доски.',
          cancelLabel: 'Оставить',
          confirmLabel: 'Удалить',
          variant: 'danger',
        });
        onResolved(value);
      }}
    >
      Открыть подтверждение
    </button>
  );
}

function AlertLauncher({ onResolved }: { onResolved: () => void }) {
  const { alert } = useDialog();

  return (
    <button
      type="button"
      onClick={async () => {
        await alert({
          title: 'Готово',
          description: 'Действие выполнено.',
          confirmLabel: 'Понятно',
        });
        onResolved();
      }}
    >
      Открыть уведомление
    </button>
  );
}

describe('modal accessibility contracts', () => {
  it('keeps keyboard focus inside text prompt modals and closes on Escape', async () => {
    const user = userEvent.setup();
    const onCancel = vi.fn();

    render(
      <TextPromptModal
        open
        title="Переименовать папку"
        label="Название папки"
        value=""
        onValueChange={vi.fn()}
        onCancel={onCancel}
        onConfirm={vi.fn()}
        confirmLabel="Сохранить"
      />,
    );

    const dialog = screen.getByRole('dialog', { name: 'Переименовать папку' });
    const closeButton = screen.getByRole('button', { name: 'Закрыть' });
    const input = screen.getByRole('textbox', { name: 'Название папки' });
    const cancelButton = screen.getByRole('button', { name: 'Отмена' });
    const saveButton = screen.getByRole('button', { name: 'Сохранить' });

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.activeElement).toBe(input);

    await user.tab();
    expect(document.activeElement).toBe(cancelButton);

    await user.tab();
    expect(document.activeElement).toBe(saveButton);

    await user.tab();
    expect(document.activeElement).toBe(closeButton);

    await user.tab();
    expect(document.activeElement).toBe(input);

    await user.keyboard('{Shift>}{Tab}{/Shift}');
    expect(document.activeElement).toBe(closeButton);

    await user.keyboard('{Escape}');
    expect(onCancel).toHaveBeenCalledTimes(1);
  });

  it('keeps keyboard focus inside confirm dialogs', async () => {
    const user = userEvent.setup();

    render(
      <ConfirmDialog
        open
        title="Удалить документ"
        description="Документ будет удален из дела."
        cancelLabel="Оставить"
        confirmLabel="Удалить"
        onCancel={vi.fn()}
        onConfirm={vi.fn()}
      />,
    );

    const dialog = screen.getByRole('dialog', { name: 'Удалить документ' });
    const cancelButton = screen.getByRole('button', { name: 'Оставить' });
    const confirmButton = screen.getByRole('button', { name: 'Удалить' });

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.activeElement).toBe(cancelButton);

    await user.tab();
    expect(document.activeElement).toBe(confirmButton);

    await user.tab();
    expect(document.activeElement).toBe(cancelButton);
  });

  it('closes shared prompt dialogs on Escape', async () => {
    const user = userEvent.setup();
    const onResolved = vi.fn();

    render(
      <DialogProvider>
        <PromptLauncher onResolved={onResolved} />
      </DialogProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Открыть ввод' }));

    expect(screen.getByRole('dialog', { name: 'Название представления' })).toBeTruthy();
    const input = screen.getByRole('textbox', { name: 'Название' });
    const cancelButton = screen.getByRole('button', { name: 'Отмена' });
    const confirmButton = screen.getByRole('button', { name: 'Подтвердить' });

    expect((input as HTMLInputElement).value).toBe('Мой фильтр');
    expect(document.activeElement).toBe(input);

    await user.tab();
    expect(document.activeElement).toBe(cancelButton);

    await user.tab();
    expect(document.activeElement).toBe(confirmButton);

    await user.tab();
    expect(document.activeElement).toBe(input);

    await user.keyboard('{Escape}');

    expect(onResolved).toHaveBeenCalledWith(null);
    expect(screen.queryByRole('dialog', { name: 'Название представления' })).toBeNull();
  });

  it('closes shared confirm dialogs as cancellation on Escape', async () => {
    const user = userEvent.setup();
    const onResolved = vi.fn();

    render(
      <DialogProvider>
        <ConfirmLauncher onResolved={onResolved} />
      </DialogProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Открыть подтверждение' }));

    const dialog = screen.getByRole('dialog', { name: 'Удалить заметку' });
    const cancelButton = screen.getByRole('button', { name: 'Оставить' });
    const confirmButton = screen.getByRole('button', { name: 'Удалить' });

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.activeElement).toBe(cancelButton);

    await user.tab();
    expect(document.activeElement).toBe(confirmButton);

    await user.tab();
    expect(document.activeElement).toBe(cancelButton);

    await user.keyboard('{Escape}');

    expect(onResolved).toHaveBeenCalledWith(false);
    expect(screen.queryByRole('dialog', { name: 'Удалить заметку' })).toBeNull();
  });

  it('closes shared alert dialogs on Escape', async () => {
    const user = userEvent.setup();
    const onResolved = vi.fn();

    render(
      <DialogProvider>
        <AlertLauncher onResolved={onResolved} />
      </DialogProvider>,
    );

    await user.click(screen.getByRole('button', { name: 'Открыть уведомление' }));

    const dialog = screen.getByRole('dialog', { name: 'Готово' });
    const okButton = screen.getByRole('button', { name: 'Понятно' });

    expect(dialog.getAttribute('aria-modal')).toBe('true');
    expect(document.activeElement).toBe(okButton);

    await user.tab();
    expect(document.activeElement).toBe(okButton);

    await user.keyboard('{Escape}');

    expect(onResolved).toHaveBeenCalledTimes(1);
    expect(screen.queryByRole('dialog', { name: 'Готово' })).toBeNull();
  });
});
