#!/usr/bin/env python3
"""Run real SQL integration tests using a private runtime file, never command-line credentials."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("runtime_file", type=Path)
arguments = parser.parse_args()
if os.name != "nt" and arguments.runtime_file.stat().st_mode & 0o077:
    raise SystemExit("Runtime credentials must be readable only by their owner.")
runtime = json.loads(arguments.runtime_file.read_text())
environment = os.environ.copy()
environment["HISABTAK_TEST_SQL_CONNECTION"] = runtime["connection"]
environment["HISABTAK_TEST_SQL_DATA_DIRECTORY"] = runtime["server_data_directory"]
report = ROOT / "tests/HisabTak.Infrastructure.Tests/TestResults/sql-integration.trx"
previous = (report.stat().st_mtime_ns, report.stat().st_size) if report.exists() else None
result = subprocess.run(["dotnet", "test", "tests/HisabTak.Infrastructure.Tests", "--configuration", "Release",
                         "--no-build", "--no-restore", "--filter", "FullyQualifiedName~SqlServerIntegrationTests",
                         "--logger", "trx;LogFileName=sql-integration.trx"], cwd=ROOT, env=environment)
if result.returncode:
    raise SystemExit(result.returncode)
if not report.exists() or (report.stat().st_mtime_ns, report.stat().st_size) == previous:
    raise SystemExit("No fresh SQL integration report was produced.")
counts = ET.parse(report).find(".//t:Counters", {"t": "http://microsoft.com/schemas/VisualStudio/TeamTest/2010"}).attrib
if int(counts["total"]) == 0 or int(counts["executed"]) != int(counts["total"]) or int(counts["failed"]) != 0:
    raise SystemExit("SQL integration did not execute every selected test successfully.")
print("All selected SQL integration tests executed and passed.")
