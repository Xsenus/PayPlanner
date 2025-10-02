/**
 * Application Entry Point
 *
 * Initializes the React application and wraps it with necessary providers.
 * The AuthProvider ensures authentication context is available throughout the app.
 */

import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App.tsx';
import './index.css';
import { AuthProvider } from './contexts/AuthContext';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <AuthProvider>
      <App />
    </AuthProvider>
  </StrictMode>
);
