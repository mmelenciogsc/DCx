# Local model benchmark manifests

DCx can review and run bounded local-model quality fixtures without downloading a model or changing a production selection. Open **Local models**, then **Open quality benchmark review**. Browse to a local JSON manifest, review every digest and threshold, and check the approval box for one run. Approval starts unchecked, expires after 20 minutes, and is revoked by a path change or any completed, canceled, or failed run.

Transcription runs require an already-registered, explicitly approved Whisper backend. Embedding manifests can be reviewed, but execution remains unavailable until DCx has an approved production embedding adapter and resource probe. A passing fixture does not establish publisher authenticity, licensing rights, general accuracy, or production suitability.

## Transcription schema version 1

All WAV paths must be unique relative paths beneath the manifest directory. WAVs, the manifest, and every containing directory must be regular local, non-reparse inputs. The manifest accepts no comments, trailing commas, duplicate properties, unknown properties, absolute fixture paths, network paths, or traversal outside its directory.

```json
{
  "schemaVersion": 1,
  "kind": "transcription",
  "name": "Reviewed English speech suite",
  "provenance": {
    "source": "Describe the fixture source",
    "license": "SPDX identifier or reviewed license name"
  },
  "limits": {
    "maximumWordErrorRate": 0.1,
    "maximumCharacterErrorRate": 0.05,
    "resources": {
      "maximumElapsedMilliseconds": 60000,
      "maximumPeakWorkingSetBytes": 1073741824,
      "maximumPeakGpuBytes": null,
      "requireUnloadConfirmation": true
    }
  },
  "fixtures": [
    {
      "id": "speech-001",
      "waveFile": "audio/speech-001.wav",
      "expectedText": "The exact reviewed reference transcript."
    }
  ]
}
```

## Embedding-retrieval schema version 1

```json
{
  "schemaVersion": 1,
  "kind": "embeddingRetrieval",
  "name": "Reviewed retrieval suite",
  "provenance": {
    "source": "Describe the fixture source",
    "license": "SPDX identifier or reviewed license name"
  },
  "limits": {
    "k": 3,
    "minimumRecallAtK": 0.9,
    "minimumMeanReciprocalRank": 0.8,
    "resources": {
      "maximumElapsedMilliseconds": 30000,
      "maximumPeakWorkingSetBytes": 536870912,
      "maximumPeakGpuBytes": null,
      "requireUnloadConfirmation": true
    }
  },
  "documents": [
    { "id": "document-001", "text": "Reviewed local corpus text." }
  ],
  "queries": [
    {
      "id": "query-001",
      "text": "Reviewed local query text.",
      "relevantDocumentIds": ["document-001"]
    }
  ]
}
```

## Retained evidence

DCx replaces the latest evidence file for each benchmark type beneath the current user's private application-data directory. It retains only aggregate metrics, failure codes, timestamps, and approval/backend/suite/policy digests. It does not retain fixture paths, reference transcripts, generated transcripts, corpus text, queries, relevance identifiers, or vectors. Cancellation never replaces prior completed evidence.
