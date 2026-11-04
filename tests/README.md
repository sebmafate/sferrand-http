# Test suite

139 tests, MSTest 4, run on `net8.0` and `net10.0`. No test needs the Internet. This page explains how the suite is organized
and how to add to it; how to run it is in [CONTRIBUTING.md](../CONTRIBUTING.md).

## Strategy

| Layer | Class(es) | What it protects | Speed |
|---|---|---|---|
| Pure logic | `RetryOptionsTests`, `RequestUriTests` | Backoff maths, jitter bounds, `Retry-After`, URL building and encoding. Randomness and clock are injected, so results are exact. | instant |
| Value object | `HttpResultTests` | Deserialization rules, error results, synthetic results. | instant |
| Behaviour through a stub | `HttpClientBaseTests`, `HttpClientBaseEdgeCaseTests`, `SimpleHttpClientFactoryTests` | The whole request pipeline (headers, auth, retries, timeouts, cancellation, logging) against a recording `HttpMessageHandler`. | instant to ~1 s |
| Real network stack | `IntegrationTests` (`[TestCategory("Integration")]`) | What a stub cannot reproduce: real header validation, real sockets, refused connections, concurrency on one pooled `HttpClient`. A loopback `HttpListener` on a free port. | ~1 s |

The stub tests make up most of the suite because they are deterministic. The integration tests are few on purpose: each
one covers something the stub would silently get wrong.

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

Coverage at 1.0.0: 100 % of lines, about 97 % of branches (command in CONTRIBUTING). It is a minimum, not proof: a manual
mutation pass (deliberately broken copies of the library) found two tests that would not have noticed a regression
(cancellation versus timeout on a `POST`; the encoding of query parameter names). They were strengthened, and the
procedure is described in CONTRIBUTING.

## Known limits

- The `net8.0` run uses the .NET 8 runtime only when it is installed (CI does). Locally it may roll forward.
- No load or performance tests: concurrency tests check correctness (40 to 100 simultaneous calls), not throughput.
- The tests do not cover HTTP/2 or TLS behaviour, which belong to the platform, not to this library.
