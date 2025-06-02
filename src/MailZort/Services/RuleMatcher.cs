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
        if (email.IsRead)
        {
            if (DateTimeOffset.Now.Subtract(email.ReceivedDate).TotalHours > 2)
                return true;

        }
        if (rule.DaysOld <= 0)
            return true;

        var emailAge = DateTimeOffset.Now.Subtract(email.ReceivedDate).TotalDays;
        return emailAge >= rule.DaysOld;
    }
    // Enhanced version with detailed debug information
    public bool CheckRuleMatch(Rule rule, EmailReceivedEventArgs email)
    {
        _logger.LogDebug("Checking rule: {RuleId} with {ValueCount} values", rule.Name, rule.Values?.Count ?? 0);

        foreach (var value in rule.Values!)
        {
            _logger.LogDebug("Testing value: '{Value}' with expression type: {ExpressionType}", value, rule.ExpressionType);

            var matchResult = rule.ExpressionType switch
            {
                ExpressionType.Contains => CheckContainsMatchWithDebug(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                ExpressionType.MatchesRegex => CheckRegexMatchWithDebug(rule.LookIn, email, value, rule.Name ?? "Unknown Rule"),
                _ => new MatchResult { IsMatch = false, MatchLocation = "Unknown expression type" }
            };

            if (matchResult.IsMatch)
            {
                _logger.LogDebug("Match found! Rule: {RuleId}, Value: '{Value}', Location: {Location}",
                    rule.Name, value, matchResult.MatchLocation);

                var passesAge = PassesAgeFilter(rule, email);
                _logger.LogDebug("Age filter result: {PassesAge} for rule: {RuleId}", passesAge, rule.Name);

                if (passesAge)
                {
                    _logger.LogInformation("RULE MATCHED! Rule: {RuleId}, Value: '{Value}', Location: {Location}, Subject: '{Subject}'",
                        rule.Name, value, matchResult.MatchLocation, email.Subject);
                    return true;
                }
                else
                {
                    _logger.LogDebug("Match found but failed age filter. Rule: {RuleId}", rule.Name);
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
            LookIn.SenderEmail => CheckSingleFieldContains("SenderEmail", email.SenderAddress, searchValue),
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

        return new MatchResult { IsMatch = false, MatchLocation = "No match in any field" };
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

    // Helper methods (unchanged)
    private static bool ContainsIgnoreCase(string? text, string searchValue)
    {
        return !string.IsNullOrEmpty(text) &&
               text.Contains(searchValue, StringComparison.CurrentCultureIgnoreCase);
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
