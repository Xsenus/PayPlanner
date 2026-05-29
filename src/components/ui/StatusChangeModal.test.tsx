import { render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';

import { StatusChangeModal, type StatusChangeOption } from './StatusChangeModal';

const options: StatusChangeOption[] = [
  { value: 'Draft', label: 'Черновик', description: 'Документ вернется в черновик.' },
  { value: 'NotSigned', label: 'Не подписан', description: 'Документ будет отмечен как неподписанный.' },
  { value: 'RequiresClientSignature', label: 'Требуется подпись клиента', description: 'Документ требует подписи клиента.' },
  { value: 'OnApproval', label: 'На согласовании', description: 'Документ будет отправлен на внутреннее согласование.' },
  { value: 'Approved', label: 'Согласован', description: 'Документ будет отмечен как согласованный.' },
  { value: 'ApprovalRejected', label: 'Отказ в согласовании', description: 'В журнал попадет отказ в согласовании.' },
  { value: 'NeedsCorrection', label: 'Требует исправления', description: 'Документ будет возвращен на исправление.' },
  { value: 'OnSigning', label: 'На подписании', description: 'Документ будет отправлен на подписание.' },
  { value: 'SignedByClient', label: 'Подписан клиентом', description: 'Документ будет отмечен как подписанный клиентом.' },
  { value: 'Cancelled', label: 'Аннулирован', description: 'Изменится только статус документа.' },
];

describe('StatusChangeModal', () => {
  it('keeps the action buttons visible by scrolling the modal body inside a viewport-limited dialog', () => {
    render(
      <StatusChangeModal
        open
        title="Изменить статус счета"
        subject="Счет №70"
        currentLabel="Требуется подпись клиента"
        currentValue="RequiresClientSignature"
        value="SignedByClient"
        options={options}
        onChange={vi.fn()}
        onCancel={vi.fn()}
        onConfirm={vi.fn()}
        confirmLabel="Подтвердить"
        reason=""
        onReasonChange={vi.fn()}
        consequences={[
          'Статус документа изменится с «Требуется подпись клиента» на «Подписан клиентом».',
          'Статус оплаты, сумма, дата оплаты и связанные платежи не меняются.',
          'Изменение сохранится сразу после подтверждения и список счетов обновится.',
        ]}
      />,
    );

    const dialog = screen.getByTestId('status-change-dialog');
    const body = screen.getByTestId('status-change-dialog-body');
    const actions = screen.getByTestId('status-change-dialog-actions');

    expect(dialog.className).toContain('max-h-[calc(100vh-1rem)]');
    expect(dialog.className).toContain('flex-col');
    expect(dialog.className).toContain('overflow-hidden');
    expect(dialog.className).toContain('max-w-4xl');
    expect(body.className).toContain('overflow-y-auto');
    expect(body.className).toContain('min-h-0');
    expect(actions.className).toContain('shrink-0');
    expect(screen.getByRole('button', { name: 'Подтвердить' })).toBeTruthy();
  });
});
