import { fireEvent, render, screen, within } from '@testing-library/react';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { SystemSelect } from './SystemSelect';

function ControlledSelect({ onValue }: { onValue?: (value: string) => void }) {
  const [value, setValue] = useState('work');
  return (
    <SystemSelect
      aria-label="Статус"
      data-testid="status-select"
      value={value}
      onChange={(event) => {
        setValue(event.target.value);
        onValue?.(event.target.value);
      }}
    >
      <option value="new">Новая</option>
      <option value="work">В работе</option>
      <option value="done">Готово</option>
    </SystemSelect>
  );
}

describe('SystemSelect', () => {
  it('показывает системный список и сохраняет интерфейс нативного onChange', () => {
    const onValue = vi.fn();
    render(<ControlledSelect onValue={onValue} />);

    fireEvent.click(screen.getByRole('combobox'));
    const listbox = screen.getByRole('listbox', { name: 'Статус' });
    fireEvent.click(within(listbox).getByRole('option', { name: 'Готово' }));

    expect(onValue).toHaveBeenCalledWith('done');
    expect(screen.getByRole('combobox').textContent).toContain('Готово');
    expect(screen.queryByRole('listbox')).toBeNull();
  });

  it('оставляет скрытый select для форм и существующих автотестов', () => {
    const onValue = vi.fn();
    render(<ControlledSelect onValue={onValue} />);

    const nativeSelect = screen.getByTestId('status-select');
    expect(nativeSelect.getAttribute('data-system-select-native')).toBe('true');
    fireEvent.change(nativeSelect, { target: { value: 'new' } });

    expect(onValue).toHaveBeenCalledWith('new');
    expect(screen.getByRole('combobox').textContent).toContain('Новая');
  });

  it('добавляет поиск только для длинных списков и фильтрует варианты', () => {
    render(
      <SystemSelect aria-label="Исполнитель" defaultValue="user-1">
        {Array.from({ length: 15 }, (_, index) => (
          <option key={index} value={`user-${index + 1}`}>Сотрудник {index + 1}</option>
        ))}
      </SystemSelect>,
    );

    fireEvent.click(screen.getByRole('combobox'));
    const search = screen.getByPlaceholderText('Поиск по списку');
    fireEvent.change(search, { target: { value: 'Сотрудник 14' } });

    const listbox = screen.getByRole('listbox');
    expect(within(listbox).getByRole('option', { name: 'Сотрудник 14' })).toBeTruthy();
    expect(within(listbox).queryByRole('option', { name: 'Сотрудник 2' })).toBeNull();
  });

  it('не позволяет выбрать отключённый вариант', () => {
    const onChange = vi.fn();
    render(
      <SystemSelect aria-label="Роль" value="user" onChange={onChange}>
        <option value="user">Пользователь</option>
        <option value="admin" disabled>Администратор</option>
      </SystemSelect>,
    );

    fireEvent.click(screen.getByRole('combobox'));
    fireEvent.click(screen.getByRole('option', { name: 'Администратор' }));
    expect(onChange).not.toHaveBeenCalled();
  });
});
