# Architecture Decision Alignment

This document is the human alignment checklist before implementation starts. Future rows with pending alignment status should be confirmed with the user before dependent stories are generated or executed.

## Current Proposed Operating Model

| Decision | Proposed Choice | Reason | Status |
| --- | --- | --- | --- |
| BMad track | BMad Method by default; Quick Flow only for small isolated fixes | The requested pipeline is architecture-sensitive and should produce PRD, architecture, epics, stories, and review gates | Accepted |
| Installed modules | `bmm,cis` | BMM provides the coding lifecycle; CIS covers structured brainstorming | Accepted |
| Codex integration | `.agents/skills` | The installer generated 52 Codex skills there | Accepted |
| Artifact root | `_bmad-output` | Matches BMad config and keeps generated planning/implementation outputs together | Accepted |
| Planning artifacts | `_bmad-output/planning-artifacts` | BMad default for PRD, architecture, epics, and readiness reports | Accepted |
| Implementation artifacts | `_bmad-output/implementation-artifacts` | BMad default for sprint tracking and story execution artifacts | Accepted |
| Project knowledge | `docs` | BMad config uses this for durable project knowledge | Accepted |
| Architecture gate | User approval required before epics/stories | Prevents agents from encoding unapproved stack, data, API, or deployment assumptions | Accepted |
| Story autonomy | Agents may implement only accepted story scope | Keeps implementation bounded and reviewable | Accepted |
| Review gate | Code review required before story completion | BMad includes adversarial review and edge-case review support | Accepted |
| Parallel story work | Allowed only with disjoint file ownership | Reduces merge conflicts and architecture drift | Accepted |

## Questions for User Alignment

1. Accepted: this workspace defaults to the full BMad Method track, with `bmad-quick-dev` reserved only for small fixes.
2. Accepted: architecture approval is a hard gate before `bmad-create-epics-and-stories`.
3. Accepted: every story requires `bmad-code-review` before it can be marked done.
4. Accepted: parallel agent work requires explicit file/module ownership boundaries.
5. Accepted: no additional mandatory gates are required by default; security, UX, and deployment reviews are added when project risk warrants them.

## Architecture Decisions to Capture Per Project

Use `_bmad-output/planning-artifacts/architecture-decisions.md` to track project-specific decisions:

- Runtime and language versions.
- Framework and starter template.
- Package manager and dependency policy.
- Data store, migration model, and caching.
- API style and contract documentation.
- Authentication and authorization.
- Error handling and logging.
- Testing pyramid and required commands.
- Deployment target and environment strategy.
- Secrets management.
- Monitoring and incident response.

## Decision Status Values

- `Open`: decision has not been discussed.
- `Proposed`: recommendation exists but user has not accepted it.
- `Accepted`: user accepted the choice.
- `Rejected`: option was considered and rejected.
- `Superseded`: replaced by a later decision.
