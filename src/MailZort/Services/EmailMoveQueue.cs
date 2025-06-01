
using System.Collections.Concurrent;

namespace MailZort.Services
{
    // Queue item for move operations
    public class EmailMoveOperation
    {
        public string SourceFolder { get; set; } = string.Empty;
        public string DestinationFolder { get; set; } = string.Empty;
        public List<Email> Emails { get; set; } = new();
        public DateTime QueuedAt { get; set; } = DateTime.UtcNow;
    }

    // Event args for move operations
    public class EmailMovedEventArgs : EventArgs
    {
        public string SourceFolder { get; set; } = string.Empty;
        public string DestinationFolder { get; set; } = string.Empty;
        public int EmailCount { get; set; }
        public List<Email> Emails { get; set; } = new();
    }

    // Intermediary service that provides the interface between EmailMover and the BackgroundService
    public interface IEmailMoveQueue
    {
        void QueueMoveOperation(EmailMoveOperation moveOperation);
        int GetQueuedMoveCount();
        event EventHandler<EmailMovedEventArgs>? EmailMoved;
    }

    public class EmailMoveQueue : IEmailMoveQueue
    {
        private readonly ConcurrentQueue<EmailMoveOperation> _moveQueue = new();
        private readonly ILogger<EmailMoveQueue> _logger;

        public event EventHandler<EmailMovedEventArgs>? EmailMoved;

        // Internal event that the BackgroundService subscribes to
        internal event EventHandler? MoveQueued;

        public EmailMoveQueue(ILogger<EmailMoveQueue> logger)
        {
            _logger = logger;
        }

        public void QueueMoveOperation(EmailMoveOperation moveOperation)
        {
            _moveQueue.Enqueue(moveOperation);
            _logger.LogDebug("Queued move operation: {Count} emails from {Source} to {Destination}",
                moveOperation.Emails.Count, moveOperation.SourceFolder, moveOperation.DestinationFolder);

            // Notify the monitoring service
            MoveQueued?.Invoke(this, EventArgs.Empty);
        }

        public int GetQueuedMoveCount()
        {
            return _moveQueue.Count;
        }

        internal bool TryDequeue(out EmailMoveOperation? moveOperation)
        {
            return _moveQueue.TryDequeue(out moveOperation);
        }

        internal void NotifyEmailMoved(EmailMovedEventArgs args)
        {
            EmailMoved?.Invoke(this, args);
        }
    }
}
