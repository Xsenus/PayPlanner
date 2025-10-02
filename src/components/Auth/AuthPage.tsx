/**
 * Authentication Page Component
 *
 * Main authentication page that allows switching between login and registration forms.
 * Manages the view state and provides a seamless transition between auth modes.
 */

import React, { useState } from 'react';
import { LoginForm } from './LoginForm';
import { RegisterForm } from './RegisterForm';

/**
 * Auth view type
 * Determines which form to display (login or register)
 */
type AuthView = 'login' | 'register';

/**
 * AuthPage Component
 *
 * Container component that renders either the login or registration form
 * based on the current view state. Provides callbacks for switching between views.
 */
export const AuthPage: React.FC = () => {
  // Current authentication view (login or register)
  const [view, setView] = useState<AuthView>('login');

  /**
   * Switch to registration view
   */
  const switchToRegister = () => {
    setView('register');
  };

  /**
   * Switch to login view
   */
  const switchToLogin = () => {
    setView('login');
  };

  // Render the appropriate form based on current view
  return (
    <>
      {view === 'login' ? (
        <LoginForm onSwitchToRegister={switchToRegister} />
      ) : (
        <RegisterForm onSwitchToLogin={switchToLogin} />
      )}
    </>
  );
};
