import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';

import {
  ClientPassportEntriesForm,
  type ClientPassportEntryFormValue,
} from './ClientPassportEntriesForm';

const baseEntry: ClientPassportEntryFormValue = {
  series: '36 19',
  number: '664580',
  issuedBy: 'ОУФМС России по Самарской области',
  issueDate: '2019-06-15',
  departmentCode: '630-001',
  birthPlacePassport: 'г. Самара',
  birthPlaceFias: 'Самарская обл, г Самара',
  validFrom: '2019-06-15',
  validTo: '2045-06-15',
};

describe('ClientPassportEntriesForm integration', () => {
  it('selects a historical issue date through the shared month and year picker', async () => {
    const user = userEvent.setup();
    const onUpdateEntry = vi.fn();
    render(<ClientPassportEntriesForm entries={[baseEntry]} canEdit onUpdateEntry={onUpdateEntry} onRemoveEntry={vi.fn()} onAddEntry={vi.fn()} />);
    await user.click(screen.getByRole('button', { name: /Выбрать.*Дата выдачи/ }));
    await user.click(screen.getByRole('button', { name: 'Выбрать месяц и год' }));
    const year = screen.getByRole('spinbutton', { name: 'Год' });
    await user.clear(year);
    await user.type(year, '1985');
    await user.click(screen.getByRole('button', { name: 'Март' }));
    await user.click(screen.getByRole('button', { name: '15.03.1985' }));
    await user.click(screen.getByRole('button', { name: 'Применить' }));
    expect(onUpdateEntry).toHaveBeenCalledExactlyOnceWith(0, 'issueDate', '1985-03-15');
  });

  it('does not allow changing an issue date when the passport form is read-only', async () => {
    const user = userEvent.setup();
    const onUpdateEntry = vi.fn();
    render(<ClientPassportEntriesForm entries={[baseEntry]} canEdit={false} onUpdateEntry={onUpdateEntry} onRemoveEntry={vi.fn()} onAddEntry={vi.fn()} />);
    const button = screen.getByRole('button', { name: /Выбрать.*Дата выдачи/ });
    expect(button.getAttribute('aria-disabled')).toBe('true');
    expect(button.querySelector('.lucide-x')).toBeNull();
    await user.click(button);
    expect(screen.queryByRole('dialog', { name: 'Выбор даты' })).toBeNull();
    expect(onUpdateEntry).not.toHaveBeenCalled();
  });

  it('renders passport removal as an icon-only action with an accessible label', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.getByRole('button', { name: 'Удалить паспорт' })).toBeTruthy();
    expect(screen.queryByText(/^Удалить$/)).toBeNull();
  });

  it('hides passport validity dates by default', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.queryByText('Действует с')).toBeNull();
    expect(screen.queryByText('Действует по')).toBeNull();
    expect(screen.getByText('Дата выдачи')).toBeTruthy();
  });

  it('does not render the birth place FIAS field', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.queryByText('Место рождения (ФИАС)')).toBeNull();
  });

  it('can restore validity dates through the shared toggle', () => {
    render(
      <ClientPassportEntriesForm
        entries={[baseEntry]}
        canEdit
        showValidityFields
        onUpdateEntry={vi.fn()}
        onRemoveEntry={vi.fn()}
        onAddEntry={vi.fn()}
      />,
    );

    expect(screen.getByText('Действует с')).toBeTruthy();
    expect(screen.getByText('Действует по')).toBeTruthy();
  });
});
