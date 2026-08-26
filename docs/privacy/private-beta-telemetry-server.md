# Private-beta telemetry server approval contract

The client upload control remains unavailable until the founder approves a production endpoint and this
server-side contract has a named operator and concrete retention values. Client allowlisting is necessary
but insufficient because the network service can observe connection metadata outside the JSON body.

The service must accept only the sealed diagnostic schema documented in `docs/privacy/observability.md`,
reject unknown fields, enforce request and rate limits, and avoid recording request bodies in proxy,
application, error, analytics, or support logs. It must never join events to installation, advertising,
account, fingerprint, or stable device identifiers. Source IP and TLS metadata visible to network
infrastructure must use the shortest supported retention, with access restricted to operational security
needs.

Approval for one endpoint records the legal entity and operator, hosting region, subprocessors, exact
event retention, connection-log retention, backup retention, access roles, MFA and audit controls, deletion
procedure, incident-response owner and notification path, sampling policy, data-processing terms, and the
public user disclosure. None of those values may remain unknown at approval time. A material schema,
provider, region, retention, correlation, sampling, or access change requires a new privacy review before
deployment.

The client defaults sharing off and sends nothing until saved consent is true. Revoking consent stops new
queue admission; the server approval must also define how a user requests deletion of already accepted
events when applicable. Transport failure cannot block dictation or shutdown. Private-beta operators use
the bounded GitHub issue forms for feedback and attach only the product-generated privacy-safe diagnostic
export—never raw logs or crash dumps.

Endpoint credentials and infrastructure configuration stay outside the repository. The release evidence
boolean `telemetryServerPolicy` can become true only after the founder reviews the concrete operating
record against this contract. Until then, no production telemetry endpoint is embedded or enabled.
