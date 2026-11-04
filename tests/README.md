# Test suite

465 tests, MSTest 4, run on `net8.0` and `net10.0`. No test needs the Internet. This page explains how the suite is organized
and how to add to it; how to run it is in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Strategy

| Layer | Class(es) | What it protects | Speed |
|---|---|---|---|
| Pure logic | `RetryOptionsTests`, `RequestUriTests` | Backoff maths, jitter bounds, `Retry-After`, URL building and encoding. Randomness and clock are injected, so results are exact. | instant |
| Value object | `HttpResultTests` | Deserialization rules, error results, synthetic results. | instant |
| Behaviour through a stub | `HttpClientBaseTests`, `HttpClientBaseEdgeCaseTests`, `SimpleHttpClientFactoryTests` | The whole request pipeline (headers, auth, retries, timeouts, cancellation, logging) against a recording `HttpMessageHandler`. | instant to ~1 s |
| Resilience features | `RedirectTests`, `ResilienceTests`, `TelemetryTests` | Redirect rules (method, body, credentials, refusals, limits), the overall deadline, `Accept`, idempotency keys, and spans and metrics observed with real listeners. | instant |
| Typed errors and pagination | `ErrorsTests`, `PaginationTests` | `ProblemDetails`, `ApiException`, the GraphQL envelope; the `Link` parser and the page walk, including a hostile next link, loops and limits. | instant |
| Bodies and downloads | `BodiesTests` | Form, multipart and streamed downloads, including a stream that fails after some bytes. | instant |
| Registration | `RegistrationTests` | `AddSFerrandHttpClient`, options validation including a failure **at start-up**, and use with a source-generated JSON context. | instant |
| The published test double | `StubHandlerTests` | `SFerrand.Http.Testing` is a package: its behaviour is a contract. | instant |
| Security regressions | `SecurityTests` | Each way the client could leak a credential, reach an address it was not configured for, forge a header, climb a path, log a secret or exhaust memory. Written **before** the fixes: they failed on the code that did not have them (50 of 69 cases). | instant |
| Real network stack | `IntegrationTests` (`[TestCategory("Integration")]`) | What a stub cannot reproduce: real header validation, real sockets, refused connections, concurrency on one pooled `HttpClient`, **a line break in a header on the wire, cross-origin redirects, an endless body, a stalled body**. A loopback `HttpListener` on a free port. | ~2 s |

The stub tests make up most of the suite because they are deterministic. The integration tests are few on purpose: each
one covers something the stub would silently get wrong.

## Verified beyond the unit tests

Two claims are proved by running something, not by a test class: a **trimmed application with reflection-based JSON turned off** (published with the full trim mode, then run: a POST round trip, a list,
and the refusal of an unknown type), and the **real connection behaviour** the design depends on (a header with a line break, a cross-origin redirect, an endless body, a stalled body, a connection dropped in the
middle of a body: see `IntegrationTests`). A consumer project restoring both packages from a local feed also checks that the packages work from the outside.

## Security tests

`SecurityTests` is organized by threat (trusted origins, clear text, secrets in addresses/messages/logs, header forging, path traversal, memory). The convention: **a security fix starts with a test that fails for the right reason**, and
when the behaviour depends on the framework, the claim is first checked on a real connection (the integration tests) rather than assumed. Two of the original findings were not what I expected: a line break in a header value *is*
sent as a second header, and a cross-origin redirect *keeps* custom headers while dropping `Authorization`.

## Support code (`Support/`)

- **`StubHandler`**: a recording `HttpMessageHandler`. Queue responses with `Respond(...)`, transport failures with
  `Throw(...)`, or `Hang()` to simulate a server that never answers; read what was sent from `Requests`. The last queued
  response repeats, so `Respond(503)` means "always 503".
- **`TestClient` / `TestClients`**: expose the protected API of `HttpClientBase`, and build clients with *fast* retries
  (1 ms base delay, 10 ms cap) and a 5 s timeout, so retry tests do not sleep.
- **`ListLogger`**: captures log messages for assertions.

## Conventions

- **Name = behaviour**: `Subject_Condition_ExpectedOutcome` (`Post_IsNotRetriedByDefault`). A failing test name should tell you
  what broke without opening the file.
- **One reason to fail** per test. Tables (`[DataRow]`) are used when only the data varies.
- **Document the why**: every class has a summary, and tests whose purpose is not obvious from the name carry an XML
  summary explaining which bug or trap they guard against. If you add a test for a subtle case, do the same.
- Tests are independent and run **in parallel** at method level (`MSTestSettings.cs`): no static state, no fixed ports.
- A **30 s timeout** per test (`test.runsettings`) turns a hang into a failure instead of a blocked pipeline.

## Timing-sensitive tests

A few tests involve real time. They use wide margins, but know them when diagnosing a flaky run on a loaded machine:

| Test | Timing |
|---|---|
| `RetryAfterInSeconds_IsActuallyWaitedFor` | waits about 1 s, asserts at least 900 ms elapsed |
| `AttemptsThatTimeOut_AreRetried_ThenReportedAsTimeoutException`, `ATimeoutOnFirstAttempt_CanBeFollowedByASuccess` | 40 ms attempt timeout against a handler that hangs |
| `CallerCancellation_*` | cancellation after 50 to 100 ms |
| `ASlowServer_TriggersTheAttemptTimeout` | server answers after 2 s, attempt budget 100 ms |

The timeouts only need to be *shorter than* the hang, so a slow machine makes them fire later, not wrongly.

## Coverage and mutation testing

Coverage at 1.0.0: about 99 % of lines, about 97 % of branches (command in CONTRIBUTING; the rest is defensive code the test host cannot reach). It is a minimum, not proof: a manual
mutation pass (deliberately broken copies of the library) found tests that would not have noticed a regression, and they were
strengthened: the security pass (39 mutations) found ten, the pass on the features added afterwards (149 mutations of the redirect engine,
retries, pagination, errors, bodies, options validation and registration, then 18 of the test double) found about thirty, among them
the exact default redirect limit, a total timeout reported as an attempt timeout, a caller-supplied idempotency key being replaced, a failing
page that announces a next one, a `Link` relative to the wrong base, and quoted commas in a `Link` parameter. The mutants that remain are explained
equivalents (see docs/DESIGN.md); the procedure is described in CONTRIBUTING.

## Known limits

- The `net8.0` run uses the .NET 8 runtime only when it is installed (CI does). Locally it may roll forward.
- No load or performance tests: concurrency tests check correctness (40 to 100 simultaneous calls), not throughput.
- The tests do not cover HTTP/2 or TLS behaviour, which belong to the platform, not to this library.
