# PayPal sandbox contract boundary

Verified from official PayPal developer resources on 2026-10-17. This is a development contract record, not merchant eligibility or production authorization.

## Official sources

| Contract | Official URL | SHA-256 at audit |
|---|---|---|
| REST OAuth authentication | https://developer.paypal.com/api/rest/authentication/ | `b2ab9761c24c931a778bc82c4eccf9338a572a65155c7adb48fa5c919abe5c99` |
| Webhooks v1 schema | https://developer.paypal.com/api/webhooks/v1/schema.json | `a10f9d6d44b4732393bf07001979110e0f6a584efad07e31d0d4ad76d6300ca3` |
| Payouts batch v1 schema | https://developer.paypal.com/api/payments.payouts-batch/v1/schema.json | `d0e01b9cb185a9ed9f1ace47a94796869ffee3dd754bb41cd50baed575b34298` |

The audit performed only three HTTPS GET requests to these documentation resources. It used no credentials and made no request to a PayPal API origin.

## Implemented sandbox operations

- Fixed origin: `https://api-m.sandbox.paypal.com`.
- OAuth: `POST /v1/oauth2/token`, HTTP Basic client credentials and `grant_type=client_credentials`.
- Webhook evidence: `POST /v1/notifications/verify-webhook-signature` with `auth_algo`, `cert_url`, `transmission_id`, `transmission_sig`, `transmission_time`, `webhook_id` and the exact parsed event; admitted results are `SUCCESS` and `FAILURE`.
- Payout submission: `POST /v1/payments/payouts` with stable sender batch/item IDs and exact USD values.
- Reconciliation: `GET /v1/payments/payouts/{provider-batch-id}` with items requested. Batch states are `DENIED`, `PENDING`, `PROCESSING`, `SUCCESS` and `CANCELED`.

The production class exposes sandbox behavior only. Live payout mode has no transport path. An approved item binds an immutable recipient-snapshot ID and destination digest; both the eligible directory record and returned provider receiver must match it. Responses remain untrusted until strict shape, identity, receiver, item and amount checks pass. Completed mixed outcomes post only successful items and hold failed amounts in contributor payable without automatic retry.

## Acceptance boundary

The local handler-backed tests establish serialization, validation, idempotency and accounting behavior without contacting PayPal. They do not establish that an account can use Subscriptions or Payouts, that recipients are eligible, that webhooks are configured correctly, or that regional/legal requirements are met.

Credentialed sandbox acceptance is blocked pending owner authorization and credentials. Before it can run, DCx also needs an accessible owner-approval surface backed by a protected immutable recipient-snapshot directory, subscription mapping, separately approved failed-item retry/remediation and continued reconciliation for later returned/refunded/reversed items. No real payment, payout or subscription should be initiated from this implementation.
