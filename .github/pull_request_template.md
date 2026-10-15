## Change

Describe the change and why it preserves existing consumers.

## Compatibility surface

Select exactly one. Comments and documentation alone use None. New public APIs should be additive and off by default. Removing or changing an existing public declaration is blocked by the Compat gate API check, not waived by this form.

- [ ] **None**
- [ ] **Additive, off by default**
- [ ] **Changed**
- [ ] **Library code**

Describe existing callers, serialized payloads, defaults, error behavior and editor behavior affected by this change. Link exact-build evidence where applicable.

## Compat gate labels

- `compat: breaking-approved`: a maintainer applies this only when a public symbol is removed or changed on purpose and the migration in [docs/compat-gate.md](../docs/compat-gate.md) is in place. Without it the API check fails.
- `editor-tested`: a maintainer applies this after the s&box editor run is recorded in the server repo. Required when this PR changes `Code/` or `Editor/`.
- `pending-sbox-editor-test` is added automatically while `editor-tested` is missing.
