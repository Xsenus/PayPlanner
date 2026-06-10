import { useEffect, type KeyboardEvent, type RefObject } from 'react';

const focusableSelector = [
  'a[href]',
  'button:not([disabled])',
  'input:not([disabled])',
  'select:not([disabled])',
  'textarea:not([disabled])',
  '[tabindex]:not([tabindex="-1"])',
].join(',');

function getFocusableElements(container: HTMLElement): HTMLElement[] {
  return Array.from(container.querySelectorAll<HTMLElement>(focusableSelector)).filter(
    (element) => !element.hasAttribute('disabled') && element.getAttribute('aria-hidden') !== 'true',
  );
}

export function useModalFocusTrap(
  containerRef: RefObject<HTMLElement | null>,
  open: boolean,
  onEscape: () => void,
  initialFocusRef?: RefObject<HTMLElement | null>,
) {
  useEffect(() => {
    if (!open || typeof document === 'undefined') return;

    const previousFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    const container = containerRef.current;
    if (!container) return;

    const firstTarget = initialFocusRef?.current ?? getFocusableElements(container)[0] ?? container;
    firstTarget.focus();

    return () => {
      previousFocus?.focus();
    };
  }, [containerRef, initialFocusRef, open]);

  return (event: KeyboardEvent<HTMLElement>) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      onEscape();
      return;
    }

    if (event.key !== 'Tab') return;

    const container = containerRef.current;
    if (!container) return;

    const focusableElements = getFocusableElements(container);
    if (focusableElements.length === 0) {
      event.preventDefault();
      container.focus();
      return;
    }

    const firstElement = focusableElements[0];
    const lastElement = focusableElements[focusableElements.length - 1];
    const activeElement = document.activeElement;

    if (event.shiftKey && activeElement === firstElement) {
      event.preventDefault();
      lastElement.focus();
      return;
    }

    if (!event.shiftKey && activeElement === lastElement) {
      event.preventDefault();
      firstElement.focus();
      return;
    }

    if (!container.contains(activeElement)) {
      event.preventDefault();
      firstElement.focus();
    }
  };
}
