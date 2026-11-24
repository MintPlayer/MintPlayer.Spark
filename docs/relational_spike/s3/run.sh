#!/usr/bin/env bash
# Runs the S3 sequence: for each model version, scaffold, rebuild (dotnet run builds), verify; then a no-change gen.
cd "$(dirname "$0")"
export SPARK536_PG_PASSWORD="$(grep -o 'Password=[^;"]*' /c/Repos/MintPlayer.Spark/docs/relational_spike/Program.cs | head -1 | cut -d= -f2)"
: > run.log
dotnet run -- reset >> run.log 2>&1
for n in 1 2 3 4 5 6; do
  echo "=== gen $n" >> run.log
  dotnet run -- gen $n >> run.log 2>&1
  echo "=== verify $n" >> run.log
  dotnet run -- verify $n >> run.log 2>&1
done
echo "=== gen 6 again (no change)" >> run.log
dotnet run -- gen 6 >> run.log 2>&1
echo "DONE" >> run.log
