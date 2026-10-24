# Agent Skill distribution

Issue [#23](https://github.com/kwadraten/patchouli/issues/23) distributes exactly one bundled
`patchouli` skill. `.agents/skills/patchouli/` is its source; `Patchouli.Host` embeds `SKILL.md`
and the five ordinary reference documents. This contract describes delivery and runtime selection,
not authenticated real-Agent acceptance. In particular, it does not claim an end-to-end
`patchouli.find → patchouli.fetch → patchouli.cite` run against a real authenticated Agent.
`AgentExternalSkillAccessTests` runs real CLI child processes from an unrelated working directory,
with all `PATCHOULI_*` environment variables removed, against an isolated explicit MCP endpoint.
It covers `find → fetch → cite`, not the behavior of an authenticated Agent session.

## Installation scopes

There are two scopes. **Settings → AI集成 → Agent 集成** explicitly installs or clears only the
Agent's user-level directory (outside any Library). A successful user-level install registers its
target in Patchouli's device-local maintenance state, separate from `AppSettings`. On desktop
application startup, `UpdateUserInstallationsAsync` upgrades only registered installations whose
target directory still exists and whose version marker differs from the bundled application version.
Upgrade atomically replaces the registered user-level Skill directory. A removed target is not
recreated; an unregistered directory is never changed. Same-version content and unmarked
installations are not inspected or repaired. A running Agent is not stopped or restarted, though
files on disk may be replaced while it is running.

Before a detected, valid Agent is launched, `PrepareWorkspaceAsync` checks the user-level locations
that this Agent can read. If any `patchouli` directory exists there, that directory wins as-is:
Patchouli does not inspect its files, version, damage, or user edits, and does not repair it. The
launch preparation removes managed workspace copies to avoid duplicate discovery. To reset a
user-level installation, the user must clear it and then explicitly install it again. If no user-level
directory exists, launch preparation creates/updates the managed copy in `agent-workspace/`.
Disabled, unknown, unsupported, missing, or invalid Agents do not cause Skill preparation.

Effective user-level and managed paths are fixed catalog adapters in `AgentSkillLocations`; this
document intentionally does not enumerate compatibility aliases. In particular, project/workspace
compatibility roots must not be described as global user-level paths. Coordination checks/removes
every compatible managed copy visible to the selected Agent, not just its native path. If cleanup
of any visible duplicate fails, launch preparation fails rather than knowingly starting with
duplicates. Unknown ids and traversal are rejected; directory operations reject links/reparse
points and report errors.

For Antigravity, the native user-level target is `~/.gemini/config/skills/patchouli/SKILL.md`.
This does not change its managed workspace mapping. Existing user files at the former
`.gemini/antigravity-cli/skills` location are not automatically deleted or migrated.

## Change and safety boundaries

- At launch, an existing Agent-readable user-level directory takes precedence solely by existence:
  launch selection does not inspect its contents/version or repair it. Separately, application
  upgrades may replace an existing, explicitly registered user-level installation when its version
  marker differs. Same-version and unmarked installs are not repaired; unregistered directories
  are never changed. Clear then explicitly inject to reset a user-level installation.
- Managed workspace copies are coordinated before launch. Workspace preparation failure blocks
  launch with a retryable diagnostic; Settings is not presented as a way to install that managed
  copy.
- Application upgrades replace only explicit, still-existing user-level registrations with a different
  version marker. Deleting a target prevents later automatic recreation. This maintenance is
  independent of opening a Library or enabling the Agent integration.
- Neither startup maintenance nor launch preparation stops or restarts a running external Agent.
  Disk files may be replaced during upgrade; a running process may continue using instructions it
  already loaded until the user restarts it.
- Skill operations do not change MCP configuration, tokens, or permissions. MCP authorization and
  its capability/data boundaries are independent of Skill delivery.
- Status inspection does not create directories. User and managed files are device-local and are
  not Library snapshot content.

Plugin VFS and Action projection are not part of this Skill contract. See the Skill's own guidance
for delivered user behavior; repository protocol documents remain normative for implementation.
