#!/usr/bin/env python3
"""Create/stop an owned SQL test container. Credentials stay in private runtime files."""
import argparse
import io
import json
import os
from pathlib import Path
import secrets
import sys
import socket
import subprocess
import tarfile
import tempfile
import time

IMAGE = "mcr.microsoft.com/mssql/server@sha256:cf8f0ab2ebe61feec2eed2629061a07d247f791c2a8ddda74a4ac9f06a291718"
ROOT = Path(__file__).resolve().parents[1]


def docker(*args, **kwargs):
    environment = os.environ.copy()
    for name in ("DOCKER_HOST", "DOCKER_CONTEXT", "DOCKER_TLS", "DOCKER_TLS_VERIFY", "DOCKER_CERT_PATH"):
        environment.pop(name, None)
    return subprocess.run(["docker", "--host=unix:///var/run/docker.sock", *args],
                          env=environment, check=True, **kwargs)


def private_json(path, value):
    with os.fdopen(os.open(path, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
        json.dump(value, output)


def startup_diagnostics(name, password, connection, last_probe):
    """Bounded diagnostics for this disposable server only; never dump its environment."""
    def safe(value):
        return value.replace(connection, "[connection redacted]").replace(password, "[password redacted]")

    print("Last SQL probe: " + safe(last_probe)[-2000:], file=sys.stderr)
    for arguments in [
        ("inspect", "--format", "status={{.State.Status}} exit={{.State.ExitCode}} oom={{.State.OOMKilled}}", name),
        ("logs", "--tail", "80", name),
    ]:
        try:
            result = docker(*arguments, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=15)
            # Redact before truncation so a truncation boundary cannot expose a partial secret.
            print(safe(result.stdout)[-16000:], file=sys.stderr)
        except (subprocess.SubprocessError, OSError) as error:
            print("Diagnostic command unavailable: " + type(error).__name__, file=sys.stderr)


def start(runtime_file):
    if runtime_file.is_relative_to(ROOT):
        raise SystemExit("Runtime credentials must remain outside the repository.")
    if runtime_file.exists():
        raise SystemExit("Runtime file already exists; preserve it or choose another path.")
    docker("info", "--format", "{{.ServerVersion}}")
    # Standard registry access, proxy and artifact digest checks remain enabled.
    docker("pull", IMAGE)
    directory = Path(tempfile.mkdtemp(prefix="hisabtak-sql-"))
    if directory.resolve().is_relative_to(ROOT):
        raise SystemExit("The temporary credential directory must remain outside the repository.")
    name = "hisabtak-sql-test-" + secrets.token_hex(6)
    password = "Aa1!" + secrets.token_urlsafe(32)
    environment_file = directory / "server.env"
    with os.fdopen(os.open(environment_file, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600), "w") as output:
        output.write("MSSQL_SA_PASSWORD=" + password + "\n")
    certificate = directory / "server.crt"
    key = directory / "server.key"
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-sha256", "-days", "7", "-nodes",
                    "-keyout", str(key), "-out", str(certificate), "-subj", "/CN=localhost",
                    "-addext", "subjectAltName=DNS:localhost,IP:127.0.0.1"],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    key.chmod(0o600)
    with socket.socket() as probe:
        probe.bind(("127.0.0.1", 0))
        port = probe.getsockname()[1]
    docker("create", "--name", name, "--publish", f"127.0.0.1:{port}:1433",
           "--memory", "3g", "--cpus", "2", "--env", "ACCEPT_EULA=Y", "--env", "MSSQL_PID=Developer",
           "--env-file", str(environment_file), IMAGE, stdout=subprocess.DEVNULL)
    connection = (f"Server=localhost,{port};Database=master;User ID=sa;Password={password};"
                  f"Encrypt=Strict;TrustServerCertificate=False;Server Certificate={certificate};Connect Timeout=2")
    runtime = {"container": name, "directory": str(directory), "image": IMAGE,
               "connection": connection, "server_data_directory": "/var/opt/mssql/data"}
    # Save before start so a failed startup remains recoverable without revealing credentials.
    private_json(runtime_file, runtime)
    config = ("[network]\ntlscert = /var/opt/mssql/tls/server.crt\n"
              "tlskey = /var/opt/mssql/tls/server.key\ntlsprotocols = 1.2\nforceencryption = 1\n")
    archive = io.BytesIO()
    with tarfile.open(fileobj=archive, mode="w") as tar:
        entry = tarfile.TarInfo("tls")
        entry.type, entry.mode, entry.uid, entry.gid = tarfile.DIRTYPE, 0o700, 10001, 0
        tar.addfile(entry)
        for filename, contents, mode in [("tls/server.crt", certificate.read_bytes(), 0o600),
                                         ("tls/server.key", key.read_bytes(), 0o600),
                                         ("mssql.conf", config.encode(), 0o600)]:
            entry = tarfile.TarInfo(filename)
            entry.size, entry.mode, entry.uid, entry.gid = len(contents), mode, 10001, 0
            tar.addfile(entry, io.BytesIO(contents))
    docker("cp", "--archive", "-", f"{name}:/var/opt/mssql", input=archive.getvalue())
    docker("start", name, stdout=subprocess.DEVNULL)
    environment = os.environ.copy()
    environment["HISABTAK_Database__ConnectionString"] = connection
    deadline = time.monotonic() + 60
    last_probe = "No probe completed."
    while time.monotonic() < deadline:
        try:
            result = subprocess.run(["dotnet", "run", "--project", "src/HisabTak.Tools", "--configuration", "Release",
                                     "--no-build", "--no-restore", "--", "probe"], cwd=ROOT, env=environment,
                                    stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, timeout=10)
        except subprocess.TimeoutExpired:
            last_probe = "Probe process exceeded its 10-second timeout."
            continue
        last_probe = result.stdout.replace(connection, "[connection redacted]").replace(password, "[password redacted]")
        if result.returncode == 0:
            print("SQL SELECT 1 passed with strict certificate verification.")
            print("Private runtime file:", runtime_file)
            return
        time.sleep(1)
    startup_diagnostics(name, password, connection, last_probe)
    raise SystemExit("SQL readiness failed. The owned container and private runtime file were preserved for diagnosis.")


def stop(runtime_file):
    runtime = json.loads(runtime_file.read_text())
    name = runtime["container"]
    if not name.startswith("hisabtak-sql-test-") or any(c not in "abcdefghijklmnopqrstuvwxyz0123456789-" for c in name):
        raise SystemExit("Refusing to stop a container not owned by this helper.")
    docker("rm", "--force", name, stdout=subprocess.DEVNULL)
    print("Owned test container removed. Private runtime files were preserved; do not reuse this stopped instance.")


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("action", choices=["start", "stop"])
    parser.add_argument("--runtime-file", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        (start if arguments.action == "start" else stop)(arguments.runtime_file.resolve())
    except subprocess.CalledProcessError as error:
        raise SystemExit(f"Setup command failed (exit {error.returncode}); credentials were not printed.") from None
