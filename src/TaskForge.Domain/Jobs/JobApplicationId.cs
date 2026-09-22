namespace TaskForge.Domain.Jobs;

public static class JobApplicationId
{
    public const int MaximumLength = 100;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        string trimmed = value.Trim();
        return trimmed.Length <= MaximumLength
            && trimmed.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    public static string Normalize(string value)
    {
        if (!IsValid(value))
        {
            throw new ArgumentException("Application ID must contain 1 to 100 ASCII letters, digits, dots, underscores, or hyphens.", nameof(value));
        }

        return value.Trim().ToLowerInvariant();
    }
}
