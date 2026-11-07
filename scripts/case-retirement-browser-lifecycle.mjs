// Serialized into Playwright CLI's minimal VM. Keep all state and dependencies
// inside the function; this observes actual requests without replacing them.
export async function waitForCaseRestartResolution(unresolved, succeeded, waitAtPhase, retired, operation) {
  if (operation !== undefined) {
    if (operation?.attempt !== 1 || !['fulfilled', 'rejected'].includes(operation.phase)
      || (operation.phase === 'fulfilled' && (operation.ownedTerminal !== true
        || !['retired', 'uploaded'].includes(operation.receiptStatus))))
      throw new Error('The real owned retirement operation has no settled terminal result.');
    // Reconnection can replace a notice after the real API promise has already
    // acknowledged the receipt. A durable row alone cannot choose this branch.
    const accepted = operation.phase === 'fulfilled';
    const result = accepted ? (retired && operation.receiptStatus === 'retired' ? succeeded.or(retired).first() : succeeded)
      : retired ? unresolved.or(retired).first() : unresolved;
    await waitAtPhase(result, 'restart-resolution');
    return !accepted;
  }
  // The restart acknowledgement precedes the browser's fallback proof read.
  // Decide only after the UI has actually resolved that read, under its existing deadline.
  // A reconnect refresh can replace a transient notice with the durable retired
  // receipt. It still requires explicit confirmation; it is not an acknowledged release.
  const retained = retired ? unresolved.or(retired) : unresolved;
  const outcome = retired ? retained.or(succeeded).first() : retained.or(succeeded);
  await waitAtPhase(outcome, 'restart-resolution');
  // A successful notice takes precedence over the durable row that may remain.
  return retired ? !await succeeded.isVisible() && (await unresolved.isVisible() || await retired.isVisible()) : unresolved.isVisible();
}

// Observe the real API promise without changing its receiver, arguments, identity,
// result or rejection. No request, token, error text or receipt payload is stored.
export function observeCaseRetirementOperation(service, caseId, uploadKey) {
  const descriptor = Object.getOwnPropertyDescriptor(service, 'retireCaseDocumentUpload');
  const original = service.retireCaseDocumentUpload;
  let current = { attempt: 0, phase: 'idle', ownedTerminal: false, receiptStatus: 'unknown' };
  const wrapped = function (...args) {
    const matching = args[0] === caseId && typeof args[1] === 'string' && args[1].toLowerCase() === uploadKey.toLowerCase();
    const observation = matching ? { attempt: Math.min(current.attempt + 1, 65), phase: 'pending', ownedTerminal: false, receiptStatus: 'unknown' } : null;
    if (observation) current = observation;
    let result;
    try { result = original.apply(this, args); }
    catch (error) { if (observation) observation.phase = 'rejected'; throw error; }
    if (observation) void Promise.resolve(result).then(receipt => {
      observation.phase = 'fulfilled';
      try {
        const own = typeof receipt?.uploadKey === 'string' && receipt.uploadKey.toLowerCase() === uploadKey.toLowerCase();
        observation.receiptStatus = ['retired', 'uploaded'].includes(receipt?.status) ? receipt.status : 'unknown';
        observation.ownedTerminal = own && (receipt.status === 'retired' || receipt.status === 'uploaded'
          && Number.isSafeInteger(receipt.documentId) && receipt.documentId > 0
          && Number.isSafeInteger(receipt.version) && receipt.version > 0
          && typeof receipt.sha256 === 'string' && /^[a-f0-9]{64}$/i.test(receipt.sha256)
          && typeof receipt.documentKey === 'string' && receipt.documentKey.length > 0);
      } catch { observation.ownedTerminal = false; observation.receiptStatus = 'unknown'; }
    }, () => { observation.phase = 'rejected'; });
    return result;
  };
  Object.defineProperty(service, 'retireCaseDocumentUpload', descriptor
    ? { ...descriptor, value: wrapped } : { configurable: true, writable: true, enumerable: false, value: wrapped });
  return { snapshot: () => ({ ...current }), restore: () => {
    if (service.retireCaseDocumentUpload !== wrapped) return;
    if (descriptor) Object.defineProperty(service, 'retireCaseDocumentUpload', descriptor);
    else delete service.retireCaseDocumentUpload;
  } };
}

// Observe the real response without replacing transport or retaining its payload.
// Only bounded metadata for this exact owned key may enter failure diagnostics.
export function createCaseRetirementProofTrace(page, apiOrigin, caseId, uploadKey) {
  const records = [];
  const started = Date.now();
  const elapsed = () => Math.max(0, Math.min(3_600_000, Date.now() - started));
  const observe = response => {
    const request = response.request();
    const url = new URL(request.url());
    const keys = url.searchParams.getAll('uploadKeys');
    if (records.length >= 64 || request.method() !== 'GET' || url.origin !== apiOrigin
      || url.pathname !== '/api/v2/cases/' + caseId + '/documents/uploads'
      || keys.length !== 1 || keys[0].toLowerCase() !== uploadKey.toLowerCase()) return;
    const status = response.status();
    const record = { status: Number.isInteger(status) && status >= 100 && status <= 599 ? status : 0,
      responseAtMs: elapsed(), bodyAtMs: null, body: 'pending', count: null, exactRetired: false };
    records.push(record);
    void Promise.resolve().then(() => response.json()).then(payload => {
      record.bodyAtMs = elapsed();
      record.body = Array.isArray(payload) ? 'array' : 'other';
      record.count = Array.isArray(payload) ? Math.min(payload.length, 65) : null;
      record.exactRetired = status === 200 && Array.isArray(payload) && payload.length === 1
        && typeof payload[0]?.uploadKey === 'string' && payload[0].uploadKey.toLowerCase() === uploadKey.toLowerCase()
        && payload[0].status === 'retired';
    }).catch(() => { record.bodyAtMs = elapsed(); record.body = 'unreadable'; });
  };
  page.on('response', observe);
  return { snapshot: () => records.map(record => ({ ...record })), dispose: () => page.off('response', observe) };
}

// Fetch the real upload once, then finish earlier reads before publishing its
// response to React. Upload start and completion both change the panel revision.
export async function completeCaseUploadResponse(route, caseReads) {
  const response = await route.fetch();
  await caseReads.drain('document-upload');
  return route.fulfill({ response });
}

export function createCaseStartupReadTrace(page, apiOrigin, caseId) {
  const records = [];
  const requests = new WeakMap();
  const observe = request => {
    const url = new URL(request.url());
    if (url.origin !== apiOrigin || records.length >= 64) return;
    const kind = request.method() === 'POST' && url.pathname === '/api/auth/login' ? 'login'
      : request.method() !== 'GET' ? null
        : url.pathname === '/api/v2/cases/' + caseId ? 'case-details'
          : url.pathname === '/api/notifications/inbox' ? 'inbox'
            : url.pathname === '/api/v2/cases/appeal-deadlines/expiring' ? 'appeal-deadlines'
              : url.pathname.startsWith('/api/') ? 'other-read' : null;
    if (!kind) return;
    const record = { kind, started: Date.now(), finished: null, status: null, outcome: 'pending' };
    requests.set(request, record);
    records.push(record);
  };
  const response = value => {
    const record = requests.get(value.request());
    const status = value.status();
    if (record && Number.isInteger(status) && status >= 100 && status <= 599) record.status = status;
  };
  const finish = outcome => request => {
    const record = requests.get(request);
    if (record) { record.finished = Date.now(); record.outcome = outcome; }
  };
  const finished = finish('finished');
  const failed = finish('failed');
  page.on('request', observe);
  page.on('response', response);
  page.on('requestfinished', finished);
  page.on('requestfailed', failed);
  return {
    snapshot: () => records.map(record => ({ kind: record.kind, status: record.status, outcome: record.outcome,
      elapsedMs: Math.min(3_600_000, Math.max(0, (record.finished ?? Date.now()) - record.started)) })),
    dispose() {
      page.off('request', observe); page.off('response', response);
      page.off('requestfinished', finished); page.off('requestfailed', failed);
    },
  };
}

export function isCaseBoundaryRead(request, apiOrigin, caseId) {
  const url = new URL(request.url());
  return url.origin === apiOrigin && ((request.method() === 'GET' && url.pathname.startsWith('/api/'))
    || (request.method() === 'POST' && url.pathname === '/api/v2/cases/' + caseId + '/activity'));
}

export function createCaseReadLifecycle(page, isOwnedRead, timeoutMs) {
  const pending = new Set();
  let draining = false;
  let failuresDuringDrain = 0;
  let disposed = false;
  const check = (condition, message) => { if (!condition) throw new Error(message); };
  const begin = request => { if (isOwnedRead(request)) pending.add(request); };
  const finish = request => pending.delete(request);
  const fail = request => { if (pending.delete(request) && draining) failuresDuringDrain++; };
  page.on('request', begin);
  page.on('requestfinished', finish);
  page.on('requestfailed', fail);
  return {
    snapshot: () => ({ pending: pending.size, failuresDuringDrain }),
    drain: async (phase, beforeReadDrain) => {
      check(!disposed && !draining, 'Case reads cannot start an overlapping or closed drain');
      check(['client-navigation', 'server-restart', 'editor-open', 'retirement-confirmation', 'document-tab', 'document-upload', 'browser-close'].includes(phase), 'Unknown case read boundary');
      draining = true;
      let active = true;
      let waitingForFrame = false;
      let timer;
      try {
        await Promise.race([
          (async () => {
            // Any initial readiness prerequisite shares this same deadline.
            if (beforeReadDrain) await beforeReadDrain();
            if (!active) return;
            do {
              await Promise.all([...pending].map(async request => {
                const response = await request.response();
                check(response !== null, 'An existing case read failed before ' + phase);
                if (request.method() === 'POST')
                  check(response.status() === 200, 'The owned case activity write must succeed before ' + phase);
                check(await response.finished() === null, 'An existing case response body failed before ' + phase);
              }));
              if (!active) return;
              waitingForFrame = true;
              await page.evaluate(() => new Promise(resolveFrame => requestAnimationFrame(() => resolveFrame())));
              waitingForFrame = false;
              if (!active) return;
              check(failuresDuringDrain === 0, 'An existing case read was interrupted before ' + phase);
            } while (pending.size);
          })(),
          new Promise((_, reject) => {
            timer = setTimeout(() => reject(new Error('Existing case reads did not complete before ' + phase + ' within ' + timeoutMs
              + 'ms (pending=' + pending.size + ', frame=' + waitingForFrame + ')')), timeoutMs);
          }),
        ]);
        check(pending.size === 0, 'Owned boundary must not abort existing case reads');
      } finally {
        active = false;
        clearTimeout(timer);
        draining = false;
      }
    },
    dispose: () => {
      disposed = true;
      page.off('request', begin);
      page.off('requestfinished', finish);
      page.off('requestfailed', fail);
    },
  };
}

// Serialized into Playwright CLI. Keep all dependencies and state inside this
// function. This observes sockets without changing reconnect policy or timing.
export function createCaseRestartObserver(apiOrigin, onProof) {
  const expectedOrigin = new URL(apiOrigin).origin;
  const sockets = new Map();
  let begun = false;
  let acknowledged = false;
  let previous = [];
  const live = record => !record.closed && record.socket.isClosed() === false;
  const previousClosed = () => previous.length > 0 && previous.every(record => record.closed);
  const hasProof = () => acknowledged && previousClosed()
    && [...sockets.values()].some(record => record.afterPreviousClose && record.handshake && live(record));
  const notify = () => { if (hasProof()) onProof?.(); };
  return {
    observeSocket(socket) {
      if (typeof socket?.url !== 'function' || typeof socket?.isClosed !== 'function') return false;
      let address;
      try { address = new URL(socket.url()); } catch { return false; }
      if (!['ws:', 'wss:'].includes(address.protocol)
        || address.origin.replace(/^ws/, 'http') !== expectedOrigin
        || address.pathname !== '/hubs/notifications') return false;
      if (sockets.has(socket)) return true;
      sockets.set(socket, {
        socket,
        closed: socket.isClosed() === true,
        handshake: false,
        afterPreviousClose: begun && previousClosed(),
      });
      return true;
    },
    onHandshake(socket, payload) {
      const record = sockets.get(socket);
      if (!record || !live(record) || typeof payload !== 'string' || !payload.startsWith('{}\u001e')) return;
      record.handshake = true;
      notify();
    },
    onClose(socket) {
      const record = sockets.get(socket);
      if (record) record.closed = true;
    },
    beginRestart() {
      if (begun) throw new Error('The owned restart can begin only once');
      if (![...sockets.values()].some(record => record.handshake && live(record)))
        throw new Error('An established notifications connection is required before the owned restart');
      previous = [...sockets.values()].filter(live);
      begun = true;
    },
    acknowledgeRestart() {
      if (!begun || acknowledged) throw new Error('The owned restart acknowledgement is out of order');
      acknowledged = true;
      notify();
    },
    hasProof,
    snapshot() {
      const records = [...sockets.values()];
      return {
        initialReady: !begun && records.some(record => record.handshake && live(record)),
        begun,
        acknowledged,
        previousSockets: previous.length,
        previousClosed: previous.filter(record => record.closed).length,
        newCandidates: records.filter(record => record.afterPreviousClose).length,
        liveHandshakes: records.filter(record => record.afterPreviousClose && record.handshake && live(record)).length,
        proved: hasProof(),
      };
    },
  };
}
