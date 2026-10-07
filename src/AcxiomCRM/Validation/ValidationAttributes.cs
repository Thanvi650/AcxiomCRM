using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;

namespace AcxiomCRM.Validation;

/// <summary>Shared validation patterns so client, server and API use identical rules.</summary>
public static class ValidationPatterns
{
    /// <summary>Basic but strict email shape: local@domain.tld, no spaces.</summary>
    public const string Email = @"^[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}$";

    /// <summary>Project phone rule: 10-digit Indian mobile number starting with 6–9.</summary>
    public const string Phone = @"^[6-9][0-9]{9}$";

    /// <summary>Password policy (mirrors Identity options): 8+ chars, upper, lower, digit, symbol.</summary>
    public const string Password = @"^(?=.*[a-z])(?=.*[A-Z])(?=.*\d)(?=.*[^A-Za-z\d]).{8,100}$";

    public const string EmailMessage = "Enter a valid email address.";
    public const string PhoneMessage = "Enter a valid phone number.";
    public const string PasswordMessage =
        "Password must be at least 8 characters and include upper-case and lower-case letters, a number and a symbol.";
}

/// <summary>
/// Base for rules that only apply while another property is NOT one of the exempt values
/// (e.g. "close date cannot be in the past unless Stage is Won or Lost").
/// Emits unobtrusive data-val-* attributes so the rule also runs in the browser.
/// </summary>
public abstract class ConditionalValidationAttribute : ValidationAttribute, IClientModelValidator
{
    protected ConditionalValidationAttribute(string defaultMessage, string? conditionProperty, string[] exemptValues)
        : base(defaultMessage)
    {
        ConditionProperty = conditionProperty;
        ExemptValues = exemptValues;
    }

    public string? ConditionProperty { get; }
    public string[] ExemptValues { get; }

    protected abstract string ClientRuleName { get; }

    protected bool IsExempt(ValidationContext context)
    {
        if (ConditionProperty is null) return false;
        var value = context.ObjectType.GetProperty(ConditionProperty)?.GetValue(context.ObjectInstance)?.ToString();
        return value is not null && ExemptValues.Contains(value, StringComparer.OrdinalIgnoreCase);
    }

    protected ValidationResult Fail(ValidationContext context) =>
        new(FormatErrorMessage(context.DisplayName), context.MemberName is null ? null : new[] { context.MemberName });

    public void AddValidation(ClientModelValidationContext context)
    {
        Merge(context.Attributes, "data-val", "true");
        Merge(context.Attributes, $"data-val-{ClientRuleName}", FormatErrorMessage(context.ModelMetadata.GetDisplayName()));
        if (ConditionProperty is not null)
        {
            Merge(context.Attributes, $"data-val-{ClientRuleName}-other", ConditionProperty);
            Merge(context.Attributes, $"data-val-{ClientRuleName}-exempt", string.Join(",", ExemptValues));
        }
    }

    private static void Merge(IDictionary<string, string> attributes, string key, string value)
    {
        if (!attributes.ContainsKey(key)) attributes.Add(key, value);
    }
}

/// <summary>The date part must be today or later.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class NotInPastAttribute : ConditionalValidationAttribute
{
    public NotInPastAttribute(string? conditionProperty = null, params string[] exemptValues)
        : base("{0} cannot be in the past.", conditionProperty, exemptValues)
    {
    }

    protected override string ClientRuleName => "notinpast";

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not DateTime date || IsExempt(validationContext)) return ValidationResult.Success;
        return date.Date < DateTime.Today ? Fail(validationContext) : ValidationResult.Success;
    }
}

/// <summary>The number must be strictly greater than zero.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class PositiveAttribute : ConditionalValidationAttribute
{
    public PositiveAttribute(string? conditionProperty = null, params string[] exemptValues)
        : base("{0} must be greater than 0.", conditionProperty, exemptValues)
    {
    }

    protected override string ClientRuleName => "positive";

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is null || IsExempt(validationContext)) return ValidationResult.Success;
        var number = Convert.ToDecimal(value);
        return number > 0 ? ValidationResult.Success : Fail(validationContext);
    }
}

/// <summary>Value must be one of the application role names.</summary>
[AttributeUsage(AttributeTargets.Property)]
public sealed class ValidRoleAttribute : ValidationAttribute
{
    public ValidRoleAttribute() : base("Select a valid role.")
    {
    }

    public override bool IsValid(object? value) =>
        value is string role && Models.Roles.All.Contains(role);
}
