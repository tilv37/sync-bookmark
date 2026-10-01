# RELEASE

## Extension package + AMO signing

For daily use, package and sign the extension (`extension/manifest.json`,
currently `0.1.0`):

```bash
cd extension && rm -f bmsync.xpi bmsync.zip && zip -r ../bmsync.xpi * \
  -x 'lib/*.test.js' 'lib/mock-bookmarks.js' '*.zip' '*.xpi'
```

Test files and the `browser.bookmarks` mock are dev-only (no production file
imports them); excluding
them keeps the reviewed package to exactly what runs. Always delete a
previous zip first: `zip -r` would otherwise nest the old archive inside
the new one.

Then submit `bmsync.xpi` for signing at
[addons.mozilla.org](https://addons.mozilla.org) (AMO): self-distribution
(signed but unlisted) fits this single-user tool; review times apply to listed
versions. Bump `manifest.json` `version` for every signed upload, and keep the
gecko `id` stable so updates apply cleanly. Temporary `about:debugging` loads
stay unsigned for development.

New AMO submissions must declare data collection in the manifest
(`browser_specific_settings.gecko.data_collection_permissions`, mandatory
since Nov 2025) — the upload is rejected without it. This extension declares
`"required": ["bookmarksInfo"]`: the full bookmark tree (names, URLs, folder
names) is transmitted to the user's own server, which is the extension's
stated purpose. `test/check-extension.sh` §10 guards this declaration.

The zip includes three icon files derived from the `extension/icon.png` source
(600×600): `icon-48.png` / `icon-96.png` are the manifest-declared toolbar and
listing icons (exact pixel sizes — AMO rejects mismatches), while `icon.png`
feeds the popup/options headers and the README logo. Regenerate the sized
copies after any artwork change (Pillow LANCZOS downscale is fine).

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
