using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;
using MimeKit;

public class EmailMonitoringService : BackgroundService
{
    private readonly ILogger<EmailMonitoringService> _logger;
    private readonly EmailSettings _config;
    private ImapClient? _idleClient;
    private int _lastProcessedCount = 0;
    private const int BatchSize = 50;
    private const int ReconnectDelayMs = 30000;
    private const int IdleRetryDelayMs = 5000;
    private const int FetchClientTimeoutMs = 10000;

    private CancellationToken _serviceCancellationToken;

    public event EventHandler<EmailReceivedEventArgs>? EmailReceived;
    public event EventHandler<ProcessingProgressEventArgs>? ProcessingProgress;

    public EmailMonitoringService(ILogger<EmailMonitoringService> logger, EmailSettings config)
    {
        _logger = logger;
        _config = config;
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
        _idleClient = new ImapClient();

        try
        {
            await ConnectToServerAsync(_idleClient, cancellationToken);

            var inbox = _idleClient.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            _logger.LogInformation("✅ Connected successfully. Processing existing emails...");
            await ProcessExistingEmailsAsync(cancellationToken);

            _logger.LogInformation("👀 Now monitoring for new emails...");
            inbox.CountChanged += OnCountChanged;
            await MonitorForNewEmailsAsync(cancellationToken);
        }
        finally
        {
            await DisconnectAsync(_idleClient, cancellationToken);
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
        // Use a separate client for initial processing to avoid affecting the idle client
        using var fetchClient = new ImapClient();

        try
        {
            await ConnectToServerAsync(fetchClient, cancellationToken);
            var inbox = fetchClient.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var totalEmails = inbox.Count;
            _logger.LogInformation("📬 Processing {TotalEmails} existing emails...", totalEmails);

            ReportProgress(0, totalEmails, false);

            for (int i = 0; i < totalEmails; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    await ProcessSingleEmailAsync(fetchClient, inbox, i, isExisting: true, cancellationToken);

                    if (ShouldReportProgress(i + 1, totalEmails))
                    {
                        ReportProgress(i + 1, totalEmails, false);
                        await Task.Delay(50, cancellationToken); // Small delay
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
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing existing emails");
            throw;
        }
        finally
        {
            await DisconnectAsync(fetchClient, cancellationToken);
        }
    }

    private async Task ProcessSingleEmailAsync(ImapClient client, IMailFolder folder, int index, bool isExisting, CancellationToken cancellationToken)
    {
        var message = await folder.GetMessageAsync(index, cancellationToken);

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

    private async Task MonitorForNewEmailsAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _idleClient!.IsConnected)
        {
            try
            {
                if (_idleClient.Capabilities.HasFlag(ImapCapabilities.Idle))
                {
                    // Create a CancellationTokenSource that we can cancel to interrupt IDLE
                    using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    try
                    {
                        await _idleClient.IdleAsync(idleCts.Token);
                    }
                    catch (OperationCanceledException) when (idleCts.Token.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                    {
                        // IDLE was interrupted by our code, not by service shutdown
                        _logger.LogDebug("IDLE interrupted for new email processing");
                    }
                }
                else
                {
                    await Task.Delay(ReconnectDelayMs, cancellationToken);

                    // Use separate client for status check when IDLE is not supported
                    await CheckForNewEmailsWithSeparateClient(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "IDLE monitoring error, retrying...");
                await Task.Delay(IdleRetryDelayMs, cancellationToken);
            }
        }
    }

    private async Task CheckForNewEmailsWithSeparateClient(CancellationToken cancellationToken)
    {
        using var fetchClient = new ImapClient();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(FetchClientTimeoutMs);

            await ConnectToServerAsync(fetchClient, timeoutCts.Token);
            var inbox = fetchClient.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

            await inbox.StatusAsync(StatusItems.Count | StatusItems.Recent, timeoutCts.Token);

            var currentCount = inbox.Count;
            if (currentCount > _lastProcessedCount)
            {
                await ProcessNewEmailsWithSeparateClient(inbox, fetchClient, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error checking for new emails with separate client");
        }
        finally
        {
            await DisconnectAsync(fetchClient, CancellationToken.None);
        }
    }

    // Fixed: Changed to async void handler that properly queues work
    private void OnCountChanged(object sender, EventArgs e)
    {
        if (sender is not IMailFolder folder)
            return;

        // Don't block the event handler - fire and forget with proper error handling
        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessNewEmailsAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing new emails in background task");
            }
        });
    }

    // Modified method to use a separate client for fetching new emails
    private async Task ProcessNewEmailsAsync()
    {
        // Use the service's cancellation token
        if (_serviceCancellationToken.IsCancellationRequested)
            return;

        // Create a new client specifically for fetching emails
        using var fetchClient = new ImapClient();

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_serviceCancellationToken);
            timeoutCts.CancelAfter(FetchClientTimeoutMs);

            await ConnectToServerAsync(fetchClient, timeoutCts.Token);
            var inbox = fetchClient.Inbox;
            await inbox.OpenAsync(FolderAccess.ReadOnly, timeoutCts.Token);

            var currentCount = inbox.Count;
            if (currentCount > _lastProcessedCount)
            {
                await ProcessNewEmailsWithSeparateClient(inbox, fetchClient, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (_serviceCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing new emails with separate client");
        }
        finally
        {
            await DisconnectAsync(fetchClient, CancellationToken.None);
        }
    }

    private async Task ProcessNewEmailsWithSeparateClient(IMailFolder inbox, ImapClient fetchClient, CancellationToken cancellationToken)
    {
        var currentCount = inbox.Count;
        if (currentCount > _lastProcessedCount)
        {
            var newEmailCount = currentCount - _lastProcessedCount;
            _logger.LogInformation("🔔 Processing {NewEmailCount} new email(s) with separate client!", newEmailCount);

            for (int i = _lastProcessedCount; i < currentCount; i++)
            {
                if (cancellationToken.IsCancellationRequested)
                    break;

                try
                {
                    await ProcessSingleEmailAsync(fetchClient, inbox, i, isExisting: false, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Error processing new email at index {Index}", i);
                }
            }

            _lastProcessedCount = currentCount;
        }
    }

    private async Task DisconnectAsync(ImapClient client, CancellationToken cancellationToken)
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
        await base.StopAsync(cancellationToken);

    }
}
