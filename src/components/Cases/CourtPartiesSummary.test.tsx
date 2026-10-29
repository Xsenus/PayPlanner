import { fireEvent, render, screen, within } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import type { ClientCaseCourtParty } from '../../types';
import { CourtPartiesSummary } from './CourtPartiesSummary';

const party = (id: number, overrides: Partial<ClientCaseCourtParty> = {}): ClientCaseCourtParty => ({
  id, clientCaseId: 15, displayName: `Участник ${id}`, side: 'respondent', sideLabel: 'Ответчик',
  processRole: 'Ответчик', isArchived: false, createdAt: '2026-10-28T09:00:00Z', ...overrides,
});

describe('court parties overview', () => {
  it('shows the seventh participant without entering edit mode or retrying the read', () => {
    const onRetry = vi.fn();
    render(<CourtPartiesSummary parties={Array.from({ length: 7 }, (_, index) => party(index + 1))} loading={false} error={null} onRetry={onRetry} />);
    expect(screen.queryByText('Участник 7')).toBeNull();
    expect(screen.getByText('Показано 6 из 7')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё 1' }));
    expect(screen.getByText('Участник 7')).toBeTruthy();
    expect(screen.getByText('Показано 7 из 7')).toBeTruthy();
    expect(onRetry).not.toHaveBeenCalled();
  });

  it('reveals a long list in bounded pages and can collapse it again', () => {
    const parties = Array.from({ length: 37 }, (_, index) => party(index + 1));
    render(<CourtPartiesSummary parties={parties} loading={false} error={null} onRetry={vi.fn()} />);
    const list = screen.getByRole('list', { name: 'Список участников процесса' });
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё 20' }));
    expect(within(list).getAllByRole('listitem')).toHaveLength(26);
    fireEvent.click(screen.getByRole('button', { name: 'Показать ещё 11' }));
    expect(within(list).getAllByRole('listitem')).toHaveLength(37);
    for (const item of parties) expect(within(list).getByText(item.displayName)).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Показать ещё/ })).toBeNull();
    fireEvent.click(screen.getByRole('button', { name: 'Свернуть список' }));
    expect(within(list).getAllByRole('listitem')).toHaveLength(6);
  });

  it('excludes archived entries from both the count and visible list', () => {
    render(<CourtPartiesSummary parties={[party(1), party(2, { isArchived: true })]} loading={false} error={null} onRetry={vi.fn()} />);
    expect(screen.getByText('Участник 1')).toBeTruthy();
    expect(screen.queryByText('Участник 2')).toBeNull();
    expect(within(screen.getByRole('region', { name: 'Участники процесса' })).getByText('1')).toBeTruthy();
    expect(screen.queryByRole('button', { name: /Показать ещё/ })).toBeNull();
  });

  it('keeps old names and an empty-state conclusion hidden while a fresh read is loading', () => {
    render(<CourtPartiesSummary parties={[party(1, { displayName: 'Прежняя сторона' })]} loading error={null} onRetry={vi.fn()} />);
    expect(screen.getByRole('status').textContent).toContain('Загружаем участников процесса');
    expect(screen.queryByText('Прежняя сторона')).toBeNull();
    expect(screen.queryByText('Активные участники процесса не добавлены.')).toBeNull();
  });

  it('shows a failed read with a retry and does not call it an empty list', () => {
    const onRetry = vi.fn();
    render(<CourtPartiesSummary parties={[party(1)]} loading={false} error="Не удалось загрузить участников процесса." onRetry={onRetry} />);
    expect(screen.getByRole('alert').textContent).toContain('Не удалось загрузить участников процесса.');
    expect(screen.queryByText('Участник 1')).toBeNull();
    expect(screen.queryByText('Активные участники процесса не добавлены.')).toBeNull();
    expect(screen.getByText('Недоступно')).toBeTruthy();
    fireEvent.click(screen.getByRole('button', { name: 'Повторить загрузку участников' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('shows a real empty state after a successful read, including archived-only data', () => {
    render(<CourtPartiesSummary parties={[party(1, { isArchived: true })]} loading={false} error={null} onRetry={vi.fn()} />);
    expect(screen.getByText('Активные участники процесса не добавлены.')).toBeTruthy();
    expect(screen.queryByRole('alert')).toBeNull();
    expect(screen.queryByRole('status')).toBeNull();
  });
});
