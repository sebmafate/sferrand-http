# Contributing

Thanks for your interest. This document explains how to build, test and document a change so that it is ready to merge.

## Prerequisites

- The **.NET 10 SDK** (it builds both target frameworks, `net8.0` and `net10.0`).
- The **.NET 8 runtime** as well if you want the `net8.0` test run to execute on .NET 8. Without it the test host rolls
  forward to the newest installed runtime (`RollForward=Major` in the test project): the `net8.0` build is then compiled
  against the .NET 8 API surface but executed on a newer runtime. CI installs both, so it is the reference.

## Everyday commands

```bash
dotnet build -c Release            # warnings are errors
dotnet test                        # both target frameworks
dotnet test -f net10.0             # one target framework
dotnet test --filter TestCategory=Integration      # only the tests using a real local HTTP server
dotnet test --filter "TestCategory!=Integration"   # everything else (instant, no sockets)
dotnet pack src/SFerrand.Http -c Release -o artifacts
```

### Coverage

```bash
dotnet test -f net10.0 --collect:"XPlat Code Coverage" --results-directory /tmp/cov
grep -o '<coverage line-rate="[^"]*" branch-rate="[^"]*"' /tmp/cov/*/coverage.cobertura.xml
```

Reference values at 1.0.0: **100 % of lines, about 97 % of branches**. The uncovered branches are defensive fallbacks the
test host cannot reach: the user agent when the process has no entry assembly, and a `Retry-After` header carrying neither a
delay nor a date. A change should not lower either number without a reason stated in the pull request.
Coverage is a floor, not a goal: see "Mutation check" below for what it cannot tell you.

## What a change must contain

1. **Tests.** A bug fix starts with a test that fails for the right reason. A new behaviour is tested at its edges
   (null, empty, boundary values, cancellation), not only on the happy path. Read [tests/README.md](tests/README.md) first.
2. **XML documentation** on every public and protected member, including `<param>`, `<returns>` and `<exception>` where
   they apply. The build enforces the minimum (`CS1591` is an error), reviewers enforce the quality: say *what* and
   *when* (units, defaults, what happens on failure), not just repeat the name.
3. **README / docs updates** when behaviour visible to users changes. Every code sample in the README must compile and
   produce the output it claims; re-run it when you touch it.
4. **CHANGELOG entry** under `Unreleased`.
5. **A design note** in [docs/DESIGN.md](docs/DESIGN.md) if you change a decision recorded there, or make a new non-obvious one.

## Conventions

- C# 12, nullable reference types on, warnings as errors, 4-space indentation (see `.editorconfig`).
- Async all the way: never block on a task (`.Result`, `.Wait()`), always pass the `CancellationToken` down,
  `ConfigureAwait(false)` in library code.
- No state on the shared `HttpClient`: everything per request.
- Never log a query string, a body or a credential, and never put one in an exception message: describe an address with `RequestUri.Describe` (scheme, host, port).
- Anything that reaches the wire from outside the application (a header, a path segment, an address) is validated first. Do not add a header without `SetHeader`, and do not bypass the trusted-origin
  check. A change touching these needs a regression test that fails without it, in `SecurityTests`.
- Keep the dependency list short; a new package needs a justification in the pull request.

## Mutation check (manual)

Line and branch coverage only prove that code *ran*. Before a release, or after touching the retry or URL logic, break
the code on purpose in a scratch copy and make sure a test goes red:

- invert a condition (`attempt < maxAttempts` to `<=`), drop a guard, change a status code in the retry set;
- remove the encoding of a query parameter, leak a token into a log message;
- remove one of the security guards (trusted origin, clear-text check, header validation, dot segments, size limit) or widen one of their lists;
- make the retry wait ignore the cancellation token.

**Classify by the exit code of `dotnet test`, never by parsing its (localized) output**, and validate the harness with a mutation that must survive and one that must not: a harness that parses text once
reported every mutant as killed. A mutation that survives means a missing or weak test: add one (or explain why the mutant is equivalent). Two weak tests were found and fixed this way before 1.0.0
(cancellation versus timeout on a `POST`, and the encoding of query parameter *names*).

## Releases

Versions come from git tags (MinVer); there is nothing to bump in the project files. The procedure, the secrets and the
checks are in [RELEASING.md](RELEASING.md).

## Pull requests

Keep them focused, describe the *why*, and mention anything you could not verify (for instance behaviour that depends on a
specific runtime or platform).
