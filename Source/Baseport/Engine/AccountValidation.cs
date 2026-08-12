using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Baseport;

// Trust-boundary validation for admin accounts.
public static class AccountValidation
{
    // Deliberately narrow: a username appears in logs and audit paths, so it is restricted to characters that cannot be confused or used to forge a line.
    private static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public const int UsernameMin = 3;
    public const int UsernameMax = 64;
    public const int EmailMax = 254; // RFC 5321 maximum path length

    public const int PasswordMin = 10;
    public const int PasswordMax = 128;

    public static string? PasswordProblem(string password) => password.Length switch
    {
        < PasswordMin => $"The new password must be at least {PasswordMin} characters.",
        > PasswordMax => $"The new password must be at most {PasswordMax} characters.",
        _ => null
    };

    public static List<string> Validate(string username, string email)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(username))
            errors.Add("Username is required.");
        else if (username.Length < UsernameMin || username.Length > UsernameMax)
            errors.Add($"Username must be between {UsernameMin} and {UsernameMax} characters.");
        else if (!UsernamePattern.IsMatch(username))
            errors.Add("Username may only contain letters, digits, dots, underscores and hyphens.");

        // Optional, but a stored value has to be a real address: an account whose e-mail is a SQL fragment or a token is a sign something else went wrong.
        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Length > EmailMax) errors.Add($"Email is too long (max {EmailMax} characters).");
            else if (!IsEmail(email)) errors.Add("Email is not a valid address.");
        }
        return errors;
    }

    // MailAddress is the framework's own parser, so it is right about the cases a hand-rolled regex gets wrong.
    public static bool IsEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        if (!MailAddress.TryCreate(value, out var parsed)) return false;
        if (!string.Equals(parsed.Address, value, StringComparison.Ordinal)) return false;
        // A bare "user@host" parses, but a real address needs a dotted domain.
        var at = value.LastIndexOf('@');
        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
