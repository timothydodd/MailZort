using System.Collections.Concurrent;
using System.Data;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;
using ServiceStack.OrmLite;

namespace MailZort.Services
{

    public class EmailMonitoringService : BackgroundService
    {
        private readonly ILogger<EmailMonitoringService> _logger;
        private readonly EmailSettings _config;
        private readonly MailDb _mailDb;
        private ImapClient? _client;
        private CancellationTokenSource? _idleDoneSource;
        private bool _newMessagesFlag = false;
        private int _lastProcessedCount = 0;
        private readonly IBatchRuleProcessor _batchRuleProcessor;
        private const int ReconnectDelayMs = 600000;
        private const int IdleRetryDelayMs = 5000;
        private const int IdleTimeoutMinutes = 9;
        private const int HourlyReprocessIntervalMs = 7200000; // 2 hour in milliseconds
        private readonly IEmailMover _emailMover;
        private CancellationToken _serviceCancellationToken;
        private readonly ConcurrentQueue<EmailMoveOperation> _moveQueue = new();
        private DateTime _lastFullReprocess = DateTime.MinValue;


        public EmailMonitoringService(
            ILogger<EmailMonitoringService> logger,
            EmailSettings config,
            MailDb mailDb,
            IEmailMover emailMover,
            IBatchRuleProcessor batchRuleProcessor)
        {
            _logger = logger;
            _config = config;
            _mailDb = mailDb;
            _emailMover = emailMover;
            _batchRuleProcessor = batchRuleProcessor;
        }


        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _serviceCancellationToken = stoppingToken;
            _logger.LogInformation("📧 Email monitoring service starting...");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await ConnectAndMonitorAsync(stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("📧 Email monitoring service is stopping...");
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error in email monitoring. Retrying in {DelaySeconds} seconds...",
                        ReconnectDelayMs / 1000);
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

                _logger.LogInformation("✅ Connected successfully. Processing existing emails...");
                await ProcessMails(false, cancellationToken);
                _lastFullReprocess = DateTime.UtcNow; // Track when we last did a full reprocess

                // Subscribe to events
                inbox.CountChanged += OnCountChanged;
                inbox.MessageExpunged += OnMessageExpunged;

                _logger.LogInformation("👀 Now monitoring for new emails...");
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
            _logger.LogInformation("🔌 Connecting to IMAP server {Server}:{Port}...", _config.Server, _config.Port);

            await client.ConnectAsync(_config.Server, _config.Port, SecureSocketOptions.SslOnConnect, cancellationToken);
            await client.AuthenticateAsync(_config.Username, _config.Password, cancellationToken);
        }

        private async Task ProcessMails(bool lastSeenOnly, CancellationToken cancellationToken)
        {
            if (_client?.Inbox == null)
                return;

            var totalEmails = _client.Inbox.Count;
            _logger.LogInformation("📬 Processing {TotalEmails} existing emails...", totalEmails);


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
                _logger.LogInformation("No emails to process in the inbox.");
                return;
            }
            for (int i = 0; i < fetchedMessages.Count; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    var message = fetchedMessages[i];
                    await ProcessSingleEmail(message, _client.Inbox, emailsToProcess);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing email at index {Index}", i);
                }
            }

            _lastProcessedCount = totalEmails;
            _logger.LogInformation("✅ Finished processing {TotalEmails} existing emails", totalEmails);
            await this.ProcessBatchAsync(emailsToProcess);
            // Small delay after processing existing emails
            await Task.Delay(50, cancellationToken);
        }

        private async Task ProcessBatchAsync(List<EmailReceivedEventArgs> emailsToProcess)
        {
            if (!emailsToProcess.Any())
            {
                _logger.LogDebug("No emails in queue to process");
                return;
            }

            var stopwatch = System.Diagnostics.Stopwatch.StartNew();

            _logger.LogInformation("🔄 Starting batch processing of {EmailCount} emails...", emailsToProcess.Count);

            // Process emails in batch
            var triggers = await _batchRuleProcessor.ProcessEmailBatchAsync(emailsToProcess);

            // Execute triggers if any matches found
            var emailsMoved = 0;
            if (triggers.Any())
            {
                var ops = _emailMover.ExecuteTriggersAsync(triggers);
                _logger.LogInformation("📦 Executed {OperationCount} email move operations", ops.Count);
                // Count how many emails were moved
                foreach (var op in ops)
                {
                    this._moveQueue.Enqueue(op);
                }

                emailsMoved = triggers.Count;
            }

            stopwatch.Stop();
            var batchEventArgs = new BatchProcessingEventArgs
            {
                EmailsProcessed = emailsToProcess.Count,
                RulesMatched = triggers.Count,
                EmailsMoved = emailsMoved,
                ProcessingTime = stopwatch.Elapsed
            };
            _logger.LogInformation("🔄 Batch completed: {EmailsProcessed} emails, {RulesMatched} matches, {EmailsMoved} moved in {ProcessingTime}ms",
                 batchEventArgs.EmailsProcessed, batchEventArgs.RulesMatched, batchEventArgs.EmailsMoved, batchEventArgs.ProcessingTime.TotalMilliseconds);
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

                ReceivedDate = message.Date.DateTime,
                Folder = folder.Name,
                IsImportant = message.Flags.HasValue && message.Flags.Value.HasFlag(MessageFlags.Flagged),
                IsRead = message.Flags.HasValue && message.Flags.Value.HasFlag(MessageFlags.Seen),
                UniqueId = message.UniqueId
            };
            if (emailArgs.IsImportant)
            {
                // log import emails and skip them
                _logger.LogInformation("⭐ Important email detected: {Subject} from {Sender} at {Date}",
                    emailArgs.Subject, emailArgs.SenderName, emailArgs.ReceivedDate);
                return; // Skip processing important emails for now
            }
            processList.Add(emailArgs);

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
                        _logger.LogInformation("⏰ Hourly reprocessing time reached. Exiting IDLE to reprocess all emails...");
                        await ProcessMails(false, stoppingToken);
                        _lastFullReprocess = DateTime.UtcNow;
                        continue; // Skip the IDLE cycle and immediately check again
                    }

                    // Process any queued move operations
                    if (_moveQueue.Count > 0)
                    {
                        await ProcessQueuedMoveOperationsAsync();
                    }

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
                    _idleDoneSource = new CancellationTokenSource(actualTimeout);

                    try
                    {
                        // Enter IDLE mode – will block until done token is canceled or timeout
                        if (_client.Capabilities.HasFlag(ImapCapabilities.Idle))
                        {
                            _logger.LogDebug("📱 Entering IDLE mode for {Timeout} (next full reprocess in {NextReprocess})",
                                actualTimeout, remainingTime);
                            await _client.IdleAsync(_idleDoneSource.Token, stoppingToken);
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
                            _logger.LogInformation("📧 Monitoring stopped due to service shutdown");
                            break;
                        }
                        // Expected when _idleDoneSource is triggered by event handlers, move queue, or timeout
                        _logger.LogDebug("IDLE interrupted for processing");
                    }
                    finally
                    {
                        _idleDoneSource?.Dispose();
                        _idleDoneSource = null;
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

            using var dbConnection = _mailDb.GetConnection();

            while (_moveQueue.TryDequeue(out var moveOperation) && moveOperation != null)
            {
                try
                {
                    await ProcessSingleMoveOperationAsync(dbConnection, moveOperation);
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
                _logger.LogInformation("✅ Processed {Count} move operations in {ElapsedMs}ms",
                    processedCount, stopwatch.ElapsedMilliseconds);
            }
        }

        private async Task ProcessSingleMoveOperationAsync(IDbConnection dbConnection, EmailMoveOperation moveOperation)
        {
            if (!moveOperation.Emails.Any())
                return;

            try
            {
                // Open source folder in ReadWrite mode
                var sourceFolder = _client!.GetFolder(moveOperation.SourceFolder);
                await sourceFolder.OpenAsync(FolderAccess.ReadWrite);

                // Get destination folder
                var destinationFolder = GetDestinationFolder(moveOperation.DestinationFolder);

                // Perform the move

                await sourceFolder.MoveToAsync(moveOperation.EmailIds, destinationFolder);

                // Save to database
                SaveEmailsToDatabase(dbConnection, moveOperation.Emails);


                _logger.LogInformation("📁 Moved {Count} emails from {Source} to {Destination}",
                    moveOperation.Emails.Count, moveOperation.SourceFolder, moveOperation.DestinationFolder);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error moving emails from {Source} to {Destination}",
                    moveOperation.SourceFolder, moveOperation.DestinationFolder);
                throw;
            }
        }

        private IMailFolder GetDestinationFolder(string folderName)
        {
            if (string.Equals(folderName, "trash", StringComparison.CurrentCultureIgnoreCase))
            {
                return _client!.Capabilities.HasFlag(ImapCapabilities.SpecialUse)
                    ? _client.GetFolder(SpecialFolder.Trash)
                    : _client.GetFolder(_config.Trash);
            }

            return _client!.GetFolder(folderName);
        }

        private void SaveEmailsToDatabase(IDbConnection dbConnection, List<Email> emails)
        {
            foreach (var email in emails)
            {
                try
                {
                    dbConnection.Insert(email);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error saving email {MessageIndex} to database", email.MessageIndex);
                }
            }
        }

        private void OnCountChanged(object? sender, EventArgs e)
        {
            var folder = (IMailFolder)sender!;
            if (folder.Count > _lastProcessedCount)
            {
                _logger.LogDebug("[Event] Inbox count increased to {CurrentCount} (was {LastCount}) – new message likely.",
                    folder.Count, _lastProcessedCount);
                _newMessagesFlag = true;
                _idleDoneSource?.Cancel();  // Signal the IDLE loop to wake up
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
            _logger.LogInformation("📧 Email monitoring service is stopping...");


            // Cancel any ongoing IDLE operation
            _idleDoneSource?.Cancel();

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
