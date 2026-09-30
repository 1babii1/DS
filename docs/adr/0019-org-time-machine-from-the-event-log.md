# 19. The org on a past date, folded from the event log

## Status
Accepted. Builds on [0002](0002-outbox-pattern-for-integration-events.md) (events through the outbox) and
[0015](0015-mcp-tools-read-through-the-service-apis.md) (tools read through the service APIs).

## Context
Every change to the organization (a department created, renamed, moved or deleted; a person hired, transferred or
terminated) is already published as an event, and AuditService already keeps every event with its body. Nobody could
ask "what did the org look like on 3 March, and who worked where", and the log could not have answered correctly
anyway: AuditService stamped each entry with the moment it *received* the message, and the outbox publisher did not
send the moment the event *happened*. A message delayed, replayed or read after an outage would sit at the wrong time.

## Decision

**The event's own time travels with the message.** The outbox publisher adds an `occurred-at` header (ISO 8601 UTC,
the outbox row's own time). AuditService places an entry at that time and falls back to its receive time when the
header is missing or unusable. Additive: no other consumer changes. Entries stored before this keep the time they
were received, which for real-time consumption is within seconds of the truth.

**The snapshot is a fold over the log, in AuditService.** `GET /api/audit/org-chart?at=` reads the recorded events of
eight types up to that instant, in the order they happened (ties in recording order), and rebuilds the department tree
(names, parents and depth as they were) and who worked in each department, by name and position. Nothing new is stored
and no service is added. Alternatives considered: a versioned read model with `valid_from`/`valid_to` (faster
queries, but a second source that can drift from the log) and temporal tables in the owning services (spread across
two services and joined at read time). The fold keeps the log the single source of truth and the acceptance test
("a hire, transfer and termination give the right slice at every date in between") checks exactly what is recorded.
If the log grows, snapshots can be added later without changing the contract.

**Rules of the fold.** A department exists from its creation until its deletion; its name and parent are the last
creation, rename or move. A person's place is their last hire or transfer, unless a termination followed. An event
about something never seen (a rename of an unknown department) is ignored, not invented. A department whose parent
does not exist at that moment is a root; a person whose department does not exist is listed as unplaced rather than
lost. An unreadable event is counted in the answer, so a gap is visible. A cycle in the log cannot hang the fold.

**The API.** A bare date means the end of that UTC day, a future time is the present, an absurd one is refused, and a
history too large to replay (50,000 events) is refused rather than answered from part of it. Any signed-in user may
read it, the same audience as the employee directory; the answer holds names and positions, never contact details, and
the raw log with its bodies stays admin-only. It also reports where the recorded history starts, for a date slider.

**Surfaces.** A `/history` page in the frontend (a slider from the first recorded event to today, the tree as it was,
people per department) and a read tool for the assistant, `get_org_snapshot(at, departmentId?)`, read as the caller
like every other tool, bounded (200 departments; 100 people of one department) and validating the date before it
sends anything.

**Development data.** A small fictional, dated history (28 events, January to August 2026) with a script that loads it
into `audit.entries` through the postgres container, the way `backup-postgres.sh` reaches the database, idempotently
and reversibly. It is deliberately **not** published to Kafka: on the real topics the events would also reach every
other consumer and create login accounts, welcome bonuses and search documents for people who do not exist.

## Consequences
- **History exists only from when AuditService first consumed.** An early date on a long-running stack can be empty or
  incomplete; the answer says where the recorded history starts.
- **The fold trusts the log.** A change that was never recorded is invisible to it. A rename or move that arrives out
  of order is placed by its own time, but a missing event cannot be recovered.
- **Cost grows with the log.** Each request folds every relevant event up to the instant (a bounded number, so the
  cost is bounded too); snapshots would be the next step if it mattered.
- **Times come from the producer's clock.** Skew between services is as large as their clock skew; ties are broken by
  recording order.
- **There is no "head of department".** The domain has no such concept, so the question "who led the department in
  March" is not answerable. Adding one (a command, an event, a field) is a separate feature.
- Only what the events carry is shown: department locations and position definitions beyond their creation are not
  part of the picture.
- The existing `OccurredAt` column now means the event's time when the producer sent one; `ReceivedAt` remains the
  consumer's time.

## What is and is not verified
Tests, each guard mutation-checked: the header is sent, read as UTC, and ignored when unusable, and a late message keeps
its true place; the fold at every intermediate instant of a scripted history (exactly at an event and just before it),
ties, late arrival, unknown ids, orphaned departments and people, cycles, unreadable events, no contact detail in the
output; the endpoint (dates, bare date as end of day, future, absurd input, size limit that ignores event types the fold
does not read, any signed-in user with the raw log still admin-only); the development history loaded through the real
emitter into a real database, twice, removed cleanly, and read back at the dates the story is built around; the tool
(caller's token, escaped date, validation before sending, bounds, fixed failure messages, DI registration); the page's
date and tree logic.

Not verified: the `/history` page in a browser; the endpoint through nginx with a real token; the seed script run
against the Docker stack's database (it needs the new AuditService image for the `occurred-at` header to matter, and it
loads by SQL so it does not); behaviour on a log of real size.
