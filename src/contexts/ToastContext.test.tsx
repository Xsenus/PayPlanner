import { fireEvent, render, screen } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ToastProvider } from './ToastContext';
import { useToast } from '../hooks/useToast';

function ToastHarness({
  onFirstAction,
  onSecondAction,
}: {
  onFirstAction?: () => void;
  onSecondAction?: () => void;
}) {
  const toast = useToast();

  return (
    <div>
      <button
        type="button"
        onClick={() => {
          toast.showToast({
            title: 'Первое событие',
            description: 'Первое описание',
            actionLabel: 'Открыть первое',
            onAction: onFirstAction,
            dedupeKey: 'task:1',
            durationMs: 0,
          });
          toast.showToast({
            title: 'Последнее событие',
            description: 'Последнее описание',
            actionLabel: 'Открыть последнее',
            onAction: onSecondAction,
            dedupeKey: 'task:1',
            durationMs: 0,
          });
        }}
      >
        Replace
      </button>
      <button
        type="button"
        onClick={() => {
          ['Первое', 'Второе', 'Третье', 'Четвертое'].forEach((title) => {
            toast.showToast({ title, description: title, durationMs: 0 });
          });
        }}
      >
        Flood
      </button>
      <button
        type="button"
        onClick={() => {
          toast.showToast({
            title: 'Проверка платежа',
            description: 'Платеж требует внимания.',
            domain: 'payment',
            durationMs: 0,
          });
        }}
      >
        Domain
      </button>
    </div>
  );
}

describe('ToastProvider', () => {
  it('replaces an existing toast with the same dedupe key and keeps the latest action', () => {
    const firstAction = vi.fn();
    const secondAction = vi.fn();

    render(
      <ToastProvider>
        <ToastHarness onFirstAction={firstAction} onSecondAction={secondAction} />
      </ToastProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Replace' }));

    expect(screen.queryByText('Первое событие')).toBeNull();
    expect(screen.getByText('Последнее событие')).toBeTruthy();

    fireEvent.click(screen.getByRole('button', { name: 'Открыть последнее' }));

    expect(firstAction).not.toHaveBeenCalled();
    expect(secondAction).toHaveBeenCalledTimes(1);
  });

  it('keeps only the latest three visible toasts', () => {
    render(
      <ToastProvider>
        <ToastHarness />
      </ToastProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Flood' }));

    expect(screen.queryAllByText('Первое')).toHaveLength(0);
    expect(screen.getAllByText('Второе').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Третье').length).toBeGreaterThan(0);
    expect(screen.getAllByText('Четвертое').length).toBeGreaterThan(0);
  });

  it('shows a compact domain marker for domain-specific toasts', () => {
    render(
      <ToastProvider>
        <ToastHarness />
      </ToastProvider>,
    );

    fireEvent.click(screen.getByRole('button', { name: 'Domain' }));

    expect(screen.getByText('Деньги')).toBeTruthy();
    expect(screen.getByText('Проверка платежа')).toBeTruthy();
  });
});
