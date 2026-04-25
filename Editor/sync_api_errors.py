"""Structured Network Storage management API error formatting."""

import json


class SyncApiError(Exception):
    """Raised after a management API error has already been printed."""


def _as_list(value):
    if value is None:
        return []
    return value if isinstance(value, list) else [value]


def _compact_json(value):
    return json.dumps(value, ensure_ascii=False, separators=(",", ":"))[:500]


def _diag_text(item):
    if isinstance(item, str):
        return item
    if not isinstance(item, dict):
        return _compact_json(item)

    message = item.get("message") or item.get("detail") or item.get("reason") or item.get("error")
    code = item.get("code") or item.get("type") or item.get("budgetCategory")
    if isinstance(message, (dict, list)):
        message = _compact_json(message)

    head = str(message or code or _compact_json(item))
    if code and message and str(code) not in head:
        head = f"{code}: {head}"

    context = []
    path = item.get("sourcePath") or item.get("path") or item.get("file")
    if path:
        line = item.get("line") or item.get("sourceLine")
        column = item.get("column") or item.get("sourceColumn")
        if line and column:
            context.append(f"{path}:{line}:{column}")
        elif line:
            context.append(f"{path}:{line}")
        else:
            context.append(str(path))

    node = item.get("nodeId") or item.get("canonicalNode") or item.get("stepId")
    if node:
        context.append(f"node={node}")

    observed = item.get("observed") or item.get("actual")
    limit = item.get("limit") or item.get("max")
    if observed is not None and limit is not None:
        context.append(f"observed={observed} limit={limit}")

    suggestion = item.get("suggestion") or item.get("fix")
    if suggestion:
        context.append(f"fix={suggestion}")

    return f"{head} ({', '.join(context)})" if context else head


def _resource_label(item):
    if not isinstance(item, dict):
        return None
    kind = item.get("kind") or item.get("resourceKind") or item.get("type")
    resource_id = item.get("id") or item.get("resourceId") or item.get("slug") or item.get("name")
    path = item.get("sourcePath") or item.get("path")
    label = ":".join(str(x) for x in (kind, resource_id) if x)
    if path:
        label = f"{label} ({path})" if label else str(path)
    return label or None


def _print_diag_items(items, indent="    "):
    count = 0
    for item in _as_list(items):
        print(f"{indent}- {_diag_text(item)}")
        count += 1
    return count


def print_api_error(status_code, error_body):
    print(f"  HTTP {status_code}")
    if not error_body:
        return

    try:
        payload = json.loads(error_body)
    except Exception:
        print(f"  {error_body[:2000]}")
        return

    printed = 0
    if isinstance(payload, dict):
        for key in ("message", "error", "detail", "title"):
            value = payload.get(key)
            if isinstance(value, str):
                print(f"  {value}")
                printed += 1

        for key in ("diagnostics", "errors", "messages"):
            printed += _print_diag_items(payload.get(key))

        resources = payload.get("resources") or payload.get("results") or payload.get("items")
        for resource in _as_list(resources):
            label = _resource_label(resource)
            if label:
                print(f"  Resource {label}:")
                printed += 1

            nested = 0
            if isinstance(resource, dict):
                for key in ("diagnostics", "errors", "messages"):
                    nested += _print_diag_items(resource.get(key), indent="      ")
                if nested == 0 and resource.get("ok") is not True and resource.get("success") is not True:
                    nested += _print_diag_items(resource, indent="      ")
            else:
                nested += _print_diag_items(resource, indent="      ")
            printed += nested
    else:
        printed += _print_diag_items(payload)

    if printed == 0:
        print(f"  {_compact_json(payload)}")
