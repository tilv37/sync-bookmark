# RELEASE

## Extension package + AMO signing

For daily use, package and sign the extension (`extension/manifest.json`,
currently `0.1.0`):

```bash
cd extension && zip -r ../bmsync.xpi *
```

Then submit `bmsync.xpi` for signing at
[addons.mozilla.org](https://addons.mozilla.org) (AMO): self-distribution
(signed but unlisted) fits this single-user tool; review times apply to listed
versions. Bump `manifest.json` `version` for every signed upload, and keep the
gecko `id` stable so updates apply cleanly. Temporary `about:debugging` loads
stay unsigned for development.

The zip includes `extension/icon.png` (600×600 source): it serves as the
toolbar icon (`action.default_icon`), the add-on listing icons (`icons`
48/96, scaled by Firefox), the popup/options headers, and the options-tab
favicon — no extra assets needed.

## Server image publish

```bash
cd deploy
docker compose build
docker tag bmsync <registry>/bmsync:<version>
docker push <registry>/bmsync:<version>
```

The image is `FROM scratch` + NativeAOT (~20–30 MB class; see
`bmsync/Dockerfile`). Tests run **inside** the build before the AOT publish, so
a broken image is never produced. The build context must stay the repo root —
the Domain tests locate `test/hlc_vectors.json` by walking up from the binary
dir.

## Versioning

- Server and extension share the `state.json` schema version (`"v": 1`,
  `bmsync/src/BookmarkSync.Domain/State.cs`). A mismatch refuses to start /
  rejects the upload — so release server + extension together whenever the
  protocol changes, and note it in the release.
- Tag releases in git (`v0.1.0`, …); the tag, the image tag, and
  `manifest.json` `version` should match.
