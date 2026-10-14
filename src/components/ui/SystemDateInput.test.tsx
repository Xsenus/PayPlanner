import { fireEvent, render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { resolveDateMenuTop, SystemDateInput } from './SystemDateInput';

function ControlledDate({ initial = '' }: { initial?: string }) {
  const [value, setValue] = useState(initial);
  return (
    <SystemDateInput
      aria-label="Срок"
      type="date"
      value={value}
      onChange={(event) => setValue(event.target.value)}
    />
  );
}

describe('SystemDateInput', () => {
  it.each([
    { type: 'date' as const, value: '2026-07-21', restriction: 'disabled' as const },
    { type: 'date' as const, value: '2026-07-21', restriction: 'readOnly' as const },
    { type: 'time' as const, value: '10:30', restriction: 'disabled' as const },
    { type: 'time' as const, value: '10:30', restriction: 'readOnly' as const },
    { type: 'datetime-local' as const, value: '2026-07-21T10:30', restriction: 'disabled' as const },
    { type: 'datetime-local' as const, value: '2026-07-21T10:30', restriction: 'readOnly' as const },
  ])('closes the $type picker when $restriction is applied and preserves the value', async ({ type, value, restriction }) => {
    const user = userEvent.setup();
    const onChange = vi.fn();
    const view = render(<SystemDateInput aria-label="Срок" type={type} value={value} onChange={onChange} />);
    await user.click(screen.getByRole('button', { name: 'Выбрать: Срок' }));
    expect(screen.getByRole('dialog')).toBeTruthy();
    view.rerender(<SystemDateInput aria-label="Срок" type={type} value={value} onChange={onChange} {...{ [restriction]: true }} />);
    expect(screen.queryByRole('dialog')).toBeNull();
    expect(screen.getByRole('button', { name: 'Выбрать: Срок' }).querySelector('.lucide-x')).toBeNull();
    expect((screen.getByLabelText('Срок') as HTMLInputElement).value).toBe(value);
    expect(onChange).not.toHaveBeenCalled();
    view.rerender(<SystemDateInput aria-label="Срок" type={type} value={value} onChange={onChange} />);
    expect(screen.queryByRole('dialog')).toBeNull();
  });

  it('keeps a native date input for forms and existing integrations', () => {
    const onChange = vi.fn();
    const { container } = render(<SystemDateInput type="date" name="dueDate" value="2026-07-21" onChange={onChange} />);
    const nativeInput = container.querySelector('input[type="date"]') as HTMLInputElement;

    expect(nativeInput).toBeTruthy();
    expect(nativeInput.name).toBe('dueDate');
    expect(screen.getByRole('button', { name: 'Выбрать: dueDate' }).textContent).toContain('21.07.2026');

    fireEvent.change(nativeInput, { target: { value: '2026-07-22' } });
    expect(onChange).toHaveBeenCalled();
  });

  it('preserves labels that wrap the original field', () => {
    render(
      <label>
        С даты
        <SystemDateInput type="date" value="2026-07-21" onChange={() => undefined} />
      </label>,
    );

    expect((screen.getByLabelText('С даты') as HTMLInputElement).value).toBe('2026-07-21');
  });

  it('selects a date in the styled calendar and emits the native change contract', async () => {
    const user = userEvent.setup();
    render(<ControlledDate initial="2026-07-21" />);

    await user.click(screen.getByRole('button', { name: 'Выбрать: Срок' }));
    expect(screen.getByRole('dialog', { name: 'Выбор даты' })).toBeTruthy();
    await user.click(screen.getByRole('button', { name: '22.07.2026' }));
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    expect(screen.getByRole('button', { name: 'Выбрать: Срок' }).textContent).toContain('22.07.2026');
  });

  it('allows selecting any month and entering a year from the calendar header', async () => {
    const user = userEvent.setup();
    render(<ControlledDate initial="2026-07-21" />);

    await user.click(screen.getByRole('button', { name: 'Выбрать: Срок' }));
    await user.click(screen.getByRole('button', { name: 'Выбрать месяц и год' }));

    const yearInput = screen.getByRole('spinbutton', { name: 'Год' });
    expect((screen.getByRole('button', { name: 'Применить' }) as HTMLButtonElement).disabled).toBe(true);
    await user.clear(yearInput);
    await user.type(yearInput, '1985');
    await user.click(screen.getByRole('button', { name: 'Март' }));

    expect(screen.getByRole('button', { name: 'Выбрать месяц и год' }).textContent).toContain('Март 1985');
    await user.click(screen.getByRole('button', { name: '15.03.1985' }));
    await user.click(screen.getByRole('button', { name: 'Применить' }));

    expect(screen.getByRole('button', { name: 'Выбрать: Срок' }).textContent).toContain('15.03.1985');
  });

  it('uses the full navigation buttons in both day and year selection modes', async () => {
    const user = userEvent.setup();
    render(<SystemDateInput aria-label="Дата" type="date" value="2026-09-07" onChange={() => undefined} />);

    await user.click(screen.getByRole('button', { name: 'Выбрать: Дата' }));
    await user.click(screen.getByRole('button', { name: 'Предыдущий месяц' }));
    expect(screen.getByRole('button', { name: 'Выбрать месяц и год' }).textContent).toContain('Август 2026');

    await user.click(screen.getByRole('button', { name: 'Выбрать месяц и год' }));
    await user.click(screen.getByRole('button', { name: 'Предыдущий год' }));
    expect((screen.getByRole('spinbutton', { name: 'Год' }) as HTMLInputElement).value).toBe('2025');
  });

  it('keeps unavailable months disabled when the field has date limits', async () => {
    const user = userEvent.setup();
    render(<SystemDateInput aria-label="Период" type="date" value="2026-06-15" min="2026-05-10" max="2026-08-20" onChange={() => undefined} />);

    await user.click(screen.getByRole('button', { name: 'Выбрать: Период' }));
    await user.click(screen.getByRole('button', { name: 'Выбрать месяц и год' }));

    expect((screen.getByRole('button', { name: 'Январь' }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole('button', { name: 'Май' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Август' }) as HTMLButtonElement).disabled).toBe(false);
    expect((screen.getByRole('button', { name: 'Сентябрь' }) as HTMLButtonElement).disabled).toBe(true);
  });

  it('shows date and time without the browser datetime picker', async () => {
    const user = userEvent.setup();
    render(<SystemDateInput aria-label="Дата выполнения" type="datetime-local" value="2026-07-21T14:06" onChange={() => undefined} />);

    expect(screen.getByRole('button', { name: 'Выбрать: Дата выполнения' }).textContent).toContain('21.07.2026, 14:06');
    await user.click(screen.getByRole('button', { name: 'Выбрать: Дата выполнения' }));
    expect((screen.getByRole('spinbutton', { name: 'Часы' }) as HTMLInputElement).value).toBe('14');
    expect((screen.getByRole('spinbutton', { name: 'Минуты' }) as HTMLInputElement).value).toBe('06');
  });

  it('keeps the calendar inside the viewport when neither side has enough room', () => {
    expect(resolveDateMenuTop(306, 379, 360, 711)).toBe(343);
  });

  it('opens above the field when there is enough room there', async () => {
    const user = userEvent.setup();
    render(<SystemDateInput aria-label="Дата" type="date" value="2026-07-21" onChange={() => undefined} />);
    const button = screen.getByRole('button', { name: 'Выбрать: Дата' });
    vi.spyOn(button, 'getBoundingClientRect').mockReturnValue({
      x: 20, y: 700, top: 700, left: 20, right: 220, bottom: 740, width: 200, height: 40, toJSON: () => ({}),
    });
    Object.defineProperty(window, 'innerHeight', { configurable: true, value: 720 });

    await user.click(button);
    await new Promise((resolve) => requestAnimationFrame(resolve));
    const dialog = screen.getByRole('dialog', { name: 'Выбор даты' });
    expect(dialog.style.top).not.toBe('');
  });
});
