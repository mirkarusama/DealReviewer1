#!/usr/bin/env bash
# Runs the regression cases in tests/cases.json against FileReaderTool/DealReview_code.
#   tests/run.sh            compare with tests/expected (exit code 1 on any difference)
#   tests/run.sh --update   rewrite tests/expected from this run (review the git diff before committing)
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"
dotnet run --project "$here/Regression/Regression.csproj" -c Release -v q -- "$(dirname "$here")" "$@"
