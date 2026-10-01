"""``python -m myproxy_server`` production entry point.

Loads settings from the environment (``MYPROXY_*``), requires the
least-privilege x-ui Unix socket boundary, initialises the SQLite schema and
serves HTTPS/HTTP.
"""

from __future__ import annotations

from .config import Settings
from .server import run


def main() -> None:
    settings = Settings.from_env()
    settings.require_configured_deployment()
    run(settings)


if __name__ == "__main__":
    main()
