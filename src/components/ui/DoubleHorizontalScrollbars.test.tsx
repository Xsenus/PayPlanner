import { render, waitFor } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { DoubleHorizontalScrollbars } from './DoubleHorizontalScrollbars';

let targetClientWidth = 500;
let targetScrollWidth = 1200;
const resizeObserverCallbacks: ResizeObserverCallback[] = [];
const originalClientWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'clientWidth');
const originalScrollWidth = Object.getOwnPropertyDescriptor(HTMLElement.prototype, 'scrollWidth');
const originalInnerWidth = Object.getOwnPropertyDescriptor(window, 'innerWidth');
const originalResizeObserver = window.ResizeObserver;

class MockResizeObserver implements ResizeObserver {
  private readonly callback: ResizeObserverCallback;

  constructor(callback: ResizeObserverCallback) {
    this.callback = callback;
    resizeObserverCallbacks.push(callback);
  }

  observe = vi.fn();
  unobserve = vi.fn();
  disconnect = vi.fn();
  takeRecords = vi.fn(() => []);
}

function dispatchResizeObserver() {
  for (const callback of resizeObserverCallbacks) {
    callback([], {} as ResizeObserver);
  }
}

function getScrollContainer() {
  return document.querySelector('[data-testid="wide-table-scroll"]') as HTMLElement;
}

describe('DoubleHorizontalScrollbars', () => {
  beforeEach(() => {
    targetClientWidth = 500;
    targetScrollWidth = 1200;
    resizeObserverCallbacks.length = 0;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1200 });
    Object.defineProperty(HTMLElement.prototype, 'clientWidth', {
      configurable: true,
      get() {
        return (this as HTMLElement).dataset.testScrollTarget === '1' ? targetClientWidth : 0;
      },
    });
    Object.defineProperty(HTMLElement.prototype, 'scrollWidth', {
      configurable: true,
      get() {
        return (this as HTMLElement).dataset.testScrollTarget === '1' ? targetScrollWidth : 0;
      },
    });
    window.ResizeObserver = MockResizeObserver;
  });

  afterEach(() => {
    document.body.innerHTML = '';
    vi.restoreAllMocks();
    if (originalClientWidth) {
      Object.defineProperty(HTMLElement.prototype, 'clientWidth', originalClientWidth);
    }
    if (originalScrollWidth) {
      Object.defineProperty(HTMLElement.prototype, 'scrollWidth', originalScrollWidth);
    }
    if (originalInnerWidth) {
      Object.defineProperty(window, 'innerWidth', originalInnerWidth);
    }
    window.ResizeObserver = originalResizeObserver;
  });

  it('resets stale table scroll when viewport scaling changes the available width', async () => {
    render(
      <>
        <DoubleHorizontalScrollbars />
        <div
          data-testid="wide-table-scroll"
          data-test-scroll-target="1"
          className="overflow-x-auto"
          style={{ overflowX: 'auto' }}
        >
          <table>
            <tbody>
              <tr>
                <td>Wide content</td>
              </tr>
            </tbody>
          </table>
        </div>
      </>,
    );

    await waitFor(() => expect(document.querySelector('[data-pp-top-scrollbar]')).toBeTruthy());

    const scrollContainer = getScrollContainer();
    scrollContainer.scrollLeft = 420;
    targetClientWidth = 760;
    Object.defineProperty(window, 'innerWidth', { configurable: true, value: 1366 });

    window.dispatchEvent(new Event('resize'));
    dispatchResizeObserver();

    expect(scrollContainer.scrollLeft).toBe(0);
  });

  it('clamps stale table scroll when content becomes narrower than the old scroll position', async () => {
    render(
      <>
        <DoubleHorizontalScrollbars />
        <div
          data-testid="wide-table-scroll"
          data-test-scroll-target="1"
          className="overflow-x-auto"
          style={{ overflowX: 'auto' }}
        >
          <table>
            <tbody>
              <tr>
                <td>Wide content</td>
              </tr>
            </tbody>
          </table>
        </div>
      </>,
    );

    await waitFor(() => expect(document.querySelector('[data-pp-top-scrollbar]')).toBeTruthy());

    const scrollContainer = getScrollContainer();
    scrollContainer.scrollLeft = 900;
    targetScrollWidth = 900;

    window.dispatchEvent(new Event('scroll'));

    expect(scrollContainer.scrollLeft).toBe(400);
  });
});
