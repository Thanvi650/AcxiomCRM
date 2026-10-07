IF OBJECT_ID(N'[__EFMigrationsHistory]') IS NULL
BEGIN
    CREATE TABLE [__EFMigrationsHistory] (
        [MigrationId] nvarchar(150) NOT NULL,
        [ProductVersion] nvarchar(32) NOT NULL,
        CONSTRAINT [PK___EFMigrationsHistory] PRIMARY KEY ([MigrationId])
    );
END;
GO

BEGIN TRANSACTION;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoles] (
        [Id] nvarchar(450) NOT NULL,
        [Name] nvarchar(256) NULL,
        [NormalizedName] nvarchar(256) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoles] PRIMARY KEY ([Id])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUsers] (
        [Id] nvarchar(450) NOT NULL,
        [FullName] nvarchar(100) NOT NULL,
        [IsActive] bit NOT NULL,
        [ManagerId] nvarchar(450) NULL,
        [CreatedDate] datetime2 NOT NULL,
        [UserName] nvarchar(256) NULL,
        [NormalizedUserName] nvarchar(256) NULL,
        [Email] nvarchar(256) NULL,
        [NormalizedEmail] nvarchar(256) NULL,
        [EmailConfirmed] bit NOT NULL,
        [PasswordHash] nvarchar(max) NULL,
        [SecurityStamp] nvarchar(max) NULL,
        [ConcurrencyStamp] nvarchar(max) NULL,
        [PhoneNumber] nvarchar(max) NULL,
        [PhoneNumberConfirmed] bit NOT NULL,
        [TwoFactorEnabled] bit NOT NULL,
        [LockoutEnd] datetimeoffset NULL,
        [LockoutEnabled] bit NOT NULL,
        [AccessFailedCount] int NOT NULL,
        CONSTRAINT [PK_AspNetUsers] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUsers_AspNetUsers_ManagerId] FOREIGN KEY ([ManagerId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AuditLogs] (
        [AuditLogId] bigint NOT NULL IDENTITY,
        [UserId] nvarchar(450) NULL,
        [UserName] nvarchar(256) NULL,
        [Action] nvarchar(50) NOT NULL,
        [EntityName] nvarchar(50) NOT NULL,
        [RecordId] nvarchar(450) NULL,
        [OldValue] nvarchar(max) NULL,
        [NewValue] nvarchar(max) NULL,
        [Result] nvarchar(30) NOT NULL,
        [CreatedDate] datetime2 NOT NULL,
        [IpAddress] nvarchar(64) NULL,
        CONSTRAINT [PK_AuditLogs] PRIMARY KEY ([AuditLogId])
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetRoleClaims] (
        [Id] int NOT NULL IDENTITY,
        [RoleId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetRoleClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetRoleClaims_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserClaims] (
        [Id] int NOT NULL IDENTITY,
        [UserId] nvarchar(450) NOT NULL,
        [ClaimType] nvarchar(max) NULL,
        [ClaimValue] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserClaims] PRIMARY KEY ([Id]),
        CONSTRAINT [FK_AspNetUserClaims_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserLogins] (
        [LoginProvider] nvarchar(450) NOT NULL,
        [ProviderKey] nvarchar(450) NOT NULL,
        [ProviderDisplayName] nvarchar(max) NULL,
        [UserId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserLogins] PRIMARY KEY ([LoginProvider], [ProviderKey]),
        CONSTRAINT [FK_AspNetUserLogins_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserRoles] (
        [UserId] nvarchar(450) NOT NULL,
        [RoleId] nvarchar(450) NOT NULL,
        CONSTRAINT [PK_AspNetUserRoles] PRIMARY KEY ([UserId], [RoleId]),
        CONSTRAINT [FK_AspNetUserRoles_AspNetRoles_RoleId] FOREIGN KEY ([RoleId]) REFERENCES [AspNetRoles] ([Id]) ON DELETE CASCADE,
        CONSTRAINT [FK_AspNetUserRoles_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [AspNetUserTokens] (
        [UserId] nvarchar(450) NOT NULL,
        [LoginProvider] nvarchar(450) NOT NULL,
        [Name] nvarchar(450) NOT NULL,
        [Value] nvarchar(max) NULL,
        CONSTRAINT [PK_AspNetUserTokens] PRIMARY KEY ([UserId], [LoginProvider], [Name]),
        CONSTRAINT [FK_AspNetUserTokens_AspNetUsers_UserId] FOREIGN KEY ([UserId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE CASCADE
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [Customers] (
        [CustomerId] int NOT NULL IDENTITY,
        [CustomerCode] nvarchar(20) NOT NULL,
        [CustomerName] nvarchar(100) NOT NULL,
        [Email] nvarchar(150) NOT NULL,
        [Phone] nvarchar(15) NOT NULL,
        [CompanyName] nvarchar(150) NULL,
        [Address] nvarchar(250) NULL,
        [City] nvarchar(80) NULL,
        [State] nvarchar(80) NULL,
        [Status] nvarchar(30) NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [AssignedToId] nvarchar(450) NULL,
        [CreatedDate] datetime2 NOT NULL,
        [CreatedBy] nvarchar(max) NULL,
        [ModifiedDate] datetime2 NULL,
        CONSTRAINT [PK_Customers] PRIMARY KEY ([CustomerId]),
        CONSTRAINT [FK_Customers_AspNetUsers_AssignedToId] FOREIGN KEY ([AssignedToId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [Leads] (
        [LeadId] int NOT NULL IDENTITY,
        [LeadCode] nvarchar(20) NOT NULL,
        [LeadName] nvarchar(100) NOT NULL,
        [Email] nvarchar(150) NULL,
        [Phone] nvarchar(15) NULL,
        [CompanyName] nvarchar(150) NULL,
        [Source] nvarchar(30) NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [Priority] nvarchar(30) NOT NULL,
        [ExpectedValue] decimal(18,2) NOT NULL,
        [Notes] nvarchar(1000) NULL,
        [AssignedToId] nvarchar(450) NULL,
        [CreatedDate] datetime2 NOT NULL,
        [ModifiedDate] datetime2 NULL,
        [ConvertedCustomerId] int NULL,
        [ConvertedDate] datetime2 NULL,
        CONSTRAINT [PK_Leads] PRIMARY KEY ([LeadId]),
        CONSTRAINT [FK_Leads_AspNetUsers_AssignedToId] FOREIGN KEY ([AssignedToId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Leads_Customers_ConvertedCustomerId] FOREIGN KEY ([ConvertedCustomerId]) REFERENCES [Customers] ([CustomerId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [Activities] (
        [ActivityId] int NOT NULL IDENTITY,
        [ActivityType] nvarchar(30) NOT NULL,
        [Subject] nvarchar(150) NOT NULL,
        [Description] nvarchar(1000) NULL,
        [ActivityDate] datetime2 NOT NULL,
        [CustomerId] int NULL,
        [LeadId] int NULL,
        [AssignedToId] nvarchar(450) NULL,
        [Status] nvarchar(30) NOT NULL,
        [CreatedDate] datetime2 NOT NULL,
        CONSTRAINT [PK_Activities] PRIMARY KEY ([ActivityId]),
        CONSTRAINT [FK_Activities_AspNetUsers_AssignedToId] FOREIGN KEY ([AssignedToId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Activities_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([CustomerId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Activities_Leads_LeadId] FOREIGN KEY ([LeadId]) REFERENCES [Leads] ([LeadId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [Opportunities] (
        [OpportunityId] int NOT NULL IDENTITY,
        [OpportunityName] nvarchar(150) NOT NULL,
        [CustomerId] int NOT NULL,
        [LeadId] int NULL,
        [Amount] decimal(18,2) NOT NULL,
        [Stage] nvarchar(30) NOT NULL,
        [Probability] int NOT NULL,
        [ExpectedCloseDate] datetime2 NOT NULL,
        [Status] nvarchar(30) NOT NULL,
        [Source] nvarchar(100) NULL,
        [Notes] nvarchar(1000) NULL,
        [AssignedToId] nvarchar(450) NULL,
        [CreatedDate] datetime2 NOT NULL,
        [ModifiedDate] datetime2 NULL,
        [ClosedDate] datetime2 NULL,
        CONSTRAINT [PK_Opportunities] PRIMARY KEY ([OpportunityId]),
        CONSTRAINT [CK_Opportunity_Amount] CHECK (Amount >= 0),
        CONSTRAINT [CK_Opportunity_Probability] CHECK (Probability BETWEEN 0 AND 100),
        CONSTRAINT [FK_Opportunities_AspNetUsers_AssignedToId] FOREIGN KEY ([AssignedToId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Opportunities_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([CustomerId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_Opportunities_Leads_LeadId] FOREIGN KEY ([LeadId]) REFERENCES [Leads] ([LeadId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE TABLE [FollowUps] (
        [FollowUpId] int NOT NULL IDENTITY,
        [CustomerId] int NULL,
        [LeadId] int NULL,
        [OpportunityId] int NULL,
        [FollowUpDate] datetime2 NOT NULL,
        [FollowUpType] nvarchar(30) NOT NULL,
        [Subject] nvarchar(150) NOT NULL,
        [Remarks] nvarchar(1000) NULL,
        [Status] nvarchar(30) NOT NULL,
        [AssignedToId] nvarchar(450) NULL,
        [CreatedDate] datetime2 NOT NULL,
        [CompletedDate] datetime2 NULL,
        CONSTRAINT [PK_FollowUps] PRIMARY KEY ([FollowUpId]),
        CONSTRAINT [FK_FollowUps_AspNetUsers_AssignedToId] FOREIGN KEY ([AssignedToId]) REFERENCES [AspNetUsers] ([Id]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FollowUps_Customers_CustomerId] FOREIGN KEY ([CustomerId]) REFERENCES [Customers] ([CustomerId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FollowUps_Leads_LeadId] FOREIGN KEY ([LeadId]) REFERENCES [Leads] ([LeadId]) ON DELETE NO ACTION,
        CONSTRAINT [FK_FollowUps_Opportunities_OpportunityId] FOREIGN KEY ([OpportunityId]) REFERENCES [Opportunities] ([OpportunityId]) ON DELETE NO ACTION
    );
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Activities_ActivityDate] ON [Activities] ([ActivityDate]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Activities_AssignedToId] ON [Activities] ([AssignedToId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Activities_CustomerId] ON [Activities] ([CustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Activities_LeadId] ON [Activities] ([LeadId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetRoleClaims_RoleId] ON [AspNetRoleClaims] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [RoleNameIndex] ON [AspNetRoles] ([NormalizedName]) WHERE [NormalizedName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserClaims_UserId] ON [AspNetUserClaims] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserLogins_UserId] ON [AspNetUserLogins] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUserRoles_RoleId] ON [AspNetUserRoles] ([RoleId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [EmailIndex] ON [AspNetUsers] ([NormalizedEmail]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AspNetUsers_ManagerId] ON [AspNetUsers] ([ManagerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    EXEC(N'CREATE UNIQUE INDEX [UserNameIndex] ON [AspNetUsers] ([NormalizedUserName]) WHERE [NormalizedUserName] IS NOT NULL');
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_CreatedDate] ON [AuditLogs] ([CreatedDate]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_EntityName_Action] ON [AuditLogs] ([EntityName], [Action]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_AuditLogs_UserId] ON [AuditLogs] ([UserId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Customers_AssignedToId] ON [Customers] ([AssignedToId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Customers_CustomerCode] ON [Customers] ([CustomerCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Customers_CustomerName] ON [Customers] ([CustomerName]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Customers_Email] ON [Customers] ([Email]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Customers_Phone] ON [Customers] ([Phone]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FollowUps_AssignedToId] ON [FollowUps] ([AssignedToId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FollowUps_CustomerId] ON [FollowUps] ([CustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FollowUps_LeadId] ON [FollowUps] ([LeadId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FollowUps_OpportunityId] ON [FollowUps] ([OpportunityId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_FollowUps_Status_FollowUpDate] ON [FollowUps] ([Status], [FollowUpDate]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Leads_AssignedToId] ON [Leads] ([AssignedToId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Leads_ConvertedCustomerId] ON [Leads] ([ConvertedCustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE UNIQUE INDEX [IX_Leads_LeadCode] ON [Leads] ([LeadCode]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Leads_Status] ON [Leads] ([Status]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Opportunities_AssignedToId] ON [Opportunities] ([AssignedToId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Opportunities_CustomerId] ON [Opportunities] ([CustomerId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Opportunities_LeadId] ON [Opportunities] ([LeadId]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    CREATE INDEX [IX_Opportunities_Stage] ON [Opportunities] ([Stage]);
END;
GO

IF NOT EXISTS (
    SELECT * FROM [__EFMigrationsHistory]
    WHERE [MigrationId] = N'20261007150556_InitialCreate'
)
BEGIN
    INSERT INTO [__EFMigrationsHistory] ([MigrationId], [ProductVersion])
    VALUES (N'20261007150556_InitialCreate', N'8.0.11');
END;
GO

COMMIT;
GO

