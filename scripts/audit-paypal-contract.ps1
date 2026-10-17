[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\paypal-contract-$([Guid]::NewGuid().ToString('N'))"
$payoutPath = Join-Path $runRoot "payouts-v1-schema.json"
$webhookPath = Join-Path $runRoot "webhooks-v1-schema.json"
$subscriptionPath = Join-Path $runRoot "subscriptions-v1-schema.json"
$subscriptionWebhookPath = Join-Path $runRoot "subscription-webhooks.md"
$salePath = Join-Path $runRoot "payments-v1-schema.json"
$authPath = Join-Path $runRoot "authentication.html"
$payoutUrl = "https://developer.paypal.com/api/payments.payouts-batch/v1/schema.json"
$webhookUrl = "https://developer.paypal.com/api/webhooks/v1/schema.json"
$subscriptionUrl = "https://developer.paypal.com/api/subscriptions/v1/schema.json"
$subscriptionWebhookUrl = "https://developer.paypal.com/subscriptions/webhooks.md"
$saleUrl = "https://developer.paypal.com/api/deprecated/payments/v1/schema.json"
$authUrl = "https://developer.paypal.com/api/rest/authentication/"
$sandboxOrigin = "https://api-m.sandbox.paypal.com"

function Require-ExactSet {
    param(
        [Parameter(Mandatory = $true)] [object[]] $Actual,
        [Parameter(Mandatory = $true)] [string[]] $Expected,
        [Parameter(Mandatory = $true)] [string] $Label
    )

    $actualValues = @($Actual | ForEach-Object { [string]$_ } | Sort-Object)
    $expectedValues = @($Expected | Sort-Object)
    if (($actualValues -join "`n") -cne ($expectedValues -join "`n")) {
        throw "$Label changed in the official PayPal contract."
    }
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$webRequestParameters = @{
    UseBasicParsing = $true
    MaximumRedirection = 5
    TimeoutSec = 30
    Headers = @{ "User-Agent" = "DCx-contract-audit/1.0" }
}
Invoke-WebRequest @webRequestParameters -Uri $payoutUrl -OutFile $payoutPath | Out-Null
Invoke-WebRequest @webRequestParameters -Uri $webhookUrl -OutFile $webhookPath | Out-Null
Invoke-WebRequest @webRequestParameters -Uri $subscriptionUrl -OutFile $subscriptionPath | Out-Null
Invoke-WebRequest @webRequestParameters -Uri $subscriptionWebhookUrl -OutFile $subscriptionWebhookPath | Out-Null
Invoke-WebRequest @webRequestParameters -Uri $saleUrl -OutFile $salePath | Out-Null
Invoke-WebRequest @webRequestParameters -Uri $authUrl -OutFile $authPath | Out-Null

$payout = Get-Content -LiteralPath $payoutPath -Raw | ConvertFrom-Json
$webhook = Get-Content -LiteralPath $webhookPath -Raw | ConvertFrom-Json
$subscription = Get-Content -LiteralPath $subscriptionPath -Raw | ConvertFrom-Json
$subscriptionWebhooks = Get-Content -LiteralPath $subscriptionWebhookPath -Raw
$sale = Get-Content -LiteralPath $salePath -Raw | ConvertFrom-Json
$auth = Get-Content -LiteralPath $authPath -Raw

if (@($payout.servers).Count -ne 1 -or $payout.servers[0].url -cne $sandboxOrigin -or
    @($webhook.servers).Count -ne 1 -or $webhook.servers[0].url -cne $sandboxOrigin -or
    @($subscription.servers).Count -ne 1 -or $subscription.servers[0].url -cne $sandboxOrigin) {
    throw "The official PayPal API schemas no longer expose the one expected sandbox origin."
}

if ($payout.components.securitySchemes.Oauth2.type -cne "oauth2" -or
    $null -eq $payout.components.securitySchemes.Oauth2.flows.clientCredentials -or
    $webhook.components.securitySchemes.Oauth2.type -cne "oauth2" -or
    $null -eq $webhook.components.securitySchemes.Oauth2.flows.clientCredentials -or
    $subscription.components.securitySchemes.Oauth2.type -cne "oauth2" -or
    $null -eq $subscription.components.securitySchemes.Oauth2.flows.clientCredentials) {
    throw "The official PayPal OAuth2 client-credentials contract changed."
}

$payoutPost = $payout.paths.'/v1/payments/payouts'.post
$payoutGet = $payout.paths.'/v1/payments/payouts/{id}'.get
$verifyPost = $webhook.paths.'/v1/notifications/verify-webhook-signature'.post
if ($null -eq $payoutPost -or $null -eq $payoutGet -or $null -eq $verifyPost -or
    $null -eq $payoutPost.responses.'201' -or $null -eq $payoutGet.responses.'200' -or
    $null -eq $verifyPost.responses.'200') {
    throw "A required official PayPal operation or success response changed."
}

$subscriptionCreate = $subscription.paths.'/v1/billing/subscriptions'.post
$subscriptionGet = $subscription.paths.'/v1/billing/subscriptions/{id}'.get
$subscriptionResource = $subscription.components.schemas.subscription.allOf[2].properties
$saleResource = $sale.components.schemas.sale
$refundResource = $sale.components.schemas.refund
if ($null -eq $subscriptionCreate -or $null -eq $subscriptionGet -or
    $subscriptionResource.id.maxLength -ne 50 -or
    $subscriptionResource.plan_id.maxLength -ne 50 -or
    $null -eq $saleResource.properties.billing_agreement_id -or
    $null -eq $saleResource.properties.amount -or
    $null -eq $refundResource.properties.sale_id -or
    $null -eq $refundResource.properties.amount) {
    throw "A required PayPal subscription, sale, or refund resource contract changed."
}

Require-ExactSet -Label "PayPal sale required fields" `
    -Actual $saleResource.required `
    -Expected @("id", "amount", "state", "parent_payment", "create_time")
Require-ExactSet -Label "PayPal sale amount required fields" `
    -Actual $sale.components.schemas.amount.required `
    -Expected @("currency", "total")

$subscriptionEventNames = @(
    "BILLING.SUBSCRIPTION.ACTIVATED",
    "BILLING.SUBSCRIPTION.CANCELLED",
    "BILLING.SUBSCRIPTION.SUSPENDED",
    "BILLING.SUBSCRIPTION.EXPIRED",
    "PAYMENT.SALE.COMPLETED",
    "PAYMENT.SALE.REFUNDED",
    "PAYMENT.SALE.REVERSED")
foreach ($eventName in $subscriptionEventNames) {
    if (-not $subscriptionWebhooks.Contains("``$eventName``")) {
        throw "The official PayPal subscription webhook catalog no longer contains $eventName."
    }
}

Require-ExactSet -Label "PayPal payout batch statuses" `
    -Actual $payout.components.schemas.batch_enum.enum `
    -Expected @("DENIED", "PENDING", "PROCESSING", "SUCCESS", "CANCELED")
Require-ExactSet -Label "PayPal webhook verification statuses" `
    -Actual $webhook.components.schemas.verify_webhook_signature_response.properties.verification_status.enum `
    -Expected @("SUCCESS", "FAILURE")
Require-ExactSet -Label "PayPal webhook verification required fields" `
    -Actual $webhook.components.schemas.verify_webhook_signature.required `
    -Expected @(
        "auth_algo", "cert_url", "transmission_id", "transmission_sig",
        "transmission_time", "webhook_id", "webhook_event")
Require-ExactSet -Label "PayPal payout request required fields" `
    -Actual $payout.components.schemas.create_payout_request.required `
    -Expected @("sender_batch_header", "items")

if (-not $auth.Contains("https://api-m.sandbox.paypal.com/v1/oauth2/token") -or
    -not $auth.Contains("grant_type=client_credentials") -or
    -not $auth.Contains("Basic Auth")) {
    throw "The official PayPal authentication page no longer proves the expected sandbox OAuth request."
}

$evidence = [ordered]@{
    schema = 1
    fetchedUtc = [DateTimeOffset]::UtcNow.ToString("O")
    networkScope = "Read-only GET requests to the six listed official developer.paypal.com URLs; no API credential or PayPal API request."
    credentialsUsed = 0
    paypalApiRequests = 0
    paymentOrPayoutAttempts = 0
    sandboxOrigin = $sandboxOrigin
    oauthTokenPath = "/v1/oauth2/token"
    oauthGrant = "client_credentials"
    operations = @(
        "POST /v1/notifications/verify-webhook-signature",
        "POST /v1/billing/subscriptions",
        "GET /v1/billing/subscriptions/{id}",
        "POST /v1/payments/payouts",
        "GET /v1/payments/payouts/{id}")
    subscriptionWebhookEvents = $subscriptionEventNames
    payoutBatchStatuses = @("DENIED", "PENDING", "PROCESSING", "SUCCESS", "CANCELED")
    webhookVerificationStatuses = @("SUCCESS", "FAILURE")
    officialSources = @(
        [ordered]@{
            url = $payoutUrl
            sha256 = (Get-FileHash -LiteralPath $payoutPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            url = $webhookUrl
            sha256 = (Get-FileHash -LiteralPath $webhookPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            url = $subscriptionUrl
            sha256 = (Get-FileHash -LiteralPath $subscriptionPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            url = $subscriptionWebhookUrl
            sha256 = (Get-FileHash -LiteralPath $subscriptionWebhookPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            url = $saleUrl
            sha256 = (Get-FileHash -LiteralPath $salePath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            url = $authUrl
            sha256 = (Get-FileHash -LiteralPath $authPath -Algorithm SHA256).Hash.ToLowerInvariant()
        })
}
$evidencePath = Join-Path $runRoot "paypal-contract-audit.json"
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $evidencePath -Encoding UTF8

"Official PayPal sandbox origin=PASS"
"OAuth2 client credentials and token request=PASS"
"Webhook verification request/status contract=PASS"
"Subscription plan/resource and event catalog contract=PASS"
"Subscription sale/refund resource relationship contract=PASS"
"Payout create/get and five-state batch contract=PASS"
"Credentials/API/payment/payout attempts=0/0/0/0"
"PayPal contract evidence=$evidencePath"
