import { render, screen } from '@testing-library/react';
import { afterEach, describe, expect, it, vi } from 'vitest';

import { RouteErrorBoundary } from './RouteErrorBoundary';

function FailedSection(): never {
  throw new Error('Failed to fetch dynamically imported module: /assets/old.js');
}

describe('RouteErrorBoundary', () => {
  afterEach(() => {
    vi.restoreAllMocks();
    window.sessionStorage.removeItem('pp.chunk-load-retry-url');
  });

  it('keeps a chunk-load failure visible until the user chooses to refresh', () => {
    vi.spyOn(console, 'error').mockImplementation(() => undefined);
    const suppressExpectedRenderError = (event: ErrorEvent) => event.preventDefault();
    window.addEventListener('error', suppressExpectedRenderError);

    try {
      render(
        <RouteErrorBoundary resetKey="task-journal" onGoHome={vi.fn()}>
          <FailedSection />
        </RouteErrorBoundary>,
      );
    } finally {
      window.removeEventListener('error', suppressExpectedRenderError);
    }

    expect(screen.getByText('Раздел обновился, нужно перезагрузить страницу')).toBeTruthy();
    expect(screen.getByText(/Сначала сохраните несохранённые данные/u)).toBeTruthy();
    expect(screen.getByRole('button', { name: 'Обновить страницу' })).toBeTruthy();
    expect(window.sessionStorage.getItem('pp.chunk-load-retry-url')).toBeNull();
  });
});
