# Self-Registration with Admin Approval Feature

## Overview

This feature adds self-registration functionality to PayPlanner with admin approval workflow. New users can register themselves, but they cannot log in until an administrator approves their account. Email notifications are sent to users throughout the process.

## Changes Made

### Database Changes (Non-Destructive)

**Migration File**: `backend/Migrations/add_user_approval_fields.sql`

Added three new fields to the `Users` table:
- `IsApproved` (INTEGER, default 0) - Whether the user is approved
- `ApprovedAt` (TEXT, nullable) - When the user was approved
- `ApprovedByUserId` (INTEGER, nullable) - Which admin approved the user

**Important**: The migration automatically approves all existing users to maintain backward compatibility.

### Backend Changes

#### New Models
- `RegisterRequest.cs` - Request model for user registration
- `IEmailService.cs` - Email service interface
- `EmailService.cs` - Email service implementation

#### Updated Models
- `User.cs` - Added approval fields
- `LoginResponse.cs` / `UserDto` - Added approval status to DTOs

#### Updated Services
- `AuthService.cs` - Added new methods:
  - `RegisterAsync()` - Handle new user registration
  - `ApproveUserAsync()` - Approve a pending user
  - `RejectUserAsync()` - Reject a pending user
  - `GetAllUsersAsync()` - Now supports filtering by status
  - Updated `LoginAsync()` - Checks approval status
  - Updated `CreateUserAsync()` - Auto-approves admin-created users

#### New Endpoints

**Registration**
- `POST /api/auth/register` - Self-registration endpoint
  - Body: `{ email, password, fullName }`
  - Creates user with `IsApproved = false`
  - Sends confirmation email to user
  - Optionally notifies admins

**Login (Updated)**
- `POST /api/auth/login` - Now returns 403 with "PendingApproval" for unapproved users

**Admin User Management**
- `GET /api/admin/users?status=pending|approved` - List users by approval status
- `POST /api/admin/users/{id}/approve` - Approve a pending user
- `POST /api/admin/users/{id}/reject` - Reject a pending user
  - Body: `{ reason?: string }`

### Frontend Changes

#### New Components
- `Register.tsx` - Self-registration form
- `AwaitingApproval.tsx` - Post-registration confirmation page

#### Updated Components
- `Login.tsx` - Added registration link and pending approval handling
- `App.tsx` - Added auth view routing (login/register/awaiting)
- `Users.tsx` - Added approval workflow UI with filtering tabs

#### Updated Services
- `authService.ts` - Added methods:
  - `getPendingUsers()` - Get users awaiting approval
  - `approveUser()` - Approve a user
  - `rejectUser()` - Reject a user

### Email Configuration

**Required Environment Variables** (add to `appsettings.json`):

```json
{
  "Email": {
    "Enabled": true,
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "SmtpUsername": "your-username",
    "SmtpPassword": "your-password",
    "FromEmail": "noreply@payplanner.com",
    "FromName": "PayPlanner"
  },
  "Features": {
    "RegistrationEnabled": true,
    "NotifyAdminsOnRegistration": false
  }
}
```

**Email Templates**:
- Registration pending notification (sent to user)
- Registration approved notification (sent to user)
- Registration rejected notification (sent to user)
- New registration alert (sent to admins, if enabled)

## User Flow

### Registration Flow

1. User clicks "Register" on login page
2. Fills out registration form (full name, email, password)
3. Submits registration
4. System creates user account with `IsApproved = false`
5. User receives "Registration Pending" email
6. User sees "Awaiting Approval" page
7. Optionally, admins receive notification email

### Admin Approval Flow

1. Admin logs in
2. Navigates to Users page
3. Sees "Pending Approval" tab with badge showing count
4. Reviews pending users
5. Clicks "Approve" or "Reject"
6. User receives approval/rejection email

### Approved User Login Flow

1. User receives approval email
2. User returns to login page
3. Enters credentials
4. System checks `IsApproved = true`
5. User is logged in successfully

### Unapproved User Login Flow

1. User attempts to log in before approval
2. System checks `IsApproved = false`
3. Returns 403 with "PendingApproval" error
4. Frontend redirects to "Awaiting Approval" page

## Configuration Options

### Feature Flags

**Features:RegistrationEnabled** (bool, default: true)
- Enables/disables self-registration
- Set to `false` to disable the registration feature

**Features:NotifyAdminsOnRegistration** (bool, default: false)
- When `true`, sends email to all admin users when someone registers
- Requires email to be configured

### Email Settings

**Email:Enabled** (bool, default: false)
- Master switch for email functionality
- Set to `false` during development to disable emails

## Testing

### Manual Testing Steps

1. **Registration**
   - Navigate to login page
   - Click "Register"
   - Fill form and submit
   - Verify "Awaiting Approval" page appears
   - Check email was sent (if enabled)

2. **Login Blocking**
   - Try to log in with pending account
   - Verify redirect to "Awaiting Approval" page

3. **Admin Approval**
   - Log in as admin
   - Navigate to Users
   - Click "Pending Approval" tab
   - Approve a user
   - Verify approval email sent (if enabled)

4. **Approved User Login**
   - Log in with approved account
   - Verify successful login

5. **Admin Rejection**
   - Create another pending user
   - Reject with optional reason
   - Verify rejection email sent
   - Verify user removed from database

## Security Considerations

- All passwords are hashed using BCrypt
- Email addresses must be unique
- Default role for self-registered users is "user" (lowest privilege)
- Admin-created users are auto-approved
- Existing users remain unaffected (auto-approved during migration)
- Rejection permanently deletes the user account

## Backward Compatibility

- All existing users are automatically approved during migration
- Existing login flow unchanged for approved users
- Admin user creation still works and auto-approves users
- No breaking changes to existing APIs

## Troubleshooting

**Users can't register**
- Check `Features:RegistrationEnabled` is `true`
- Verify database migration ran successfully
- Check browser console for errors

**Emails not sending**
- Verify `Email:Enabled` is `true`
- Check SMTP configuration
- Review application logs for email errors
- Test SMTP credentials separately

**Admin can't see pending users**
- Verify user has "admin" role
- Check database for users with `IsApproved = 0`
- Try refreshing the Users page

## Future Enhancements

Potential improvements for future iterations:
- Email verification before approval
- Batch approval/rejection
- Custom email templates
- Registration form with more fields
- Self-service password reset
- Audit log for approval actions
