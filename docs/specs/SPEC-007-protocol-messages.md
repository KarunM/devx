# SPEC-007: Protocol Messages

- Status: Draft
- Version: 1.0

# Purpose

Defines the wire format for ECP protocol requests and responses.

The protocol is transport-agnostic. JSON is the MVP serialization format.

# Request Envelope

Every request SHALL include:

- `version`
- `operation`
- `sessionId`
- `payload`

Example:

```json
{
  "version": "1.0",
  "operation": "SEARCH_SYMBOL",
  "sessionId": "00000000-0000-0000-0000-000000000000",
  "payload": {
    "query": "ExecutionGraph"
  }
}
```

# Response Envelope

Every response SHALL include:

- `success`
- `data`
- `diagnostics`
- `errors`

Example:

```json
{
  "success": true,
  "data": {},
  "diagnostics": [],
  "errors": []
}
```

# Versioning

- `1.0` is the initial supported protocol version.
- Clients and servers SHOULD reject unsupported versions with a structured error.

# Errors

## NotFound

Requested resource could not be found.

## ValidationError

Request payload failed validation.

## InvalidOperation

Operation is unknown or not supported by the engine.

## SessionNotFound

The referenced session does not exist.

## RepositoryNotIndexed

The active repository has not been indexed yet.

## HandlerNotImplemented

The protocol operation exists, but its handler is not implemented.

# Serialization

- JSON for MVP
- No provider-specific behavior
- No transport-specific behavior

