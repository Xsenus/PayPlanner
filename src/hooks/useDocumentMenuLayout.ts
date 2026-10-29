import { useCallback, useLayoutEffect, useRef } from 'react';

// Keep grouped print menus in their existing focus/DOM scope; use SystemSelect's viewport bounds pattern.
function fit(menu: HTMLDivElement | null, widthRem: number) {
  if (!menu?.isConnected || !menu.parentElement) return;
  const gap = 12;
  const anchor = menu.parentElement.getBoundingClientRect();
  let leftEdge = gap;
  let rightEdge = window.innerWidth - gap;
  let topEdge = gap;
  let bottomEdge = window.innerHeight - gap;
  // Page/modal cards may clip their descendants even when the menu fits the viewport.
  for (let parent: HTMLElement | null = menu.parentElement; parent; parent = parent.parentElement) {
    const style = getComputedStyle(parent);
    const rect = parent.getBoundingClientRect();
    if (['hidden', 'auto', 'scroll', 'clip'].includes(style.overflowX)) {
      leftEdge = Math.max(leftEdge, rect.left + parseFloat(style.borderLeftWidth || '0') + 4);
      rightEdge = Math.min(rightEdge, rect.right - parseFloat(style.borderRightWidth || '0') - 4);
    }
    if (['hidden', 'auto', 'scroll', 'clip'].includes(style.overflowY)) {
      topEdge = Math.max(topEdge, rect.top + parseFloat(style.borderTopWidth || '0') + 4);
      bottomEdge = Math.min(bottomEdge, rect.bottom - parseFloat(style.borderBottomWidth || '0') - 4);
    }
  }
  const rem = parseFloat(getComputedStyle(document.documentElement).fontSize) || 16;
  const width = Math.max(1, Math.min(widthRem * rem, rightEdge - leftEdge));
  const left = Math.min(Math.max(anchor.left, leftEdge), Math.max(leftEdge, rightEdge - width));
  const above = Math.max(0, anchor.top - topEdge - 8);
  const below = Math.max(0, bottomEdge - anchor.bottom - 8);
  const openAbove = above >= below;
  Object.assign(menu.style, {
    left: `${left - anchor.left}px`, width: `${width}px`, marginBottom: '0px',
    top: openAbove ? 'auto' : 'calc(100% + 8px)', bottom: openAbove ? 'calc(100% + 8px)' : 'auto',
    maxHeight: `${Math.max(32, Math.min(window.innerHeight * 0.7, openAbove ? above : below))}px`,
  });
}

export function useDocumentMenuLayout(open: boolean, widthRem: number) {
  const menu = useRef<HTMLDivElement | null>(null);
  const attach = useCallback((node: HTMLDivElement | null) => {
    menu.current = node;
    fit(node, widthRem);
  }, [widthRem]);
  useLayoutEffect(() => {
    if (!open) return;
    let frame = 0;
    const schedule = (event: Event) => {
      if (event.target instanceof Node && menu.current?.contains(event.target)) return;
      if (!frame) frame = window.requestAnimationFrame(() => {
        frame = 0;
        fit(menu.current, widthRem);
      });
    };
    fit(menu.current, widthRem);
    window.addEventListener('resize', schedule);
    window.addEventListener('scroll', schedule, true);
    return () => {
      window.removeEventListener('resize', schedule);
      window.removeEventListener('scroll', schedule, true);
      window.cancelAnimationFrame(frame);
    };
  }, [open, widthRem]);
  return attach;
}
