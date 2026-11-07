using Feedback.Api.Domain;

namespace Feedback.Api.Intake;

/// <summary>Checks a raw portal submit. Errors are keyed by field name, as ValidationProblem expects.</summary>
public static class SubmissionValidator
{
    public const int TitleMax = 120;
    public const int DescriptionMax = 2000;
    public const int EmailMax = 254;

    public static (ValidSubmission? Valid, Dictionary<string, string[]> Errors) Validate(
        string? title, string? description, string? category, string? email)
    {
        var errors = new Dictionary<string, string[]>();
        title = title?.Trim();
        description = description?.Trim();
        email = email?.Trim();

        if (string.IsNullOrEmpty(title))
            errors["title"] = ["Give your request a title."];
        else if (title.Length > TitleMax)
            errors["title"] = [$"Keep the title to {TitleMax} characters."];

        if (string.IsNullOrEmpty(description))
            errors["description"] = ["Tell us what you need, and why."];
        else if (description.Length > DescriptionMax)
            errors["description"] = [$"Keep it to {DescriptionMax} characters."];

        if (!Categories.IsValid(category))
            errors["category"] = ["Choose the part of the app."];

        if (!LooksLikeEmail(email))
            errors["email"] = ["Enter a valid email address."];

        return errors.Count == 0
            ? (new ValidSubmission(title!, description!, category!, email!), errors)
            : (null, errors);
    }

    // Deliberately loose: one @, no whitespace, a dot in the domain. Delivery is never attempted (assumption 8).
    private static bool LooksLikeEmail(string? email)
    {
        if (string.IsNullOrEmpty(email) || email.Length > EmailMax || email.Any(char.IsWhiteSpace)) return false;
        var at = email.IndexOf('@');
        if (at <= 0 || at != email.LastIndexOf('@')) return false;
        var domain = email[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }
}
