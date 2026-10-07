import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App.tsx';
import './index.css';
import { registerPayPlannerApplicationWorker } from './services/applicationWorker';
import { installStaleAssetReload } from './utils/staleAssetReload';
import { parseRestoredRuntimeMode } from './utils/restoredRuntimeSafety';
import { prepareRestoredBrowserSession, RESTORED_BROWSER_FAILURE } from './services/restoredBrowserSession';

installStaleAssetReload();

const restoredRuntime = parseRestoredRuntimeMode(import.meta.env.VITE_RESTORED_RUNTIME_ISOLATION);

if (import.meta.env.PROD && !restoredRuntime) {
  void registerPayPlannerApplicationWorker().catch(() => {
    // The application remains fully usable online when PWA registration is
    // unavailable. Web Push diagnostics will surface registration failures.
  });
}

function renderApplication() {
  createRoot(document.getElementById('root')!).render(
    <StrictMode>
      <App />
    </StrictMode>
  );
}

if (restoredRuntime) {
  void prepareRestoredBrowserSession().then(renderApplication).catch(() => {
    const root = document.getElementById('root');
    if (!root) return;
    root.setAttribute('role', 'alert');
    root.textContent = RESTORED_BROWSER_FAILURE;
  });
} else {
  renderApplication();
}
