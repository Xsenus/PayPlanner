import { describe, expect, it } from 'vitest';
import { isTaskJournalActive, type TaskJournalStatus } from './taskJournalStore';

describe('task lifecycle visibility', () => {
  it.each([
    ['done', false], ['cancelled', false], [' CANCELLED ', false], [' Done ', false],
    ['new', true], ['inProgress', true], ['waiting', true], ['onApproval', true],
    ['unknown', true], ['', true],
  ])('preserves active tasks and recognises closed status %s', (status, active) => {
    expect(isTaskJournalActive(status as TaskJournalStatus)).toBe(active);
  });
});
