# Frozen v1 inputs

These six inputs were exported once from the pre-remediation production v1 codec and a controlled production-provider runtime.
Tests verify their SHA256 manifest; they never regenerate them or read live Def XML as a substitute.

- ready: three committed bodies with known geometry and GM.
- pending: two allocated bodies with uncommitted registration and its pending receipt.
- unknown: opaque missing-handler configuration/state bytes.
- retired-map: minimal save-codec/native identity record, with an applied request and a retired parent. Full native map restoration is exercised separately by the existing integration fixtures.
- malformed and unsupported: explicit format rejection inputs.

The authoring command is `dotnet run --project tools/CelestialBenchmark -- --scenario fixtures --output <empty-authoring-directory>`.
It refuses to overwrite a populated directory. Changes to these files require an explicit fixture review and manifest update.
