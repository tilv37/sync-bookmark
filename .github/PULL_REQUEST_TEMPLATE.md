# Pull request

## What changed and why

## Sync scenario tested

<!-- e.g. two profiles: A delete folder, sync both; clock-skew pass; or `./test/all.sh` layers run -->

## Device pair and state sizes

<!-- Firefox version(s), profiles vs machines, item counts before/after -->

## Checklist

- [ ] `./test/all.sh` passes (or note skipped layers and why)
- [ ] No protocol change, or server + extension updated together (`state.json` schema noted)
- [ ] HLC touched? Vectors regenerated (`BMSYNC_WRITE_VECTORS=1`) and both sides replay them
