import { useEffect } from 'react';

type EnhancedScroll = {
  topScroll: HTMLDivElement;
  spacer: HTMLDivElement;
  cleanup: () => void;
  update: (resetStaleScroll?: boolean) => void;
};

const TOP_SCROLL_ATTR = 'data-pp-top-scrollbar';
const ENHANCED_ATTR = 'data-pp-double-scroll-enhanced';

function getViewportWidth() {
  return window.visualViewport?.width ?? window.innerWidth;
}

function shouldEnhance(element: HTMLElement) {
  if (element.dataset.ppDoubleScrollEnhanced === '1') return false;
  if (element.closest(`[${TOP_SCROLL_ATTR}]`)) return false;
  if (!element.querySelector('table')) return false;

  const style = window.getComputedStyle(element);
  return style.overflowX === 'auto' || style.overflowX === 'scroll';
}

function createTopScroll(scrollElement: HTMLElement): EnhancedScroll {
  const topScroll = document.createElement('div');
  const spacer = document.createElement('div');
  let syncing = false;
  let lastViewportWidth = getViewportWidth();
  let lastClientWidth = scrollElement.clientWidth;

  topScroll.setAttribute(TOP_SCROLL_ATTR, '1');
  topScroll.setAttribute('aria-label', 'Горизонтальная прокрутка таблицы');
  topScroll.tabIndex = 0;
  topScroll.className = 'pp-top-horizontal-scroll';
  topScroll.style.overflowX = 'auto';
  topScroll.style.overflowY = 'hidden';
  topScroll.style.height = '14px';
  topScroll.style.border = '1px solid';
  topScroll.style.borderRadius = '6px';
  topScroll.style.scrollbarWidth = 'thin';
  topScroll.style.position = 'fixed';
  topScroll.style.top = '66px';
  topScroll.style.zIndex = '10';

  spacer.style.height = '1px';
  spacer.style.pointerEvents = 'none';
  topScroll.appendChild(spacer);

  const update = (resetStaleScroll = false) => {
    const viewportWidth = getViewportWidth();
    const clientWidth = scrollElement.clientWidth;
    const viewportChanged = Math.abs(viewportWidth - lastViewportWidth) > 0.5;
    const clientWidthChanged = Math.abs(clientWidth - lastClientWidth) > 0.5;
    const maxScrollLeft = Math.max(0, scrollElement.scrollWidth - clientWidth);

    if (resetStaleScroll && (viewportChanged || clientWidthChanged)) {
      scrollElement.scrollLeft = 0;
    } else if (scrollElement.scrollLeft > maxScrollLeft) {
      scrollElement.scrollLeft = maxScrollLeft;
    }

    lastViewportWidth = viewportWidth;
    lastClientWidth = clientWidth;

    const hasOverflow = scrollElement.scrollWidth > scrollElement.clientWidth + 1;
    const rect = scrollElement.getBoundingClientRect();
    const stickyTop = 66;
    const tableTopHasPassed = rect.top < stickyTop;
    const tableStillVisible = rect.bottom > stickyTop + 48;
    const shouldShow = hasOverflow && tableTopHasPassed && tableStillVisible;
    topScroll.style.display = shouldShow ? 'block' : 'none';
    topScroll.style.left = `${Math.max(0, rect.left)}px`;
    topScroll.style.width = `${Math.max(0, Math.min(rect.width, window.innerWidth - Math.max(0, rect.left) - 12))}px`;
    spacer.style.width = `${scrollElement.scrollWidth}px`;
    topScroll.scrollLeft = scrollElement.scrollLeft;
  };

  const handleViewportResize = () => update(true);
  const handleWindowScroll = () => update(false);

  const syncFromTop = () => {
    if (syncing) return;
    syncing = true;
    scrollElement.scrollLeft = topScroll.scrollLeft;
    window.requestAnimationFrame(() => {
      syncing = false;
    });
  };

  const syncFromBottom = () => {
    if (syncing) return;
    syncing = true;
    topScroll.scrollLeft = scrollElement.scrollLeft;
    window.requestAnimationFrame(() => {
      syncing = false;
    });
  };

  topScroll.addEventListener('scroll', syncFromTop, { passive: true });
  scrollElement.addEventListener('scroll', syncFromBottom, { passive: true });

  const resizeObserver = new ResizeObserver(() => update(true));
  resizeObserver.observe(scrollElement);
  const table = scrollElement.querySelector('table');
  if (table) {
    resizeObserver.observe(table);
  }
  window.addEventListener('resize', handleViewportResize);
  window.addEventListener('scroll', handleWindowScroll, { passive: true });
  window.visualViewport?.addEventListener('resize', handleViewportResize);

  document.body.appendChild(topScroll);
  scrollElement.setAttribute(ENHANCED_ATTR, '1');
  update();

  return {
    topScroll,
    spacer,
    update,
    cleanup: () => {
      topScroll.removeEventListener('scroll', syncFromTop);
      scrollElement.removeEventListener('scroll', syncFromBottom);
      window.removeEventListener('resize', handleViewportResize);
      window.removeEventListener('scroll', handleWindowScroll);
      window.visualViewport?.removeEventListener('resize', handleViewportResize);
      resizeObserver.disconnect();
      scrollElement.removeAttribute(ENHANCED_ATTR);
      topScroll.remove();
    },
  };
}

export function DoubleHorizontalScrollbars() {
  useEffect(() => {
    const enhanced = new Map<HTMLElement, EnhancedScroll>();

    const scan = () => {
      for (const [element, instance] of enhanced) {
        if (!document.body.contains(element)) {
          instance.cleanup();
          enhanced.delete(element);
          continue;
        }
        instance.update();
      }

      document.querySelectorAll<HTMLElement>('.overflow-x-auto').forEach((element) => {
        if (!shouldEnhance(element)) return;
        enhanced.set(element, createTopScroll(element));
      });
    };

    const observer = new MutationObserver(() => {
      window.requestAnimationFrame(scan);
    });

    observer.observe(document.body, {
      childList: true,
      subtree: true,
    });

    scan();

    return () => {
      observer.disconnect();
      enhanced.forEach((instance) => instance.cleanup());
      enhanced.clear();
    };
  }, []);

  return null;
}
