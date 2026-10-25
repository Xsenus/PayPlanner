import { afterEach, vi } from 'vitest';
import { cleanup } from '@testing-library/react';

const hasDom = typeof window !== 'undefined' && typeof document !== 'undefined';

afterEach(() => {
  if (!hasDom) return;

  cleanup();
  vi.mocked(window.open).mockClear();
});

function createMockWindow(): Window {
  let href = '';
  const mockWindow = {
    closed: false,
    document: {
      open: vi.fn(),
      write: vi.fn(),
      close: vi.fn(),
    },
    focus: vi.fn(),
    print: vi.fn(),
    close: vi.fn(),
    onload: null,
    location: {
      get href() {
        return href;
      },
      set href(value: string) {
        href = value;
      },
    },
  };

  return mockWindow as unknown as Window;
}

if (hasDom) {
  Object.defineProperty(window, 'open', {
    writable: true,
    configurable: true,
    value: vi.fn(() => createMockWindow()),
  });

  document.addEventListener(
    'click',
    (event) => {
      const target = event.target;
      if (!(target instanceof Element)) return;

      const anchor = target.closest('a');
      if (!(anchor instanceof HTMLAnchorElement)) return;

      if (anchor.hasAttribute('download') || anchor.href.startsWith('blob:')) {
        event.preventDefault();
      }
    },
    true,
  );

  const originalAnchorClick = HTMLAnchorElement.prototype.click;
  Object.defineProperty(HTMLAnchorElement.prototype, 'click', {
    configurable: true,
    value(this: HTMLAnchorElement) {
      if (this.hasAttribute('download') || this.href.startsWith('blob:')) {
        return;
      }

      return originalAnchorClick.call(this);
    },
  });

  if (!('matchMedia' in window)) {
    Object.defineProperty(window, 'matchMedia', {
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        onchange: null,
        addListener: vi.fn(),
        removeListener: vi.fn(),
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
        dispatchEvent: vi.fn(),
      })),
    });
  }

  if (!('createObjectURL' in URL)) {
    Object.defineProperty(URL, 'createObjectURL', {
      writable: true,
      value: vi.fn(() => 'blob:test'),
    });
  }

  if (!('revokeObjectURL' in URL)) {
    Object.defineProperty(URL, 'revokeObjectURL', {
      writable: true,
      value: vi.fn(),
    });
  }
}
