"""MkDocs hooks for the MyProxy documentation site (see mkdocs.yml).

The site's own pages (home page and guides) live in docs/. It also publishes
the technical Markdown that already lives across the repository -- the README,
policy files, docs/ and component READMEs -- without moving or duplicating it.

The site is built twice from the same page list: in Chinese from the original
files (mkdocs.yml, served at the site root) and in English from translations
kept next to them with an ".en.md" suffix (mkdocs.en.yml, served under /en/).
Both builds share one set of site paths, so the header language button opens
the same page in the other language. A page without its translation fails the
English build.

Relative links are rewritten so they keep working on the site:

* a link to another published page points at that page in the current
  language; a link that names the other language's file (README.md from an
  English page, README.en.md from a Chinese one) points at the other site;
* a link to any other repository file or folder points at it on GitHub.

The "edit this page" button points at the page's real file in the repository.

The site's look follows the desktop client: colours, corner radii, motion
timings, shadows and the font stack are read from the client's own design
tokens (windows/MyProxy/Themes/*.xaml) at build time and emitted as CSS
variables, one set per client skin.  docs/assets/stylesheets/myproxy.css maps
those variables onto the Material theme, so a skin retuned in the client is
picked up by the next site build instead of drifting.
"""

from __future__ import annotations

import posixpath
import re
from pathlib import Path

from mkdocs.structure.files import File, InclusionLevel

REPO_URL = "https://github.com/KunyQi/MyProxy"
BRANCH = "main"
ROOT = Path(__file__).resolve().parent.parent
DOCS_DIR = "docs"
SOURCE_LANG = "zh"
EN_SUFFIX = ".en.md"

# Repository path -> site path, for pages outside docs/ (docs/**/*.md keep
# their own relative paths). The site's home page is docs/index.md; the README
# is published as a reference page.
EXTRA_PAGES = {
    "README.md": "readme.md",
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
# Already written in English: both languages publish the same file.
SHARED_PAGES = {"THIRD_PARTY_NOTICES.md"}

# Site colour scheme -> the client skin's token file (see windows/MyProxy/Core/UiTheme.cs:
# Classic is shown as 经典, Porcelain as 瓷白, Ceramic as 釉陶).
SKINS = {
    "classic": "windows/MyProxy/Themes/DesignTokens.xaml",
    "porcelain": "windows/MyProxy/Themes/Porcelain.xaml",
    "ceramic": "windows/MyProxy/Themes/Ceramic.xaml",
}
# Colour tokens the stylesheet relies on; every skin must define all of them.
REQUIRED_COLORS = (
    "Background", "Surface", "SurfaceSunken", "TextPrimary", "TextSecondary",
    "TextDisabled", "Border", "BorderStrong", "Accent", "AccentHover",
    "AccentText", "Success", "SuccessText", "Error", "ErrorText", "Shadow",
)
# After the client's own Font.Family: Inter (bundled by the Linux client), then
# platform UI fonts with CJK coverage.
FONT_FALLBACK = (
    'Inter, -apple-system, BlinkMacSystemFont, "PingFang SC", "Hiragino Sans GB", '
    '"Microsoft YaHei", "Noto Sans SC", "Noto Sans CJK SC", system-ui, sans-serif'
)
APP_ICON = "windows/MyProxy/Assets/app.svg"
SITE_TOKENS = "assets/stylesheets/tokens.css"
SITE_ICON = "assets/images/app.svg"

_SCHEME = re.compile(r"^[a-zA-Z][a-zA-Z0-9+.-]*:")
_FENCE = re.compile(r"^\s*(`{3,}|~{3,})")
# Inline links and images: ](target) or ](target "title")
_LINK = re.compile(r'\]\(([^)\s]+)(\s+"[^"]*")?\)')

_lang = SOURCE_LANG
_alternates: dict[str, str] = {}
_repo_to_site: dict[str, str] = {}
_site_to_repo: dict[str, str] = {}
_site_to_source: dict[str, str] = {}


def on_files(files, config):
    """Register every published page in this build's language, the design tokens and the icon."""
    global _lang, _alternates
    _lang = config.theme["language"]
    _alternates = {alt["lang"]: alt["link"] for alt in config.extra.get("alternate") or []}
    if _lang not in _alternates:
        raise ValueError(f"extra.alternate has no entry for the build language {_lang!r}")
    _repo_to_site.clear()
    _site_to_repo.clear()
    _site_to_source.clear()
    for repo_path, site_path in site_pages().items():
        source = _source_path(repo_path)
        existing = files.get_file_from_path(site_path)
        if existing is not None and not existing.inclusion.is_excluded():
            # A docs/ page MkDocs already loaded is used as is; anything else is a clash.
            if source != f"{DOCS_DIR}/{site_path}":
                raise ValueError(f"{site_path} in {DOCS_DIR}/ would shadow {source}")
        else:
            if existing is not None:
                files.remove(existing)
            path = ROOT / source
            if not path.is_file():
                raise ValueError(f"missing translation {source} for {repo_path}")
            # Explicitly included: MkDocs re-applies exclude_docs after on_files, and the
            # English build excludes every native *.md.
            page = File.generated(config, site_path, content=path.read_text(encoding="utf-8"),
                                  inclusion=InclusionLevel.INCLUDED)
            files.append(page)
        _repo_to_site[repo_path] = site_path
        _site_to_repo[site_path] = repo_path
        _site_to_source[site_path] = source
    _add(files, File.generated(config, SITE_TOKENS, content=design_tokens_css()), "client design tokens")
    _add(files, File.generated(config, SITE_ICON, abs_src_path=str(ROOT / APP_ICON)), APP_ICON)
    return files


def on_page_markdown(markdown, page, config, files):
    repo_path = _site_to_repo.get(page.file.src_uri)
    if repo_path is None:
        return markdown
    page.edit_url = f"{REPO_URL}/edit/{BRANCH}/{_site_to_source[page.file.src_uri]}"
    return _rewrite_links(markdown, repo_path, page.file.src_uri)


def site_pages() -> dict[str, str]:
    """Repository path of each published page (in the source language) -> its site path."""
    pages = {}
    for path in sorted((ROOT / DOCS_DIR).rglob("*.md")):
        relative = path.relative_to(ROOT / DOCS_DIR).as_posix()
        if not relative.endswith(EN_SUFFIX):
            pages[f"{DOCS_DIR}/{relative}"] = relative
    pages.update(EXTRA_PAGES)
    return pages


def _source_path(repo_path: str) -> str:
    if _lang == SOURCE_LANG or repo_path in SHARED_PAGES:
        return repo_path
    return repo_path[: -len(".md")] + EN_SUFFIX


def _page_url(site_path: str) -> str:
    """Directory-style URL of a page, relative to its language's site root."""
    if site_path == "index.md":
        return ""
    if site_path.endswith("/index.md"):
        return site_path[: -len("index.md")]
    return site_path[: -len(".md")] + "/"


def design_tokens_css() -> str:
    """CSS variables (--mp-*) for each client skin, read from its XAML tokens."""
    blocks = [
        "/* Generated by scripts/mkdocs_hooks.py from windows/MyProxy/Themes/*.xaml. */",
        "/* Do not edit: change the client tokens instead. */",
    ]
    for scheme, repo_path in SKINS.items():
        tokens = _read_skin(repo_path)
        lines = [f"  --mp-{_kebab(name)}: {value};" for name, value in tokens["colors"].items()]
        lines += [f"  --mp-radius-{size.lower()}: {value}px;" for size, value in tokens["radius"].items()]
        lines += [f"  --mp-motion-{speed.lower()}: {value}ms;" for speed, value in tokens["motion"].items()]
        lines += [f"  --mp-shadow-{kind.lower()}: {value};" for kind, value in tokens["shadow"].items()]
        lines.append(f"  --mp-font-family: {tokens['font']};")
        blocks.append(f'[data-md-color-scheme="{scheme}"] {{\n' + "\n".join(lines) + "\n}")
    return "\n\n".join(blocks) + "\n"


def _read_skin(repo_path: str) -> dict:
    xaml = (ROOT / repo_path).read_text(encoding="utf-8")
    colors = dict(re.findall(r'<Color x:Key="Color\.([A-Za-z]+)">(#[0-9A-Fa-f]{6})</Color>', xaml))
    radius = dict(re.findall(r'<CornerRadius x:Key="Radius\.(SM|MD|LG)">(\d+)</CornerRadius>', xaml))
    motion = {
        speed: round(float(seconds) * 1000)
        for speed, seconds in re.findall(r'<Duration x:Key="Motion\.Duration\.(Fast|Base)">0:0:([\d.]+)</Duration>', xaml)
    }
    families = re.findall(r'<FontFamily x:Key="Font\.Family">([^<]+)</FontFamily>', xaml)
    missing = [name for name in REQUIRED_COLORS if name not in colors]
    missing += [f"Radius.{size}" for size in ("SM", "MD", "LG") if size not in radius]
    missing += [f"Motion.Duration.{speed}" for speed in ("Fast", "Base") if speed not in motion]
    missing += ["Font.Family"] if not families else []
    shadow = {}
    for kind in ("Contact", "Ambient"):
        match = re.search(rf'<DropShadowEffect x:Key="Shadow\.{kind}"(.*?)/>', xaml, re.S)
        if match is None:
            missing.append(f"Shadow.{kind}")
            continue
        attrs = dict(re.findall(r'(BlurRadius|ShadowDepth|Opacity)="([\d.]+)"', match.group(1)))
        shadow[kind] = _css_shadow(colors.get("Shadow", "#000000"), attrs)
    if missing:
        raise ValueError(f"{repo_path} is missing design tokens used by the site: {', '.join(missing)}")
    font = ", ".join(f'"{name.strip()}"' for name in families[0].split(",")) + ", " + FONT_FALLBACK
    return {"colors": colors, "radius": radius, "motion": motion, "shadow": shadow, "font": font}


def _css_shadow(color: str, attrs: dict) -> str:
    # WPF DropShadowEffect with Direction=270 casts straight down by ShadowDepth.
    blur, depth, opacity = (float(attrs.get(key, 0)) for key in ("BlurRadius", "ShadowDepth", "Opacity"))
    if opacity == 0:
        return "0 0 0 0 transparent"  # stays valid inside a comma-separated box-shadow list
    red, green, blue = (int(color[i:i + 2], 16) for i in (1, 3, 5))
    return f"0 {depth:g}px {blur:g}px rgb({red} {green} {blue} / {opacity:g})"


def _kebab(name: str) -> str:
    return re.sub(r"(?<!^)(?=[A-Z])", "-", name).lower()


def _add(files, file, source: str) -> None:
    if files.get_file_from_path(file.src_uri) is not None:
        raise ValueError(f"{file.src_uri} already exists and would be shadowed by {source}")
    files.append(file)


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
    # Translations sit next to their originals, so links resolve from the original's folder.
    resolved = posixpath.normpath(posixpath.join(posixpath.dirname(repo_path), path))
    if resolved.startswith(".."):
        return target
    page, lang = resolved, SOURCE_LANG
    if resolved.endswith(EN_SUFFIX):
        page, lang = resolved[: -len(EN_SUFFIX)] + ".md", "en"
    elif page in SHARED_PAGES or repo_path in SHARED_PAGES:
        # A page shared by both languages, or a plain link written on one, stays
        # in the language being read.
        lang = _lang
    if page in _repo_to_site:
        target_site = _repo_to_site[page]
        if lang == _lang:
            start = posixpath.dirname(site_path) or "."
            return posixpath.relpath(target_site, start) + hash_mark + anchor
        return _alternates[lang] + _page_url(target_site) + hash_mark + anchor
    absolute = ROOT / resolved
    if not absolute.exists():
        # Leave it alone so the strict build reports the broken link.
        return target
    kind = "tree" if absolute.is_dir() else "blob"
    return f"{REPO_URL}/{kind}/{BRANCH}/{resolved}" + hash_mark + anchor
