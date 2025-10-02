# Authentication Implementation Guide

This document explains the authentication system implemented in the PayPlanner application.

## Overview

The application now supports two authentication modes:

1. **C# API Authentication** (Production): Uses JWT-based authentication with the C# backend
2. **Supabase Demo Mode** (Development Fallback): Uses Supabase authentication when C# API is unavailable

The system automatically detects which mode to use in development mode.

## Architecture

### Backend (C#)

#### Models
- `User` - Main user entity with email, password hash, and profile information
- `Role` - Roles for authorization (admin, manager, user)
- `UserRole` - Junction table linking users to roles

#### Services
- `AuthService` - Handles authentication, registration, user management, and role assignment
  - Login/Register with JWT token generation
  - Password hashing with BCrypt
  - User CRUD operations
  - Role assignment/removal

#### Endpoints
All endpoints are prefixed with `/api`:

**Public Endpoints:**
- `POST /auth/login` - Login with email/password
- `POST /auth/register` - Register new user

**Protected Endpoints:**
- `GET /auth/me` - Get current user profile
- `GET /users` - List all users (admin only)
- `GET /users/{id}` - Get user by ID (admin only)
- `PUT /users/{id}` - Update user (admin only)
- `POST /users/{id}/password` - Change user password (admin only)
- `POST /users/{userId}/roles/{roleId}` - Assign role to user (admin only)
- `DELETE /users/{userId}/roles/{roleId}` - Remove role from user (admin only)
- `GET /roles` - List all roles

#### Configuration
JWT settings in `appsettings.json`:
```json
{
  "Jwt": {
    "Key": "your-super-secret-key-min-32-chars-long-12345",
    "Issuer": "PayPlanner",
    "Audience": "PayPlanner"
  }
}
```

#### Database Migration
The authentication tables are created via EF Core migration:
- `backend/Migrations/20251002160000_AddAuthTables.cs`

Run migrations to create the tables and seed default roles (admin, manager, user).

### Frontend (React + TypeScript)

#### Auth Context
`src/contexts/AuthContext.tsx` - Unified authentication provider that:
- Detects if C# API is available
- Falls back to Supabase in development if API is unavailable
- Provides consistent authentication interface

#### Services
- `authApi.ts` - C# API authentication service
  - Login, register, logout
  - Token storage in localStorage
  - API availability check

- `userApi.ts` - User management API service
  - User CRUD operations
  - Role management
  - Password changes

#### Components
- `UserManagement` - Admin-only component for managing users and roles
  - List all users
  - View user roles
  - Assign/remove roles
  - Change user passwords

#### Navigation
The navigation automatically shows a "Users" tab for admin users.

## Usage

### Initial Setup

1. **Run the C# API:**
   ```bash
   cd backend
   dotnet restore
   dotnet ef database update
   dotnet run
   ```

2. **Create the first admin user:**
   Use the registration endpoint to create a user, then manually assign the admin role in the database:
   ```sql
   INSERT INTO user_roles (user_id, role_id, assigned_at)
   VALUES ('user-id-here', (SELECT id FROM roles WHERE name = 'admin'), datetime('now'));
   ```

3. **Start the frontend:**
   ```bash
   npm install
   npm run dev
   ```

### Development Mode Behavior

In development mode (`npm run dev`):
- The system checks if the C# API is available at `http://localhost:5080/api`
- If available, uses C# authentication
- If unavailable, falls back to Supabase demo mode

In production mode:
- Always uses C# API authentication
- No Supabase fallback

### User Management

Admin users can access the User Management page to:
1. View all users and their roles
2. Assign roles to users (admin, manager, user)
3. Remove roles from users
4. Change user passwords
5. Manage user accounts

### API Integration

All API requests automatically include the JWT token in the Authorization header:
```typescript
Authorization: Bearer <token>
```

The token is stored in localStorage and persists across page reloads.

## Security Features

- **Password Hashing**: BCrypt with automatic salt generation
- **JWT Tokens**: 7-day expiration, signed with HMAC-SHA256
- **Role-Based Access Control**: Endpoints protected by role requirements
- **CORS**: Configured to allow cross-origin requests
- **Token Storage**: Secure localStorage with automatic cleanup on logout

## Default Roles

1. **admin** - Full system access including user management
2. **manager** - Elevated permissions (can be customized)
3. **user** - Standard user with basic permissions

New users automatically receive the "user" role upon registration.

## API Environment Variables

Frontend `.env` configuration:
```
VITE_API_URL=http://localhost:5080/api
VITE_SUPABASE_URL=<your-supabase-url>
VITE_SUPABASE_ANON_KEY=<your-supabase-key>
```

## Troubleshooting

### API Not Found
If you see authentication errors:
1. Ensure the C# API is running on port 5080
2. Check CORS configuration in `Program.cs`
3. Verify JWT settings in `appsettings.json`

### Supabase Fallback Not Working
In development mode only:
1. Ensure Supabase credentials are in `.env`
2. Check browser console for API check errors
3. Verify the migration ran successfully in Supabase

### Cannot Access User Management
1. Ensure your user has the "admin" role
2. Check the database `user_roles` table
3. Verify JWT token includes role claims
