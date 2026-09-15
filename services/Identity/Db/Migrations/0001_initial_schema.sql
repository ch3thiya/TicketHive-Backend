-- Enable UUID generation extension if not exists
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Table for tracking application-level user accounts mapped to WSO2
CREATE TABLE IF NOT EXISTS user_accounts (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    wso2_sub VARCHAR(255) UNIQUE NOT NULL, -- Subject ID from WSO2 token (sub claim)
    email VARCHAR(255) UNIQUE NOT NULL,
    full_name VARCHAR(255) NOT NULL,
    role VARCHAR(50) NOT NULL,             -- 'Customer', 'Organizer', 'Admin'
    approval_status VARCHAR(50) NOT NULL,  -- 'pending', 'approved', 'rejected'
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL
);

-- Table for tracking organizer approval requests
CREATE TABLE IF NOT EXISTS organizer_requests (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    user_account_id UUID REFERENCES user_accounts(id) ON DELETE CASCADE,
    organization_name VARCHAR(255) NOT NULL,
    business_email VARCHAR(255) NOT NULL,
    phone VARCHAR(50) NOT NULL,
    event_type VARCHAR(255) NOT NULL,
    about TEXT NOT NULL,
    status VARCHAR(50) NOT NULL DEFAULT 'pending', -- 'pending', 'approved', 'rejected'
    created_at TIMESTAMP WITH TIME ZONE DEFAULT CURRENT_TIMESTAMP NOT NULL,
    reviewed_at TIMESTAMP WITH TIME ZONE
);

-- Add index on wso2_sub for fast JWT lookup
CREATE INDEX IF NOT EXISTS idx_user_accounts_wso2_sub ON user_accounts(wso2_sub);
