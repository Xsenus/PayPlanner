# Authentication Setup Instructions

## Required NuGet Packages

Add these packages to your project:

```bash
dotnet add package Microsoft.AspNetCore.Authentication.JwtBearer
dotnet add package BCrypt.Net-Next
dotnet add package System.IdentityModel.Tokens.Jwt
```

## Database Migration

Run the SQL migration script located at:
`Migrations/add_users_and_roles.sql`

This will create:
- `Roles` table with default roles (admin, manager, user)
- `Users` table with authentication fields
- Default admin user: `admin@payplanner.local` / `admin123`

## Update PaymentContext.cs

Add these DbSets to your PaymentContext class:

```csharp
public DbSet<User> Users { get; set; }
public DbSet<Role> Roles { get; set; }
```

## Update appsettings.json

Add JWT configuration:

```json
{
  "Jwt": {
    "Secret": "your-secret-key-must-be-at-least-32-characters-long!",
    "Issuer": "PayPlanner",
    "Audience": "PayPlanner"
  }
}
```

## Update Program.cs

Add authentication services and endpoints. See the updated `Program.cs` file.

## API Endpoints

Once configured, these endpoints will be available:

### Authentication
- `POST /api/auth/login` - Login with email/password
- `GET /api/auth/me` - Get current user info

### Users (Admin only)
- `GET /api/users` - Get all users
- `GET /api/users/{id}` - Get user by ID
- `POST /api/users` - Create new user
- `PUT /api/users/{id}` - Update user
- `DELETE /api/users/{id}` - Delete user

### Roles
- `GET /api/roles` - Get all roles

## Default Admin Account

After running the migration:
- **Email**: admin@payplanner.local
- **Password**: admin123

**Important**: Change this password after first login!
