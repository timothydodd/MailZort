using System.Text.RegularExpressions;

namespace MailZort.Services;
public class RuleMatcher
{
    private readonly ILogger<RuleMatcher> _logger;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(5);

    public RuleMatcher(ILogger<RuleMatcher> logger)
    {
        _logger = logger;
    }
    private static bool PassesAgeFilter(Rule rule, EmailReceivedEventArgs email)
    {
        if (email.IsRead && rule.ExpressionType != ExpressionType.AllEmails)
        {
            if (DateTimeOffset.Now.Subtract(email.ReceivedDate).TotalHours < 2)
                return false; // Grace period: don't process recently-read emails
        }
        if (rule.DaysOld <= 0)
            return true;

        var emailAge = DateTimeOffset.Now.Subtract(email.ReceivedDate).TotalDays;
        return emailAge >= rule.DaysOld;
    }

    private static bool PassesStatusFilters(Rule rule, EmailReceivedEventArgs email)
    {
        // Check RequireUnread filter - if set to true, email must be unread
        if (rule.RequireUnread == true && email.IsRead)
            return false;

        // Check RequireNotImportant filter - if set to true, email must NOT be important
        if (rule.RequireNotImportant == true && email.IsImportant)
            return false;

        return true;
    }
    private bool ProcessMatch(Rule rule, EmailReceivedEventArgs email, string value, MatchResult matchResult)
    {
        _logger.LogDebug("Match found! Rule: {RuleId}, Value: '{Value}', Location: {Location}",
                  rule.Name, value, matchResult.MatchLocation);

        var passesAge = PassesAgeFilter(rule, email);
        _logger.LogDebug("Age filter result: {PassesAge} for rule: {RuleId}", passesAge, rule.Name);

        var passesStatus = PassesStatusFilters(rule, email);
        _logger.LogDebug("Status filter result: {PassesStatus} for rule: {RuleId} (IsRead: {IsRead}, IsImportant: {IsImportant})",
            passesStatus, rule.Name, email.IsRead, email.IsImportant);

        if (passesAge && passesStatus)
        {
            var ruleValues = string.Join(", ", rule.FrozenValues ?? rule.Values?.ToArray() ?? Array.Empty<string>());
            _logger.LogInformation("RULE MATCHED! Rule: {RuleId}, Value: '{Value}', Location: {Location}, Subject: '{Subject}', RuleValues: [{RuleValues}]",
                rule.Name, value, matchResult.MatchLocation, email.Subject, ruleValues);
            return true;
        }
        else
        {
            _logger.LogDebug("Match found but failed filters (age: {PassesAge}, status: {PassesStatus}). Rule: {RuleId}",
                passesAge, passesStatus, rule.Name);
            return false;
        }
    }
    // Enhanced version with detailed debug information
    public bool CheckRuleMatch(Rule rule, EmailReceivedEventArgs email)
    {
        _logger.LogDebug("Checking rule: {RuleId} with {ValueCount} values", rule.Name, rule.Values?.Count ?? 0);
        if (rule.ExpressionType == ExpressionType.AllEmails)
        {
            _logger.LogDebug("ExpressionType is AllEmails, automatically passing match for rule: {RuleId}", rule.Name);
            if (ProcessMatch(rule, email, "AllEmails", new MatchResult { IsMatch = true, MatchLocation = "AllEmails" }))
            {
                return true;
            }
            else
            {
                return false;
            }
        }
        var valuesToCheck = rule.FrozenValues ?? rule.Values?.ToArray() ?? Array.Empty<string>();
        foreach (var value in valuesToCheck)
        {
            _logger.LogDebug("Testing value: '{Value}' with expression type: {ExpressionType}", value, rule.ExpressionType);

            var matchResult = rule.ExpressionType switch
            {
                ExpressionType.Contains => CheckContainsMatchWithDebug(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.DoesNotContain => CheckDoesNotContainMatch(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.Is => CheckIsMatch(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.IsNot => CheckIsNotMatch(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.StartsWith => CheckStartsWithMatch(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.EndsWith => CheckEndsWithMatch(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.MatchesRegex => CheckRegexMatchWithDebug(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.DoesNotMatchRegex => CheckDoesNotMatchRegex(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                _ => new MatchResult { IsMatch = false, MatchLocation = "Unknown expression type" }
            };

            if (matchResult.IsMatch)
            {
                if (ProcessMatch(rule, email, value, matchResult))
                {
                    return true;
                }
            }
            else
            {
                _logger.LogDebug("No match for value: '{Value}' in rule: {RuleId}", value, rule.Name);
            }
        }

        _logger.LogDebug("No matches found for rule: {RuleId}", rule.Name);
        return false;
    }

    private MatchResult CheckContainsMatchWithDebug(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking contains match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);

        return lookIn switch
        {
            LookIn.All => CheckAllFieldsContains(email, searchValue, ruleId),
            LookIn.Subject => CheckSingleFieldContains("Subject", email.Subject, searchValue),
            LookIn.Body => CheckSingleFieldContains("Body", email.Body, searchValue),
            LookIn.Sender => CheckSingleFieldContains("Sender", email.SenderName, searchValue),
            LookIn.Recipient => CheckSingleFieldContains("Recipient", email.From, searchValue),
            LookIn.SenderEmail => CheckSingleFieldContains("SenderEmail", email.SenderAddress, searchValue),
            _ => new MatchResult { IsMatch = false, MatchLocation = "Invalid LookIn value" }
        };
    }

    private MatchResult CheckDoesNotContainMatch(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking does-not-contain match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);
        var result = CheckContainsMatchWithDebug(lookIn, email, searchValue, ruleId);
        return new MatchResult
        {
            IsMatch = !result.IsMatch,
            MatchLocation = result.IsMatch ? $"Found in {result.MatchLocation} (negated)" : "DoesNotContain passed"
        };
    }

    private MatchResult CheckIsMatch(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking exact match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);

        return lookIn switch
        {
            LookIn.All => CheckAllFieldsExact(email, searchValue, ruleId),
            LookIn.Subject => CheckSingleFieldExact("Subject", email.Subject, searchValue),
            LookIn.Body => CheckSingleFieldExact("Body", email.Body, searchValue),
            LookIn.Sender => CheckSingleFieldExact("Sender", email.SenderName, searchValue),
            LookIn.Recipient => CheckSingleFieldExact("Recipient", email.From, searchValue),
            LookIn.SenderEmail => CheckSingleFieldExact("SenderEmail", email.SenderAddress, searchValue),
            _ => new MatchResult { IsMatch = false, MatchLocation = "Invalid LookIn value" }
        };
    }

    private MatchResult CheckIsNotMatch(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking is-not match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);
        var result = CheckIsMatch(lookIn, email, searchValue, ruleId);
        return new MatchResult
        {
            IsMatch = !result.IsMatch,
            MatchLocation = result.IsMatch ? $"Found in {result.MatchLocation} (negated)" : "IsNot passed"
        };
    }

    private MatchResult CheckStartsWithMatch(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking starts-with match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);

        return lookIn switch
        {
            LookIn.All => CheckAllFieldsStartsWith(email, searchValue, ruleId),
            LookIn.Subject => CheckSingleFieldStartsWith("Subject", email.Subject, searchValue),
            LookIn.Body => CheckSingleFieldStartsWith("Body", email.Body, searchValue),
            LookIn.Sender => CheckSingleFieldStartsWith("Sender", email.SenderName, searchValue),
            LookIn.Recipient => CheckSingleFieldStartsWith("Recipient", email.From, searchValue),
            LookIn.SenderEmail => CheckSingleFieldStartsWith("SenderEmail", email.SenderAddress, searchValue),
            _ => new MatchResult { IsMatch = false, MatchLocation = "Invalid LookIn value" }
        };
    }

    private MatchResult CheckEndsWithMatch(LookIn lookIn, EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        _logger.LogDebug("Checking ends-with match in {LookIn} for value: '{SearchValue}'", lookIn, searchValue);

        return lookIn switch
        {
            LookIn.All => CheckAllFieldsEndsWith(email, searchValue, ruleId),
            LookIn.Subject => CheckSingleFieldEndsWith("Subject", email.Subject, searchValue),
            LookIn.Body => CheckSingleFieldEndsWith("Body", email.Body, searchValue),
            LookIn.Sender => CheckSingleFieldEndsWith("Sender", email.SenderName, searchValue),
            LookIn.Recipient => CheckSingleFieldEndsWith("Recipient", email.From, searchValue),
            LookIn.SenderEmail => CheckSingleFieldEndsWith("SenderEmail", email.SenderAddress, searchValue),
            _ => new MatchResult { IsMatch = false, MatchLocation = "Invalid LookIn value" }
        };
    }

    private MatchResult CheckRegexMatchWithDebug(LookIn lookIn, EmailReceivedEventArgs email, string pattern, object ruleId)
    {
        _logger.LogDebug("Checking regex match in {LookIn} for pattern: '{Pattern}'", lookIn, pattern);

        try
        {
            var regex = new Regex(pattern, RegexOptions.IgnoreCase, RegexTimeout);

            return lookIn switch
            {
                LookIn.All => CheckAllFieldsRegex(email, regex, pattern, ruleId),
                LookIn.Subject => CheckSingleFieldRegex("Subject", email.Subject, regex, pattern),
                LookIn.Body => CheckSingleFieldRegex("Body", email.Body, regex, pattern),
                LookIn.Sender => CheckSingleFieldRegex("Sender", email.SenderName, regex, pattern),
                LookIn.Recipient => CheckSingleFieldRegex("Recipient", email.From, regex, pattern),
                LookIn.SenderEmail => CheckSingleFieldRegex("SenderEmail", email.SenderAddress, regex, pattern),
                _ => new MatchResult { IsMatch = false, MatchLocation = "Invalid LookIn value" }
            };
        }
        catch (RegexMatchTimeoutException)
        {
            _logger.LogWarning("Regex timeout for pattern: {Pattern} in rule: {RuleId}", pattern, ruleId);
            return new MatchResult { IsMatch = false, MatchLocation = "Regex timeout" };
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning("Invalid regex pattern: {Pattern} in rule: {RuleId}. Error: {Error}", pattern, ruleId, ex.Message);
            return new MatchResult { IsMatch = false, MatchLocation = "Invalid regex pattern" };
        }
    }

    private MatchResult CheckDoesNotMatchRegex(LookIn lookIn, EmailReceivedEventArgs email, string pattern, object ruleId)
    {
        _logger.LogDebug("Checking does-not-match-regex in {LookIn} for pattern: '{Pattern}'", lookIn, pattern);
        var result = CheckRegexMatchWithDebug(lookIn, email, pattern, ruleId);
        return new MatchResult
        {
            IsMatch = !result.IsMatch,
            MatchLocation = result.IsMatch ? $"Found in {result.MatchLocation} (negated)" : "DoesNotMatchRegex passed"
        };
    }

    private MatchResult CheckAllFieldsContains(EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        // Check Subject
        if (ContainsIgnoreCase(email.Subject, searchValue))
        {
            _logger.LogDebug("Contains match found in Subject for rule: {RuleId}", ruleId);
            return new MatchResult { IsMatch = true, MatchLocation = "Subject", MatchedText = email.Subject };
        }

        // Check Sender Name
        if (ContainsIgnoreCase(email.SenderName, searchValue))
        {
            _logger.LogDebug("Contains match found in SenderName for rule: {RuleId}", ruleId);
            return new MatchResult { IsMatch = true, MatchLocation = "SenderName", MatchedText = email.SenderName };
        }

        // Check Body
        if (ContainsIgnoreCase(email.Body, searchValue))
        {
            _logger.LogDebug("Contains match found in Body for rule: {RuleId}", ruleId);
            return new MatchResult { IsMatch = true, MatchLocation = "Body", MatchedText = TruncateForLogging(email.Body) };
        }

        // Check Recipient
        if (ContainsIgnoreCase(email.From, searchValue))
        {
            _logger.LogDebug("Contains match found in Recipient for rule: {RuleId}", ruleId);
            return new MatchResult { IsMatch = true, MatchLocation = "Recipient", MatchedText = email.From };
        }

        return new MatchResult { IsMatch = false, MatchLocation = "No match in any field" };
    }

    private MatchResult CheckAllFieldsExact(EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        if (EqualsIgnoreCase(email.Subject, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Subject", MatchedText = email.Subject };
        if (EqualsIgnoreCase(email.SenderName, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderName", MatchedText = email.SenderName };
        if (EqualsIgnoreCase(email.SenderAddress, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderAddress", MatchedText = email.SenderAddress };
        if (EqualsIgnoreCase(email.From, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Recipient", MatchedText = email.From };
        return new MatchResult { IsMatch = false, MatchLocation = "No exact match in any field" };
    }

    private MatchResult CheckAllFieldsStartsWith(EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        if (StartsWithIgnoreCase(email.Subject, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Subject", MatchedText = email.Subject };
        if (StartsWithIgnoreCase(email.SenderName, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderName", MatchedText = email.SenderName };
        if (StartsWithIgnoreCase(email.SenderAddress, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderAddress", MatchedText = email.SenderAddress };
        if (StartsWithIgnoreCase(email.Body, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Body", MatchedText = TruncateForLogging(email.Body) };
        if (StartsWithIgnoreCase(email.From, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Recipient", MatchedText = email.From };
        return new MatchResult { IsMatch = false, MatchLocation = "No starts-with match in any field" };
    }

    private MatchResult CheckAllFieldsEndsWith(EmailReceivedEventArgs email, string searchValue, object ruleId)
    {
        if (EndsWithIgnoreCase(email.Subject, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Subject", MatchedText = email.Subject };
        if (EndsWithIgnoreCase(email.SenderName, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderName", MatchedText = email.SenderName };
        if (EndsWithIgnoreCase(email.SenderAddress, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "SenderAddress", MatchedText = email.SenderAddress };
        if (EndsWithIgnoreCase(email.Body, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Body", MatchedText = TruncateForLogging(email.Body) };
        if (EndsWithIgnoreCase(email.From, searchValue))
            return new MatchResult { IsMatch = true, MatchLocation = "Recipient", MatchedText = email.From };
        return new MatchResult { IsMatch = false, MatchLocation = "No ends-with match in any field" };
    }

    private MatchResult CheckAllFieldsRegex(EmailReceivedEventArgs email, Regex regex, string pattern, object ruleId)
    {
        // Check Subject
        if (IsRegexMatch(regex, email.Subject))
        {
            var match = regex.Match(email.Subject);
            _logger.LogDebug("Regex match found in Subject for rule: {RuleId}. Matched: '{MatchedValue}'", ruleId, match.Value);
            return new MatchResult { IsMatch = true, MatchLocation = "Subject", MatchedText = email.Subject, RegexMatch = match.Value };
        }

        // Check Body
        if (IsRegexMatch(regex, email.Body))
        {
            var match = regex.Match(email.Body);
            _logger.LogDebug("Regex match found in Body for rule: {RuleId}. Matched: '{MatchedValue}'", ruleId, match.Value);
            return new MatchResult { IsMatch = true, MatchLocation = "Body", MatchedText = TruncateForLogging(email.Body), RegexMatch = match.Value };
        }

        // Check Sender Name
        if (IsRegexMatch(regex, email.SenderName))
        {
            var match = regex.Match(email.SenderName);
            _logger.LogDebug("Regex match found in SenderName for rule: {RuleId}. Matched: '{MatchedValue}'", ruleId, match.Value);
            return new MatchResult { IsMatch = true, MatchLocation = "SenderName", MatchedText = email.SenderName, RegexMatch = match.Value };
        }

        // Check Sender Email
        if (IsRegexMatch(regex, email.SenderAddress))
        {
            var match = regex.Match(email.SenderAddress);
            _logger.LogDebug("Regex match found in SenderAddress for rule: {RuleId}. Matched: '{MatchedValue}'", ruleId, match.Value);
            return new MatchResult { IsMatch = true, MatchLocation = "SenderAddress", MatchedText = email.SenderAddress, RegexMatch = match.Value };
        }

        // Check Recipient
        if (IsRegexMatch(regex, email.From))
        {
            var match = regex.Match(email.From);
            _logger.LogDebug("Regex match found in Recipient for rule: {RuleId}. Matched: '{MatchedValue}'", ruleId, match.Value);
            return new MatchResult { IsMatch = true, MatchLocation = "Recipient", MatchedText = email.From, RegexMatch = match.Value };
        }

        return new MatchResult { IsMatch = false, MatchLocation = "No regex match in any field" };
    }

    private MatchResult CheckSingleFieldContains(string fieldName, string? fieldValue, string searchValue)
    {
        var isMatch = ContainsIgnoreCase(fieldValue, searchValue);
        _logger.LogDebug("Contains check in {FieldName}: {IsMatch}. Field value: '{FieldValue}'",
            fieldName, isMatch, TruncateForLogging(fieldValue));

        return new MatchResult
        {
            IsMatch = isMatch,
            MatchLocation = isMatch ? fieldName : $"No match in {fieldName}",
            MatchedText = isMatch ? fieldValue : null
        };
    }

    private MatchResult CheckSingleFieldExact(string fieldName, string? fieldValue, string searchValue)
    {
        var isMatch = EqualsIgnoreCase(fieldValue, searchValue);
        _logger.LogDebug("Exact check in {FieldName}: {IsMatch}. Field value: '{FieldValue}'",
            fieldName, isMatch, TruncateForLogging(fieldValue));

        return new MatchResult
        {
            IsMatch = isMatch,
            MatchLocation = isMatch ? fieldName : $"No match in {fieldName}",
            MatchedText = isMatch ? fieldValue : null
        };
    }

    private MatchResult CheckSingleFieldStartsWith(string fieldName, string? fieldValue, string searchValue)
    {
        var isMatch = StartsWithIgnoreCase(fieldValue, searchValue);
        _logger.LogDebug("StartsWith check in {FieldName}: {IsMatch}. Field value: '{FieldValue}'",
            fieldName, isMatch, TruncateForLogging(fieldValue));

        return new MatchResult
        {
            IsMatch = isMatch,
            MatchLocation = isMatch ? fieldName : $"No match in {fieldName}",
            MatchedText = isMatch ? fieldValue : null
        };
    }

    private MatchResult CheckSingleFieldEndsWith(string fieldName, string? fieldValue, string searchValue)
    {
        var isMatch = EndsWithIgnoreCase(fieldValue, searchValue);
        _logger.LogDebug("EndsWith check in {FieldName}: {IsMatch}. Field value: '{FieldValue}'",
            fieldName, isMatch, TruncateForLogging(fieldValue));

        return new MatchResult
        {
            IsMatch = isMatch,
            MatchLocation = isMatch ? fieldName : $"No match in {fieldName}",
            MatchedText = isMatch ? fieldValue : null
        };
    }

    private MatchResult CheckSingleFieldRegex(string fieldName, string? fieldValue, Regex regex, string pattern)
    {
        var isMatch = IsRegexMatch(regex, fieldValue);

        if (isMatch && !string.IsNullOrWhiteSpace(fieldValue))
        {
            var match = regex.Match(fieldValue);
            _logger.LogDebug("Regex check in {FieldName}: {IsMatch}. Matched: '{MatchedValue}'. Field value: '{FieldValue}'",
                fieldName, isMatch, match.Value, TruncateForLogging(fieldValue));

            return new MatchResult
            {
                IsMatch = true,
                MatchLocation = fieldName,
                MatchedText = fieldValue,
                RegexMatch = match.Value
            };
        }

        _logger.LogDebug("Regex check in {FieldName}: {IsMatch}. Field value: '{FieldValue}'",
            fieldName, isMatch, TruncateForLogging(fieldValue));

        return new MatchResult
        {
            IsMatch = false,
            MatchLocation = $"No regex match in {fieldName}",
            MatchedText = fieldValue
        };
    }

    // Helper methods
    private static bool ContainsIgnoreCase(string? text, string searchValue)
    {
        return !string.IsNullOrEmpty(text) &&
               text.Contains(searchValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EqualsIgnoreCase(string? text, string searchValue)
    {
        return string.Equals(text, searchValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool StartsWithIgnoreCase(string? text, string searchValue)
    {
        return !string.IsNullOrEmpty(text) &&
               text.StartsWith(searchValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool EndsWithIgnoreCase(string? text, string searchValue)
    {
        return !string.IsNullOrEmpty(text) &&
               text.EndsWith(searchValue, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRegexMatch(Regex regex, string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && regex.IsMatch(text);
    }

    private static string? TruncateForLogging(string? text, int maxLength = 100)
    {
        if (string.IsNullOrEmpty(text))
            return text;

        return text.Length <= maxLength ? text : text.Substring(0, maxLength) + "...";
    }

    // Result class to hold match information
    public class MatchResult
    {
        public bool IsMatch { get; set; }
        public string MatchLocation { get; set; } = string.Empty;
        public string? MatchedText { get; set; }
        public string? RegexMatch { get; set; }  // The actual regex matched substring
    }
}
