namespace PiCompanion.Application.Skills;

public static class SkillCompletionQuery
{
    public static bool TryParse(string text, out string query)
    {
        query = string.Empty;
        if (string.IsNullOrEmpty(text) || text.Contains('\n') || text.Contains('\r'))
        {
            return false;
        }

        if (text.StartsWith("/skill:", StringComparison.Ordinal))
        {
            var suffix = text["/skill:".Length..];
            if (suffix.Any(char.IsWhiteSpace))
            {
                return false;
            }

            query = suffix;
            return true;
        }

        if (text[0] != '/' || text.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        var shorthand = text[1..];
        if (shorthand.Any(char.IsWhiteSpace))
        {
            return false;
        }

        query = shorthand;
        return true;
    }

    public static string CreateInvocation(string skillName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(skillName);
        return $"/skill:{skillName.Trim()} ";
    }
}
