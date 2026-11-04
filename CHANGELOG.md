# Changelog

All notable changes to this project are documented here.
The format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and the project uses [Semantic Versioning](https://semver.org/).

## [Unreleased]

## [1.0.0] - 2026-11-04

First version.

### Added

- `HttpClientBase`: base class for typed REST and GraphQL clients (`GetAsync`, `ListAsync`, `PostAsync`, `PutAsync`,
  `PatchAsync`, `DeleteAsync`, `SendGraphQLAsync`, and `SendAsync` for any other verb).
- `HttpResult<T>`: status, headers, raw body and deserialized body in one object; HTTP errors are values, not exceptions.
- Retries with exponential backoff, equal jitter and `Retry-After` support (`RetryOptions`); only idempotent methods
  are retried unless `RetryNonIdempotentMethods` is set.
- Per-attempt timeouts (`HttpClientOptions.RequestTimeout`) reported as `TimeoutException`, distinct from caller cancellation.
- Per-request authentication (`TokenFactory` / `AsyncTokenFactory`) and headers, so a shared `HttpClient` never accumulates state.
- URL building that always appends the resource to the base address, with encoded identifiers and query parameters.
- Logging that never includes the query string, request bodies or tokens.
- `SimpleHttpClientFactory`: a minimal, thread-safe `IHttpClientFactory` with an injectable handler, for tests and tools.
- Security by default: calls only go to declared origins (`BaseUri`, `GraphQLUri`, `TrustedOrigins`); clear-text `http` is refused except on the loopback interface (`AllowInsecureHttp`); header names and
  values are validated (line breaks, control characters, headers owned by the HTTP stack); `.`/`..` segments, fragments and embedded credentials are refused in addresses; responses are limited by
  `MaxResponseBytes` (`HttpResponseTooLargeException`); nothing secret in logs or messages. See `SECURITY.md`.
- `SimpleHttpClientFactory` creates clients that do not follow redirects.
- Redirects followed by the client itself, hop by hop (`MaxRedirects`): every target checked like a call, credentials stop at the first change of origin.
- `TotalTimeout`: an overall deadline for a call, retries and waits included. `Accept: application/json` by default.
- `Retry.IdempotencyKeyHeader`: `POST` and `PATCH` carry a key, the same on every attempt, and become safe to retry.
- Typed errors: `ProblemDetails` (RFC 9457), `HttpResult.Problem`, `ApiException`, `EnsureSuccess()`. GraphQL: `GraphQLResponse<T>`, `GraphQLError`, `GraphQLException`, `QueryGraphQLAsync`.
- Pagination: `GetPagesAsync` and `ListAllAsync` as `IAsyncEnumerable` (`Link` header, page numbers, or a cursor from the body).
- `PostFormAsync`, `PostMultipartAsync` (`MultipartForm`), and `DownloadAsync` (bounded streaming, `MaxDownloadBytes`, `DownloadInterruptedException`).
- Telemetry under the name `SFerrand.Http`: a span per logical call and three metrics; log messages generated at compile time.
- `AddSFerrandHttpClient<TClient>`: per-client options, a handler that does not follow redirects, options validated at start-up (`HttpClientOptionsValidator`).
- Trimmable and AOT-compatible: JSON metadata comes from `JsonTypeInfo`, so a source-generated context is enough.
- `SFerrand.Http.Testing`: a scripted, recording `HttpMessageHandler` (`StubHandler`) as a separate package.
- Targets `net8.0` and `net10.0`; XML documentation shipped with the package.
