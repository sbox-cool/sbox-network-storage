"""Script endpoint files for the Python sync tool (issue #45).

A script endpoint is an envelope `endpoints/<id>.endpoint.yml` with
`runtime: javascript|typescript` and its code in a sibling `<id>.endpoint.js` or
`<id>.endpoint.ts` file. The server stores the code under `script`, so push inlines
the file as `script` in the envelope source text. YAML-only endpoints are untouched.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

from source_compiler import parse_source_yaml, unwrap_source_body

SCRIPT_EXTENSIONS = {"javascript": ".js", "typescript": ".ts"}

# Characters that YAML 1.2 does not treat as printable, or that some parsers fold as
# line breaks. They are escaped so the inlined scalar reads back byte-for-byte.
_UNSAFE_YAML_CHARS = re.compile("[\x7f-\x9f\u2028\u2029\ufeff]")


class ScriptFileError(Exception):
    """An endpoint's script cannot be pushed as written. The message names the source file."""


def inline_script_sources(items, ns_dir):
    """Return source items with script endpoint files inlined as `script`.

    `items` are raw source payloads (`kind`, `id`, `sourcePath` relative to `ns_dir`,
    `sourceText`). Non-endpoint items and YAML-only endpoints are returned unchanged.
    Raises ScriptFileError listing every problem, so nothing is sent when any is found.
    """
    result = []
    errors = []
    for item in items:
        if item.get("kind") != "endpoint":
            result.append(item)
            continue
        try:
            result.append(_inline_item(item, Path(ns_dir)))
        except ScriptFileError as error:
            errors.append(str(error))
            result.append(item)
    if errors:
        raise ScriptFileError("\n".join(errors))
    return result


def script_text_from_file(path):
    """Read a script file as UTF-8 text. A leading BOM is dropped; line endings are kept."""
    try:
        return path.read_bytes().decode("utf-8-sig")
    except UnicodeDecodeError as error:
        raise ScriptFileError(f"{path.name}: not valid UTF-8 ({error.reason} at byte {error.start})") from error


def source_text_with_script(source_text, script):
    """Append `script` to an envelope's YAML as a double-quoted scalar.

    JSON string syntax is valid YAML double-quoted syntax. Characters that YAML parsers
    may treat as breaks or non-printable are escaped, so the scalar reads back exactly.
    """
    encoded = json.dumps(script, ensure_ascii=False)
    encoded = _UNSAFE_YAML_CHARS.sub(lambda m: "\\u%04x" % ord(m.group(0)), encoded)
    body = source_text if source_text.endswith("\n") else source_text + "\n"
    return f"{body}script: {encoded}\n"


def _inline_item(item, ns_dir):
    source_path = item.get("sourcePath") or item.get("id", "")
    directory = (ns_dir / source_path).parent
    endpoint_id = item.get("id", "")
    data = _envelope_mapping(item.get("sourceText", ""), source_path)
    if data is None:
        return item

    runtime = data.get("runtime")
    expected_ext = SCRIPT_EXTENSIONS.get(runtime)
    if expected_ext is None:
        if "scriptFile" in data:
            raise ScriptFileError(f"{source_path}: scriptFile needs runtime: javascript or runtime: typescript")
        stray = [directory / f"{endpoint_id}.endpoint{ext}" for ext in SCRIPT_EXTENSIONS.values()]
        stray = [path for path in stray if path.is_file()]
        if stray:
            raise ScriptFileError(f"{source_path}: {stray[0].name} exists but runtime is not javascript or typescript")
        return item

    if "scriptFile" in data:
        script_name = data["scriptFile"]
        if not _is_bare_file_name(script_name):
            raise ScriptFileError(f"{source_path}: scriptFile must be a file name next to the endpoint, without folders")
        if not script_name.endswith(expected_ext):
            raise ScriptFileError(f"{source_path}: scriptFile {script_name} must end with {expected_ext} for runtime {runtime}")
        script_path = directory / script_name
        if not script_path.is_file():
            raise ScriptFileError(f"{source_path}: scriptFile {script_name} not found")
    else:
        other_ext = [ext for ext in SCRIPT_EXTENSIONS.values() if ext != expected_ext]
        for ext in other_ext:
            other = directory / f"{endpoint_id}.endpoint{ext}"
            if other.is_file():
                raise ScriptFileError(f"{source_path}: {other.name} exists but runtime is {runtime}; remove one of the two files")
        script_path = directory / f"{endpoint_id}.endpoint{expected_ext}"
        if not script_path.is_file():
            return item

    if Path(source_path).suffix.lower() == ".json":
        raise ScriptFileError(f"{source_path}: script files need a .endpoint.yml envelope; a JSON envelope cannot reference {script_path.name}")

    if "script" in data:
        raise ScriptFileError(f"{source_path}: script is set both inline and in {script_path.name}; keep one")

    script = script_text_from_file(script_path)
    return {**item, "sourceText": source_text_with_script(item["sourceText"], script)}


def _envelope_mapping(source_text, source_path):
    data, diagnostics = parse_source_yaml(source_text, source_path)
    if data is None or any(d.get("severity") == "error" for d in diagnostics):
        return None
    if not isinstance(data, dict):
        return None
    return unwrap_source_body(data, "endpoint")


def _is_bare_file_name(value):
    if not isinstance(value, str) or not value or value in (".", ".."):
        return False
    return "/" not in value and "\\" not in value and ":" not in value and Path(value).name == value
