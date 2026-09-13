using System.Net.Mail;
using System.Text.RegularExpressions;

namespace Baseport;

public static class AccountValidation
{

    private static readonly Regex UsernamePattern = new(@"^[A-Za-z0-9._-]+$", RegexOptions.Compiled);

    public const int UsernameMin = 3;
    public const int UsernameMax = 64;
    public const int EmailMax = 254;

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

        if (!string.IsNullOrWhiteSpace(email))
        {
            if (email.Length > EmailMax) errors.Add($"Email is too long (max {EmailMax} characters).");
            else if (!IsEmail(email)) errors.Add("Email is not a valid address.");
        }
        return errors;
    }

    public static bool IsEmail(string value)
    {
        if (value.Any(char.IsWhiteSpace)) return false;
        if (!MailAddress.TryCreate(value, out var parsed)) return false;
        if (!string.Equals(parsed.Address, value, StringComparison.Ordinal)) return false;

        var at = value.LastIndexOf('@');
        var domain = value[(at + 1)..];
        return domain.Contains('.') && !domain.StartsWith('.') && !domain.EndsWith('.');
    }
}
