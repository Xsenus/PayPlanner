import { fireEvent, render, screen } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import Navigation from './Navigation';
import { getTabHref } from '../../appRouting';

vi.mock('../../contexts/AuthContext', () => ({
  useAuth: () => ({
    user: { id: 1, role: { id: 1, name: 'admin' } },
    isAdmin: () => true,
  }),
}));

const expandedSections = {
  tasks: true,
  contacts: true,
  accounts: true,
  money: true,
  payments: true,
  acts: true,
};

function renderNavigation(onTabChange = vi.fn()) {
  window.localStorage.setItem('pp.sidebar_expanded', JSON.stringify(expandedSections));
  render(<Navigation activeTab="calendar" onTabChange={onTabChange} />);
  return onTabChange;
}

function findLinkByHref(tabHref: string) {
  const normalizedHref = new URL(tabHref, window.location.origin).href;
  return screen
    .getAllByRole('link')
    .find((link) => link.getAttribute('href') === tabHref || (link as HTMLAnchorElement).href === normalizedHref);
}

describe('Navigation links', () => {
  beforeEach(() => {
    window.history.replaceState({}, '', '/');
    window.localStorage.clear();
  });

  it('renders navigation destinations as real links', () => {
    renderNavigation();

    expect(findLinkByHref(getTabHref('calendar'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('tasksApprovals'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('tasksCalendar'))).toBeTruthy();
    expect(screen.getByRole('link', { name: /Клиенты/i }).getAttribute('href')).toBe(getTabHref('clients'));
    expect(findLinkByHref(getTabHref('accountsIncome'))).toBeTruthy();
    expect(findLinkByHref(getTabHref('moneyEMoney'))).toBeTruthy();
  });

  it('opens the task calendar submenu inside the single page app', () => {
    const onTabChange = renderNavigation();
    const link = findLinkByHref(getTabHref('tasksCalendar'));

    expect(link).toBeTruthy();
    fireEvent.click(link!);

    expect(onTabChange).toHaveBeenCalledWith('tasksCalendar');
  });

  it('opens the approvals submenu inside the single page app', () => {
    const onTabChange = renderNavigation();
    const link = findLinkByHref(getTabHref('tasksApprovals'));

    expect(link).toBeTruthy();
    fireEvent.click(link!);

    expect(onTabChange).toHaveBeenCalledWith('tasksApprovals');
  });

  it('keeps normal left clicks inside the single page app', () => {
    const onTabChange = renderNavigation();
    const link = screen.getByRole('link', { name: /Клиенты/i });

    const clickEvent = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
    });

    link.dispatchEvent(clickEvent);

    expect(clickEvent.defaultPrevented).toBe(true);
    expect(onTabChange).toHaveBeenCalledWith('clients');
  });

  it('allows browser tab gestures on navigation links', () => {
    const onTabChange = renderNavigation();
    const link = findLinkByHref(getTabHref('moneyEMoney'));

    expect(link).toBeTruthy();
    link!.removeAttribute('href');

    const ctrlClickEvent = new MouseEvent('click', {
      bubbles: true,
      cancelable: true,
      button: 0,
      ctrlKey: true,
    });

    link!.dispatchEvent(ctrlClickEvent);

    expect(ctrlClickEvent.defaultPrevented).toBe(false);
    expect(onTabChange).not.toHaveBeenCalled();

    fireEvent.click(link!, { button: 1 });

    expect(onTabChange).not.toHaveBeenCalled();
  });
});
