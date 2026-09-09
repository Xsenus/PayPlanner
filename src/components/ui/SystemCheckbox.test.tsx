import { fireEvent, render, screen } from '@testing-library/react';
import { createRef, useState } from 'react';
import { describe, expect, it, vi } from 'vitest';
import { SystemCheckbox } from './SystemCheckbox';

function ControlledCheckbox() {
  const [checked, setChecked] = useState(false);
  return (
    <label>
      Получать уведомления
      <SystemCheckbox checked={checked} onChange={(event) => setChecked(event.target.checked)} />
    </label>
  );
}

describe('SystemCheckbox', () => {
  it('preserves the native checkbox event contract', () => {
    const onChange = vi.fn();
    render(<SystemCheckbox aria-label="Выбрать" checked={false} onChange={onChange} />);

    const checkbox = screen.getByRole('checkbox', { name: 'Выбрать' }) as HTMLInputElement;
    fireEvent.click(checkbox);
    expect(onChange).toHaveBeenCalled();
  });

  it('works inside a wrapping label', () => {
    render(<ControlledCheckbox />);
    const checkbox = screen.getByRole('checkbox', { name: 'Получать уведомления' }) as HTMLInputElement;
    fireEvent.click(checkbox);
    expect(checkbox.checked).toBe(true);
  });

  it('forwards the native input ref and indeterminate state', () => {
    const ref = createRef<HTMLInputElement>();
    render(<SystemCheckbox ref={ref} aria-label="Выбрать часть" indeterminate />);

    expect(ref.current?.type).toBe('checkbox');
    expect(ref.current?.indeterminate).toBe(true);
  });

  it('uses the compact toggle view for semantic switches', () => {
    const { container } = render(<SystemCheckbox role="switch" aria-label="Автозагрузка" checked onChange={() => undefined} />);

    expect(screen.getByRole('switch', { name: 'Автозагрузка' })).toBeTruthy();
    expect((container.querySelector('[data-system-checkbox="switch"]') as HTMLElement).style.width).toBe('36px');
  });

  it('exposes stable visual layers for checked and indeterminate states', () => {
    const { container, rerender } = render(
      <SystemCheckbox aria-label="Выбрать строки" checked onChange={() => undefined} />,
    );

    const input = screen.getByRole('checkbox', { name: 'Выбрать строки' }) as HTMLInputElement;
    expect(input.checked).toBe(true);
    expect(container.querySelector('.pp-system-checkbox-box')).toBeTruthy();
    expect(container.querySelector('.pp-system-checkbox-check')).toBeTruthy();

    rerender(
      <SystemCheckbox
        aria-label="Выбрать строки"
        checked={false}
        indeterminate
        onChange={() => undefined}
      />,
    );

    expect(input.indeterminate).toBe(true);
    expect(container.querySelector('.pp-system-checkbox-indeterminate')).toBeTruthy();
  });
});
