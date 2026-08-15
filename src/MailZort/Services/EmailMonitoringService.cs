using System.Collections.Concurrent;
using MailKit;
using MailZort.Data;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace MailZort.Services
{

    public class EmailMonitoringService : BackgroundService
    {
        private readonly ILogger<EmailMonitoringService> _logger;
        private readonly EmailSettings _config;
        private ImapClient? _client;
        private CancellationTokenSource? _idleDoneSource;
        private volatile bool _newMessagesFlag = false;
        private int _lastProcessedCount = 0;
        private readonly IBatchRuleProcessor _batchRuleProcessor;
        private const int ReconnectDelayMs = 600000;
        private const int IdleRetryDelayMs = 5000;
        private const int IdleTimeoutMinutes = 9;
        private const int HourlyReprocessIntervalMs = 7200000; // 2 hour in milliseconds
        private readonly IEmailMover _emailMover;
        private readonly ISenderStore _senders;
        private readonly IMessageLocationStore _locations;
        private readonly IActivityStore _activity;
        private readonly ISettingsStore _settings;
        private readonly IRuleStore _rules;
        private readonly IPendingActionStore _pendingActions;
        private readonly MailServiceStatus _status;
        private CancellationToken _serviceCancellationToken;
        private readonly ConcurrentQueue<EmailMoveOperation> _moveQueue = new();
        private readonly ConcurrentQueue<EmailFlagOperation> _flagQueue = new();
        private readonly ConcurrentQueue<EmailPurgeOperation> _purgeQueue = new();
        private DateTime _lastFullReprocess = DateTime.MinValue;
        private const string JunkFolderKeyword = "junk";
        private const string BlacklistRuleName = "Blacklisted sender";
        private const string TrustedRuleName = "Trusted sender";
        /// <summary>Sender details for every message seen in the current observation pass, by message key.</summary>
        private readonly Dictionary<string, ObservedMessage> _observedSenders = new(StringComparer.Ordinal);

        public EmailMonitoringService(
            ILogger<EmailMonitoringService> logger,
            EmailSettings config,
            IEmailMover emailMover,
            IBatchRuleProcessor batchRuleProcessor,
            ISenderStore senders,
            IMessageLocationStore locations,
            IActivityStore activity,
            ISettingsStore settings,
            IRuleStore rules,
            IPendingActionStore pendingActions,
            MailServiceStatus status)
        {
            _logger = logger;
            _config = config;
            _emailMover = emailMover;
            _batchRuleProcessor = batchRuleProcessor;
            _senders = senders;
            _locations = locations;
            _activity = activity;
            _settings = settings;
            _rules = rules;
            _pendingActions = pendingActions;
            _status = status;

            // The portal can queue a move while we are sitting in IDLE - wake up for it.
            _pendingActions.ActionQueued += OnActionQueued;
        }

        private void OnActionQueued() => WakeFromIdle();

        /// <summary>
        /// Breaks out of IDLE so queued work runs now. The token source is swapped and disposed
        /// each cycle, so losing the race with that is expected and harmless.
        /// </summary>
        private void WakeFromIdle()
        {
            var source = _idleDoneSource;
            if (source == null)
                return;

            try
            {
                source.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The IDLE cycle already ended on its own.
            }
        }

        /// <summary>Keeps the activity table from growing without bound.</summary>
        private async Task TrimActivityHistoryAsync(CancellationToken cancellationToken)
        {
            var retentionDays = _settings.GetInt(SettingKeys.ActivityRetentionDays, 90);
            if (retentionDays <= 0)
                return;

            try
            {
                var removed = await _activity.PurgeOlderThanAsync(
                    DateTime.UtcNow.AddDays(-retentionDays), cancellationToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Trimmed {Count} activity entries older than {Days} days",
                        removed, retentionDays);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not trim activity history");
            }
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _serviceCancellationToken = stoppingToken;
            _logger.LogInformation("Email monitoring service starting");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndMonitorAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("Email monitoring service is stopping");
                    _status.SetDisconnected(null, null);
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in email monitoring. Retrying in {DelaySeconds} seconds...",
                        ReconnectDelayMs / 1000);
                    _status.SetDisconnected(ex.Message, TimeSpan.FromMilliseconds(ReconnectDelayMs));
                    await Task.Delay(ReconnectDelayMs, stoppingToken);
                }
            }
        }

        private async Task ConnectAndMonitorAsync(CancellationToken cancellationToken)
        {
            _client = new ImapClient();

            try
            {
                await ConnectToServerAsync(_client, cancellationToken);

                var inbox = _client.Inbox;
                await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                _logger.LogInformation("Connected to mail server");
                _status.SetConnected(_client.Capabilities.HasFlag(ImapCapabilities.Idle));
                await ProcessMails(false, cancellationToken);
                _lastFullReprocess = DateTime.UtcNow; // Track when we last did a full reprocess

                // Subscribe to events
                inbox.CountChanged += OnCountChanged;
                inbox.MessageExpunged += OnMessageExpunged;

                _logger.LogDebug("Now monitoring for new emails");
                await MonitorForNewEmailsAsync(cancellationToken);
            }
            finally
            {
                // Cleanup: remove event handlers before disconnecting
                if (_client?.Inbox != null)
                {
                    _client.Inbox.CountChanged -= OnCountChanged;
                    _client.Inbox.MessageExpunged -= OnMessageExpunged;
                }
                await DisconnectAsync(_client, cancellationToken);
            }
        }

        private async Task ConnectToServerAsync(ImapClient client, CancellationToken cancellationToken)
        {
            _logger.LogDebug("Connecting to IMAP server {Server}:{Port}", _config.Server, _config.Port);

            _status.SetConnecting(_config.Server, _config.Username);
            await client.ConnectAsync(_config.Server, _config.Port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken);
        }

        private async Task ProcessMails(bool lastSeenOnly, CancellationToken cancellationToken)
        {
            if (_client?.Inbox == null)
                return;

            var totalEmails = _client.Inbox.Count;
            var passStopwatch = System.Diagnostics.Stopwatch.StartNew();
            _status.SetProcessing(lastSeenOnly ? "Checking new mail" : "Full mailbox pass");
            _logger.LogDebug("Processing {TotalEmails} existing emails", totalEmails);


            IList<IMessageSummary>? fetchedMessages = null;
            var flags = MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.BodyStructure | MessageSummaryItems.Full;
            var emailsToProcess = new List<EmailReceivedEventArgs>();
            if (lastSeenOnly)
            {
                var items = await _client.Inbox.SearchAsync(SearchQuery.NotSeen, cancellationToken);
                fetchedMessages = await _client.Inbox.FetchAsync(items, flags, cancellationToken);
            }
            else
            {
                fetchedMessages = await _client.Inbox.FetchAsync(0, totalEmails - 1, flags, cancellationToken);
            }
            if (fetchedMessages == null || fetchedMessages.Count == 0)
            {
                // Still fall through: an empty inbox does not mean there is nothing to
                // observe in Junk, or nothing to rescue out of it.
                _logger.LogDebug("No emails to process in the inbox");
                fetchedMessages = Array.Empty<IMessageSummary>();
            }
            var futureDated = new List<IMessageSummary>();
            var blacklisted = new List<IMessageSummary>();

            for (int i = 0; i < fetchedMessages.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var message = fetchedMessages[i];

                    // Future-dated mail is quarantined before anything else sees it - no body fetch,
                    // no rules, no Trash. Just a log line and a permanent delete.
                    if (IsFutureDated(message))
                    {
                        futureDated.Add(message);
                        continue;
                    }

                    // Blacklisted senders go straight back to Junk without troubling the rules.
                    if (IsBlacklistedSender(message))
                    {
                        blacklisted.Add(message);
                        continue;
                    }

                    await ProcessSingleEmail(message, _client.Inbox, emailsToProcess);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing email at index {Index}", i);
                }
            }

            await QueueFutureDatedPurgeAsync(futureDated, _client.Inbox.FullName, cancellationToken);
            await QueueBlacklistedToJunkAsync(blacklisted, _client.Inbox.FullName, cancellationToken);

            _lastProcessedCount = totalEmails;
            _logger.LogDebug("Finished processing {TotalEmails} existing emails", totalEmails);
            var triggers = await this.ProcessBatchAsync(emailsToProcess);

            // Run inbox cleanup after rule processing
            if (_settings.GetBool(SettingKeys.InboxCleanupEnabled, _config.InboxCleanupEnabled))
            {
                var ruleMatchedIds = new HashSet<UniqueId>(triggers.Select(t => t.Id));
                ProcessInboxCleanup(emailsToProcess, ruleMatchedIds);
            }

            // Watching folder-to-folder moves needs a complete picture of the inbox,
            // so it only runs on a full pass - not on the new-mail-only path.
            if (ReputationEnabled && !lastSeenOnly)
            {
                await ObserveSenderReputationAsync(emailsToProcess, cancellationToken);
            }

            if (!lastSeenOnly)
            {
                await TrimActivityHistoryAsync(cancellationToken);
            }

            passStopwatch.Stop();
            _status.RecordPass(!lastSeenOnly, emailsToProcess.Count, triggers.Count, passStopwatch.Elapsed,
                _client.Inbox.Count, TimeSpan.FromMilliseconds(HourlyReprocessIntervalMs));
            _status.SetIdle();

            // Small delay after processing existing emails
            await Task.Delay(50, cancellationToken);
        }

        private async Task<List<RuleTrigger>> ProcessBatchAsync(List<EmailReceivedEventArgs> emailsToProcess)
        {
            if (!emailsToProcess.Any())
            {
                _logger.LogDebug("No emails in queue to process");
                return new List<RuleTrigger>();
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogDebug("Starting batch processing of {EmailCount} emails", emailsToProcess.Count);

            // Process emails in batch
            var triggers = _batchRuleProcessor.ProcessEmailBatch(emailsToProcess);

            // Execute triggers if any matches found
            var emailsMoved = 0;
            if (triggers.Any())
            {
                // Extract and queue flag operations
                var flagOps = _emailMover.ExtractFlagOperations(triggers);
                foreach (var flagOp in flagOps)
                {
                    _flagQueue.Enqueue(flagOp);
                }

                var ops = _emailMover.ExecuteTriggers(triggers);
                _logger.LogDebug("Executed {OperationCount} move operations and {FlagCount} flag operations",
                    ops.Count, flagOps.Count);
                foreach (var op in ops)
                {
                    this._moveQueue.Enqueue(op);
                }

                emailsMoved = triggers.Count(t => t.Action == RuleAction.Move);
            }

            stopwatch.Stop();
            var batchEventArgs = new BatchProcessingEventArgs
            {
                EmailsProcessed = emailsToProcess.Count,
                RulesMatched = triggers.Count,
                EmailsMoved = emailsMoved,
                ProcessingTime = stopwatch.Elapsed
            };
            _logger.LogInformation("Processed {EmailsProcessed} emails, {RulesMatched} matched, {EmailsMoved} moved in {ProcessingTime}ms",
                 batchEventArgs.EmailsProcessed, batchEventArgs.RulesMatched, batchEventArgs.EmailsMoved, batchEventArgs.ProcessingTime.TotalMilliseconds);

            return triggers;
        }

        private async Task ProcessSingleEmail(IMessageSummary message, IMailFolder folder, List<EmailReceivedEventArgs> processList)
        {

            string senderName = message.Envelope.From.FirstOrDefault()?.Name ?? "";
            string senderEmail = message.Envelope.From.FirstOrDefault()?.ToString() ?? "";

            var senderNames = string.Join(";", message.Envelope.From.Select(x => x.Name ?? string.Empty));
            var senderAddresses = string.Join(";", message.Envelope.From.OfType<MailboxAddress>().Select(x => x.Address));

            TextPart? bodyPart = null;
            if (message.HtmlBody != null)
            {
                bodyPart = await folder.GetBodyPartAsync(message.UniqueId, message.HtmlBody) as TextPart;
            }
            else if (message.TextBody != null)
            {
                bodyPart = await folder.GetBodyPartAsync(message.UniqueId, message.TextBody) as TextPart;
            }

            var emailArgs = new EmailReceivedEventArgs
            {
                From = message.Envelope.From.FirstOrDefault()?.ToString() ?? string.Empty,
                SenderName = senderNames,
                SenderAddress = senderAddresses,
                Subject = message.Envelope.Subject ?? string.Empty,
                Body = bodyPart != null ? bodyPart.Text : "",
                Recipients = string.Join(";", message.Envelope.To.Select(x => x.ToString())),

                // UtcDateTime, not DateTime: the latter is the sender's wall clock with the offset
                // thrown away, which skews every age filter by that sender's timezone.
                ReceivedDate = message.Date.UtcDateTime,
                Folder = folder.Name,
                IsImportant = message.Flags.HasValue && message.Flags.Value.HasFlag(MessageFlags.Flagged),
                IsRead = message.Flags.HasValue && message.Flags.Value.HasFlag(MessageFlags.Seen),
                UniqueId = message.UniqueId,
                MessageKey = MessageKeyFor(message)
            };
            if (emailArgs.IsImportant)
            {
                _logger.LogDebug("Important email detected (skipping rules): {Subject} from {Sender} at {Date}",
                    emailArgs.Subject, emailArgs.SenderName, emailArgs.ReceivedDate);
            }
            processList.Add(emailArgs);

        }

        /// <summary>
        /// Identity that survives a move between folders. Message-Id is preserved by every
        /// server on MOVE/COPY; the fallback covers the rare message that has none.
        /// </summary>
        /// <summary>
        /// True only for a real Message-Id. Synthesised keys (the '~' prefix) identify a message
        /// across passes but cannot be found again by header search, so they cannot be moved back.
        /// </summary>
        private static bool IsFindableKey(string? key) =>
            !string.IsNullOrEmpty(key) && !key.StartsWith('~');

        private static string MessageKeyFor(IMessageSummary message)
        {
            var messageId = message.Envelope?.MessageId;
            if (!string.IsNullOrWhiteSpace(messageId))
                return messageId.Trim();

            var sender = message.Envelope?.From.FirstOrDefault()?.ToString() ?? string.Empty;
            var subject = message.Envelope?.Subject ?? string.Empty;
            return $"~{sender}|{subject}|{message.Date.UtcTicks}";
        }

        private static string PrimarySenderAddress(IMessageSummary message) =>
            message.Envelope?.From.OfType<MailboxAddress>().FirstOrDefault()?.Address ?? string.Empty;

        private bool ReputationEnabled =>
            _settings.GetBool(SettingKeys.SenderReputationEnabled, _config.SenderReputationEnabled);

        private bool IsBlacklistedSender(IMessageSummary message)
        {
            if (!ReputationEnabled)
                return false;

            // Never second-guess a message you flagged yourself.
            if (message.Flags.HasValue && message.Flags.Value.HasFlag(MessageFlags.Flagged))
                return false;

            return _senders.IsBlacklisted(PrimarySenderAddress(message));
        }

        private async Task QueueBlacklistedToJunkAsync(List<IMessageSummary> messages, string sourceFolder,
            CancellationToken cancellationToken)
        {
            if (messages.Count == 0)
                return;

            // Our own move must not be read back as the user demoting the sender again.
            await _locations.SuppressAsync(messages.Select(MessageKeyFor), cancellationToken);

            _moveQueue.Enqueue(new EmailMoveOperation
            {
                SourceFolder = sourceFolder,
                DestinationFolder = JunkFolderKeyword,
                EmailIds = messages.Select(m => m.UniqueId).ToList(),
                Emails = messages.Select(m => new Email
                {
                    MessageIndex = (int)m.UniqueId.Id,
                    Subject = m.Envelope?.Subject ?? string.Empty,
                    SenderName = m.Envelope?.From.FirstOrDefault()?.Name ?? string.Empty,
                    SenderEmailaddress = PrimarySenderAddress(m),
                    Date = m.Date,
                    Folder = sourceFolder,
                    MoveTo = JunkFolderKeyword,
                    Rule = BlacklistRuleName,
                    MessageKey = MessageKeyFor(m)
                }).ToList()
            });

            _logger.LogInformation("Queued {Count} emails from blacklisted senders for the Junk folder", messages.Count);
        }

        /// <summary>
        /// Compares where every watched message is now against where it was on the previous pass.
        /// Out of Junk into a real folder means you trust the sender; into Junk from a real folder
        /// counts against them.
        /// </summary>
        private async Task ObserveSenderReputationAsync(List<EmailReceivedEventArgs> inboxEmails,
            CancellationToken cancellationToken)
        {
            try
            {
                var junkFolder = GetJunkFolder();
                if (junkFolder == null)
                {
                    _logger.LogWarning("Sender reputation is enabled but no Junk folder could be resolved - skipping");
                    return;
                }

                var trashFullName = GetDestinationFolder("trash").FullName;
                var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
                var previous = await _locations.GetSnapshotAsync(cancellationToken);
                var suppressed = await _locations.GetSuppressedAsync(cancellationToken);
                var junkMessages = new List<ObservedMessage>();

                _observedSenders.Clear();

                // The inbox has already been fetched by the caller - reuse it rather than refetch.
                var inboxName = _client!.Inbox.FullName;
                foreach (var email in inboxEmails)
                {
                    if (string.IsNullOrEmpty(email.MessageKey))
                        continue;

                    snapshot[email.MessageKey] = inboxName;
                    _observedSenders[email.MessageKey] = new ObservedMessage
                    {
                        Key = email.MessageKey,
                        Uid = email.UniqueId,
                        SenderAddress = email.SenderAddress,
                        SenderName = email.SenderName,
                        Subject = email.Subject,
                        Date = email.ReceivedDate
                    };
                }

                var watchFolders = new List<string> { junkFolder.FullName, trashFullName };
                var configured = _settings.GetList(SettingKeys.ReputationWatchFolders);
                watchFolders.AddRange(configured.Count > 0 ? configured : _config.ReputationWatchFolders);

                foreach (var folderName in watchFolders.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (string.Equals(folderName, inboxName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var observed = await ObserveFolderAsync(folderName, cancellationToken);
                    foreach (var message in observed)
                    {
                        snapshot[message.Key] = folderName;
                    }

                    if (string.Equals(folderName, junkFolder.FullName, StringComparison.OrdinalIgnoreCase))
                    {
                        junkMessages.AddRange(observed);
                    }
                }

                await DetectReputationEventsAsync(snapshot, previous, suppressed, junkFolder.FullName, trashFullName,
                    cancellationToken);

                // Replace first: it clears the consumed suppressions, then the rescues below add
                // fresh ones for the moves we are about to make.
                await _locations.ReplaceSnapshotAsync(snapshot, cancellationToken);
                await QueueTrustedRescuesAsync(junkMessages, junkFolder.FullName, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error observing sender reputation");
            }
            finally
            {
                // The caller expects the inbox to still be selected.
                if (_client?.Inbox is { IsOpen: false })
                {
                    await _client.Inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
                }
            }
        }

        private async Task DetectReputationEventsAsync(Dictionary<string, string> snapshot,
            IReadOnlyDictionary<string, string> previous, HashSet<string> suppressed, string junkName, string trashName,
            CancellationToken cancellationToken)
        {
            if (previous.Count == 0)
            {
                _logger.LogInformation("First sender reputation pass - recorded {Count} messages as the baseline",
                    snapshot.Count);
                return;
            }

            foreach (var (key, currentFolder) in snapshot)
            {
                if (!previous.TryGetValue(key, out var previousFolder))
                    continue; // Newly arrived - nothing moved.

                if (string.Equals(previousFolder, currentFolder, StringComparison.OrdinalIgnoreCase))
                    continue;

                var wasJunk = string.Equals(previousFolder, junkName, StringComparison.OrdinalIgnoreCase);
                var isJunk = string.Equals(currentFolder, junkName, StringComparison.OrdinalIgnoreCase);
                var isTrash = string.Equals(currentFolder, trashName, StringComparison.OrdinalIgnoreCase);
                var wasTrash = string.Equals(previousFolder, trashName, StringComparison.OrdinalIgnoreCase);

                // Junk -> Trash is a deletion, not a vote of confidence.
                var rescued = wasJunk && !isJunk && !isTrash;
                var demoted = !wasJunk && !wasTrash && isJunk;

                if (!rescued && !demoted)
                    continue;

                if (suppressed.Contains(key))
                {
                    _logger.LogDebug("Ignoring {From} -> {To} for '{Key}' - MailZort made that move",
                        previousFolder, currentFolder, key);
                    continue;
                }

                if (!_observedSenders.TryGetValue(key, out var details))
                {
                    _logger.LogDebug("Saw '{Key}' move {From} -> {To} but have no sender for it", key, previousFolder, currentFolder);
                    continue;
                }

                if (rescued)
                {
                    await _senders.RecordRescueAsync(details.SenderAddress, details.Subject, cancellationToken);
                }
                else
                {
                    await _senders.RecordSpamMoveAsync(details.SenderAddress, details.Subject, cancellationToken);
                }
            }
        }

        private async Task QueueTrustedRescuesAsync(List<ObservedMessage> junkMessages, string junkFolderName,
            CancellationToken cancellationToken)
        {
            var rescues = junkMessages
                .Where(m => _senders.IsTrusted(m.SenderAddress))
                .ToList();

            if (rescues.Count == 0)
                return;

            await _locations.SuppressAsync(rescues.Select(m => m.Key), cancellationToken);

            foreach (var message in rescues)
            {
                _logger.LogInformation("Rescuing email from trusted sender out of Junk: from='{From}' subject='{Subject}'",
                    message.SenderAddress, message.Subject);
            }

            _moveQueue.Enqueue(new EmailMoveOperation
            {
                SourceFolder = junkFolderName,
                DestinationFolder = _client!.Inbox.FullName,
                EmailIds = rescues.Select(m => m.Uid).ToList(),
                Emails = rescues.Select(m => new Email
                {
                    MessageIndex = (int)m.Uid.Id,
                    Subject = m.Subject,
                    SenderName = m.SenderName,
                    SenderEmailaddress = m.SenderAddress,
                    Date = m.Date,
                    Folder = junkFolderName,
                    MoveTo = _client!.Inbox.FullName,
                    Rule = TrustedRuleName,
                    MessageKey = m.Key
                }).ToList()
            });
        }

        private async Task<List<ObservedMessage>> ObserveFolderAsync(string folderName, CancellationToken cancellationToken)
        {
            var results = new List<ObservedMessage>();
            IMailFolder? folder = null;

            try
            {
                folder = GetDestinationFolder(folderName);
                await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

                var lookback = _settings.GetInt(SettingKeys.ReputationLookbackDays, _config.ReputationLookbackDays);
                var cutoff = DateTime.UtcNow.AddDays(-Math.Max(1, lookback));
                var uids = await folder.SearchAsync(SearchQuery.DeliveredAfter(cutoff), cancellationToken);
                if (uids.Count == 0)
                    return results;

                var summaries = await folder.FetchAsync(uids,
                    MessageSummaryItems.Envelope | MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate,
                    cancellationToken);

                foreach (var summary in summaries)
                {
                    var observed = new ObservedMessage
                    {
                        Key = MessageKeyFor(summary),
                        Uid = summary.UniqueId,
                        SenderAddress = PrimarySenderAddress(summary),
                        SenderName = summary.Envelope?.From.FirstOrDefault()?.Name ?? string.Empty,
                        Subject = summary.Envelope?.Subject ?? string.Empty,
                        Date = summary.Date
                    };
                    results.Add(observed);
                    _observedSenders[observed.Key] = observed;
                }

                _logger.LogDebug("Observed {Count} recent messages in {Folder}", results.Count, folderName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not observe folder {Folder} for sender reputation", folderName);
            }
            finally
            {
                if (folder is { IsOpen: true })
                {
                    await folder.CloseAsync(cancellationToken: cancellationToken);
                }
            }

            return results;
        }

        private string JunkFolderName
        {
            get
            {
                var configured = _settings.Get(SettingKeys.JunkFolder);
                return string.IsNullOrWhiteSpace(configured) ? _config.Junk : configured;
            }
        }

        private IMailFolder? GetJunkFolder()
        {
            try
            {
                if (_client!.Capabilities.HasFlag(ImapCapabilities.SpecialUse))
                {
                    var special = _client.GetFolder(SpecialFolder.Junk);
                    if (special != null)
                        return special;
                }

                return _client.GetFolder(JunkFolderName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not resolve the Junk folder ('{Junk}')", JunkFolderName);
                return null;
            }
        }

        private sealed class ObservedMessage
        {
            public required string Key { get; init; }
            public UniqueId Uid { get; init; }
            public string SenderAddress { get; init; } = string.Empty;
            public string SenderName { get; init; } = string.Empty;
            public string Subject { get; init; } = string.Empty;
            public DateTimeOffset Date { get; init; }
        }

        private bool IsFutureDated(IMessageSummary message)
        {
            if (!_settings.GetBool(SettingKeys.PurgeFutureDatedEnabled, _config.PurgeFutureDatedEnabled))
                return false;

            var minutes = _settings.GetInt(SettingKeys.FutureDatedToleranceMinutes, _config.FutureDatedToleranceMinutes);
            return message.Date > DateTimeOffset.UtcNow.Add(TimeSpan.FromMinutes(Math.Max(0, minutes)));
        }

        private async Task QueueFutureDatedPurgeAsync(List<IMessageSummary> messages, string folderName,
            CancellationToken cancellationToken)
        {
            if (messages.Count == 0)
                return;

            var purged = messages.Select(m => new PurgedEmail
            {
                From = m.Envelope?.From.FirstOrDefault()?.ToString() ?? string.Empty,
                Subject = m.Envelope?.Subject ?? string.Empty,
                Date = m.Date
            }).ToList();

            foreach (var email in purged)
            {
                _logger.LogInformation("Deleting future-dated email: from='{From}' subject='{Subject}' date='{Date}'",
                    email.From, email.Subject, email.Date);
            }

            // Logged now rather than at expunge time: this is the last point the message exists.
            await _activity.LogManyAsync(messages.Select(m => new DbActivity
            {
                EventType = ActivityType.Purged,
                MessageKey = MessageKeyFor(m),
                Subject = m.Envelope?.Subject ?? string.Empty,
                SenderName = m.Envelope?.From.FirstOrDefault()?.Name ?? string.Empty,
                SenderAddress = PrimarySenderAddress(m),
                FromFolder = folderName,
                RuleName = "Future-dated",
                Detail = $"Dated {m.Date:u}, permanently deleted",
                CanUndo = false
            }), cancellationToken);

            _purgeQueue.Enqueue(new EmailPurgeOperation
            {
                SourceFolder = folderName,
                EmailIds = messages.Select(m => m.UniqueId).ToList(),
                Emails = purged,
                Reason = "Future-dated"
            });
        }

        private void ProcessInboxCleanup(List<EmailReceivedEventArgs> emails, HashSet<UniqueId> ruleMatchedIds)
        {
            var daysOld = _settings.GetInt(SettingKeys.InboxCleanupDaysOld, _config.InboxCleanupDaysOld);
            var importantFolder = _settings.Get(SettingKeys.ImportantFolder) ?? _config.ImportantFolder;
            var cutoffDate = DateTime.UtcNow.AddDays(-daysOld);
            var candidates = emails
                .Where(e => !ruleMatchedIds.Contains(e.UniqueId))
                .Where(e => e.Folder.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
                .Where(e => e.ReceivedDate < cutoffDate)
                .Where(e => !_senders.IsTrusted(e.SenderAddress))
                .ToList();

            if (!candidates.Any())
            {
                _logger.LogDebug("No inbox emails older than {Days} days for cleanup", daysOld);
                return;
            }

            var important = candidates.Where(e => e.IsImportant).ToList();
            var nonImportant = candidates.Where(e => !e.IsImportant).ToList();

            _logger.LogInformation("Inbox cleanup: {ImportantCount} important -> {ImportantFolder}, {NonImportantCount} non-important -> Trash",
                important.Count, importantFolder, nonImportant.Count);

            if (important.Any())
            {
                _moveQueue.Enqueue(new EmailMoveOperation
                {
                    SourceFolder = "INBOX",
                    DestinationFolder = importantFolder,
                    EmailIds = important.Select(e => e.UniqueId).ToList(),
                    Emails = important.Select(e => new Email
                    {
                        MessageIndex = (int)e.UniqueId.Id,
                        Subject = e.Subject,
                        SenderName = e.SenderName,
                        SenderEmailaddress = e.SenderAddress,
                        Date = e.ReceivedDate,
                        Folder = "INBOX",
                        MoveTo = importantFolder,
                        Rule = "Inbox cleanup",
                        MessageKey = e.MessageKey
                    }).ToList()
                });
            }

            if (nonImportant.Any())
            {
                _moveQueue.Enqueue(new EmailMoveOperation
                {
                    SourceFolder = "INBOX",
                    DestinationFolder = "Trash",
                    EmailIds = nonImportant.Select(e => e.UniqueId).ToList(),
                    Emails = nonImportant.Select(e => new Email
                    {
                        MessageIndex = (int)e.UniqueId.Id,
                        Subject = e.Subject,
                        SenderName = e.SenderName,
                        SenderEmailaddress = e.SenderAddress,
                        Date = e.ReceivedDate,
                        Folder = "INBOX",
                        MoveTo = "Trash",
                        Rule = "Inbox cleanup",
                        MessageKey = e.MessageKey
                    }).ToList()
                });
            }
        }

        private async Task MonitorForNewEmailsAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested && _client?.IsConnected == true)
            {
                try
                {
                    // Check if it's time for hourly full reprocessing
                    var timeSinceLastReprocess = DateTime.UtcNow - _lastFullReprocess;
                    if (timeSinceLastReprocess.TotalMilliseconds >= HourlyReprocessIntervalMs)
                    {
                        _logger.LogDebug("Reprocess interval reached. Exiting IDLE to reprocess all emails");
                        await ProcessMails(false, stoppingToken);
                        _lastFullReprocess = DateTime.UtcNow;
                        continue; // Skip the IDLE cycle and immediately check again
                    }

                    // Portal-requested moves go first - someone is waiting on them.
                    await ProcessPendingActionsAsync();

                    // Process any queued permanent deletes first
                    if (_purgeQueue.Count > 0)
                    {
                        await ProcessQueuedPurgeOperationsAsync();
                    }

                    // Process any queued flag operations
                    if (_flagQueue.Count > 0)
                    {
                        await ProcessQueuedFlagOperationsAsync();
                    }

                    // Process any queued move operations
                    if (_moveQueue.Count > 0)
                    {
                        await ProcessQueuedMoveOperationsAsync();
                    }

                    // Ensure inbox is open before entering IDLE (move operations may have closed it)
                    if (!_client.Inbox.IsOpen)
                    {
                        await _client.Inbox.OpenAsync(FolderAccess.ReadOnly, stoppingToken);
                    }

                    // Moving mail out of the inbox lowers its count. Without re-baselining here,
                    // CountChanged never fires again until the count climbs back past the old
                    // high-water mark, and new mail sits unprocessed until the next full sweep.
                    // Only ever lower it, so arrivals during the moves are still noticed.
                    _lastProcessedCount = Math.Min(_lastProcessedCount, _client.Inbox.Count);

                    // Calculate remaining time until next full reprocess
                    var remainingTime = TimeSpan.FromMilliseconds(HourlyReprocessIntervalMs) - timeSinceLastReprocess;
                    var idleTimeout = TimeSpan.FromMinutes(IdleTimeoutMinutes);

                    // Use the shorter of the two timeouts
                    var actualTimeout = remainingTime < idleTimeout ? remainingTime : idleTimeout;

                    // Don't IDLE if the timeout would be very short
                    if (actualTimeout.TotalSeconds < 30)
                    {
                        await Task.Delay(1000, stoppingToken); // Brief delay before checking again
                        continue;
                    }

                    // Use a fresh CancellationTokenSource for each IDLE cycle
                    var idleDone = new CancellationTokenSource(actualTimeout);
                    _idleDoneSource = idleDone;

                    try
                    {
                        // Enter IDLE mode - will block until done token is canceled or timeout
                        if (_client.Capabilities.HasFlag(ImapCapabilities.Idle))
                        {
                            _logger.LogDebug("Entering IDLE mode for {Timeout} (next full reprocess in {NextReprocess})",
                                actualTimeout, remainingTime);
                            await _client.IdleAsync(idleDone.Token, stoppingToken);
                        }
                        else
                        {
                            // Fallback for servers that don't support IDLE
                            await Task.Delay(Math.Min((int)actualTimeout.TotalMilliseconds, ReconnectDelayMs), stoppingToken);
                            await CheckForNewEmails();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogDebug("Monitoring stopped due to service shutdown");
                            break;
                        }
                        // Expected when _idleDoneSource is triggered by event handlers, move queue, or timeout
                        _logger.LogDebug("IDLE interrupted for processing");
                    }
                    finally
                    {
                        _idleDoneSource = null;
                        idleDone.Dispose();
                    }

                    // After Idle() returns, check flags and handle events
                    if (_newMessagesFlag)
                    {
                        _newMessagesFlag = false;
                        await CheckForNewEmails();
                    }

                    // Brief delay before next IDLE cycle
                    await Task.Delay(100, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in IDLE monitoring loop");
                    await Task.Delay(IdleRetryDelayMs, stoppingToken);
                }
            }
        }

        private async Task CheckForNewEmails()
        {
            try
            {
                if (_client?.Inbox == null)
                    return;

                var currentCount = _client.Inbox.Count;
                if (currentCount > _lastProcessedCount)
                {
                    await ProcessMails(true, _serviceCancellationToken);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new emails");
            }
        }


        private async Task ProcessQueuedMoveOperationsAsync()
        {
            if (_client == null || !_client.IsConnected)
            {
                _logger.LogWarning("Cannot process move operations: client not connected");
                return;
            }

            var processedCount = 0;
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            while (_moveQueue.TryDequeue(out var moveOperation) && moveOperation != null)
            {
                try
                {
                    await ProcessSingleMoveOperationAsync(moveOperation);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing move operation from {Source} to {Destination}",
                        moveOperation.SourceFolder, moveOperation.DestinationFolder);
                }

                // Check for cancellation between operations
                if (_serviceCancellationToken.IsCancellationRequested)
                    break;
            }

            if (processedCount > 0)
            {
                stopwatch.Stop();
                _logger.LogDebug("Processed {Count} move operations in {ElapsedMs}ms",
                    processedCount, stopwatch.ElapsedMilliseconds);
            }
        }

        private async Task ProcessQueuedFlagOperationsAsync()
        {
            if (_client == null || !_client.IsConnected)
            {
                _logger.LogWarning("Cannot process flag operations: client not connected");
                return;
            }

            var processedCount = 0;

            while (_flagQueue.TryDequeue(out var flagOperation) && flagOperation != null)
            {
                try
                {
                    await ProcessSingleFlagOperationAsync(flagOperation);
                    processedCount++;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing flag operation for folder {Source}",
                        flagOperation.SourceFolder);
                }

                if (_serviceCancellationToken.IsCancellationRequested)
                    break;
            }

            if (processedCount > 0)
            {
                _logger.LogDebug("Processed {Count} flag operations", processedCount);
            }
        }

        /// <summary>
        /// Runs the moves the portal asked for. The UID changed when the message was moved, so
        /// each one is found again by Message-Id in whichever folder it now sits.
        /// </summary>
        private async Task ProcessPendingActionsAsync()
        {
            if (_client == null || !_client.IsConnected)
                return;

            var actions = await _pendingActions.TakePendingAsync(cancellationToken: _serviceCancellationToken);
            if (actions.Count == 0)
                return;

            foreach (var action in actions)
            {
                IMailFolder? source = null;
                try
                {
                    if (string.IsNullOrWhiteSpace(action.MessageKey))
                        throw new InvalidOperationException("No Message-Id recorded, cannot locate the message");

                    source = GetDestinationFolder(action.SourceFolder);
                    await source.OpenAsync(FolderAccess.ReadWrite, _serviceCancellationToken);

                    var uids = await source.SearchAsync(
                        SearchQuery.HeaderContains("Message-Id", action.MessageKey), _serviceCancellationToken);

                    if (uids.Count == 0)
                        throw new InvalidOperationException($"Message not found in {action.SourceFolder}");

                    var target = GetDestinationFolder(action.TargetFolder);
                    await source.MoveToAsync(uids, target, _serviceCancellationToken);

                    // This move is ours; it must not read back as the user's judgement next pass.
                    await _locations.SuppressAsync(new[] { action.MessageKey }, _serviceCancellationToken);
                    await _pendingActions.CompleteAsync(action.Id, _serviceCancellationToken);

                    if (action.ActivityId.HasValue)
                    {
                        await _activity.MarkUndoneAsync(action.ActivityId.Value, _serviceCancellationToken);
                    }

                    await _activity.LogAsync(new DbActivity
                    {
                        EventType = ActivityType.Restored,
                        MessageKey = action.MessageKey,
                        Subject = action.Subject,
                        SenderAddress = action.SenderAddress,
                        FromFolder = action.SourceFolder,
                        ToFolder = target.FullName,
                        RuleName = action.RequestedBy,
                        Detail = $"Moved back by {action.RequestedBy ?? "portal"}",
                        CanUndo = false
                    }, _serviceCancellationToken);

                    _logger.LogInformation("Moved '{Subject}' back from {Source} to {Target} for {User}",
                        action.Subject, action.SourceFolder, action.TargetFolder, action.RequestedBy);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Could not run pending action {Id} ({Source} -> {Target})",
                        action.Id, action.SourceFolder, action.TargetFolder);
                    await _pendingActions.FailAsync(action.Id, ex.Message, _serviceCancellationToken);
                }
                finally
                {
                    if (source is { IsOpen: true })
                    {
                        await source.CloseAsync();
                    }
                }
            }
        }

        private async Task ProcessQueuedPurgeOperationsAsync()
        {
            if (_client == null || !_client.IsConnected)
            {
                _logger.LogWarning("Cannot process purge operations: client not connected");
                return;
            }

            while (_purgeQueue.TryDequeue(out var purgeOperation) && purgeOperation != null)
            {
                try
                {
                    await ProcessSinglePurgeOperationAsync(purgeOperation);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing purge operation in {Source}", purgeOperation.SourceFolder);
                }

                if (_serviceCancellationToken.IsCancellationRequested)
                    break;
            }
        }

        private async Task ProcessSinglePurgeOperationAsync(EmailPurgeOperation purgeOperation)
        {
            if (!purgeOperation.EmailIds.Any())
                return;

            IMailFolder? folder = null;
            try
            {
                folder = _client!.GetFolder(purgeOperation.SourceFolder);
                await folder.OpenAsync(FolderAccess.ReadWrite);

                await folder.AddFlagsAsync(purgeOperation.EmailIds, MessageFlags.Deleted, true);
                await folder.ExpungeAsync(purgeOperation.EmailIds);

                foreach (var email in purgeOperation.Emails)
                {
                    _logger.LogInformation("Permanently deleted email ({Reason}): from='{From}' subject='{Subject}' date='{Date}'",
                        purgeOperation.Reason, email.From, email.Subject, email.Date);
                }

                _lastProcessedCount = Math.Max(0, _lastProcessedCount - purgeOperation.EmailIds.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error permanently deleting {Count} emails in {Folder}",
                    purgeOperation.EmailIds.Count, purgeOperation.SourceFolder);
                throw;
            }
            finally
            {
                if (folder is { IsOpen: true })
                {
                    await folder.CloseAsync();
                }
            }
        }

        private async Task ProcessSingleFlagOperationAsync(EmailFlagOperation flagOperation)
        {
            if (!flagOperation.EmailIds.Any())
                return;

            IMailFolder? folder = null;
            try
            {
                folder = _client!.GetFolder(flagOperation.SourceFolder);
                await folder.OpenAsync(FolderAccess.ReadWrite);
                await folder.AddFlagsAsync(flagOperation.EmailIds, MessageFlags.Flagged, true);

                _logger.LogInformation("Flagged {Count} emails as important in {Folder}",
                    flagOperation.EmailIds.Count, flagOperation.SourceFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error flagging emails in {Folder}", flagOperation.SourceFolder);
                throw;
            }
            finally
            {
                if (folder is { IsOpen: true })
                {
                    await folder.CloseAsync();
                }
            }
        }

        private async Task ProcessSingleMoveOperationAsync(EmailMoveOperation moveOperation)
        {
            if (!moveOperation.Emails.Any())
                return;

            IMailFolder? sourceFolder = null;
            try
            {
                // Open source folder in ReadWrite mode
                sourceFolder = _client!.GetFolder(moveOperation.SourceFolder);
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite);

                // Get destination folder
                var destinationFolder = GetDestinationFolder(moveOperation.DestinationFolder);

                // Perform the move
                await sourceFolder.MoveToAsync(moveOperation.EmailIds, destinationFolder);

                foreach (var email in moveOperation.Emails)
                {
                    _logger.LogInformation("Moved email: from='{From}' subject='{Subject}' to='{Destination}' rule='{Rule}'",
                        email.SenderEmailaddress, email.Subject, moveOperation.DestinationFolder, email.Rule ?? "unknown");
                }

                // The portal's history reads from these rows, and 'Move back' replays them in reverse.
                await _activity.LogManyAsync(moveOperation.Emails.Select(email => new DbActivity
                {
                    EventType = ActivityType.Moved,
                    MessageKey = email.MessageKey,
                    Subject = email.Subject,
                    SenderName = email.SenderName,
                    SenderAddress = email.SenderEmailaddress,
                    FromFolder = moveOperation.SourceFolder,
                    ToFolder = destinationFolder.FullName,
                    RuleName = email.Rule,
                    CanUndo = IsFindableKey(email.MessageKey)
                }), _serviceCancellationToken);

                await _rules.RecordMatchesAsync(
                    moveOperation.Emails.Select(e => e.Rule ?? string.Empty), _serviceCancellationToken);

                _logger.LogDebug("Moved {Count} emails from {Source} to {Destination}",
                    moveOperation.Emails.Count, moveOperation.SourceFolder, moveOperation.DestinationFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving emails from {Source} to {Destination}",
                    moveOperation.SourceFolder, moveOperation.DestinationFolder);
                throw;
            }
            finally
            {
                if (sourceFolder is { IsOpen: true })
                {
                    await sourceFolder.CloseAsync();
                }
            }
        }

        private IMailFolder GetDestinationFolder(string folderName)
        {
            if (string.Equals(folderName, "trash", StringComparison.OrdinalIgnoreCase))
            {
                var trashName = _settings.Get(SettingKeys.TrashFolder) ?? _config.Trash ?? "Trash";
                return _client!.Capabilities.HasFlag(ImapCapabilities.SpecialUse)
                    ? _client.GetFolder(SpecialFolder.Trash)
                    : _client.GetFolder(trashName);
            }

            if (string.Equals(folderName, JunkFolderKeyword, StringComparison.OrdinalIgnoreCase))
            {
                return GetJunkFolder() ?? _client!.GetFolder(JunkFolderName);
            }

            return _client!.GetFolder(folderName);
        }

        private void OnCountChanged(object? sender, EventArgs e)
        {
            var folder = (IMailFolder)sender!;
            if (folder.Count > _lastProcessedCount)
            {
                _logger.LogDebug("[Event] Inbox count increased to {CurrentCount} (was {LastCount}) - new message likely.",
                    folder.Count, _lastProcessedCount);
                _newMessagesFlag = true;
                WakeFromIdle();
            }
        }

        private void OnMessageExpunged(object? sender, MessageEventArgs e)
        {
            _logger.LogDebug("[Event] Message at index {Index} was expunged.", e.Index);
        }

        private async Task DisconnectAsync(ImapClient? client, CancellationToken cancellationToken)
        {
            if (client?.IsConnected == true)
            {
                try
                {
                    await client.DisconnectAsync(true, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error disconnecting from IMAP server");
                }
            }

            client?.Dispose();
        }

        public override async Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("Email monitoring service is stopping");

            // Cancel any ongoing IDLE operation
            WakeFromIdle();

            await base.StopAsync(cancellationToken);
        }

        bool _disposed = false;
        public override void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _idleDoneSource?.Dispose();
            _client?.Dispose();

            base.Dispose();
        }
    }
}
