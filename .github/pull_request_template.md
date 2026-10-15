## Change

Describe the change and why it preserves existing consumers.

## Compatibility surface

Select exactly one. Comments and documentation alone use None. New public APIs should be additive and off by default. Removing or changing an existing public declaration is blocked, not waived by this form.

- [ ] **None**
- [ ] **Additive, off by default**
- [ ] **Changed**
- [ ] **Library code**

Describe existing callers, serialized payloads, defaults, error behavior and editor behavior affected by this change. Link exact-build evidence where applicable.

## Maintainer review

A maintainer must independently review meaningful source/CI changes and comment with this PR's full current head SHA and matching classification:

`/compatibility-review FULL_HEAD_SHA none|additive-default-off|changed|library-code`

This acknowledges the exact head; it does not waive removed APIs or missing native/editor evidence. New pushes invalidate review and certification. See [compatibility policy](https://github.com/sbox-cool/sboxns.com/tree/9dd4d2e324a671abbcde96081519c94edf2f152e/tools/compatibility).
