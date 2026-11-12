# RESEARCH: Connection resiliency for EF Core and health checks

**Status:** Research complete for the demonstrated scope. A working reference
implementation exists in `Host/`; production adoption still needs the
adaptations listed in [Question 3](#3-is-an-implementation-proposed).
**Scope:** PostgreSQL (`Npgsql`) + EF Core 10 + ASP.NET health checks, as
built in this repository.
**Date:** 2026-11-10

## Answer at a glance

| Question | Answer | Confidence |
|---|---|---|
| Has a resiliency solution been identified? | **Yes.** EF Core's built-in connection resiliency, enabled through the Npgsql provider's `EnableRetryOnFailure`. | High |
| Does the solution work for both EF and health checks? | **Yes for EF-backed health checks** (`AddDbContextCheck`), because the probe runs through the same retrying `DbContext` pipeline. It does **not** automatically cover health checks that use their own connection or client. | High for the demonstrated case; manual, small-sample evidence |
| Is an implementation proposed? | **Yes.** `Host/` is a working reference implementation, and this document proposes a production-shaped variant. The demo itself is not drop-in production code. | Medium-high |

## 1. Has a resiliency solution been identified?

**Yes.** The solution is EF Core connection resiliency configured on the
Npgsql provider. It is a provider-supplied retrying execution strategy, not a
custom retry loop and not a circuit breaker.

The relevant wiring is in `Host/Program.cs`:

```csharp
ef.UseNpgsql(
    connectionString,
    pg =>
    {
        var maxRetryCount = builder.Configuration.RetryMaxRetryCount();
        var maxRetryDelay = TimeSpan.FromSeconds(
            builder.Configuration.RetryMaxRetryDelaySeconds());

        pg.EnableRetryOnFailure(
            maxRetryCount: maxRetryCount,
            maxRetryDelay: maxRetryDelay,
            errorCodesToAdd: null);
    });
```

What this gives the application:

- **A retrying execution strategy** (`NpgsqlRetryingExecutionStrategy`)
  wraps each EF operation. On a recognized transient failure it waits and
  retries the operation, up to `maxRetryCount`.
- **Provider-aware transient detection.** With `errorCodesToAdd: null` the
  provider's own list of transient PostgreSQL SQLSTATEs is used. The demo
  exercises two of them:
  - `08001` — connection could not be established
    (`SimulatedConnectionFaultInterceptor`)
  - `57P01` — `admin_shutdown`, representing a failover
    (`SimulatedTransientErrorInterceptor`)
- **Bounded, capped backoff.** Retries start at zero delay, then grow
  geometrically with jitter, capped at `maxRetryDelay`. With the demo defaults
  (10 retries, 30-second cap), the whole budget is on the order of a couple of
  minutes, plus the time each failed attempt itself takes.
- **Configuration instead of recompilation.** `Demo:Retry:MaxRetryCount` and
  `Demo:Retry:MaxRetryDelaySeconds` control the strategy; the defaults (10 and
  30s) preserve the verified behavior. Values are validated at startup
  (`Host/DemoConfiguration.cs`).

What the solution does **not** cover:

- **Non-transient errors.** For example, a missing table (`42P01`) is not
  retried. Retrying a deterministic error only delays the failure.
- **Health checks that bypass EF.** A custom `IHealthCheck` that opens a raw
  `NpgsqlConnection`, uses a separate `HttpClient`, or queries through another
  data access layer does not inherit this strategy.
- **Caller cancellation.** Cancelling the HTTP request does not cancel the
  server-side retry ladder; the server keeps retrying after the caller has
  given up.
- **Multi-operation transactions.** Retries act per EF operation. A
  transaction spanning several operations needs the execution strategy to be
  invoked explicitly (`ExecuteAsync`) if retries are expected there. The demo's
  endpoints do not exercise this.
- **Write idempotency.** A write that committed but whose acknowledgement was
  lost could be applied again by a retry. The demo does not test this; a
  production implementation should review it.

The durable behavior contract for this solution is captured in
`openspec/specs/ef-connection-resiliency/spec.md`.

## 2. Does the solution work for both EF and health checks?

**Yes — for the EF `DbContext` health check used here.** The reason is
mechanical: `AddDbContextCheck<FooContext>()` resolves the same `FooContext`
from DI, and its default probe is `context.Database.CanConnectAsync(...)`.
That probe executes through the same EF connection/command pipeline, so the
retrying execution strategy wraps it exactly as it wraps `/items` and
`POST /items`.

The log evidence shows the strategy on the probe path: a fault, an
"NpgsqlRetryingExecutionStrategy" transient-exception warning, a re-open, and a
`Healthy` completion. This was observed for both fault classes after the
non-query interceptor was added.

### Evidence from the recorded sessions

| Observation | Result |
|---|---|
| Sustained connection faults (session 1) | `/health` `Healthy` **21/21** |
| Sustained connection faults (session 2) | `/health` `Healthy` **20/20** |
| Probe with both fault classes (session 3) | `SELECT 1` → `57P01` → retry → `08001` → retry → `Healthy` in 1672ms |
| Command class disabled, connection class active (session 4) | 25/25 probes `Healthy`, recovery chains contained connection faults only |
| Connection class disabled, command class active (session 4) | 62/64 probes `Healthy`; at least one of the remainder was a client-window cancellation, not budget exhaustion |
| Database hard-down (session 2) | HTTP **503 Unhealthy after ~194s**, after exhausting the retry budget |
| Dual-class faults at 50% each (session 3) | Probe exhaustion visible at roughly **4% per call** (single-class baseline ~0.05%) |

The important distinction is **EF-backed health checks vs. health checks in
general**. The demonstration covers `AddDbContextCheck`. It does not
generalize to every `IHealthCheck`; the baseline `FooHealthCheck` in this repo
is always healthy and never touches the database. If a health check owns its
own PostgreSQL access, it needs its own resilience policy or must be routed
through the resilient `DbContext`.

### Consequences that matter operationally

Even though correctness coverage is good, the health-check path inherits the
retry strategy's *latency* as well as its safety net:

1. **A hard-down database makes the probe slow before it fails.** The demo
   took ~194 seconds to return 503, because the strategy spent its full retry
   budget first. Common HTTP client timeouts (100–120s) expire first, so
   monitoring may report "timeout" instead of the underlying outage.
2. **Cancellation is not propagation.** Dropping the HTTP request does not
   stop the server from retrying.
3. **Budget exhaustion is not separately logged on the health-check path.**
   The retry warnings appear, but the final retry-limit-exceeded failure is
   carried inside the `Unhealthy` result (message `(null)`) rather than logged
   as an error line. This makes the health-check failure less diagnosable than
   an ordinary request failure.
4. **Fault injection can move the odds, not just the code path.** With two
   independently faulting classes at 50% each, each probe attempt survives only
   if both rolls pass. That changes *how often* exhaustion is reached, but not
   *whether* the retry strategy covers the probe.

## 3. Is an implementation proposed?

**Yes.** The repository already contains a working reference implementation,
and a production-shaped version can be proposed from it.

### Reference implementation (present in this repo)

1. **Register the demo-only fault injectors** (not for production):
   `SimulatedTransientErrorInterceptor` and
   `SimulatedConnectionFaultInterceptor`.
2. **Register the `DbContext` with the retry strategy** on `UseNpgsql`, reading
   retry count and delay from configuration (see the `Program.cs` excerpt
   above).
3. **Register the EF health check** using the default `CanConnectAsync` probe:

   ```csharp
   builder.Services
       .AddHealthChecks()
       .AddDbContextCheck<FooContext>()
       .AddCheck<FooHealthCheck>("foo");

   app.MapHealthChecks("/health");
   ```

   The explicitly commented-out `customTestQuery` lambda in `Program.cs` is
   not required: `AddDbContextCheck`'s default test query already calls
   `CanConnectAsync`.
4. **Expose the ordinary data endpoints** (`/items`, `POST /items`) so the same
   retry behavior can be compared against the probe.

### Proposed production implementation

The demo proves the mechanism. A real application should adapt it as follows:

1. **Keep EF connection resiliency, but make its budget an explicit
   operational choice.** Configure `EnableRetryOnFailure` per environment and
   document the total worst-case latency, not just the retry count.
2. **Alignment rule:** the health-probe timeout (or the orchestrator's probe
   timeout) should exceed the server-side retry budget. Otherwise the caller
   times out before the probe can report the real state.
3. **Split liveness from readiness.** Use a DB-independent liveness endpoint
   and a DB-backed readiness endpoint. A database outage should make the app
   *not ready*, not cause the orchestrator to restart it as if it were
   *unhealthy*. This repository has a single `/health`; splitting it is a
   recommended production change, not a demonstrated one.
4. **Decide the probe's budget deliberately.** Readiness may want a much
   smaller retry budget than application traffic. If the same `DbContext` must
   serve both, consider a separate registration or a dedicated readiness probe
   so the probe cannot stall for minutes.
5. **Fix observability on the probe path.** The retry-limit-exceeded failure is
   not logged separately by the health-check service. Add health-check
   result logging or wrap the probe so budget exhaustion is diagnosable, not
   just visible as a 503 with a null message.
6. **Add automated verification.** The demo is manual by design. A production
   adoption should add integration tests that inject transient failures and
   assert both recovery and exhaustion behavior.
7. **Review writes for idempotency and transaction scope.** Both are outside
   what the demo verifies.

### Suggested target shape

```csharp
// Data access: retrying provider strategy.
builder.Services.AddDbContext<AppDbContext>((sp, ef) =>
    ef.UseNpgsql(connectionString, pg =>
        pg.EnableRetryOnFailure(
            maxRetryCount: retryCount,
            maxRetryDelay: retryDelay)));

// Liveness: no external dependencies.
// Readiness: EF-backed, and therefore covered by the retry strategy.
builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddDbContextCheck<AppDbContext>(tags: ["ready"]);

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});
```

The `/health/ready` check above is the part confirmed by this research: because
it is `AddDbContextCheck`, the configured retry strategy covers it. The
liveness split and timeout alignment are proposed operational practice, not
claims verified by the demo.

## Limitations of this research

- **PostgreSQL-specific.** The mechanism and method transfer to other
  providers; the SQLSTATEs, retry timing, and exceptions do not.
- **Manual evidence, small samples.** Session records are bounded-run
  observations (`n=1` per configuration), not statistical contracts. Documented
  counts are evidence, not guarantees.
- **No automated tests.** Verification was log-based and manual. The archived
  OpenSpec spec records this as a deliberate demo-scope decision.
- **Documentation drift.** Some narrative documents predate later code changes
  (for example, statements about the health check's custom test query or about
  migrations). Treat `Host/` code and
  `openspec/specs/ef-connection-resiliency/spec.md` as the current source of
  truth.
- **Version pairing.** The host targets `net11.0` (RC SDK) while EF Core and
  Npgsql provider packages are 10.x stable. This is the representative pairing
  used for the study.

## Conclusion

The research answers all three questions affirmatively:

1. **Solution identified:** Npgsql's `EnableRetryOnFailure` execution strategy.
2. **Coverage confirmed:** it protects the EF `DbContext` health check as well
   as ordinary EF reads and writes — but only health checks that actually go
   through the resilient `DbContext`.
3. **Implementation proposed:** a working reference implementation exists, and
   a production adaptation is outlined above. The remaining work is operational
   (timeouts, liveness/readiness split, logging, automated tests, write
   semantics), not a gap in the resilience mechanism itself.

## Sources

- `Host/Program.cs` — DbContext, retry configuration, health checks, endpoints
- `Host/DemoConfiguration.cs` — configuration keys and startup validation
- `Host/SimulatedConnectionFaultInterceptor.cs` — `08001` injection
- `Host/SimulatedTransientErrorInterceptor.cs` — `57P01` injection
- `Host/FooHealthCheck.cs` — non-database baseline health check
- `openspec/specs/ef-connection-resiliency/spec.md` — behavior contract
- `TAKEAWAYS.md` — session-by-session evidence
- `QUESTIONS.md` — resolved and open questions
- `README.md`, `SUMMARY.md`, `MANAGEMENT.md` — narrative summaries
