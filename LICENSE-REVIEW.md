# License and Publication Review

## Current conclusion

This showcase copies only project-authored C# and one Unity assembly-definition file. It does not copy package source, package caches, art, audio, fonts, models, textures, prefabs, scenes, or credentials. No copyright/license headers in the selected files identify third-party source.

The repository root did not contain a `LICENSE` file during this audit. Therefore these files should be treated as **all rights reserved / license unspecified** until the owner deliberately chooses publication terms. Do not label the showcase MIT, Apache-2.0, or another open-source license without that decision.

## Dependencies referenced but not redistributed

- Unity 6000.5.5f1 APIs (`UnityEngine`).
- Netcode for GameObjects 2.13.2 (`Unity.Netcode`).
- Unity Collections (`Unity.Collections`).
- Unity Transport through the tests/diagnostics.
- Unity Test Framework 1.7.0.
- Project-owned item, inventory, weapon, player-input, prefab, and scene types omitted from this snapshot.

Use of Unity package APIs does not make this folder independently buildable and does not grant permission to redistribute package code. Anyone publishing a runnable derivative should review the Unity Editor/software terms and each package's own license in the installed package metadata.

## Items intentionally excluded from public publication

- `Library/`, `Temp/`, `Logs/`, generated project files, and raw player logs. Raw logs can contain local filesystem paths, machine/environment details, and network configuration.
- All game art and third-party asset-store content, including gun models and their separate README/license terms.
- Scenes, prefabs, ScriptableObject assets, render-pipeline assets, and project settings.
- VPN addresses, public/private IP addresses, credentials, API keys, and connection-menu defaults.
- The full game repository and unrelated gameplay code.

## Before publishing

1. Confirm authorship/permission for every copied source file and any contributions from collaborators.
2. Choose whether this is source-available for viewing or genuinely open source. Add an explicit license only after that choice.
3. Run a fresh secret scan on the exact Git staging set.
4. If adding screenshots/video, verify every visible model, texture, font, logo, and audio track is portfolio-redistributable.
5. Keep benchmark logs summarized and anonymized; do not publish raw machine logs unless reviewed manually.
6. State prominently that the snapshot depends on proprietary game code/assets and does not compile independently.

