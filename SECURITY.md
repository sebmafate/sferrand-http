# Security policy

## Reporting a vulnerability

Please **do not open a public issue** for a security problem. Report it privately to the maintainer (through the private vulnerability
reporting of the hosting platform, or by contacting the maintainer directly), with a description, a minimal reproduction and the version
concerned. You will get an answer, and a fix will be released before the details are made public.

## Supported versions

Only the latest released version receives security fixes.

## What the library defends against, and what it does not

`SFerrand.Http` is a client: its job is to send requests *on behalf of the application* with the application's credentials, using values that
may partly come from outside (an identifier typed by a user, a URL found in a response, a header copied from a request). The threats it is
designed against, and the tests that pin each defence, are listed in the [Security section of the README](README.md#security) and explained in
[docs/DESIGN.md](docs/DESIGN.md#security-decisions).

Out of scope, and the application's responsibility:

- **Which hosts are legitimate.** The client only enforces the list you give it (`BaseUri`, `GraphQLUri`, `TrustedOrigins`).
- **The handler pipeline.** Proxies, certificate validation and TLS settings belong to the `HttpMessageHandler` you configure. So does *automatic* redirect following:
  the library follows redirects itself, safely, but cannot disable the handler's own on a client it did not create (`AddSFerrandHttpClient` and `SimpleHttpClientFactory` do).
  See "Redirects" in the README.
- **Authentication flows and token storage.** You provide the token; keep it out of source control and logs.
- **Trust in the content of a response.** A response from a trusted origin is still data: validate it before acting on it.
- **Availability.** Retries are bounded and honour `Retry-After` up to a maximum, but the client is not a rate limiter or a circuit breaker.

## Reviewed since

The features added after the first release candidate were reviewed with the same questions: where does this value come from, who can influence it, what does it reach?
Pagination links and redirect targets are server-controlled and go through the same origin, scheme, credential and downgrade checks as any call; multipart names, which the
framework writes unescaped, are validated; a problem document is parsed without the serializer and its raw body kept out of exception messages; telemetry and logs carry
addresses without their query string; the redirect and download engines never replay a body to another origin or write the same bytes twice.

## History

Found by a review of the first version, and fixed before its release (all covered by regression tests that failed on the code that did not have the fix):
header injection through a line break in a header value (reproduced on a real connection), credentials and default headers sent to any origin chosen by a
caller-supplied absolute URL or `baseUri`, path traversal through `..` in an identifier or a resource, tokens over clear-text `http`, credentials echoed in
exception messages and logs, silently dropped invalid headers, an unbounded response body read into memory, and custom headers forwarded on a cross-origin redirect.
