namespace AcxiomCRM.Models;

public enum CustomerStatus
{
    Prospect,
    Active,
    Inactive
}

public enum LeadStatus
{
    New,
    Contacted,
    Qualified,
    Unqualified,
    Converted,
    Lost
}

public enum LeadSource
{
    Website,
    Referral,
    Campaign,
    ColdCall,
    Event,
    SocialMedia,
    Other
}

public enum Priority
{
    Low,
    Medium,
    High
}

public enum OpportunityStage
{
    Qualification,
    Proposal,
    Negotiation,
    Won,
    Lost
}

public enum OpportunityStatus
{
    Open,
    Won,
    Lost
}

public enum FollowUpType
{
    Call,
    Meeting,
    Email,
    Visit
}

public enum FollowUpStatus
{
    Planned,
    Completed,
    Missed,
    Cancelled
}

public enum ActivityType
{
    Call,
    Meeting,
    Email,
    Task
}

public enum ActivityStatus
{
    Planned,
    Completed,
    Cancelled
}
