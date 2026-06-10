import { renderHook, act } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { useDebouncedValue } from './useDebouncedValue';

describe('useDebouncedValue', () => {
  afterEach(() => {
    vi.useRealTimers();
  });

  it('keeps the previous value until the debounce delay passes', () => {
    vi.useFakeTimers();

    const { result, rerender } = renderHook(
      ({ value, delayMs }) => useDebouncedValue(value, delayMs),
      { initialProps: { value: 'first', delayMs: 500 } },
    );

    expect(result.current).toBe('first');

    rerender({ value: 'second', delayMs: 500 });

    expect(result.current).toBe('first');

    act(() => {
      vi.advanceTimersByTime(499);
    });

    expect(result.current).toBe('first');

    act(() => {
      vi.advanceTimersByTime(1);
    });

    expect(result.current).toBe('second');
  });

  it('cancels the previous pending update when the value changes again', () => {
    vi.useFakeTimers();

    const { result, rerender } = renderHook(
      ({ value }) => useDebouncedValue(value, 300),
      { initialProps: { value: 'initial' } },
    );

    rerender({ value: 'first update' });

    act(() => {
      vi.advanceTimersByTime(200);
    });

    rerender({ value: 'final update' });

    act(() => {
      vi.advanceTimersByTime(299);
    });

    expect(result.current).toBe('initial');

    act(() => {
      vi.advanceTimersByTime(1);
    });

    expect(result.current).toBe('final update');
  });
});
