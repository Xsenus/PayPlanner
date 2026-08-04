import { describe, expect, it } from 'vitest';
import { isKnownSignalRAnnotationWarning } from './vite.config';

describe('isKnownSignalRAnnotationWarning', () => {
  it.each(['Rollup', 'Rolldown'])(
    'recognizes the SignalR pure annotation warning emitted by %s',
    (bundler) => {
      expect(isKnownSignalRAnnotationWarning({
        code: 'INVALID_ANNOTATION',
        id: 'C:\\repo\\node_modules\\@microsoft\\signalr\\dist\\esm\\Utils.js',
        message: `A comment contains an annotation that ${bundler} cannot interpret due to the position of the comment.`,
      })).toBe(true);
    },
  );

  it('does not hide annotation warnings from application code', () => {
    expect(isKnownSignalRAnnotationWarning({
      code: 'INVALID_ANNOTATION',
      id: 'C:/repo/src/App.tsx',
      message: 'A comment contains an annotation that Rolldown cannot interpret.',
    })).toBe(false);
  });
});
