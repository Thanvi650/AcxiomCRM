namespace AcxiomCRM.Models;

/// <summary>Application role names. Each user has exactly one primary role.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Manager = "Manager";
    public const string SalesExecutive = "SalesExecutive";

    /// <summary>Comma-separated list for [Authorize(Roles = ...)].</summary>
    public const string AdminOrManager = Admin + "," + Manager;

    public static readonly string[] All = { Admin, Manager, SalesExecutive };

    public static string Display(string? role) => role switch
    {
        SalesExecutive => "Sales Executive",
        null or "" => "—",
        _ => role
    };
}
