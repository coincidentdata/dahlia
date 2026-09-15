# Contributing to Dahlia

Open issues and pull requests in this repository. Include a small reproduction
for bugs and explain the resulting behavior for changes.

Use Python 3.12 and run these checks before submitting:

```powershell
uv sync --locked
uv run pytest
uv build
```

The Python tests run without SOLIDWORKS. For live CAD changes, describe what you
tested and the SOLIDWORKS version used. Maintainers verify native behavior
internally when needed.

Maintainers integrate accepted changes into the development repository, run the
relevant checks, and publish them here. A pull request may therefore be closed
with a link to the published commit instead of being merged through GitHub.
Contributor attribution and the pull request reference are preserved.

See [AGENTS.md](AGENTS.md) for API conventions and CAD scripting guidance.
