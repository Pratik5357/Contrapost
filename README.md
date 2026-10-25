# Contrapost

A double-entry payments ledger in .NET: correct balances under concurrent load, exactly-once effects for retried requests, and reliable event publishing through a transactional outbox.

The design is in [`Contrapost-Design-Doc.md`](Contrapost-Design-Doc.md).

## Status

Design phase. The service has not been built yet.

## Development workflow

`main` is always releasable and is only changed through pull requests.

### Branches

Branch from the latest `main` and name the branch `<type>/<short-description>`:

| Prefix | Use for |
|---|---|
| `feat/` | New functionality |
| `fix/` | Bug fixes |
| `perf/` | Performance improvements |
| `refactor/` | Code changes that don't change behaviour |
| `test/` | Tests only |
| `docs/` | Documentation only |
| `chore/` | Tooling, CI, dependencies |

Example: `feat/holds-partial-capture`, `fix/outbox-backoff-overflow`.

### Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <imperative summary, 72 chars max>

<body: what changed and why>
```

Scopes in use: `api`, `db`, `worker`, `domain`, `design`, `ci`, `deps`.

### Pull requests

1. Rebase the branch on `origin/main` before opening the PR and again before merging.
2. Fill in the PR template, including how the change was tested.
3. Merge with **squash** or **rebase**, so `main` keeps a linear history.
4. Delete the branch after merging.
