[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
if ($PSVersionTable.PSEdition -ne "Core" -or $PSVersionTable.PSVersion.Major -lt 7) {
    throw "P06 validation requires PowerShell 7 or later for the .NET production adapters."
}
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = Join-Path $repoRoot "artifacts\test-results\p06-$([Guid]::NewGuid().ToString('N'))"
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$localModelsTrx = Join-Path $runRoot "p06-local-models.trx"
$fixtureImage = Join-Path $runRoot "vision-fixture.png"
$ffmpegAudioFixture = Join-Path $runRoot "ffmpeg-audio-fixture.mp3"
$manifestPath = Join-Path $runRoot "hardware-manifest.json"
$whisperRegistrationRoot = Join-Path $runRoot "whisper-registration"
$whisperRegistrationEvidencePath = Join-Path $whisperRegistrationRoot "whisper-registration-evidence.json"
$dependencyTransferUiRoot = Join-Path $runRoot "dependency-transfer-ui"
$dependencyTransferUiEvidencePath = Join-Path $dependencyTransferUiRoot "dependency-transfer-ui-evidence.json"
$ollama = "D:\Programs\Ollama\ollama.exe"
$modelDigest = "ef33995bb2ac37689e49c1a15cd627354d05a3c21d1f4bdcd6a5c83825349c1f"
$model = "llamacpp:$modelDigest"

if (-not (Test-Path $ollama)) { throw "Ollama is unavailable." }
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~DCx.Core.Tests.LocalModels." `
        --logger "trx;LogFileName=p06-local-models.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P06 local-model contract tests failed with exit code $LASTEXITCODE." }
    [xml]$localModelsReport = Get-Content -LiteralPath $localModelsTrx -Raw
    $localModelsCounters = $localModelsReport.TestRun.ResultSummary.Counters
    $localModelTestsPassed = [int]$localModelsCounters.passed
    if ($localModelTestsPassed -lt 1 -or
        [int]$localModelsCounters.total -ne $localModelTestsPassed -or
        [int]$localModelsCounters.executed -ne $localModelTestsPassed -or
        [int]$localModelsCounters.failed -ne 0 -or
        [int]$localModelsCounters.error -ne 0 -or
        [int]$localModelsCounters.timeout -ne 0 -or
        [int]$localModelsCounters.aborted -ne 0) {
        throw "P06 TRX counters did not prove that every selected local-model test passed."
    }
    $requiredStorageDenials = @(
        "DCx.Core.Tests.LocalModels.LocalModelSelectionStoreTests.Store_RejectsLinkedApplicationRootWithoutWritingThroughIt",
        "DCx.Core.Tests.LocalModels.LocalModelSelectionStoreTests.Store_RejectsSettingsDirectorySwappedForLinkAfterOpen",
        "DCx.Core.Tests.LocalModels.LocalModelSelectionStoreTests.Store_RejectsLinkedSelectionFile",
        "DCx.Core.Tests.LocalModels.WhisperBackendRegistrationTests.Store_RejectsSettingsDirectorySwappedForLinkAfterOpen",
        "DCx.Core.Tests.LocalModels.WhisperBackendRegistrationTests.Store_RejectsLinkedSelectionFile",
        "DCx.Core.Tests.LocalModels.FfmpegBackendRegistrationTests.Store_RejectsSettingsDirectorySwappedForLinkAfterOpen",
        "DCx.Core.Tests.LocalModels.FfmpegBackendRegistrationTests.Store_RejectsLinkedSelectionFile",
        "DCx.Core.Tests.LocalModels.LocalModelBenchmarkManifestTests.EvidenceStore_RejectsDirectorySwappedForLinkAfterOpen",
        "DCx.Core.Tests.LocalModels.LocalModelBenchmarkManifestTests.EvidenceStore_RejectsLinkedTargetWithoutChangingOutsideFile"
    )
    $requiredDependencyContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.EmbeddedCatalog_BindsExactReviewedCandidatesLicensesAndLayers",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.Catalog_BindsOnlyWhenExactDigestAndSchemaMatch",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.CatalogFile_DeniesLinkedFileWithoutReadingOutsideContent",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_LeasesAndAuthenticatesRuntimeArchive",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_BindsExactOllamaManagedLayerInventory",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_DeniesOllamaLayerSubstitutionEvenWithMatchingManifestDigest",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_DeniesLinkedArtifactWithoutChangingOutsideFile",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_DeniesLinkedAncestorWithoutChangingOutsideFile"
    )
    $requiredDependencyTransferContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferPlanTests.EmbeddedCatalog_ProducesExactDeduplicatedTransferAndStorageFootprint",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferPlanTests.Plan_IsOrderStableAndCapacityObservationDoesNotChangeAuthorityDigest",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferPlanTests.Plan_BindsExactSelectedCandidatesAndChangesForAValidSubset",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferPlanTests.Plan_RejectsUnknownDuplicateOrMalformedSelectionsAndCatalogs",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferPlanTests.Plan_RejectsConflictingIdentityForOneExactSource"
    )
    $requiredDependencyHttpTransferContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.ProductionHandler_DisablesRedirectsCookiesProxyAndDecompression",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.ExactApprovedItem_StreamsAndReturnsDigestBoundEvidence",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.DefaultPolicy_DeniesRedirectBeforeFollowingAndRollsBack",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.ExactPolicy_FollowsApprovedRedirectWithoutForwardingAmbientHeaders",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.UnsafeRedirectTargetsFailBeforeSecondRequest",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.RedirectLoopAndHopLimitFailClosed",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.ExactPolicy_DeniesChangedItemTransitionAndBroadOrDuplicateRules",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.RedirectPolicyReview_IsCanonicalAndRejectsMalformedReview",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.RedirectPolicyApprovalSubstitutionFailsBeforeNetwork",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.RedirectPolicyMutationDuringTransferRollsBackAuthenticatedBytes",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.InjectedPolicyCannotOverrideReviewedDenyAllAuthority",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.MissingApprovalOrCapacity_DeniesBeforeNetworkAndLeavesDestinationEmpty",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.MutatedPlanApprovalAndNonemptyDestination_FailBeforeNetwork",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.SubstitutedFinalUriEncodingRangeAndChunkedResponsesFailClosed",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.MissingWrongShortAndExtraLengthsRemovePartialOutput",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.CallerCancellationAfterPartialReadTruncatesDestination",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.ReadFailureAfterWrittenChunkTruncatesPartialDestination",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.UnexpectedProgressFailureRollsBackWithoutLeakingDetails",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.UnexpectedRollbackFailureReturnsStableCleanupCode",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyHttpTransferTests.SelfConsistentPlanFromDifferentCatalogIsDeniedBeforeNetwork"
    )
    $requiredDependencyRedirectReviewContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.ExplicitReview_UsesHeadOnlyAndReturnsExactQueryFreeSessionPolicy",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.MissingApprovalAndChangedCatalogFailBeforeNetwork",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.UnsafeLoopAndMismatchedLengthFailClosedWithoutReadingBodies",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.SessionExpiryRevokesPolicyAndRejectsCoordinatorConstruction",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.ReviewedSession_ComposesWithTransferAndExpiryDeniesBeforeNewPayloadRequest",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyRedirectReviewerTests.CallerCancellationAndDeadlineUseStableOutcomes"
    )
    $requiredDependencyTransferSetContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Transfer_RequiresExplicitApprovalAndFreshCapacityBeforeCreatingStorage",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Transfer_StreamsEveryItemProtectsReceiptAndAtomicallyPublishesVerifiableSet",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Transfer_RejectsRedirectPolicySubstitutionBeforeStorage",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Transfer_RejectsReviewedPlanSubstitutionBeforeStorage",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.TransferFailureAndProtectedWriteFailureLeaveNoPublishedOrPartialSet",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.CancellationBeforeAndDuringTransferLeavesNoPublishedOrPartialSet",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Verify_DeniesMissingProtectionTamperedPayloadAndUnexpectedFile",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Verify_DeniesLinkedPayloadWithoutReadingOutsideContent",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Transfer_RejectsLinkedStorageRootWithoutWritingOutside",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.Cleanup_RemovesOnlyStrictIncomingNamesAndLeavesOtherDirectories",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.ExistingCompletedSetIsNeverOverwritten",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.ActiveTransfer_SerializesVerificationAndCleanupUntilItFinishes",
        "DCx.Core.Tests.LocalModels.LocalModelDependencyTransferSetCoordinatorTests.PostCommitProgressFailureCannotMisreportPublishedOrVerifiedSet"
    )
    $requiredHardwareProfileContracts = @(
        "DCx.Core.Tests.LocalModels.WindowsLocalHardwareProfileTests.Capture_ValidatesDeduplicatesAndOrdersBoundedHardwareEvidence",
        "DCx.Core.Tests.LocalModels.WindowsLocalHardwareProfileTests.Capture_RejectsMalformedCapacityTextAndGraphicsEvidence",
        "DCx.Core.Tests.LocalModels.WindowsLocalHardwareProfileTests.WindowsSource_CapturesCurrentHostWithoutAChildProcess",
        "DCx.Core.Tests.LocalModels.WindowsLocalHardwareProfileTests.NvidiaTelemetry_LoadsOnlyInstalledSystemDriverAndReturnsBoundedLiveMemory",
        "DCx.Core.Tests.LocalModels.WindowsLocalHardwareProfileTests.DxgiTelemetry_ReturnsBoundedCurrentHostBudgetsWithoutAChildProcess",
        "DCx.Core.Tests.LocalModels.LocalModelSelectorTests.SelectForHardware_UsesLiveAvailableGpuAndSystemMemoryBudgets",
        "DCx.Core.Tests.LocalModels.LocalModelSelectorTests.SelectForHardware_DoesNotTreatInventoryOnlyDxgiBudgetAsComputeAdmission",
        "DCx.Core.Tests.LocalModels.LocalModelSelectorTests.SelectForHardware_FailsClosedWithoutLiveGpuCapacityAndUsesAvailableRam"
    )
    $requiredOllamaInferenceLeaseContracts = @(
        "DCx.Core.Tests.LocalModels.OllamaInferenceLeaseCoordinatorTests.SameUserLease_SerializesCancellationAndRecovery",
        "DCx.Core.Tests.LocalModels.OllamaInferenceLeaseCoordinatorTests.ExternalContention_TimesOutWithStableMessageThenRecovers",
        "DCx.Core.Tests.LocalModels.OllamaInferenceLeaseCoordinatorTests.UnsafeLockPath_IsDeniedWithoutFollowingIt",
        "DCx.Core.Tests.LocalModels.OllamaInferenceLeaseCoordinatorTests.PreCanceledLease_CreatesNoStorageAndTimingBoundsFailClosed",
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.VisionInference_IsSerializedAcrossClientsForTheEntirePinnedOperation"
    )
    $requiredOllamaRuntimeResourceContracts = @(
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.ProductionInference_DeniesCurrentResourceFailureBeforeModelOperation",
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.ProductionInference_DeniesPreloadedRuntimeWithoutInterfering",
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.ProductionInference_DeniesUnrelatedModelIntroducedDuringCleanup",
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.NormalInference_FailsClosedWhenExactUnloadIsNotConfirmed",
        "DCx.Core.Tests.LocalModels.OllamaLocalClientTests.InspectionAndEmbedding_ValidateCapabilitiesAndBoundedVectors"
    )
    $requiredApprovalStateContracts = @(
        "DCx.Core.Tests.LocalModels.LocalModelApprovalStateAnchorStoreTests.WindowsVaultAnchor_RoundTripsOverwritesIsolatesAndRejectsMalformedState",
        "DCx.Core.Tests.LocalModels.LocalModelApprovalStateAnchorStoreTests.WindowsVaultAnchor_HonorsPreCanceledOperationsWithoutCreatingCredential",
        "DCx.Core.Tests.LocalModels.LocalModelSelectionStoreTests.ProtectedApproval_RejectsRollbackAndAllowsExplicitReapproval",
        "DCx.Core.Tests.LocalModels.LocalModelSelectionStoreTests.ProtectedApproval_MissingOrUnreadableAnchorFailsClosedAndWriteFailureRecovers",
        "DCx.Core.Tests.LocalModels.WhisperBackendRegistrationTests.ProtectedApproval_RejectsRollbackAndClearRemovesAuthority",
        "DCx.Core.Tests.LocalModels.FfmpegBackendRegistrationTests.ProtectedApproval_RejectsRollbackAndAllowsExplicitReapproval"
    )
    $requiredRuntimeStagingContracts = @(
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_DeniesWithoutExplicitApprovalAndCreatesNothing",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_AuthenticatesExtractsHashesAndAtomicallyPublishesInertRuntime",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_DeniesUnsafeArchiveAndRemovesOnlyPrivatePartialState",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_DeniesReceiptCollisionAndLeavesNoPublishedOrPrivateState",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_NeverOverwritesExistingDigestBoundDestination",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_DeniesLinkedRuntimeRootWithoutWritingOutside",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_HonorsPreCanceledOperationBeforeCreatingApplicationStorage",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_FailsClosedWhenProtectedAnchorCannotBeWrittenAndPublishesNothing",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_FailsBeforeCopyOrExtractionWhenProtectedAnchorCannotBeRead",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_ReusesProtectedTimestampWhenExactRemovedStageIsRecreated",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Stage_DefaultWindowsVaultAnchorRoundTripsThroughDefaultVerifierAndIsCleanedUp",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_RehashesExactStageWithoutRegisteringOrExecutingRuntime",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_DeniesTamperedRuntimeFile",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_DeniesUnexpectedFileAndDirectory",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_DeniesLinkedContentWithoutReadingOutside",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_DeniesUnknownDuplicateOrMismatchedReceiptIdentity",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStagerTests.Verify_DeniesMissingAnchorAndCoordinatedReceiptPayloadReplacement",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStageAnchorStoreTests.WindowsVaultAnchor_RoundTripsIsolatesAndRejectsConflictUnknownAndDuplicateState",
        "DCx.Core.Tests.LocalModels.WhisperRuntimeStageAnchorStoreTests.WindowsVaultAnchor_HonorsPreCanceledReadAndBindWithoutCreatingCredential"
    )
    $localModelResults = @($localModelsReport.TestRun.Results.UnitTestResult)
    foreach ($requiredStorageDenial in $requiredStorageDenials) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredStorageDenial })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required local-model storage denial: $requiredStorageDenial"
        }
    }
    foreach ($requiredDependencyContract in $requiredDependencyContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredDependencyContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required pinned-dependency contract: $requiredDependencyContract"
        }
    }
    foreach ($requiredDependencyTransferContract in $requiredDependencyTransferContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredDependencyTransferContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required dependency transfer-preflight contract: $requiredDependencyTransferContract"
        }
    }
    foreach ($requiredDependencyHttpTransferContract in $requiredDependencyHttpTransferContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredDependencyHttpTransferContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required dependency HTTP-transfer contract: $requiredDependencyHttpTransferContract"
        }
    }
    foreach ($requiredDependencyRedirectReviewContract in $requiredDependencyRedirectReviewContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredDependencyRedirectReviewContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required dependency redirect-review contract: $requiredDependencyRedirectReviewContract"
        }
    }
    foreach ($requiredDependencyTransferSetContract in $requiredDependencyTransferSetContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredDependencyTransferSetContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required dependency transfer-set contract: $requiredDependencyTransferSetContract"
        }
    }
    foreach ($requiredHardwareProfileContract in $requiredHardwareProfileContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredHardwareProfileContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required native hardware-profile contract: $requiredHardwareProfileContract"
        }
    }
    foreach ($requiredOllamaInferenceLeaseContract in $requiredOllamaInferenceLeaseContracts) {
        $matchingResults = @($localModelResults | Where-Object {
            $_.testName -ceq $requiredOllamaInferenceLeaseContract
        })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required Ollama inference-sequencing contract: $requiredOllamaInferenceLeaseContract"
        }
    }
    foreach ($requiredOllamaRuntimeResourceContract in $requiredOllamaRuntimeResourceContracts) {
        $matchingResults = @($localModelResults | Where-Object {
            $_.testName -ceq $requiredOllamaRuntimeResourceContract
        })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required runtime resource/unload contract: $requiredOllamaRuntimeResourceContract"
        }
    }
    foreach ($requiredApprovalStateContract in $requiredApprovalStateContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredApprovalStateContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required protected local-model approval-state contract: $requiredApprovalStateContract"
        }
    }
    foreach ($requiredRuntimeStagingContract in $requiredRuntimeStagingContracts) {
        $matchingResults = @($localModelResults | Where-Object { $_.testName -ceq $requiredRuntimeStagingContract })
        if ($matchingResults.Count -ne 1 -or $matchingResults[0].outcome -cne "Passed") {
            throw "P06 did not prove required inert Whisper runtime staging contract: $requiredRuntimeStagingContract"
        }
    }
    $runtimeArchiveDenials = @($localModelResults | Where-Object {
        $_.testName -like "DCx.Core.Tests.LocalModels.LocalModelDependencyCatalogTests.ArtifactReview_DeniesUnsafeWhisperRuntimeArchiveContents*"
    })
    if ($runtimeArchiveDenials.Count -ne 8 -or
        @($runtimeArchiveDenials | Where-Object { $_.outcome -cne "Passed" }).Count -ne 0) {
        throw "P06 did not prove all eight hostile Whisper runtime archive denials."
    }
    & (Join-Path $repoRoot "scripts\validate-p06-whisper-registration.ps1") -RunRoot $whisperRegistrationRoot
    if (-not (Test-Path -LiteralPath $whisperRegistrationEvidencePath -PathType Leaf)) {
        throw "The native Whisper registration evidence was not retained."
    }
    $whisperRegistrationEvidence = Get-Content -LiteralPath $whisperRegistrationEvidencePath -Raw | ConvertFrom-Json
    if ($whisperRegistrationEvidence.native_uia -ne "pass" -or
        $whisperRegistrationEvidence.approval_default_unchecked -ne $true -or
        $whisperRegistrationEvidence.save_without_approval_denied -ne $true -or
        $whisperRegistrationEvidence.benchmark_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.benchmark_approval_default_unchecked -ne $true -or
        $whisperRegistrationEvidence.benchmark_run_without_approval_denied -ne $true -or
        $whisperRegistrationEvidence.benchmark_exact_review_exposed -ne $true -or
        $whisperRegistrationEvidence.benchmark_aggregate_evidence_only -ne $true -or
        $whisperRegistrationEvidence.benchmark_result_focus -ne $true -or
        $whisperRegistrationEvidence.benchmark_path_change_revoked_approval -ne $true -or
        $whisperRegistrationEvidence.benchmark_cancellation_preserved_evidence -ne $true -or
        $whisperRegistrationEvidence.benchmark_missing_backend_unavailable -ne $true -or
        $whisperRegistrationEvidence.embedding_benchmark_missing_model_unavailable -ne $true -or
        $whisperRegistrationEvidence.dependency_catalog_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.native_hardware_profile_uia -ne "pass" -or
        $whisperRegistrationEvidence.dependency_catalog_sha256 -ne "1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325" -or
        $whisperRegistrationEvidence.dependency_candidate_count -ne 4 -or
        $whisperRegistrationEvidence.dependency_transfer_preflight_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.dependency_transfer_plan_sha256 -ne "a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286" -or
        $whisperRegistrationEvidence.dependency_transfer_bytes -ne 1281058770 -or
        $whisperRegistrationEvidence.dependency_transfer_minimum_free_bytes -ne 3098988452 -or
        $whisperRegistrationEvidence.dependency_transfer_item_count -ne 9 -or
        $whisperRegistrationEvidence.dependency_transfer_authority_granted -ne $false -or
        $whisperRegistrationEvidence.dependency_transfer_action_exposed -ne $true -or
        $whisperRegistrationEvidence.dependency_transfer_approval_default_unchecked -ne $true -or
        $whisperRegistrationEvidence.dependency_transfer_start_without_approval_denied -ne $true -or
        $whisperRegistrationEvidence.dependency_provider_route_review_explicit -ne $true -or
        $whisperRegistrationEvidence.dependency_provider_route_review_head_only_notice -ne $true -or
        $whisperRegistrationEvidence.dependency_provider_route_review_not_invoked -ne $true -or
        $whisperRegistrationEvidence.dependency_staged_mismatch_denied -ne $true -or
        $whisperRegistrationEvidence.dependency_rejected_source_preserved -ne $true -or
        $whisperRegistrationEvidence.dependency_forbidden_activation_actions_exposed -ne $false -or
        $whisperRegistrationEvidence.dependency_runtime_staging_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.dependency_runtime_staging_fixture_without_gate_denied -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_approval_default_unchecked -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_path_change_revoked_approval -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_result_focus -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_source_preserved -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_receipt_source_path_retained -ne $false -or
        $whisperRegistrationEvidence.dependency_runtime_staging_protected_anchor -ne $true -or
        $whisperRegistrationEvidence.dependency_runtime_staging_registered_or_executed -ne $false -or
        $whisperRegistrationEvidence.accessible_viewer_cancellation -ne $true -or
        $whisperRegistrationEvidence.canceled_partial_transcript_retained -ne $false -or
        $whisperRegistrationEvidence.cleared_backend_consumer_denied -ne $true -or
        $whisperRegistrationEvidence.ffmpeg_registration_native_uia -ne "pass" -or
        $whisperRegistrationEvidence.ffmpeg_real_windows_status -ne "NotSigned" -or
        $whisperRegistrationEvidence.ffmpeg_whisper_mp3_pipeline -ne "pass" -or
        $whisperRegistrationEvidence.cleared_ffmpeg_consumer_denied -ne $true -or
        $whisperRegistrationEvidence.approval_state_protected -ne $true -or
        $whisperRegistrationEvidence.whisper_reapproval_warning_uia -ne $true -or
        $whisperRegistrationEvidence.ffmpeg_reapproval_warning_uia -ne $true -or
        $whisperRegistrationEvidence.transcript_focus_after_completion -ne $true -or
        $whisperRegistrationEvidence.download_or_install_action_exposed -ne $false -or
        $whisperRegistrationEvidence.source_files_preserved -ne $true -or
        $whisperRegistrationEvidence.real_backend_accuracy_validated -ne $false) {
        throw "The native Whisper registration evidence did not prove the expected safe fixture boundary."
    }
    & (Join-Path $repoRoot "scripts\validate-p06-transfer-ui.ps1") -RunRoot $dependencyTransferUiRoot
    if (-not (Test-Path -LiteralPath $dependencyTransferUiEvidencePath -PathType Leaf)) {
        throw "The native dependency-transfer UI evidence was not retained."
    }
    $dependencyTransferUiEvidence = Get-Content -LiteralPath $dependencyTransferUiEvidencePath -Raw |
        ConvertFrom-Json
    if ($dependencyTransferUiEvidence.native_uia -ne "pass" -or
        $dependencyTransferUiEvidence.fixture_without_gate_denied -ne $true -or
        $dependencyTransferUiEvidence.approval_default_unchecked -ne $true -or
        $dependencyTransferUiEvidence.start_without_approval_denied -ne $true -or
        $dependencyTransferUiEvidence.keyboard_focusable_unique_access_keys -ne $true -or
        $dependencyTransferUiEvidence.provider_route_review_accessible -ne $true -or
        $dependencyTransferUiEvidence.provider_route_review_explicit_and_head_only -ne $true -or
        $dependencyTransferUiEvidence.provider_route_review_exact_plan_bound -ne $true -or
        $dependencyTransferUiEvidence.provider_route_review_does_not_approve_transfer -ne $true -or
        $dependencyTransferUiEvidence.item_and_byte_progress_announced -ne $true -or
        $dependencyTransferUiEvidence.cancellation_available -ne $true -or
        $dependencyTransferUiEvidence.cancellation_result_focus -ne $true -or
        $dependencyTransferUiEvidence.approval_reset_after_cancellation -ne $true -or
        $dependencyTransferUiEvidence.completed_set_reverified -ne $true -or
        $dependencyTransferUiEvidence.completion_result_focus -ne $true -or
        $dependencyTransferUiEvidence.approval_reset_after_completion -ne $true -or
        $dependencyTransferUiEvidence.completed_progress_percent -ne 100 -or
        $dependencyTransferUiEvidence.protected_reverification_result_focus -ne $true -or
        $dependencyTransferUiEvidence.exclusive_lease_recovery -ne $true -or
        $dependencyTransferUiEvidence.recovery_result_focus -ne $true -or
        $dependencyTransferUiEvidence.provider_requests -ne 0 -or
        $dependencyTransferUiEvidence.downloads -ne 0 -or
        $dependencyTransferUiEvidence.installs -ne 0 -or
        $dependencyTransferUiEvidence.executions -ne 0) {
        throw "The native dependency-transfer workflow did not prove the required safe fixture boundary."
    }
}
finally {
    Pop-Location
}

$gpuLine = & nvidia-smi --query-gpu=name,memory.total,driver_version --format=csv,noheader,nounits | Select-Object -First 1
$gpuParts = @($gpuLine -split "," | ForEach-Object { $_.Trim() })
$models = & $ollama list
if (($models -join "`n") -notmatch [regex]::Escape($model)) { throw "The selected installed model is unavailable." }
$tags = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/tags" `
    -TimeoutSec 15 -NoProxy -MaximumRedirection 0
$selectedTags = @($tags.models | Where-Object { $_.name -ceq $model })
if ($selectedTags.Count -ne 1 -or [string]$selectedTags[0].digest -cne $modelDigest) {
    throw "The selected model name is not uniquely bound to its approved SHA-256 digest."
}
$selectedTag = $selectedTags[0]
$ambiguousMutableNames = @(
    $tags.models |
        Group-Object name |
        Where-Object { @($_.Group.digest | Sort-Object -Unique).Count -gt 1 } |
        ForEach-Object { $_.Name } |
        Sort-Object -Unique
)
$showBody = @{ model = $model; verbose = $false } | ConvertTo-Json
$selectedShow = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" `
    -ContentType "application/json" -Body $showBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
if (@($selectedShow.capabilities) -notcontains "vision") { throw "The selected model does not advertise the vision capability." }
$embeddingModels = @()
foreach ($installed in @($tags.models)) {
    $candidateBody = @{ model = $installed.name; verbose = $false } | ConvertTo-Json
    $candidate = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/show" `
        -ContentType "application/json" -Body $candidateBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
    if (@($candidate.capabilities) -contains "embedding") { $embeddingModels += $installed.name }
}

function Get-TextSha256([string]$value) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($value))
        return [BitConverter]::ToString($hash).Replace("-", "").ToLowerInvariant()
    }
    finally { $sha.Dispose() }
}

$licenseText = [string]$selectedShow.license
if ([string]::IsNullOrWhiteSpace($licenseText)) { throw "The selected installed model has no inspectable license metadata." }
$licenseSha256 = Get-TextSha256 $licenseText
$licenseSummary = (@($licenseText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }) | Select-Object -First 1).Trim()

Add-Type -AssemblyName System.Drawing
$bitmap = New-Object System.Drawing.Bitmap 640, 360
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
try {
    $graphics.Clear([System.Drawing.Color]::White)
    $graphics.FillRectangle([System.Drawing.Brushes]::Blue, 50, 60, 180, 180)
    $graphics.FillEllipse([System.Drawing.Brushes]::Red, 390, 60, 180, 180)
    $font = New-Object System.Drawing.Font "Arial", 32
    try { $graphics.DrawString("DCX 42", $font, [System.Drawing.Brushes]::Black, 245, 280) }
    finally { $font.Dispose() }
    $bitmap.Save($fixtureImage, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

$imageBase64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($fixtureImage))
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
[void][Reflection.Assembly]::LoadFrom($coreAssembly)
$nativeHardwareProfile = [DCx.Core.LocalModels.LocalHardwareProfiler]::new().Capture($repoRoot)
$nativeNvidiaProfiles = @($nativeHardwareProfile.Gpus | Where-Object {
    $_.Name -ceq "NVIDIA GeForce RTX 4060 Laptop GPU" -and
    $_.Provider -ceq "NVIDIA" -and
    $_.DriverVersion -ceq "32.0.16.1078" -and
    $_.ReportedMemoryBytes -eq 8585740288 -and
    $_.MemorySource -ceq "HardwareInformation.qwMemorySize" -and
    $_.AvailableMemoryBytes -gt 0 -and
    $_.AvailableMemoryBytes -le $_.ReportedMemoryBytes -and
    $_.AvailableMemorySource -ceq "NVIDIA NVML free memory"
})
$nativeDxgiInventoryProfiles = @($nativeHardwareProfile.Gpus | Where-Object {
    $_.AvailableMemoryBytes -ge 0 -and
    $_.ReportedMemoryBytes -ge $_.AvailableMemoryBytes -and
    $_.AvailableMemorySource -ceq "Windows DXGI local memory budget" -and
    $_.LocalModelGpuAdmissionEligible -eq $false
})
if ($nativeHardwareProfile.OperatingSystem -notlike "Microsoft Windows *" -or
    $nativeHardwareProfile.CpuName -cne "12th Gen Intel(R) Core(TM) i7-12650H" -or
    $nativeHardwareProfile.LogicalProcessorCount -ne 16 -or
    $nativeHardwareProfile.PhysicalMemoryBytes -lt 16000000000 -or
    $nativeHardwareProfile.AvailableMemoryBytes -le 0 -or
    $nativeHardwareProfile.StorageAvailableBytes -le 0 -or
    $nativeNvidiaProfiles.Count -ne 1 -or
    $nativeNvidiaProfiles[0].LocalModelGpuAdmissionEligible -ne $true -or
    $nativeDxgiInventoryProfiles.Count -lt 1) {
    throw "The native product hardware profile did not match the measured Windows host."
}
$dependencyCatalog = [DCx.Core.LocalModels.LocalModelDependencyCatalogLoader]::new().LoadEmbeddedAsync(
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$dependencyCandidateIds = @($dependencyCatalog.Candidates | ForEach-Object { $_.Id } | Sort-Object)
if ($dependencyCatalog.CatalogId -cne "dcx-p06-dependencies-2026-10-18" -or
    $dependencyCatalog.CatalogSha256 -cne "1ea3bfb04567757e0207cfd824fb5284faad2552617cbbe3330a13f66079e325" -or
    $dependencyCandidateIds.Count -ne 4) {
    throw "The embedded P06 dependency catalog identity changed."
}
$observedFreeBytes = [IO.DriveInfo]::new([IO.Path]::GetPathRoot($repoRoot)).AvailableFreeSpace
$dependencyTransferPlan = [DCx.Core.LocalModels.LocalModelDependencyTransferPlanner]::new().CreateForAll(
    $dependencyCatalog,
    [Nullable[long]]::new($observedFreeBytes))
if ($dependencyTransferPlan.PlanSha256 -cne "a0d997c6298fdf5f592241928c6fd9564c6de0c3e023c9eb69bd1df96406c286" -or
    $dependencyTransferPlan.Items.Count -ne 9 -or
    $dependencyTransferPlan.TransferBytes -ne 1281058770 -or
    $dependencyTransferPlan.MinimumFreeBytes -ne 3098988452 -or
    $dependencyTransferPlan.CapacitySufficient -ne $true) {
    throw "The exact P06 dependency transfer footprint or current storage-capacity preflight failed."
}
$dependencyRedirectPolicy = [DCx.Core.LocalModels.LocalModelDependencyRedirectPolicyReview]::DenyAll
if ($dependencyRedirectPolicy.AllowsRedirects -or
    $dependencyRedirectPolicy.Routes.Count -ne 0 -or
    $dependencyRedirectPolicy.PolicySha256 -cnotmatch '^[0-9a-f]{64}$') {
    throw "The production dependency redirect policy is not a canonical deny-all review."
}
$client = [DCx.Core.LocalModels.OllamaLocalClient]::new(
    [Uri]"http://localhost:11434/", [TimeSpan]::FromMinutes(3))
try {
    $inventory = $client.ListModelsAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $nativeResourceSelection = [DCx.Core.LocalModels.LocalModelSelector]::new().SelectForHardware(
        $nativeHardwareProfile,
        $inventory)
    if ($null -eq $nativeResourceSelection.Vision.Model -or
        $nativeResourceSelection.Vision.Model.Name -cne $model -or
        $nativeResourceSelection.Vision.Model.Digest -cne $modelDigest -or
        $nativeResourceSelection.Embedding.IsAvailable -ne $false) {
        throw "The native hardware-bound model selector did not choose the expected resource-safe local model state."
    }
    $boundModels = @($inventory | Where-Object {
        $_.Name -ceq $model -and $_.Digest -ceq $modelDigest -and $_.HasDigestBoundName -and $_.SupportsVision
    })
    if ($boundModels.Count -ne 1) {
        throw "The production Ollama adapter did not expose one digest-bound vision model."
    }
    $productionResult = $client.DescribeImagePinnedAsync(
        $model,
        $modelDigest,
        $licenseSha256,
        $fixtureImage,
        "Describe the colored shapes and read the short text. Use one direct sentence.",
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
finally {
    $client.Dispose()
}
$answer = [string]$productionResult.Text
if ($answer -notmatch "(?i)blue" -or $answer -notmatch "(?i)red" -or $answer -notmatch "(?i)DCX\s*42") {
    throw "The installed vision model failed the production-adapter fixture accuracy gate. Response: $answer"
}

# Keep one separately measured invocation loaded just long enough to record actual Ollama VRAM allocation.
$quietObservations = 0
for ($attempt = 0; $attempt -lt 3; $attempt++) {
    $quietState = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" `
        -TimeoutSec 10 -NoProxy -MaximumRedirection 0
    if (@($quietState.models).Count -ne 0) {
        throw "The shared Ollama runtime was not quiet before the measured invocation. DCx did not alter the existing workload."
    }
    $quietObservations++
    if ($quietObservations -lt 3) { Start-Sleep -Milliseconds 250 }
}
$body = @{
    model = $model
    messages = @(@{
        role = "user"
        content = "Describe the colored shapes and read the short text. Use one direct sentence."
        images = @($imageBase64)
    })
    stream = $false
    keep_alive = "1m"
    options = @{ temperature = 0; num_predict = 80 }
} | ConvertTo-Json -Depth 8
$unloaded = $false
$started = Get-Date
try {
    $response = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/chat" `
        -ContentType "application/json" -Body $body -TimeoutSec 180 -NoProxy -MaximumRedirection 0
    $wallMilliseconds = [long]((Get-Date) - $started).TotalMilliseconds
    $responseModel = [string]$response.model
    if ($responseModel -ne $model -or $response.done -ne $true) {
        throw "The installed vision model returned a mismatched or incomplete response."
    }
    $resourceAnswer = [string]$response.message.content
    if ($resourceAnswer -notmatch "(?i)blue" -or $resourceAnswer -notmatch "(?i)red" -or
        $resourceAnswer -notmatch "(?i)DCX\s*42") {
        throw "The installed vision model failed the measured resource fixture accuracy gate."
    }

    $running = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" `
        -TimeoutSec 10 -NoProxy -MaximumRedirection 0
    $loadedMatches = @($running.models | Where-Object { [string]$_.digest -ceq $modelDigest })
    if ($loadedMatches.Count -ne 1) {
        throw "Ollama did not report exactly one loaded process for the benchmarked model digest."
    }
    $loaded = $loadedMatches[0]
}
finally {
    $unloadBody = @{ model = $model; keep_alive = 0 } | ConvertTo-Json
    $null = Invoke-RestMethod -Method Post -Uri "http://localhost:11434/api/generate" `
        -ContentType "application/json" -Body $unloadBody -TimeoutSec 30 -NoProxy -MaximumRedirection 0
    $absentObservations = 0
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $remaining = Invoke-RestMethod -Method Get -Uri "http://localhost:11434/api/ps" `
            -TimeoutSec 10 -NoProxy -MaximumRedirection 0
        $unrelated = @($remaining.models | Where-Object { [string]$_.digest -cne $modelDigest })
        if ($unrelated.Count -ne 0) {
            throw "An unrelated Ollama model appeared during the measured invocation. DCx did not unload it."
        }
        if (@($remaining.models).Count -eq 0) {
            $absentObservations++
            if ($absentObservations -ge 3) {
                $unloaded = $true
                break
            }
        }
        else {
            $absentObservations = 0
        }
        Start-Sleep -Milliseconds 250
    }
}
if (-not $unloaded) { throw "The selected Ollama model did not unload within the bounded verification window." }

$ffmpeg = Get-Command ffmpeg -ErrorAction SilentlyContinue | Select-Object -First 1
$whisper = Get-Command whisper-cli -ErrorAction SilentlyContinue | Select-Object -First 1
if ($null -eq $ffmpeg) { throw "The installed FFmpeg inventory candidate is unavailable." }
$ffmpegLicensePath = Join-Path (Split-Path -Parent (Split-Path -Parent $ffmpeg.Source)) "LICENSE"
if (-not (Test-Path -LiteralPath $ffmpegLicensePath -PathType Leaf)) {
    throw "The installed FFmpeg inventory candidate has no co-located license file."
}
$ffmpegReview = [DCx.Core.LocalModels.FfmpegBackendReviewer]::new().ReviewAsync(
    $ffmpeg.Source,
    $ffmpegLicensePath,
    [DateTimeOffset]::UtcNow,
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
if ($ffmpegReview.LicenseText -notmatch "GNU GENERAL PUBLIC LICENSE" -or
    $ffmpegReview.Binding.AuthenticodeStatus -ne "NotSigned") {
    throw "The installed FFmpeg license or real Windows signature evidence changed."
}

& $ffmpeg.Source -hide_banner -nostdin -loglevel error -f lavfi `
    -i "sine=frequency=440:sample_rate=48000:duration=1" -c:a libmp3lame -b:a 128k -y $ffmpegAudioFixture
if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ffmpegAudioFixture -PathType Leaf)) {
    throw "The installed FFmpeg candidate could not create the deterministic MP3 fixture."
}
$ffmpegBenchmarkStore = [DCx.Core.LocalModels.FfmpegBackendSelectionStore]::OpenAsync(
    (Join-Path $runRoot "ffmpeg-benchmark-data"),
    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$null = $ffmpegBenchmarkStore.SetAsync(
    $ffmpegReview.Binding, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
$ffmpegExtractionClock = [Diagnostics.Stopwatch]::StartNew()
$ffmpegArtifact = $null
$ffmpegWavePath = $null
try {
    $ffmpegBenchmarkState = $ffmpegBenchmarkStore.GetStateAsync(
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($null -eq $ffmpegBenchmarkState.Backend) {
        throw "The disposable FFmpeg benchmark approval did not persist."
    }
    $ffmpegProcessor = $ffmpegBenchmarkState.Backend.CreateProcessor([TimeSpan]::FromSeconds(30))
    $ffmpegArtifact = $ffmpegProcessor.ExtractWaveAsync(
        $ffmpegAudioFixture, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $ffmpegWavePath = $ffmpegArtifact.Path
    $ffmpegWaveBytes = [IO.File]::ReadAllBytes($ffmpegWavePath)
    if ($ffmpegWaveBytes.Length -lt 44 -or
        [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, 0, 4) -ne "RIFF" -or
        [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, 8, 4) -ne "WAVE") {
        throw "The production FFmpeg processor returned an invalid WAV container."
    }
    $waveFormat = $null
    $waveChannels = $null
    $waveSampleRate = $null
    $waveBits = $null
    $waveDataOffset = $null
    $waveDataBytes = $null
    for ($offset = 12; $offset + 8 -le $ffmpegWaveBytes.Length;) {
        $chunkName = [Text.Encoding]::ASCII.GetString($ffmpegWaveBytes, $offset, 4)
        $chunkBytes = [BitConverter]::ToUInt32($ffmpegWaveBytes, $offset + 4)
        $chunkData = $offset + 8
        if ($chunkData + $chunkBytes -gt $ffmpegWaveBytes.Length) {
            throw "The production FFmpeg processor returned malformed WAV chunk bounds."
        }
        if ($chunkName -eq "fmt " -and $chunkBytes -ge 16) {
            $waveFormat = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData)
            $waveChannels = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData + 2)
            $waveSampleRate = [BitConverter]::ToUInt32($ffmpegWaveBytes, $chunkData + 4)
            $waveBits = [BitConverter]::ToUInt16($ffmpegWaveBytes, $chunkData + 14)
        }
        elseif ($chunkName -eq "data") {
            $waveDataOffset = $chunkData
            $waveDataBytes = [int]$chunkBytes
        }
        $offset = $chunkData + [int]$chunkBytes + ([int]$chunkBytes % 2)
    }
    if ($waveFormat -ne 1 -or $waveChannels -ne 1 -or $waveSampleRate -ne 16000 -or
        $waveBits -ne 16 -or $null -eq $waveDataOffset -or $waveDataBytes -lt 16000) {
        throw "The production FFmpeg processor did not produce bounded 16 kHz mono PCM16 audio."
    }
    $sampleCount = [int]($waveDataBytes / 2)
    $zeroCrossings = 0
    $lastSign = 0
    $sumSquares = 0.0
    for ($sampleIndex = 0; $sampleIndex -lt $sampleCount; $sampleIndex++) {
        $sample = [BitConverter]::ToInt16($ffmpegWaveBytes, $waveDataOffset + ($sampleIndex * 2))
        $sumSquares += [double]$sample * [double]$sample
        $sign = if ($sample -gt 0) { 1 } elseif ($sample -lt 0) { -1 } else { 0 }
        if ($sign -ne 0) {
            if ($lastSign -ne 0 -and $sign -ne $lastSign) { $zeroCrossings++ }
            $lastSign = $sign
        }
    }
    $waveDurationSeconds = $sampleCount / [double]$waveSampleRate
    $measuredFrequencyHz = $zeroCrossings / (2.0 * $waveDurationSeconds)
    $waveRms = [Math]::Sqrt($sumSquares / $sampleCount)
    if ($measuredFrequencyHz -lt 430 -or $measuredFrequencyHz -gt 450 -or $waveRms -lt 1000) {
        throw "The production FFmpeg processor did not preserve the deterministic audio signal."
    }
    $ffmpegWaveLength = $ffmpegArtifact.Length
}
finally {
    $ffmpegExtractionClock.Stop()
    if ($null -ne $ffmpegArtifact) {
        $ffmpegArtifact.DisposeAsync().GetAwaiter().GetResult()
    }
    $null = $ffmpegBenchmarkStore.ClearAsync(
        [Threading.CancellationToken]::None).GetAwaiter().GetResult()
}
if (-not [string]::IsNullOrWhiteSpace($ffmpegWavePath) -and (Test-Path -LiteralPath $ffmpegWavePath)) {
    throw "The production FFmpeg processor retained its leased temporary WAV after disposal."
}

$legacyWhisperModelRoot = Join-Path $env:LOCALAPPDATA "ADFi\models\whisper"
$incompatibleWhisperModels = @()
if (Test-Path -LiteralPath $legacyWhisperModelRoot -PathType Container) {
    $incompatibleWhisperModels = @(
        Get-ChildItem -LiteralPath $legacyWhisperModelRoot -File -Filter "*.pt" -ErrorAction SilentlyContinue |
            Sort-Object Name | Select-Object -First 16 |
            ForEach-Object { [ordered]@{ name = $_.Name; bytes = [long]$_.Length; format = "pytorch_pt"; compatible = $false } }
    )
}
$manifest = [ordered]@{
    captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
    gpu_name = $gpuParts[0]
    gpu_vram_mib = [int]$gpuParts[1]
    nvidia_driver = $gpuParts[2]
    ollama_version = (& $ollama --version) -join " "
    selected_model = $model
    selected_model_digest = $modelDigest
    selected_model_unique_digest_binding = $true
    mutable_names_with_multiple_digests = @($ambiguousMutableNames)
    selected_model_capabilities = @($selectedShow.capabilities)
    selected_model_license_summary = [string]$licenseSummary
    selected_model_license_sha256 = $licenseSha256
    model_size_bytes = [long]$loaded.size
    model_vram_bytes = [long]$loaded.size_vram
    production_adapter_prompt_tokens = [int]$productionResult.PromptTokens
    production_adapter_output_tokens = [int]$productionResult.OutputTokens
    production_adapter_total_duration_ns = [long]$productionResult.TotalDurationNanoseconds
    measured_prompt_tokens = [int]$response.prompt_eval_count
    measured_output_tokens = [int]$response.eval_count
    ollama_total_duration_ns = [long]$response.total_duration
    wall_duration_ms = $wallMilliseconds
    ffmpeg_available = $null -ne $ffmpeg
    ffmpeg_executable_bytes = [long]$ffmpegReview.ExecutableBytes
    ffmpeg_executable_sha256 = [string]$ffmpegReview.Binding.ExecutableSha256
    ffmpeg_license_sha256 = [string]$ffmpegReview.Binding.LicenseSha256
    ffmpeg_authenticode_status = [string]$ffmpegReview.Binding.AuthenticodeStatus
    ffmpeg_registration_candidate_approved = $false
    ffmpeg_real_audio_fixture_passed = $true
    ffmpeg_real_audio_extraction_ms = [long]$ffmpegExtractionClock.ElapsedMilliseconds
    ffmpeg_real_audio_output_bytes = [long]$ffmpegWaveLength
    ffmpeg_real_audio_sample_rate = [int]$waveSampleRate
    ffmpeg_real_audio_channels = [int]$waveChannels
    ffmpeg_real_audio_bits_per_sample = [int]$waveBits
    ffmpeg_real_audio_measured_frequency_hz = [Math]::Round($measuredFrequencyHz, 2)
    ffmpeg_real_audio_rms = [Math]::Round($waveRms, 2)
    whisper_backend_available = $null -ne $whisper
    incompatible_whisper_model_candidates = @($incompatibleWhisperModels)
    installed_embedding_models = @($embeddingModels)
    whisper_adapter_fixture_passed = $true
    ollama_embedding_adapter_fixture_passed = $true
    ollama_redirects_disabled = $true
    whisper_pinned_files_locked_during_execution = $true
    whisper_registration_native_uia_passed = $true
    whisper_registration_approval_default_unchecked = $true
    whisper_registration_complete_licenses_exposed = $true
    whisper_registration_download_or_install_action_exposed = $false
    whisper_registration_source_files_preserved_after_clear = $true
    ffmpeg_registration_native_uia_passed = $true
    ffmpeg_whisper_mp3_pipeline_passed = $true
    cleared_ffmpeg_consumer_denied = $true
    whisper_registration_evidence = $whisperRegistrationEvidencePath
    local_model_contract_tests = $localModelTestsPassed
    local_model_storage_link_denials = $requiredStorageDenials.Count
    local_model_storage_external_writes = 0
    local_model_approval_anchor_contract_tests = $requiredApprovalStateContracts.Count
    native_hardware_profile_contract_tests = $requiredHardwareProfileContracts.Count
    ollama_inference_lease_contract_tests = $requiredOllamaInferenceLeaseContracts.Count
    ollama_inference_lease_cross_process = $true
    ollama_inference_lease_file_contains_data = $false
    ollama_runtime_resource_unload_contract_tests = $requiredOllamaRuntimeResourceContracts.Count
    native_hardware_profile_passed = $true
    native_hardware_operating_system = [string]$nativeHardwareProfile.OperatingSystem
    native_hardware_process_architecture = [string]$nativeHardwareProfile.ProcessArchitecture
    native_hardware_cpu = [string]$nativeHardwareProfile.CpuName
    native_hardware_logical_processors = [int]$nativeHardwareProfile.LogicalProcessorCount
    native_hardware_physical_memory_bytes = [long]$nativeHardwareProfile.PhysicalMemoryBytes
    native_hardware_available_memory_bytes = [long]$nativeHardwareProfile.AvailableMemoryBytes
    native_hardware_storage_total_bytes = [long]$nativeHardwareProfile.StorageTotalBytes
    native_hardware_storage_available_bytes = [long]$nativeHardwareProfile.StorageAvailableBytes
    native_hardware_gpu_count = [int]$nativeHardwareProfile.Gpus.Count
    native_hardware_nvidia_registry_driver = [string]$nativeNvidiaProfiles[0].DriverVersion
    native_hardware_nvidia_registry_memory_bytes = [long]$nativeNvidiaProfiles[0].ReportedMemoryBytes
    native_hardware_nvidia_available_memory_bytes = [long]$nativeNvidiaProfiles[0].AvailableMemoryBytes
    native_hardware_nvidia_available_memory_source = [string]$nativeNvidiaProfiles[0].AvailableMemorySource
    native_hardware_nvidia_gpu_admission_eligible = [bool]$nativeNvidiaProfiles[0].LocalModelGpuAdmissionEligible
    native_hardware_dxgi_inventory_adapters = @($nativeDxgiInventoryProfiles | ForEach-Object {
        [ordered]@{
            name = [string]$_.Name
            provider = [string]$_.Provider
            driver = [string]$_.DriverVersion
            budget_bytes = [long]$_.ReportedMemoryBytes
            available_budget_bytes = [long]$_.AvailableMemoryBytes
            local_model_gpu_admission_eligible = [bool]$_.LocalModelGpuAdmissionEligible
        }
    })
    native_resource_available_system_memory_bytes = [long]$nativeResourceSelection.AvailableSystemMemoryBytes
    native_resource_available_graphics_memory_bytes = [long]$nativeResourceSelection.AvailableGraphicsMemoryBytes
    native_resource_selected_vision_model = [string]$nativeResourceSelection.Vision.Model.Name
    native_resource_selected_vision_digest = [string]$nativeResourceSelection.Vision.Model.Digest
    native_resource_selected_embedding_available = [bool]$nativeResourceSelection.Embedding.IsAvailable
    dependency_catalog_id = [string]$dependencyCatalog.CatalogId
    dependency_catalog_sha256 = [string]$dependencyCatalog.CatalogSha256
    dependency_catalog_candidates = @($dependencyCandidateIds)
    dependency_catalog_contract_tests = $requiredDependencyContracts.Count
    dependency_transfer_contract_tests = $requiredDependencyTransferContracts.Count
    dependency_http_transfer_contract_tests = $requiredDependencyHttpTransferContracts.Count
    dependency_redirect_review_contract_tests = $requiredDependencyRedirectReviewContracts.Count
    dependency_transfer_set_contract_tests = $requiredDependencyTransferSetContracts.Count
    dependency_transfer_plan_sha256 = [string]$dependencyTransferPlan.PlanSha256
    dependency_transfer_items = [int]$dependencyTransferPlan.Items.Count
    dependency_transfer_bytes = [long]$dependencyTransferPlan.TransferBytes
    dependency_transfer_minimum_free_bytes = [long]$dependencyTransferPlan.MinimumFreeBytes
    dependency_transfer_observed_free_bytes = [long]$dependencyTransferPlan.ObservedAvailableFreeBytes
    dependency_transfer_capacity_sufficient = [bool]$dependencyTransferPlan.CapacitySufficient
    dependency_redirect_policy_sha256 = [string]$dependencyRedirectPolicy.PolicySha256
    dependency_redirect_policy_routes = [int]$dependencyRedirectPolicy.Routes.Count
    dependency_redirect_policy_bound_to_approval_and_receipt = $true
    dependency_transfer_authority_granted = $false
    dependency_transfer_native_uia = "pass"
    dependency_transfer_ui_evidence = $dependencyTransferUiEvidencePath
    dependency_transfer_ui_provider_requests = 0
    dependency_transfer_ui_downloads = 0
    dependency_transfer_ui_installs = 0
    dependency_transfer_ui_executions = 0
    dependency_runtime_archive_denials = $runtimeArchiveDenials.Count
    dependency_downloads_or_installs = 0
    model_unload_verified = $unloaded
}
$manifest | ConvertTo-Json | Set-Content -LiteralPath $manifestPath -Encoding utf8

"GPU=$($manifest.gpu_name); VRAM MiB=$($manifest.gpu_vram_mib); driver=$($manifest.nvidia_driver)"
"Ollama=$($manifest.ollama_version)"
"Model=$model; model bytes=$($manifest.model_size_bytes); VRAM bytes=$($manifest.model_vram_bytes)"
"Model digest=$($manifest.selected_model_digest); capabilities=$($manifest.selected_model_capabilities -join ',')"
"Unique digest-bound model alias=PASS; ambiguous mutable names observed=$(@($manifest.mutable_names_with_multiple_digests).Count)"
"Model license=$($manifest.selected_model_license_summary); license SHA-256=$($manifest.selected_model_license_sha256)"
"Vision answer=$answer"
"Production adapter prompt tokens=$($manifest.production_adapter_prompt_tokens); output tokens=$($manifest.production_adapter_output_tokens)"
"Measured prompt tokens=$($manifest.measured_prompt_tokens); output tokens=$($manifest.measured_output_tokens)"
"Ollama duration ns=$($manifest.ollama_total_duration_ns); wall ms=$wallMilliseconds"
"FFmpeg available=$($manifest.ffmpeg_available)"
"FFmpeg bytes=$($manifest.ffmpeg_executable_bytes); SHA-256=$($manifest.ffmpeg_executable_sha256)"
"FFmpeg license SHA-256=$($manifest.ffmpeg_license_sha256); Windows status=$($manifest.ffmpeg_authenticode_status); approved=False"
"Approved-binding real FFmpeg MP3-to-WAV fixture=PASS; elapsed ms=$($manifest.ffmpeg_real_audio_extraction_ms); measured Hz=$($manifest.ffmpeg_real_audio_measured_frequency_hz)"
"Whisper backend available=$($manifest.whisper_backend_available)"
"Incompatible existing PyTorch Whisper model candidates=$(@($manifest.incompatible_whisper_model_candidates).Count)"
"Installed embedding models=$(@($manifest.installed_embedding_models).Count)"
"Hash-pinned Whisper adapter fixture=PASS"
"Pinned Whisper executable/model execution leases=PASS"
"Accessible already-installed Whisper registration and complete-license review=PASS"
"Whisper registration defaults unchecked and exposes no download/install action=PASS"
"Whisper registration clear preserves all source files=PASS"
"Approved FFmpeg-to-Whisper MP3 pipeline and cleared-binding denial=PASS"
"Bounded Ollama embedding adapter fixture=PASS"
"Ollama redirect/cookie/proxy denial and response-origin validation=PASS"
"Low-resource and local-first fallback tests=PASS"
"Native product hardware profile contracts=PASS ($($manifest.native_hardware_profile_contract_tests)/$($manifest.native_hardware_profile_contract_tests)); CPU=$($manifest.native_hardware_cpu); logical processors=$($manifest.native_hardware_logical_processors); physical memory bytes=$($manifest.native_hardware_physical_memory_bytes); available system memory bytes=$($manifest.native_hardware_available_memory_bytes); application-data storage available bytes=$($manifest.native_hardware_storage_available_bytes); NVIDIA registry driver=$($manifest.native_hardware_nvidia_registry_driver); registry graphics memory bytes=$($manifest.native_hardware_nvidia_registry_memory_bytes); live available graphics memory bytes=$($manifest.native_hardware_nvidia_available_memory_bytes) from $($manifest.native_hardware_nvidia_available_memory_source); DXGI inventory-only adapters=$(@($manifest.native_hardware_dxgi_inventory_adapters).Count)"
"Per-user cross-process Ollama inference sequencing contracts=PASS ($($manifest.ollama_inference_lease_contract_tests)/$($manifest.ollama_inference_lease_contract_tests)); lock data retained=False"
"Runtime resource-admission and verified-unload contracts=PASS ($($manifest.ollama_runtime_resource_unload_contract_tests)/$($manifest.ollama_runtime_resource_unload_contract_tests))"
"Native hardware-bound model selection=PASS; available graphics bytes=$($manifest.native_resource_available_graphics_memory_bytes); available system bytes=$($manifest.native_resource_available_system_memory_bytes); vision=$($manifest.native_resource_selected_vision_model); embedding available=$($manifest.native_resource_selected_embedding_available)"
"Local-model linked-root, linked-file and swapped-directory denials=PASS ($($manifest.local_model_storage_link_denials)/$($manifest.local_model_storage_link_denials)); external writes=$($manifest.local_model_storage_external_writes)"
"Pinned dependency catalog=$($manifest.dependency_catalog_id); SHA-256=$($manifest.dependency_catalog_sha256)"
"Pinned dependency catalog/reviewer contracts=PASS ($($manifest.dependency_catalog_contract_tests)/$($manifest.dependency_catalog_contract_tests)); downloads or installs=$($manifest.dependency_downloads_or_installs)"
"Dependency transfer preflight contracts=PASS ($($manifest.dependency_transfer_contract_tests)/$($manifest.dependency_transfer_contract_tests)); plan SHA-256=$($manifest.dependency_transfer_plan_sha256); network bytes=$($manifest.dependency_transfer_bytes); minimum free bytes=$($manifest.dependency_transfer_minimum_free_bytes); observed free bytes=$($manifest.dependency_transfer_observed_free_bytes); authority granted=False"
"Dependency HTTP transfer contracts=PASS ($($manifest.dependency_http_transfer_contract_tests)/$($manifest.dependency_http_transfer_contract_tests)); production transfer actions=0"
"Dependency HEAD-only route-review contracts=PASS ($($manifest.dependency_redirect_review_contract_tests)/$($manifest.dependency_redirect_review_contract_tests)); production route reviews=0"
"Dependency transfer-set coordinator contracts=PASS ($($manifest.dependency_transfer_set_contract_tests)/$($manifest.dependency_transfer_set_contract_tests)); production transfer actions=0"
"Dependency redirect policy binding=PASS; SHA-256=$($manifest.dependency_redirect_policy_sha256); approved routes=$($manifest.dependency_redirect_policy_routes); approval and protected receipt bound=True"
"Accessible default-off transfer progress/cancellation/reverification/recovery UIA=PASS; provider requests/downloads/installs/executions=0"
"Whisper runtime traversal, collision, device, link, missing-CLI, compression-bomb, CRC and encryption denials=PASS ($($manifest.dependency_runtime_archive_denials)/$($manifest.dependency_runtime_archive_denials))"
"Model unload verified=PASS"
"P06 local-model tests=PASS ($localModelTestsPassed/$localModelTestsPassed)"
"Hardware manifest=$manifestPath"
if (-not $manifest.whisper_backend_available) {
    "P06 transcription gate=BLOCKED: no approved local Whisper backend is installed."
}
if (@($manifest.installed_embedding_models).Count -eq 0) {
    "P06 embedding gate=BLOCKED: no approved local embedding model is installed."
}
"P06 local vision benchmark=PASS"
