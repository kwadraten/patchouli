# Manual verification: OS-owned home symlinks in Agent Skill paths (issue #73)

> **Status: not yet executed on real hardware.** This checklist must be run on a
> Fedora Atomic-family (Silverblue/Kinoite) install — or any distro that symlinks
> a directory *above* Patchouli's trusted roots — before issue #73 can be called
> verified. The fix landed without that environment; every step below is
> unverified on it until checked.

## Scope

Verifies `AgentSkillService.ValidatePath`/`ValidateTree`/`DeleteTree`/`DeleteSafe`
with their per-scope trusted root, and `AgentSkillLocations.GetUserTrustedRoot`.
The rule: path components **at or below** the trusted root must never be links;
links **above** it (OS layout indirection like `/home → /var/home`, or ancestors
of an env-configured base) are normalized by `IRealPathResolver` and allowed —
provided the real-resolved path keeps the identical relative suffix under the
real-resolved root.

Out of scope on purpose: the fail-closed behavior for links *inside* managed
directories is unchanged and must keep failing; Windows and macOS home layouts do
not use this indirection, and macOS `/home` is not a trusted-root ancestor.

## Test matrix

| # | Scenario | Expected |
|---|----------|----------|
| 1 | Fedora Atomic: `/home` → `/var/home` symlink, real user dirs under `/var/home` | All user-level Skill operations (status read, install, clear, startup upgrade of a registered install) succeed; workspace `agent-workspace/` copies work |
| 2 | Same OS, but the Agent's own skills dir (e.g. `~/.claude/skills`) is itself a symlink | Operation fails with the "符号链接或重解析点" error — links at/below the root are still rejected |
| 3 | A file/directory *inside* the managed `patchouli` skill dir is a symlink to outside content | Read/install/clear fail closed; nothing is deleted or written through the link |
| 4 | A symlink inside the managed dir points *back inside* it (internal link) | Still rejected — the attribute walk rejects links regardless of target |
| 5 | Env-rooted override (e.g. `XDG_CONFIG_HOME` or the locations override) is itself reached through an OS symlink | Allowed if only ancestors above the override base are links; rejected if the base or anything below is |
| 6 | A link above the root redirects the managed dir elsewhere (root resolves to a different subtree than the declared relative suffix) | Rejected by the resolved-suffix comparison |
| 7 | Registration state file under `StateDirectory` on a system where a state-dir ancestor is a link | Read/write succeeds only if the link is above `StateDirectory`; the state file read must also succeed when the state file itself is missing (read-only probe) |

## Procedure

### Preparation

1. Install or VM-boot Fedora Silverblue/Kinoite; confirm `ls -ld /home` shows a
   symlink to `/var/home`.
2. Build/publish the app there, and enable one Agent integration so user-level
   locations exist under `~` (which resolves through `/var/home`).

### Steps

1. **Baseline** — on the Atomic system, open Settings → AI集成 → Agent 集成.
   The user-level install/clear/status operations must work; the skill directory
   lands under `/var/home/...` physically while `~` paths keep working.
2. **Launch prep** — start a workspace launch for that Agent. `agent-workspace/`
   managed copies must be created/cleaned normally (workspace dir is under
   `.patchouli` state or the library; on Atomic it is also under `/var/home`).
3. **Negative case A** — replace the Agent's user `skills` dir (the catalog
   root, not `patchouli/`) with a symlink to a real directory. Status/install
   must fail with the reparse-point error and must not follow the link.
4. **Negative case B** — install normally, then replace a file inside
   `patchouli/` with a symlink to `/etc/hostname`. Clear/install must fail
   closed and must not delete the symlink target's content.
5. **Upgrade path** — with a registered user-level install whose marker is an
   older version, launch the app. Startup maintenance must upgrade it in place
   (it previously died at `ValidatePath` on the `/home` symlink).

### What to check for regressions

- A non-Atomic Linux distro (direct `/home`) must behave exactly as before.
- Error messages must still name the offending component path, not the resolved
  one, so the user sees the path they configured.

## Notes for the verifier

- The Windows test suite already exercises the seam via `mklink /J` junctions
  (resolved by `PlatformRealPathResolver`), so the mechanism is covered; what is
  not covered is the real `/home → /var/home` layout combined with XDG paths and
  a live Agent CLI.
