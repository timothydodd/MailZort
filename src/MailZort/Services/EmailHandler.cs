using System.Collections.Concurrent;

namespace MailZort.Services;
public interface IEmailHandler
{
    void HandleEmailReceived(object sender, EmailReceivedEventArgs e);
    void HandleProcessingProgress(object sender, ProcessingProgressEventArgs e);
    void HandleBatchProcessing(object sender, BatchProcessingEventArgs e);
}


public class EmailHandler : IEmailHandler
{
    private readonly ILogger<EmailHandler> _logger;
    private readonly IBatchRuleProcessor _batchRuleProcessor;
    private readonly IEmailMover _emailMover;
    private readonly EmailSettings _emailSettings;
    private readonly ConcurrentQueue<EmailReceivedEventArgs> _emailQueue;
    private readonly Timer _batchProcessingTimer;
    private readonly SemaphoreSlim _processingLock;

    public event EventHandler<BatchProcessingEventArgs>? BatchProcessingCompleted;

    public EmailHandler(
        ILogger<EmailHandler> logger,
        IBatchRuleProcessor batchRuleProcessor,
        IEmailMover emailMover,
        EmailSettings emailSettings)
    {
        _logger = logger;
        _batchRuleProcessor = batchRuleProcessor;
        _emailMover = emailMover;
        _emailSettings = emailSettings;
        _emailQueue = new ConcurrentQueue<EmailReceivedEventArgs>();
        _processingLock = new SemaphoreSlim(1, 1);

        // Set up timer for batch processing
        var interval = TimeSpan.FromSeconds(_emailSettings.BatchProcessingIntervalSeconds);
        _batchProcessingTimer = new Timer(ProcessBatchAsync, null, interval, interval);

        _logger.LogInformation("📊 Batch processing configured for every {Seconds} seconds",
            _emailSettings.BatchProcessingIntervalSeconds);
    }

    public void HandleEmailReceived(object sender, EmailReceivedEventArgs e)
    {
        // Just queue the email for batch processing
        _emailQueue.Enqueue(e);

        var prefix = e.IsExisting ? "[EXISTING]" : "[NEW]";
        _logger.LogDebug("{Prefix} Email queued: {From} - {Subject} (Queue size: {QueueSize})",
            prefix, e.From, e.Subject, _emailQueue.Count);
    }

    public void HandleProcessingProgress(object sender, ProcessingProgressEventArgs e)
    {
        if (e.IsComplete)
        {
            _logger.LogInformation("✅ Finished processing all existing emails! Queue size: {QueueSize}",
                _emailQueue.Count);
        }
        else if (e.CurrentIndex % 100 == 0)
        {
            _logger.LogInformation("📧 Processing existing emails: {Current}/{Total} ({Percent:F1}%) - Queue: {QueueSize}",
                e.CurrentIndex, e.TotalCount, e.PercentComplete, _emailQueue.Count);
        }
    }

    public void HandleBatchProcessing(object sender, BatchProcessingEventArgs e)
    {
        _logger.LogInformation("🔄 Batch completed: {EmailsProcessed} emails, {RulesMatched} matches, {EmailsMoved} moved in {ProcessingTime}ms",
            e.EmailsProcessed, e.RulesMatched, e.EmailsMoved, e.ProcessingTime.TotalMilliseconds);
    }

    private async void ProcessBatchAsync(object? state)
    {
        if (!await _processingLock.WaitAsync(100)) // Don't wait long if already processing
        {
            _logger.LogDebug("Batch processing already in progress, skipping this cycle");
            return;
        }

        try
        {
            var emailsToProcess = new List<EmailReceivedEventArgs>();

            // Dequeue all emails
            while (_emailQueue.TryDequeue(out var email))
            {
                emailsToProcess.Add(email);
            }

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
                await _emailMover.ExecuteTriggersAsync(triggers);
                emailsMoved = triggers.Count;
            }

            stopwatch.Stop();

            // Fire batch processing event
            var batchEventArgs = new BatchProcessingEventArgs
            {
                EmailsProcessed = emailsToProcess.Count,
                RulesMatched = triggers.Count,
                EmailsMoved = emailsMoved,
                ProcessingTime = stopwatch.Elapsed
            };

            BatchProcessingCompleted?.Invoke(this, batchEventArgs);
            HandleBatchProcessing(this, batchEventArgs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error during batch processing");
        }
        finally
        {
            _processingLock.Release();
        }
    }

    public void Dispose()
    {
        _batchProcessingTimer?.Dispose();
        _processingLock?.Dispose();
    }
}
