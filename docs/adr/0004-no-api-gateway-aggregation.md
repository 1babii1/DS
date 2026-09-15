# 4. No API Gateway aggregation / BFF, for now

## Status
Accepted

## Context
Richardson's API Gateway pattern includes response aggregation (a.k.a. API Composition):
the gateway - or a dedicated Backend-for-Frontend - calls several backend services for
one frontend request and combines the results, so the browser doesn't have to make (and
merge) multiple calls itself. nginx in this stack is currently a plain path-based reverse
proxy (`docker/nginx/nginx.conf`): five `location` blocks, each `proxy_pass`-ing to one
upstream, no aggregation.

Whether that's a gap depends entirely on whether the frontend actually needs data from
more than one service to render anything. Checked rather than assumed:

- The frontend (`frontend/`) currently has no page that calls more than one backend
  service. The only shipped feature (departments) talks to DirectoryService alone.
- The composition case that seemed most likely - an employee list showing department and
  position names - is already solved on the *write* side: `HireEmployeeHandler` and
  `TransferEmployeeHandler` call DirectoryService via gRPC at write time and store
  `DepartmentName`/`PositionName` directly on `Employee` (`EmployeeDto.cs`). The read path
  (`ListEmployeesHandler`, `GetEmployeeByIdHandler`) is a single query against
  EmployeeService's own table - no second service call, no aggregation needed, because the
  data was denormalized when it was written, not stitched together when it's read.

## Decision
Don't build an API Gateway aggregation layer or a BFF. If a genuine multi-service read
need shows up - a dashboard spanning services that weren't designed to denormalize into
each other, for instance - solve it then, informed by what that page actually needs,
rather than building composition infrastructure ahead of a concrete requirement.

The `portfolio-web` confidential OIDC client and its Auth.js BFF (issue #13, ADR-worthy in
its own right once that work lands) is a *different* thing: a same-origin proxy that
attaches the access token server-side so it never reaches the browser. It doesn't
aggregate responses from multiple services - it's solving token custody, not composition.

## Consequences
- One less moving part: no aggregation service to keep in sync with every backend
  service's contract changes.
- The tradeoff this defers: if a future page genuinely needs live data from two services
  that can't reasonably denormalize into each other (audit trail alongside live employee
  status, say), the frontend will make two calls itself until that's revisited - visible
  as two network requests and two loading states, not hidden by an aggregation layer that
  doesn't exist yet.
- This is a decision to revisit given evidence (a real page with a real composition need),
  not a permanent architectural stance.
