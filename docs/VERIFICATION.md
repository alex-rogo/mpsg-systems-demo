# Verification Record

## Repository audit

- Branch inspected: `enemy-ai`.
- Source basis: the current working tree, including uncommitted numeric-enemy migration changes.
- Original game source modified by this task: none.
- Showcase source files: copied byte-for-byte, then hashed in `SHA256SUMS.txt`.
- Unity version: 6000.5.5f1.
- NGO version: 2.13.2.
- Unity Test Framework: 1.7.0.

## Test evidence

The fresh showcase-verification run is stored outside this publication folder at `Temp/EnemyValidation/results-portfolio.xml` in the working repository:

- Result: Passed
- Total: 69
- Passed: 69
- Failed: 0
- Skipped: 0
- Completed: 2026-10-08 03:52:20Z

The showcase documentation/copy operation did not change runtime source. Another verification run should be performed from the exact commit used for publication and its test result attached to the release or CI job.

## Security/privacy scan scope

The selected C# and documentation were checked for common credential labels, private-key markers, IP literals, and hardcoded Windows paths. Loopback `127.0.0.1` appears in `EnemyDataRuntimeTests` only, where the integration test intentionally starts a local UTP client. No API keys, passwords, tokens, credentials, private keys, public/VPN addresses, or hardcoded personal filesystem paths are included.

## Documentation checks

- Mermaid diagrams describe only classes/functions present in the copied code.
- Performance claims are split between automated correctness and observed external logs.
- The folder is explicitly documented as non-standalone.
- No raw logs or third-party assets are included.
