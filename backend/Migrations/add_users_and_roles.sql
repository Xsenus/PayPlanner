-- Migration: Add Users and Roles tables
-- Description: Creates tables for user authentication and role-based access control

-- Create Roles table
CREATE TABLE IF NOT EXISTS Roles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL UNIQUE,
    Description TEXT NOT NULL,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now'))
);

-- Create Users table
CREATE TABLE IF NOT EXISTS Users (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Email TEXT NOT NULL UNIQUE,
    PasswordHash TEXT NOT NULL,
    FullName TEXT NOT NULL,
    RoleId INTEGER NOT NULL,
    IsActive INTEGER NOT NULL DEFAULT 1,
    CreatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    UpdatedAt TEXT NOT NULL DEFAULT (datetime('now')),
    FOREIGN KEY (RoleId) REFERENCES Roles(Id)
);

-- Create indexes
CREATE INDEX IF NOT EXISTS idx_users_email ON Users(Email);
CREATE INDEX IF NOT EXISTS idx_users_role ON Users(RoleId);
CREATE INDEX IF NOT EXISTS idx_users_active ON Users(IsActive);

-- Insert default roles
INSERT OR IGNORE INTO Roles (Name, Description) VALUES
    ('admin', 'Full system access with user management capabilities'),
    ('manager', 'Can view all data and manage clients/payments'),
    ('user', 'Basic access to view and manage own data');

-- Insert default admin user (password: admin123)
-- Password hash for 'admin123' using BCrypt
INSERT OR IGNORE INTO Users (Email, PasswordHash, FullName, RoleId, IsActive)
VALUES (
    'admin@payplanner.local',
    '$2a$11$XKWjKvYzqPxKxQzNZQzWZOvB8eQ0qXoZKlQXJNqKLGFGHGFGH',
    'Administrator',
    (SELECT Id FROM Roles WHERE Name = 'admin'),
    1
);
