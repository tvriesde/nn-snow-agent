using System.Text.RegularExpressions;

namespace Helpdesk.Backend;

public static partial class HealthQuestion
{
    public static bool TryResolve(string message, string? configuredApplication, out string application)
    {
        application = "";
        var match = StandaloneHealthQuestion().Match(message.Trim());
        if (!match.Success) return false;
        var target = match.Groups["target"].Value.Trim();
        if (target.Equals("this IT helpdesk application", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("this helpdesk application", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("this helpdesk", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("this application", StringComparison.OrdinalIgnoreCase) ||
            target.Equals("the IT helpdesk", StringComparison.OrdinalIgnoreCase))
        {
            if (string.IsNullOrWhiteSpace(configuredApplication)) return false;
            application = configuredApplication;
        }
        else
        {
            // Deliberately narrow: complex names, pronouns and multi-intent requests stay with the agent.
            if (!ApplicationTagValue().IsMatch(target)) return false;
            application = target;
        }
        return true;
    }

    [GeneratedRegex(@"^(?:is (?<target>.+?) healthy|what is (?:the )?health(?: state| status)? of (?<target>.+?))(?: right now| now)?[?.]?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex StandaloneHealthQuestion();

    [GeneratedRegex(@"^[a-z0-9]+(?:[-_][a-z0-9]+)+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 100)]
    private static partial Regex ApplicationTagValue();
}
