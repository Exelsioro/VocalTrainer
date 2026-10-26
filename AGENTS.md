# Build outputs

- Keep only the latest `dist/Release` and, if needed, `dist/Debug`.
- Do not create versioned, dated, per-person, preview or alternative build folders in `dist`.
- Do not create ZIP/distribution packages unless the user explicitly requests a package or a build to share. Use `build.ps1 -Package` when requested; packages belong in `artifacts/packages`, outside `dist`.
- Put test screenshots, logs and other verification output in `artifacts/` or `.build-tmp/`, never in `dist`.
- Use `build.ps1 -SelfContained` for the usual standalone Windows Release, and `-Configuration Debug` only when needed. Update the fixed output directory instead of keeping historical builds.
- If a running app prevents replacing or deleting an output, report the lock and preserve the prepared build outside `dist`. Do not create another output folder or terminate a user's running app without authorization.
