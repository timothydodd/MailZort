# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

This repo has a git submodule (`src/RoboDodd.OrmLite`). Clone with `--recursive`, or run
`git submodule update --init` before building or docker-building.

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

MailZort is a .NET 10 ASP.NET Core app that connects to IMAP email servers and automatically sorts
emails based on user-defined rules. One process runs two things: the mail loop
(`EmailMonitoringService`, a BackgroundService) and a Blazor Server admin portal. It runs as a
Docker container, deployed as a Kubernetes Deployment with a NodePort service.

### Portal

Blazor Server, cookie auth, seeded with `admin` / `admin` (forced change on first sign-in).
Pages: Dashboard, Activity, Senders, Rules, Settings, Account. Sign-in/out post to
`/auth/login` and `/auth/logout` - minimal-API endpoints, because a Blazor circuit cannot write
the auth cookie, and because a `/login` POST would collide with the `/login` page route.

The portal never touches IMAP. Anything needing the mailbox (e.g. "Move back") is written to the
`PendingActions` table and executed by the mail loop, which finds the message again by Message-Id
in whatever folder it now sits.

### Storage

SQLite via the `RoboDodd.OrmLite` submodule (`data/mailzort.db`; set `EmailSettings:ConnectionString`
for MySQL instead). Tables are created from the POCOs in `Data/Models.cs` with
`CreateTableIfNotExistsAsync(migrateSchema: true)`, which also adds columns a model has gained.
On first run `LegacyMigrator` imports rules from `appsettings.json` and any old
`sender-reputation.json`; after that the database is the source of truth and the appsettings copy
of `Rules` is ignored.

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
| `PortalActions.cs` | The verbs behind the portal's buttons (move back, whitelist, blacklist) |
| `PortalEndpoints.cs` | `/auth/login` and `/auth/logout` form posts |
| `Data/*Store.cs` | Rules, senders, activity, settings, users, pending actions, message locations |
| `Data/LegacyMigrator.cs` | One-time import of appsettings rules and sender-reputation.json |
| `Program.cs` | Web host, DI, auth, and startup database bootstrap |
| `DomainModels.cs` | EmailSettings, Rule, enums, and the mail-loop DTOs |

### Configuration

Settings come from `appsettings.json` (copied to `data/` on first run) and can be overridden by environment variables (e.g., `EmailSettings__Server`).

**Rule enums** (defined in Program.cs):
- **LookIn**: All(0), Subject(1), Body(2), Sender(3), Recipient(4), SenderEmail(5)
- **ExpressionType**: Contains(0), DoesNotContain(1), Is(2), IsNot(3), StartsWith(4), EndsWith(5), MatchesRegex(6), DoesNotMatchRegex(7), AllEmails(8)

### Key Behaviors

- Important/flagged emails are skipped (logged but not processed)
- Regex matching has a 5-second timeout
- Read emails get a 2-hour grace period before age filter applies
- Nullable reference types and C# 13 language features are enabled

### Future-dated mail

Messages dated more than `FutureDatedToleranceMinutes` ahead of now are permanently deleted
(`\Deleted` + `EXPUNGE`) before rules run — they never reach Trash. Sender/subject/date are logged.

### Sender reputation

On every full reprocess, MailZort snapshots which folder each recent message is in (Inbox, Junk,
Trash, plus `ReputationWatchFolders`), keyed by Message-Id, and diffs it against the previous pass:

- **Junk → any real folder** = rescue. The sender becomes `Trusted`: exempt from all rules and from
  inbox cleanup, and their future mail is moved back out of Junk automatically.
- **Real folder → Junk** = demotion. Trust is revoked on the first, and at `BlacklistThreshold`
  demotions the sender is `Blacklisted` — their inbox mail is moved to Junk before rules run.
- Junk → Trash is a deletion, not a rescue. A later rescue clears a blacklist outright.
- MailZort's own moves are suppressed from the diff so they can't feed back into the scores.

State lives in `{DataDirectory}/sender-reputation.json` and **must be on a persistent volume** —
without one, reputation and the baseline snapshot reset on every container start.
