# Agentic Coding Pipeline

This pipeline uses BMad Method as the planning and execution backbone, with Codex skills generated into `.agents/skills`.

## Pipeline Goals

- Keep human product and architecture decisions explicit.
- Convert broad intent into PRD, architecture, epics, stories, and verified code.
- Prevent agent drift by giving every implementation story enough context.
- Make review and test gates non-optional for production work.

## Phase 0: Intake and Routing

Default command: `bmad-help`

Classify the request:

- Quick Flow: bug fixes, simple tweaks, or narrow features with clear scope. Use `bmad-quick-dev`.
- BMad Method: products, platforms, multi-feature work, unclear scope, or architecture implications. Use the full pipeline.
- Enterprise: regulated, multi-tenant, security-sensitive, or operationally complex work. Use BMad Method plus explicit security, DevOps, and test strategy gates.

Output: selected track and rationale in the active task notes.

## Phase 1: Discovery and Brainstorming

Default command: `bmad-brainstorming`

Use when the goal, target user, constraints, or solution options are not clear enough for a PRD.

Recommended techniques for coding pipeline work:

- First Principles Thinking: define what an agentic coding pipeline must guarantee.
- Reverse Brainstorming: identify failure modes that would make agents produce poor code.
- Six Thinking Hats: balance facts, risk, creativity, user value, and process.
- Constraint Mapping: identify tool, repo, security, and human review constraints.
- SCAMPER: improve the pipeline once the baseline is working.

Output: `_bmad-output/brainstorming/brainstorming-session-*.md`

Gate: user agrees the problem framing is good enough to become requirements.

## Phase 2: Requirements

Default command: `bmad-create-prd`

Create the PRD before architecture. Requirements must include:

- Users and jobs-to-be-done.
- Functional requirements.
- Non-functional requirements.
- Out-of-scope items.
- Acceptance criteria.
- Risks and assumptions.

Optional commands:

- `bmad-product-brief` when the idea needs a foundation document.
- `bmad-prfaq` when the concept needs a working-backwards stress test.
- `bmad-validate-prd` before architecture.

Output: `_bmad-output/planning-artifacts/PRD.md`

Gate: PRD scope accepted by the user.

## Phase 3: Architecture Alignment

Default command: `bmad-create-architecture`

Architecture decisions must be collaborative. Use `docs/architecture-decision-alignment.md` and `_bmad-output/planning-artifacts/architecture-decisions.md` as the alignment surface.

Required decision categories:

- Application shape and runtime.
- Frameworks and major libraries.
- Data architecture.
- API and integration boundaries.
- Authentication, authorization, and security controls.
- Frontend architecture, when applicable.
- Deployment, CI/CD, configuration, secrets, and observability.
- Testing strategy and quality gates.

Output: `_bmad-output/planning-artifacts/architecture.md` plus updated decision register.

Gate: no implementation story may start with blocking decisions marked `Open` or `Proposed`.

## Phase 4: Epics and Stories

Default command: `bmad-create-epics-and-stories`

Run this after architecture so stories inherit the accepted technical decisions.

Story requirements:

- Clear user/business value.
- Acceptance criteria.
- Technical notes tied to architecture.
- File or module ownership when known.
- Test expectations.
- Dependencies and sequencing.

Output: `_bmad-output/planning-artifacts/epics/`

Gate: epics cover PRD scope and each implementation story has enough context to avoid guessing.

## Phase 5: Readiness

Default command: `bmad-check-implementation-readiness`

Validate alignment between PRD, UX, architecture, epics, and stories.

Readiness fails if:

- Requirements are not mapped to stories.
- Stories depend on unresolved architecture decisions.
- Testing strategy is missing.
- Cross-story dependencies are ambiguous.
- Implementation agents would need to invent major behavior.

Output: readiness report in `_bmad-output/planning-artifacts/`

Gate: readiness report accepted or issues moved into explicit follow-up work.

## Phase 6: Sprint Planning

Default command: `bmad-sprint-planning`

Create implementation tracking from accepted epics and stories.

Output: `_bmad-output/implementation-artifacts/sprint-status.yaml`

Gate: first story selected and ready.

## Phase 7: Story Execution Loop

Repeat for each story:

1. `bmad-create-story` creates a context-rich story file.
2. `bmad-create-story` validation checks story readiness.
3. `bmad-dev-story` implements the story and runs checks.
4. `bmad-code-review` reviews changes before completion.
5. `bmad-checkpoint-preview` is used when human review needs a guided walkthrough.
6. `bmad-retrospective` closes an epic and captures lessons.

Definition of done:

- Acceptance criteria met.
- Required tests added or updated.
- Relevant test and lint commands pass.
- Story file includes changed file list and implementation notes.
- Code review has no unresolved blockers.
- Sprint status is updated.

## Change Control

Use `bmad-correct-course` when a change affects PRD scope, accepted architecture, story sequencing, or release risk.

Do not patch around architecture decisions inside a story. Reopen alignment, update the decision register, then revise stories.
