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

## Publishing

The [Publish to PyPI workflow](.github/workflows/publish.yml) tests and builds
the Python package, then uploads it using PyPI Trusted Publishing. It runs
when a GitHub release is published, or manually from GitHub Actions.

Configure the PyPI publisher for project `dahlia-cad`, owner `coincidentdata`,
repository `dahlia`, workflow `publish.yml`, and environment `pypi`.
For the first upload, register a pending publisher in the PyPI account's
publishing settings. No API token is stored in GitHub.

Update the version in `pyproject.toml` and `uv.lock` before each release and
publish from the matching `v<version>` tag. PyPI release files cannot be
replaced; corrections need a new version. The SOLIDWORKS installer is a
separate GitHub release asset.
