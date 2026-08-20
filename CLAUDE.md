# CLAUDE.md

Standing rules for this repository. They apply to every change, not just the one being asked for.

## Project

Smart-X is an IoT telemetry ingestion and mesh-management system, built as a university Portfolio
of Evidence. The stack is .NET 10 throughout:

| Project | What it is |
| --- | --- |
| `src/SmartX.Api` | ASP.NET Core Minimal API. Owns all server-side behaviour. |
| `src/SmartX.Client` | Blazor WebAssembly standalone app using MudBlazor. |
| `src/SmartX.Shared` | Class library of contracts shared by the other two. |
| `tests/SmartX.Tests` | xUnit tests. |

The client and the API are separate applications that communicate over HTTP with CORS configured.
The client must never take a project reference on the API — the separation has to be real, not just
a folder layout.

## Commits

- Conventional Commits with a scope: `type(scope): what changed`. Types in use are `feat`, `fix`,
  `refactor`, `docs`, `test`, `chore`, `perf`.
- Lowercase after the colon. One line, under about 70 characters. A body only when the change
  genuinely needs explaining, which most don't.
- Say what changed, not why it's good. No "comprehensive", "robust", "seamlessly", "enhanced",
  "leverages", "streamlined". No emoji. No bullet-point summaries.
- **Never add co-authorship trailers, `Co-Authored-By` lines, or any tool attribution** to commit
  messages or pull request descriptions.

## Branching

- `main` must always compile. Run `dotnet build` before every commit.
- Feature work happens on a branch named `part-N/feature-name`.
- Merge back with `--no-ff` so the branch structure stays visible in the history.

## Code

- Shared models live in `SmartX.Shared` and are used by both the API and the client. Never declare
  the same model twice — a contract that exists in two places is a contract that will drift.
- Prefer generics over `object`. This project is explicitly assessed on avoiding the boxing of
  value types, so a generic parameter is the default and `object` needs a justification.
- Every API call from the client is asynchronous, all the way down. Never block on async code:
  no `.Result`, no `.Wait()`, no `GetAwaiter().GetResult()`.
- Explain non-obvious design decisions in a comment where the decision lives. The author has to be
  able to defend every line verbally, so anything that would prompt "why did you do it that way?"
  should already answer the question in the file.
