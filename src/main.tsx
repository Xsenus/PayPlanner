import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App.tsx';
import './index.css';
import { registerPayPlannerApplicationWorker } from './services/applicationWorker';
import { installStaleAssetReload } from './utils/staleAssetReload';

installStaleAssetReload();

if (import.meta.env.PROD) {
  void registerPayPlannerApplicationWorker().catch(() => {
    // The application remains fully usable online when PWA registration is
    // unavailable. Web Push diagnostics will surface registration failures.
  });
}

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>
);
