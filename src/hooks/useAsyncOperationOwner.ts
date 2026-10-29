import { useCallback, useEffect, useRef } from 'react';

// An async result may update only the screen lifetime and scope that started it.
export function useAsyncOperationOwner(scope: string) {
  const owner = useRef({ scope, epoch: 0 });
  if (owner.current.scope !== scope) {
    owner.current.scope = scope;
    ++owner.current.epoch;
  }
  useEffect(() => {
    const lifetime = owner.current;
    return () => { ++lifetime.epoch; };
  }, []);
  const invalidate = useCallback(() => { ++owner.current.epoch; }, []);
  const capture = useCallback(() => owner.current.epoch, []);
  const isCurrent = useCallback((epoch: number) => owner.current.epoch === epoch, []);
  return { invalidate, capture, isCurrent };
}
