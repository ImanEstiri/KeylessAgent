using ImanSoftware.DateTimeProvider;
using ImanSoftware.Extensions;
using ImanSoftware.KeylessAgent.Abstractions.Models.Configuration;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Git;

/// <summary>
/// Builds agent branch names: <c>&lt;prefix&gt;&lt;yyyyMMdd-HHmm&gt;-&lt;slug&gt;</c>,
/// where the slug is derived from the user's request (kebab-case, [a-z0-9-], max 40, "task" when empty).
/// </summary>
internal sealed class BranchNameBuilder
{
    private readonly IDateTimeProvider _time;

    public BranchNameBuilder(IDateTimeProvider time)
    {
        _time = time;
    }

    /// <summary>
    /// Builds a branch name for one user request.
    /// </summary>
    /// <param name="request">The user's request text.</param>
    /// <param name="options">The configured git options (branch prefix).</param>
    /// <returns>The branch name, for example <c>agent/20260101-0930-fix-login-bug</c>.</returns>
    public string Build(string request, GitOptions options)
    {
        var prefix = string.IsNullOrWhiteSpace(options.BranchPrefix) ? "agent/" : options.BranchPrefix;
        return $"{prefix}{_time.Now:yyyyMMdd-HHmm}-{BuildSlug(request)}";
    }

    /// <summary>
    /// request → kebab slug, keeping only [a-z0-9-], max 40 chars, "task" when nothing survives.
    /// </summary>
    internal static string BuildSlug(string request)
    {
        var timeout = TimeSpan.FromSeconds(1);
        var kebab = (request ?? string.Empty).Truncate(60, string.Empty).ToKebabCase();
        var spacedOut = System.Text.RegularExpressions.Regex.Replace(kebab, @"\s+", "-", default, timeout);
        var filtered = new string(spacedOut.ToLowerInvariant()
            .Where(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
            .ToArray());
        var collapsed = System.Text.RegularExpressions.Regex.Replace(filtered, "-{2,}", "-", default, timeout)
            .Trim('-').Truncate(40, string.Empty);
        return collapsed.Length == 0 ? "task" : collapsed;
    }
}
