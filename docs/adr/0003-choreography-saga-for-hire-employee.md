# 3. Choreography saga for the Hire Employee → provision account flow

## Status
Accepted

## Context
Hiring an employee needs a login account provisioned in AuthService. That's a write to a
*second* service, which the original `HireEmployeeHandler` never did - it only made a
read-only gRPC call to DirectoryService to validate the department/position. A write to
another service can fail independently of the local write that triggered it (most
plausibly: the email is already taken in AuthService), and without a way to reconcile
that failure, an employee could exist in EmployeeService with no way to log in and no
record that anything went wrong.

Two standard shapes exist for this: **orchestration** (a dedicated coordinator owns the
whole sequence and tells each service what to do) and **choreography** (each service
reacts to the previous step's event and publishes its own).

## Decision
Choreography, built entirely from pieces already in this codebase:

- `HireEmployeeHandler` now starts an employee in `PendingProvisioning` rather than
  `Active`, and publishes `EmployeeHired` through the existing outbox (ADR 0002) exactly
  as before.
- AuthService's `EmployeeEventsConsumer` (its first Kafka consumer - previously
  publish-only was never needed, since it consumes `employee.events`) reacts by
  provisioning an account and publishing `AccountProvisioned` or
  `AccountProvisioningFailed` through its own new outbox.
- EmployeeService's `AuthEventsConsumer` reacts to that outcome, completing the employee
  (`Active`) or compensating it (`ProvisioningFailed`, with the reason surfaced through
  the API).
- Both new consumers are structural copies of `AuditConsumer` (retry, dead-letter,
  idempotent `ProcessMessage`) - no second retry/dead-letter mechanism was invented.

An orchestrator was considered and rejected: it would be new infrastructure (a process
manager, its own state store) solving a problem the existing event bus and outbox already
solve for a two-step saga. Orchestration earns its complexity on sagas with several
services and branching compensation paths; this one has two participants and one
compensating step.

## Consequences
- No new architectural primitive - the saga is just two more Kafka consumers using
  patterns that already exist in the codebase, which is also why it was cheap to add.
- Compensation is visible, not silent: a failed hire lands in `ProvisioningFailed` with a
  human-readable reason on the employee record, not a stuck `PendingProvisioning` row
  nobody notices.
- Choreography's known cost applies: the full flow (`Hire → EmployeeHired →
  EmployeeEventsConsumer → AccountProvisioned/Failed → AuthEventsConsumer`) isn't visible
  in any single file - it has to be traced across two services' consumers. Acceptable at
  two participants; would be reconsidered if a third step joined this saga.
- The account-creation write and its own outbox write are *not* atomic with each other
  (`AuthService.Web/Consumers/EmployeeEventsConsumer.cs`'s `ProcessMessage` comment) -
  ASP.NET Identity's `UserStore` auto-saves independently of the outbox transaction, and
  turning that off fought Identity's internal FK ordering more than it was worth. A crash
  in that narrow window leaves an account with no `AccountProvisioned` ever published -
  an ops-visible gap (the employee stays `PendingProvisioning`), not a silent one, and
  nothing currently re-drives it automatically.
