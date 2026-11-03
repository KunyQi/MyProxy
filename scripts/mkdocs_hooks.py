"""MkDocs hooks for the MyProxy documentation site (see mkdocs.yml).

The site publishes Markdown that already lives across the repository -- the
README, policy files, docs/ and component READMEs -- without moving or
duplicating it.  Each published file is mapped from its repository path to a
site path, and relative links are rewritten so they keep working on the site:

* a link to another published page points at that page on the site;
* a link to any other repository file or folder points at it on GitHub.

The "edit this page" button points at the page's real file in the repository.
"""

from __future__ import annotations

import posixpath
import re
from pathlib import Path

from mkdocs.structure.files import File

REPO_URL = "https://github.com/KunyQi/MyProxy"
BRANCH = "main"
ROOT = Path(__file__).resolve().parent.parent
DOCS_DIR = "docs"

# Repository path -> site source path, for pages outside docs/.
EXTRA_PAGES = {
    "README.md": "index.md",
    "CHANGELOG.md": "changelog.md",
    "CONTRIBUTING.md": "contributing.md",
    "SECURITY.md": "security.md",
    "THIRD_PARTY_NOTICES.md": "third-party-notices.md",
    "server/README.md": "server/index.md",
    "server/docs/DEPLOYMENT.md": "server/deployment.md",
    "server/docs/admin-browser-bridge.md": "server/admin-browser-bridge.md",
    "linux/MyProxyLinux/README.md": "linux/index.md",
    "linux/MyProxyLinux/gui/README.md": "linux/gui.md",
}

_SCHEME = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")
_FENCE = re.compile(r"^\s*(`{3,}|~{3,})")
# Inline links and images: ](target) or ](target "title")
_LINK = re.compile(r'\]\(([^)\s]+)(\s+"[^"]*")?\)')

_repo_to_site: dict[str, str] = {}
_site_to_repo: dict[str, str] = {}


def on_files(files, config):
    """Register pages outside docs/ and record every page's repository path."""
    _repo_to_site.clear()
    _site_to_repo.clear()
    for file in files.documentation_pages():
        _register(f"{DOCS_DIR}/{file.src_uri}", file.src_uri)
    for repo_path, site_path in EXTRA_PAGES.items():
        if files.get_file_from_path(site_path) is not None:
            raise ValueError(f"{site_path} exists in {DOCS_DIR}/ and would shadow {repo_path}")
        content = (ROOT / repo_path).read_text(encoding="utf-8")
        files.append(File.generated(config, site_path, content=content))
        _register(repo_path, site_path)
    return files


def on_page_markdown(markdown, page, config, files):
    repo_path = _site_to_repo.get(page.file.src_uri)
    if repo_path is None:
        return markdown
    page.edit_url = f"{REPO_URL}/edit/{BRANCH}/{repo_path}"
    return _rewrite_links(markdown, repo_path, page.file.src_uri)


def _register(repo_path: str, site_path: str) -> None:
    _repo_to_site[repo_path] = site_path
    _site_to_repo[site_path] = repo_path


def _rewrite_links(markdown: str, repo_path: str, site_path: str) -> str:
    lines = markdown.split("\n")
    fence = None
    for index, line in enumerate(lines):
        match = _FENCE.match(line)
        if match:
            marker = match.group(1)
            if fence is None:
                fence = marker[0] * len(marker)
            elif marker.startswith(fence):
                fence = None
            continue
        if fence is None and "](" in line:
            lines[index] = _LINK.sub(lambda m: _replace(m, repo_path, site_path), line)
    return "\n".join(lines)


def _replace(match: re.Match, repo_path: str, site_path: str) -> str:
    target, title = match.group(1), match.group(2) or ""
    rewritten = _resolve(target, repo_path, site_path)
    return f"]({rewritten}{title})"


def _resolve(target: str, repo_path: str, site_path: str) -> str:
    if _SCHEME.match(target) or target.startswith(("#", "/")):
        return target
    path, hash_mark, anchor = target.partition("#")
    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(repo_path), path))
    if resolved.startswith(".."):
        return target
    if resolved in _repo_to_site:
        start = posixpath.dirname(site_path) or "."
        return posixpath.relpath(_repo_to_site[resolved], start) + hash_mark + anchor
    absolute = ROOT / resolved
    if not absolute.exists():
        # Leave it alone so the strict build reports the broken link.
        return target
    kind = "tree" if absolute.is_dir() else "blob"
    return f"{REPO_URL}/{kind}/{BRANCH}/{resolved}" + hash_mark + anchor
