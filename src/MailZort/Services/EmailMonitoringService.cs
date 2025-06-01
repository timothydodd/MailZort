using System.Data;
using MailKit;
using MailKit.Net.Imap;
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
        private readonly EmailMoveQueue _moveQueue;
        private ImapClient? _client;
        private CancellationTokenSource? _idleDoneSource;
        private bool _newMessagesFlag = false;
        private bool _pendingMovesFlag = false;
        private int _lastProcessedCount = 0;

        private const int BatchSize = 50;
        private const int ReconnectDelayMs = 600000;
        private const int IdleRetryDelayMs = 5000;
        private const int IdleTimeoutMinutes = 9;

        private CancellationToken _serviceCancellationToken;

        public event EventHandler<EmailReceivedEventArgs>? EmailReceived;
        public event EventHandler<ProcessingProgressEventArgs>? ProcessingProgress;

        public EmailMonitoringService(
            ILogger<EmailMonitoringService> logger,
            EmailSettings config,
            MailDb mailDb,
            EmailMoveQueue moveQueue)
        {
            _logger = logger;
            _config = config;
            _mailDb = mailDb;
            _moveQueue = moveQueue;

            // Subscribe to move queue events
            _moveQueue.MoveQueued += OnMoveQueued;
        }

        private void OnMoveQueued(object? sender, EventArgs e)
        {
            _pendingMovesFlag = true;
            _idleDoneSource?.Cancel(); // Wake up the IDLE loop
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
                await ProcessExistingEmailsAsync(cancellationToken);

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

        private async Task ProcessExistingEmailsAsync(CancellationToken cancellationToken)
        {
            if (_client?.Inbox == null)
                return;

            var totalEmails = _client.Inbox.Count;
            _logger.LogInformation("📬 Processing {TotalEmails} existing emails...", totalEmails);
            ReportProgress(0, totalEmails, false);

            for (int i = 0; i < totalEmails; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    ProcessSingleEmail(_client, _client.Inbox, i, isExisting: true);

                    if (ShouldReportProgress(i + 1, totalEmails))
                    {
                        ReportProgress(i + 1, totalEmails, false);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing email at index {Index}", i);
                }
            }

            _lastProcessedCount = totalEmails;
            ReportProgress(totalEmails, totalEmails, true);
            _logger.LogInformation("✅ Finished processing {TotalEmails} existing emails", totalEmails);

            // Small delay after processing existing emails
            await Task.Delay(50, cancellationToken);
        }

        private void ProcessSingleEmail(ImapClient client, IMailFolder folder, int index, bool isExisting)
        {
            var message = folder.GetMessage(index);

            var senderNames = string.Join(";", message.From.Select(x => x.Name ?? string.Empty));
            var senderAddresses = string.Join(";", message.From.OfType<MailboxAddress>().Select(x => x.Address));

            var emailArgs = new EmailReceivedEventArgs
            {
                From = message.From.ToString(),
                SenderName = senderNames,
                SenderAddress = senderAddresses,
                Subject = message.Subject ?? string.Empty,
                Body = message.TextBody ?? message.HtmlBody ?? string.Empty,
                IsExisting = isExisting,
                MessageIndex = index,
                ReceivedDate = message.Date.DateTime,
                Folder = folder.Name
            };

            EmailReceived?.Invoke(this, emailArgs);
        }

        private bool ShouldReportProgress(int current, int total)
        {
            return current % BatchSize == 0 || current % Math.Max(1, total / 10) == 0;
        }

        private void ReportProgress(int current, int total, bool isComplete)
        {
            ProcessingProgress?.Invoke(this, new ProcessingProgressEventArgs
            {
                CurrentIndex = current,
                TotalCount = total,
                IsComplete = isComplete
            });
        }

        private async Task MonitorForNewEmailsAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested && _client?.IsConnected == true)
            {
                try
                {
                    // Use a fresh CancellationTokenSource for each IDLE cycle
                    _idleDoneSource = new CancellationTokenSource(TimeSpan.FromMinutes(IdleTimeoutMinutes));

                    try
                    {
                        // Enter IDLE mode – will block until done token is canceled or timeout
                        if (_client.Capabilities.HasFlag(ImapCapabilities.Idle))
                        {
                            await _client.IdleAsync(_idleDoneSource.Token, stoppingToken);
                        }
                        else
                        {
                            // Fallback for servers that don't support IDLE
                            await Task.Delay(ReconnectDelayMs, stoppingToken);
                            CheckForNewEmails();
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        if (stoppingToken.IsCancellationRequested)
                        {
                            _logger.LogInformation("📧 Monitoring stopped due to service shutdown");
                            break;
                        }
                        // Expected when _idleDoneSource is triggered by event handlers or move queue
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
                        CheckForNewEmails();
                    }

                    // Process any queued move operations
                    if (_pendingMovesFlag)
                    {
                        _pendingMovesFlag = false;
                        await ProcessQueuedMoveOperationsAsync();
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

        private void CheckForNewEmails()
        {
            try
            {
                if (_client?.Inbox == null)
                    return;

                var currentCount = _client.Inbox.Count;
                if (currentCount > _lastProcessedCount)
                {
                    ProcessNewEmails(currentCount);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking for new emails");
            }
        }

        private void ProcessNewEmails(int currentCount)
        {
            var newEmailCount = currentCount - _lastProcessedCount;
            _logger.LogInformation("🔔 Processing {NewEmailCount} new email(s)!", newEmailCount);

            for (int i = _lastProcessedCount; i < currentCount; i++)
            {
                if (_serviceCancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    ProcessSingleEmail(_client!, _client!.Inbox, i, isExisting: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing new email at index {Index}", i);
                }
            }

            _lastProcessedCount = currentCount;
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
                var indexes = moveOperation.Emails.Select(e => e.MessageIndex).ToList();
                await sourceFolder.MoveToAsync(indexes, destinationFolder);

                // Save to database
                SaveEmailsToDatabase(dbConnection, moveOperation.Emails);

                // Notify via the queue service
                _moveQueue.NotifyEmailMoved(new EmailMovedEventArgs
                {
                    SourceFolder = moveOperation.SourceFolder,
                    DestinationFolder = moveOperation.DestinationFolder,
                    EmailCount = moveOperation.Emails.Count,
                    Emails = moveOperation.Emails
                });

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

            // Unsubscribe from move queue events
            _moveQueue.MoveQueued -= OnMoveQueued;

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

            _moveQueue.MoveQueued -= OnMoveQueued;
            _idleDoneSource?.Dispose();
            _client?.Dispose();

            base.Dispose();
        }
    }
}
