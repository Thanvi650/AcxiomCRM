using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding.Metadata;

namespace AcxiomCRM.Validation;

/// <summary>
/// Makes framework-generated validation messages user-friendly and free of internals
/// (no .NET type names, JSON parser positions or parameter names) — spec 5.2 and 10.1.
/// </summary>
public static class FriendlyValidation
{
    /// <summary>Plain-language messages for MVC model binding (also used by client-side "number" checks).</summary>
    public static void Configure(DefaultModelBindingMessageProvider messages)
    {
        messages.SetAttemptedValueIsInvalidAccessor((value, field) => $"'{value}' is not a valid value for {field}.");
        messages.SetNonPropertyAttemptedValueIsInvalidAccessor(value => $"'{value}' is not a valid value.");
        messages.SetValueIsInvalidAccessor(value => $"'{value}' is not a valid value.");
        messages.SetValueMustBeANumberAccessor(field => $"{field} must be a number.");
        messages.SetNonPropertyValueMustBeANumberAccessor(() => "The value must be a number.");
        messages.SetValueMustNotBeNullAccessor(field => $"{field} is required.");
        messages.SetMissingBindRequiredValueAccessor(field => $"{field} is required.");
        messages.SetMissingKeyOrValueAccessor(() => "A value is required.");
        messages.SetMissingRequestBodyRequiredValueAccessor(() => "A request body is required.");
        messages.SetUnknownValueIsInvalidAccessor(field => $"The value provided for {field} is not valid.");
        messages.SetNonPropertyUnknownValueIsInvalidAccessor(() => "The value provided is not valid.");
    }

    /// <summary>Builds the API's 400 response, translating JSON-path keys into field names.</summary>
    public static IActionResult ApiResponse(ActionContext context)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var jsonProblem = false;

        foreach (var (key, entry) in context.ModelState)
        {
            if (entry.Errors.Count == 0) continue;

            if (key.StartsWith("$"))
            {
                // The JSON body couldn't be read. Either one value had the wrong type (e.g. text in a
                // number, unknown enum) or the document itself is broken (e.g. cut off half-way).
                jsonProblem = true;
                var field = FieldFromPath(key);
                var wrongValueType = entry.Errors.Any(e =>
                    e.Exception?.Message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal) == true);
                if (field is null || !wrongValueType)
                {
                    Add(errors, string.Empty, "The request body is not valid JSON.");
                }
                else
                {
                    Add(errors, field, $"Enter a valid value for {field}.");
                }
                continue;
            }

            foreach (var error in entry.Errors)
            {
                Add(errors, key, string.IsNullOrEmpty(error.ErrorMessage) ? "The value provided is not valid." : error.ErrorMessage);
            }
        }

        // When the body can't be parsed, the framework also reports the action parameter itself
        // (e.g. "The input field is required."). That is noise for the caller, so drop it.
        if (jsonProblem)
        {
            foreach (var parameter in context.ActionDescriptor.Parameters)
            {
                errors.Remove(parameter.Name);
            }
        }

        var problem = new ValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "One or more validation errors occurred."
        };
        return new BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
    }

    /// <summary>"$.expectedCloseDate" -> "ExpectedCloseDate"; "$" or "$.items[0]" -> null.</summary>
    private static string? FieldFromPath(string path)
    {
        var match = Regex.Match(path, @"^\$\.([A-Za-z_][A-Za-z0-9_]*)$");
        if (!match.Success) return null;
        var name = match.Groups[1].Value;
        return char.ToUpperInvariant(name[0]) + name[1..];
    }

    private static void Add(Dictionary<string, string[]> errors, string key, string message)
    {
        errors[key] = errors.TryGetValue(key, out var existing)
            ? existing.Contains(message) ? existing : existing.Append(message).ToArray()
            : new[] { message };
    }
}
