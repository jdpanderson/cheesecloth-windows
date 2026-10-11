# AGENTS.md

## Collaboration
- We work as a team. We share what we know with each other.
- If either of us sees a better approach, we say so first.
- If the solution is not clear, we stop and work it out together.
- We state our assumptions before we act on them.

## Language
- We use short, simple English. Avoiding idioms or figures of speech.
    - This applies to messages and code comments.

## Quality
- No workarounds or hacks. We fix the root cause, and we fix it cleanly.
- We do not skip tests, silence errors, or weaken checks to make code pass.
- We stay in scope. We agree before changes outside the task.

## Design
- We choose the best solution, even if it is complex.
- If two solutions are equally good, we choose the simpler one.

## Autonomy
The agent works without permission prompts where it can:
- Read files with the Read tool; change them with Edit or Write.
- Bash is for simple commands (cargo, git, gh) with paths relative to the
  repo root. No `cd`, absolute or `~/` paths, `$(...)`, loops, heredocs,
  or `sed`/`awk`/`python` scripts.
- Commit with `git commit -m "<subject>" -m "<trailer>"`.
- Wait for CI with `gh pr checks <n> --watch`.
- Put temporary files under `target/`.
- Give these rules to every sub-agent.

## This repository
- It holds the Windows installer for
  [cheesecloth](https://github.com/jdpanderson/cheesecloth). The app, its
  service code and its docs live there; see its `docs/REPOSITORIES.md`.
- `cheesecloth.pin` pins the cheesecloth commit or release that the
  installer packages. See [README.md](README.md#the-pin-file).
- For work that also changes cheesecloth, change and merge it there first,
  then move the pin here.

## Checks
All checks run in CI (`.github/workflows/ci.yml`), on Windows and Linux
runners. They need Windows, PowerShell or tools that are not installed
locally, so push a branch and open a pull request to run them.
- **Lint:** PSScriptAnalyzer on the `.ps1` files, and actionlint on the
  workflow.
- **Binary:** builds `cheesecloth.exe` from the pinned commit, for x64 and
  arm64.
- **Installer:** builds the installer for x64 and arm64, then `check.ps1`
  installs, upgrades, reinstalls and uninstalls it on the runner.
- **Sign** and **Check signed installer:** only on `main` once signing is set
  up. They sign on a fresh runner, then run `check.ps1` on the signed
  installer on another.
