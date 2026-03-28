# Error Patterns — Diagnosis Reference

> **Note:** For the most up-to-date documentation, visit https://sbox.cool/wiki/network-storage-v3 — these repo docs may be outdated.

This document lists all error patterns recognized by the MCP's `diagnose_error` tool. When an error message matches one of these patterns, the tool returns the explanation and suggested fixes.

## Server Error Codes

### UNAUTHORIZED
**Matches:** `UNAUTHORIZED`, `401`, `unauthorized`

The API key is invalid, missing, or doesn't belong to the specified project.

**Possible Causes:**
- Wrong API key entered in Setup
- Key belongs to a different project
- Key was deleted or rotated on sbox.cool
- Missing x-api-key header in request

**Fixes:**
- Re-check credentials in Editor → Network Storage → Setup
- Verify key prefixes: public = `sbox_ns_`, secret = `sbox_sk_`
- Regenerate keys on sbox.cool dashboard if needed
- Restart s&box fully (not hot-reload) after changing credentials

---

### PROJECT_DISABLED
**Matches:** `PROJECT_DISABLED`, `project.*disabled`

The project exists but has been disabled on sbox.cool.

**Fixes:**
- Check project status on sbox.cool dashboard
- Re-enable the project

---

### QUOTA_EXCEEDED
**Matches:** `QUOTA_EXCEEDED`, `quota`

Monthly API usage limit has been exceeded.

**Fixes:**
- Check usage on sbox.cool dashboard
- Optimize call frequency
- Upgrade plan if available

---

### SCHEMA_VALIDATION_FAILED
**Matches:** `SCHEMA_VALIDATION_FAILED`, `schema.*validation`

The data being written doesn't match the collection's schema definition.

**Possible Causes:**
- Field type mismatch (e.g., sending string where number expected)
- Missing required fields
- Collection schema was updated but endpoint still sends old format

**Fixes:**
- Compare endpoint output fields with collection schema
- Use `validate_collection` tool to check schema
- Push updated collection schema via Sync Tool

---

### INVALID_JSON
**Matches:** `INVALID_JSON`, `invalid.*json`

The request body is not valid JSON.

**Fixes:**
- Validate JSON syntax
- Ensure Content-Type is application/json
- Check that POST body is not empty

---

### INVALID_BODY
**Matches:** `INVALID_BODY`, `invalid.*body`

The request body structure doesn't match what the endpoint expects.

**Fixes:**
- Check endpoint's input definition
- Use `validate_endpoint` tool to verify
- Ensure all required fields are sent

---

### RATE_LIMIT
**Matches:** `RATE_LIMIT`, `rate.?limit`

The rate limit for this collection/endpoint has been exceeded.

**Fixes:**
- Adjust rateLimits in collection settings
- Implement client-side throttling
- Check for duplicate API calls

---

### ENDPOINT_NOT_FOUND
**Matches:** `ENDPOINT_NOT_FOUND`, `endpoint.*not.*found`

The endpoint slug doesn't exist on the server.

**Possible Causes:**
- Endpoint not pushed via Sync Tool
- Typo in endpoint slug
- Endpoint was deleted on sbox.cool

**Fixes:**
- Open Sync Tool and push the endpoint
- Double-check the slug matches your JSON filename
- Verify endpoint exists on sbox.cool dashboard

---

### ENDPOINT_DISABLED
**Matches:** `ENDPOINT_DISABLED`, `endpoint.*disabled`

The endpoint exists but has `enabled: false`.

**Fixes:**
- Set `"enabled": true` in the endpoint JSON file
- Push the updated endpoint via Sync Tool

---

### CONDITION_FAILED
**Matches:** `CONDITION_FAILED`, `condition.*failed`

A condition step in the endpoint pipeline failed. This is expected game logic (e.g., player tried an action they can't do).

**Fixes:**
- This is normal game behavior — handle it in client code
- Add custom `errorCode` to your condition for better identification
- Check the `errorMessage` for details

---

### INTERNAL_ERROR
**Matches:** `INTERNAL_ERROR`, `internal.*error`

The server crashed while executing the endpoint. This indicates a misconfiguration.

**Possible Causes:**
- Endpoint references a collection that doesn't exist
- Step references an undefined alias
- Template syntax error in step values
- Server-side bug

**Fixes:**
- Use `validate_endpoint` tool to check for issues
- Verify all referenced collections are pushed
- Check template syntax (`{{input.x}}`, `{{alias.field}}`)
- Check server logs on sbox.cool dashboard

---

## Client-Side Errors

### HTTP_400
**Matches:** `Response status code does not indicate success.*400`, `status.*400`

s&box's Http API threw an exception because the server returned HTTP 400. The response body (with error details) was lost.

**Fixes:**
- Remove the `"status"` field from onFail or set it to 200
- s&box needs HTTP 200 to read the response body — use `ok: false` for errors instead

---

### NO_CREDENTIALS
**Matches:** `credentials.*not.*found`, `no.*credentials`, `network-storage.credentials.json`

The runtime credentials file (`Assets/network-storage.credentials.json`) was not found.

**Fixes:**
- Open Editor → Network Storage → Setup and save credentials
- Check `Assets/network-storage.credentials.json` exists
- Restart s&box after saving

---

### EMPTY_AUTH_TOKEN
**Matches:** `auth.*token.*empty`, `empty.*auth`, `steam.*auth`

The Steam authentication token is empty. This usually means you're not in Play mode.

**Fixes:**
- Press Play in the s&box editor
- Ensure Steam is running and you're logged in
- Steam tokens are only available during active Play sessions

---

### NOT_CONFIGURED
**Matches:** `not.*configured`, `NetworkStorage.*not.*configured`

NetworkStorage hasn't been configured. Auto-config didn't find credentials and `Configure()` wasn't called.

**Fixes:**
- Set up credentials via Editor → Network Storage → Setup
- Or call `NetworkStorage.Configure(projectId, apiKey)` manually
- Restart s&box fully after changing credentials

---

### MISSING_COLLECTION
**Matches:** `undefined.*read`, `undefined read on`

An endpoint references a collection that doesn't exist on the server.

**Fixes:**
- Push the collection via Sync Tool BEFORE pushing the endpoint
- Verify collection name matches exactly (case-sensitive, lowercase + underscores)
- Always push collections first, then endpoints

---

### UNRESOLVED_TEMPLATE
**Matches:** `{{...}}`, `template.*text`, `unresolved.*template`

Template variables (`{{...}}`) weren't resolved by the server, appearing as raw text in the response.

**Fixes:**
- Verify template variable names match step aliases (e.g., `{{player.x}}` requires a read step with `as: "player"`)
- Check for typos in the template path
- Ensure the server is running the latest version

---

## Unrecognized Errors

When an error doesn't match any known pattern, the `diagnose_error` tool returns:

> I couldn't identify this error automatically. Please copy the FULL error output from your s&box console, including any lines starting with `[NetworkStorage]`, the full JSON response if visible, and the HTTP status code if shown. Paste the complete output here so I can help diagnose the issue.
