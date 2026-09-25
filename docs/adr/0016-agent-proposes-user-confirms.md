# 16. The agent proposes, the user confirms

## Status
Accepted. Builds on [0015](0015-mcp-tools-read-through-the-service-apis.md).

## Context
With reads going through the service APIs as the caller, the next step is letting an assistant change
data: "hire Anna into Payments and give her 500". The risk is not that the model is malicious; it is that
it reads text it did not write (a department description, an email) and can be talked into proposing
something the user never wanted, or be wrong about an id. What has to hold is that nothing changes until a
person has seen exactly what will happen and agreed, and that an agent can do nothing its user could not.

## Decision

**The model can only propose.** Three MCP tools (`propose_hire_employee`, `propose_transfer_employee`,
`propose_grant_currency`) validate their arguments and return a plan: the steps in plain words, an expiry,
and a token. They hold no client to any service and change nothing. There is no tool that confirms or runs
a plan, and a test holds the tool list to that.

**Confirmation is the user's own request**, `POST /mcp/plans/confirm`, made over their own authenticated
session - not an MCP tool, so nothing the model can call reaches it. It runs the approved steps through the
service APIs with the caller's token, so every service's own authorization, validation and rate limits apply.
A plan that hands out currency also needs a recent step-up (the same claim EmployeeService asks for on
termination), so a click is not enough for money. The path sits under `/mcp` only so the existing gateway
route covers it.

**A plan is a signed token, not a row.** McpServer owns no storage (0015), and a signature is enough: HMAC
over the whole plan - steps, the user, the expiry - so nothing can be changed after the user has read it.
Confirmation checks, in order, the signature, the shape, the expiry (10 minutes) and that the plan belongs to
the caller, and answers with fixed messages that reveal nothing about why a check failed.

**The executor re-checks every step** rather than trusting a token's contents: grant amount above a ceiling
(default 1000), text with control characters (no forging a second line of the plan), a malformed email, a
grant that takes its employee from anything but an earlier, applied hire. Free text a user reads is quoted,
single-line and bounded, so instruction-like text stays a value: a hostile department name or reason cannot
add a step or change an amount.

**Writes are never retried.** Separate clients, no retry: a repeated write is a second effect unless the
service can recognise it. Grants carry an `Idempotency-Key` derived from the plan and step, so confirming the
same plan again gets the original grant back.

**The report is true.** Steps run in order and stop at the first failure; later steps are reported as not
run and earlier ones as applied. Compensation is not attempted.

## Consequences
- Confirming the same token again re-runs its steps. What stops a second effect is each service's own
  protection: the ledger's idempotency key for grants, the unique email for a hire (a second confirm gets a
  conflict), and a transfer to the same placement is a no-op. This class does not track used plans, because
  that would need storage and a single-instance assumption.
- The signing key is required in Production and generated at startup elsewhere, so in development plans die
  with the process and are not valid across instances. Rotating the key invalidates outstanding plans, which is
  acceptable at a 10-minute lifetime.
- Forwarding the caller's token forwards all its audiences (0015); the agent inherits exactly the user's rights.
  No termination tool is offered on purpose.
- **Audit is logging only.** Each step's outcome is logged with the plan id and user id, but there is no audit
  event saying "done by the agent on behalf of X"; services record their own events as usual. Recording the
  plan id downstream needs a change in those services and is not done.
- **There is no user interface for confirming yet**, and the frontend's BFF allow-list does not include this
  path. Today confirmation is an API call.
- No rate limit on the confirm endpoint.

## What is and is not verified
Verified in-process (93 tests, each safety property mutation-checked): the signature, expiry and owner checks;
that proposals change nothing and the tool list has no confirm/run tool; the executor's per-step checks, stop
on failure and honest report; the idempotency key and that writes are not retried; the confirm endpoint through
the real pipeline (unauthenticated, another user's plan, expired, altered, garbage, step-up needed and granted).

Not verified: a real model driving the tools, or any real MCP client; a live run against the services (the stack
was not rebuilt with the new settings); the gateway route for the confirm path; behavior with several instances.
One guard is redundant rather than tested: the endpoint's own `RequireAuthorization` is backed by an explicit
missing-`sub` check, so removing the attribute alone is not caught by a test.
