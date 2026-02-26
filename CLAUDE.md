# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build
dotnet build src/MailZort/MailZort.csproj

# Run
dotnet run --project src/MailZort/MailZort.csproj

# Publish release
dotnet publish src/MailZort/MailZort.csproj -c Release -o /app/out

# Docker build
docker build -t mailzort -f src/MailZort/Dockerfile src/
```

There are no test projects in this repository.

## Architecture

MailZort is a .NET 10 worker service that connects to IMAP email servers and automatically sorts emails based on user-defined rules. It runs as a Docker container, typically deployed as a Kubernetes CronJob.

### Processing Flow

1. **EmailMonitoringService** (BackgroundService) connects to IMAP server
2. Fetches existing emails, then enters IMAP IDLE monitoring for new arrivals
3. **BatchRuleProcessor** matches emails against rules using `Parallel.ForEach`
4. **RuleMatcher** evaluates each rule's expression type, LookIn scope, age filter, and status filters
5. **EmailMover** groups matched emails by source/destination folder into move operations
6. Move operations execute via a `ConcurrentQueue<EmailMoveOperation>`
7. Full reprocess runs every 2 hours as a consistency check

Only the first matching rule is applied per email (break on first match).

### Key Services (all in `src/MailZort/`)

| Service | Purpose |
|---------|---------|
| `EmailMonitoringService.cs` | Main BackgroundService - IMAP connection, IDLE monitoring, orchestration |
| `BatchRuleProcessor.cs` | Parallel batch processing of emails against rules |
| `RuleMatcher.cs` | Rule evaluation logic (expression matching, age/status filters) |
| `EmailMover.cs` | Groups rule triggers into batched move operations |
| `MailDb.cs` | SQLite database layer via ServiceStack.OrmLite |
| `SettingsHelper.cs` | Copies appsettings.json to persistent `data/` directory on first run |
| `Program.cs` | DI setup, configuration, and model definitions (EmailSettings, Rule, enums) |

### Configuration

Settings come from `appsettings.json` (copied to `data/` on first run) and can be overridden by environment variables (e.g., `EmailSettings__Server`).

**Rule enums** (defined in Program.cs):
- **LookIn**: All(0), Subject(1), Body(2), Sender(3), Recipient(4), SenderEmail(5)
- **ExpressionType**: Contains(0), DoesNotContain(1), Is(2), IsNot(3), StartsWith(4), EndsWith(5), MatchesRegex(6), DoesNotMatchRegex(7), AllEmails(8)

### Key Behaviors

- Important/flagged emails are skipped (logged but not processed)
- Regex matching has a 5-second timeout
- Read emails get a 2-hour grace period before age filter applies
- SQLite database (`data/mailv2.db`) stores moved emails when `StoreMovedMessages` is enabled
- Nullable reference types and C# 13 language features are enabled
