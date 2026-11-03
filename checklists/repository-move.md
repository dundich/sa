# Repository move checklist

- [ ] Exact source → destination mapping reviewed.
- [ ] `.slnx` project paths updated.
- [ ] `ProjectReference` paths updated.
- [ ] `Directory.Build.*`, `Directory.Packages.props`, `NuGet.config`, `global.json` behavior checked.
- [ ] Dockerfiles, compose files, shell scripts and workflow paths checked.
- [ ] README/docs relative links updated.
- [ ] Test data, snapshots, content-copy items and embedded resources checked.
- [ ] Build/test/package run from clean checkout.
- [ ] Package IDs, TFMs, assembly names, namespaces and API unchanged.
- [ ] Git diff shows moves rather than accidental content edits.
- [ ] Rollback commit/strategy identified.
