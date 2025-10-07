-- Migration: Add User Approval Fields
-- Description: Adds fields to support self-registration with admin approval workflow
-- This is an additive migration - no existing data is modified or deleted

-- Add approval fields to Users table
ALTER TABLE Users ADD COLUMN IsApproved INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Users ADD COLUMN ApprovedAt TEXT NULL;
ALTER TABLE Users ADD COLUMN ApprovedByUserId INTEGER NULL;

-- Add foreign key constraint for ApprovedByUserId
-- Note: SQLite doesn't support adding FK constraints to existing tables
-- If using a different database, uncomment the following:
-- ALTER TABLE Users ADD CONSTRAINT FK_Users_ApprovedBy
--     FOREIGN KEY (ApprovedByUserId) REFERENCES Users(Id);

-- Create index for querying pending users
CREATE INDEX IF NOT EXISTS idx_users_approval ON Users(IsApproved);

-- Approve all existing users (migration safety - existing users should remain functional)
UPDATE Users SET IsApproved = 1, ApprovedAt = datetime('now') WHERE IsApproved = 0;
