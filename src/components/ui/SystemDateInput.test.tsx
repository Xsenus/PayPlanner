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
