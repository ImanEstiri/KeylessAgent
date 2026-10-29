using System.Text.RegularExpressions;

namespace ImanSoftware.KeylessAgent.Core.Implementation.Agent;

/// <summary>
/// Redacts secret-looking values from command OUTPUT before it reaches the turn log:
/// password/passwd/pwd/secret/token/apikey/api-key/authorization/bearer followed by
/// <c>:</c> or <c>=</c> and a value become <c>&lt;key&gt;=[redacted]</c> — key names are kept,
/// values are gone. Applied to the Output FIELD before serialization, never to serialized JSON.
/// </summary>
internal static partial class SecretRedactor
{
    // The optional quotes around the key and the value keep JSON-shaped output structured
    // ("ApiKey": "abc123" -> "ApiKey": "[redacted]"); an optional bearer/basic scheme word is
    // consumed with the separator so "Authorization: Bearer <token>" loses the whole credential;
    // the value stops at whitespace, quote, semicolon or comma so `Password=x;Server=y` only
    // loses the credential.
    [GeneratedRegex(@"(?<key>\b(?:password|passwd|pwd|secret|token|api[_-]?key|apikey|authorization|bearer)\b""?\s*[=:]\s*""?(?:(?:bearer|basic)\b\s*)?)[^\s"";,]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretValueRegex();

    public static string? Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return SecretValueRegex().Replace(text, "${key}[redacted]");
    }
}
