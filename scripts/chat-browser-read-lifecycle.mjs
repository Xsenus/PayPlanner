// This function is also serialized into the owned Playwright CLI session.
// Keep it self-contained; request failures remain visible and are never ignored.
export function createChatReadLifecycle(page, isOwnedRead, timeoutMs) {
  const pending = new Set();
  const localAborts = new Set();
  const waiters = new Set();
  let started = 0; let finished = 0; let failed = 0; let locallyAborted = 0; let disposed = false;
  const begin = request => {
    if (isOwnedRead(request) && !pending.has(request)) { pending.add(request); started++; }
  };
  const settle = (request, successful) => {
    if (!pending.delete(request)) return;
    const expected = localAborts.delete(request);
    if (successful) finished++; else if (expected) locallyAborted++; else failed++;
    if (pending.size === 0) for (const waiter of [...waiters]) waiter.resolve();
  };
  const finish = request => settle(request, true);
  const fail = request => settle(request, false);
  page.on('request', begin);
  page.on('requestfinished', finish);
  page.on('requestfailed', fail);
  return {
    snapshot: () => ({ started, finished, failed, locallyAborted, pending: pending.size }),
    describe: () => `started=${started} finished=${finished} failed=${failed} locallyAborted=${locallyAborted} pending=${pending.size}`,
    expectLocalAbort: request => {
      if (disposed || !pending.has(request) || !isOwnedRead(request)) throw new Error('Only the exact owned read can receive an injected local abort.');
      // Register at the actual route.abort boundary, not from mutable flags at
      // request start: the route handler may run after those flags change.
      localAborts.add(request);
    },
    drain: () => {
      if (disposed) return Promise.reject(new Error('Owned chat read observer is closed.'));
      if (pending.size === 0) return Promise.resolve();
      return new Promise((resolve, reject) => {
        let timer;
        const complete = action => { clearTimeout(timer); waiters.delete(waiter); action(); };
        const waiter = {
          resolve: () => complete(resolve),
          reject: error => complete(() => reject(error)),
        };
        waiters.add(waiter);
        timer = setTimeout(() => waiter.reject(new Error('Owned chat reads did not finish before the existing browser deadline.')), timeoutMs);
      });
    },
    dispose: () => {
      disposed = true;
      page.off('request', begin);
      page.off('requestfinished', finish);
      page.off('requestfailed', fail);
      for (const waiter of [...waiters]) waiter.reject(new Error('Owned chat read observer closed with unfinished reads.'));
    },
  };
}

// Hard reload and browser close end every API read, including the notification
// inbox. Wait for their response bodies as well as the chat's own status reads.
// This helper is serialized into the same minimal Playwright CLI session.
export async function drainChatNavigationReads(chatReads, apiReads) {
  await Promise.all([chatReads.drain(), apiReads.drain()]);
  if (chatReads.snapshot().failed !== 0) throw new Error('An owned chat read failed before navigation.');
  if (apiReads.snapshot().failed !== 0) throw new Error('An owned API read failed before navigation.');
}

// Serialized into Playwright CLI's minimal VM, which has Date but no performance.
export function createChatPostRecorder(reads, timings) {
  let startedAt;
  return phase => {
    const now = Date.now();
    if (phase === 'start') startedAt = now;
    const state = reads.snapshot();
    timings.push('PP_CHAT_BROWSER_POST phase=' + phase
      + ' elapsedMs=' + Math.max(0, now - startedAt) + ' apiReadsStarted=' + state.started
      + ' apiReadsFinished=' + state.finished + ' apiReadsFailed=' + state.failed + ' apiReadsPending=' + state.pending);
  };
}

// Playwright may append request headers to a route.fetch failure. Keep the
// useful error and deadline while removing authentication material before output.
export function redactChatBrowserDiagnostic(value, secrets = []) {
  let safe = String(value)
    .replace(/^([ \t]*(?:-\s*)?)["']?(?:authorization|proxy-authorization|cookie|set-cookie)["']?\s*:[^\r\n]*/gim, '$1<redacted-header>')
    .replace(/\bBearer\s+[^\s"',;]+/gi, 'Bearer <redacted>')
    .replace(/\beyJ[A-Za-z0-9_-]*\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+\b/g, '<redacted-jwt>');
  for (const secret of secrets) if (typeof secret === 'string' && secret.length > 0)
    safe = safe.replaceAll(secret, '<redacted>');
  return safe;
}
