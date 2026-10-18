# AI-assisted development identity

This fork is maintained by `El3ctro99` with assistance from ChatGPT. The original
portable extraction and its earlier history come from `aeae1`'s upstream
repository; their existing attribution is preserved.

Preferred attribution for assistant-driven commits:

`Assisted-by: ChatGPT`

GitHub records the author and committer according to the commit metadata and the
authenticated publishing workflow. The attribution trailer identifies
assistant-produced changes without claiming a separate bot account, credential
or model version. Historical upstream trailers remain part of the original
record.

## Working rules

- Keep `main` product-only and review `microsoft/PowerToys` updates for selective ports.
- Do custom work on `main` or short-lived descendant feature branches.
- Prefer small, reviewable commits.
- Preserve Mouse Without Borders protocol behavior unless an intentional compatibility break is documented.
- Preserve clipboard and file-transfer functionality.
- Add or update focused tests for behavior changes where practical.
- Keep a Windows CI build focused on the Mouse Without Borders projects.
