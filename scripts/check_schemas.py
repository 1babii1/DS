#!/usr/bin/env python3
"""Compatibility gate for event schemas (ADR 0023).

Registers the schemas as they are on the base ref, in order, then the ones in the working tree, into a schema registry
(Apicurio through its Confluent-compatible API) whose subjects are set to BACKWARD_TRANSITIVE. A schema that cannot be
registered is a breaking change and fails the run. The registry alone allows removing a field (a new reader simply
ignores it), but the rule here is "deprecate, never remove", so removal is checked separately.

    scripts/check_schemas.py --registry http://localhost:8081/apis/ccompat/v7 --base origin/main

A fresh registry is expected (CI starts one per run). Standard library only.
"""
import argparse
import json
import subprocess
import sys
import urllib.error
import urllib.request
from pathlib import Path

HEADERS = {"Content-Type": "application/vnd.schemaregistry.v1+json"}

# Producer-owned schemas live next to the event records of the service that publishes them; the topic is the service's.
PRODUCERS = {
    "DirectoryService": ("directory.events.v2", "ds.directory"),
    "EmployeeService": ("employee.events.v2", "ds.employee"),
    "AuthService": ("auth.events.v2", "ds.auth"),
    "RewardsService": ("rewards.events.v2", "ds.rewards"),
    # Events every service can publish (the operator redrive audit event); registered under one synthetic topic here.
    "Shared": ("ops.events.v2", "ds.ops"),
}
PRODUCER_DIR = "IntegrationEvents/Schemas/"
READER_DIR = "Consumers/Schemas/"


def call(registry, method, path, body=None):
    request = urllib.request.Request(
        registry + path, method=method, headers=HEADERS, data=None if body is None else json.dumps(body).encode()
    )
    try:
        with urllib.request.urlopen(request, timeout=30) as response:
            return response.status, response.read().decode()
    except urllib.error.HTTPError as error:
        return error.code, error.read().decode()


def register(registry, subject, schema_text):
    """Returns (ok, message). Sets the subject's compatibility first; idempotent."""
    status, text = call(registry, "PUT", f"/config/{subject}", {"compatibility": "BACKWARD_TRANSITIVE"})
    if status != 200:
        return False, f"could not set compatibility on {subject}: {status} {text}"
    status, text = call(registry, "POST", f"/subjects/{subject}/versions", {"schema": schema_text})
    if status == 200:
        return True, ""
    try:
        message = json.loads(text).get("message", text)
    except ValueError:
        message = text
    return False, f"{status} {message}"


def git(*args):
    return subprocess.run(["git", *args], check=True, capture_output=True, text=True).stdout


def producer_of(path):
    """(service, topic, namespace) for a producer schema path, or None."""
    parts = Path(path).parts
    for service, (topic, namespace) in PRODUCERS.items():
        if service in parts and PRODUCER_DIR in path.replace("\\", "/"):
            return service, topic, namespace
    return None


def lint(path, schema):
    """Structural rules that do not need a registry. Returns a list of problems."""
    problems = []
    norm = path.replace("\\", "/")
    if PRODUCER_DIR not in norm and READER_DIR not in norm:
        problems.append(f"{path}: schemas belong in .../{PRODUCER_DIR} (producer) or .../{READER_DIR} (reader)")
        return problems
    if schema.get("type") != "record":
        problems.append(f"{path}: an event schema must be a record")
    if Path(path).stem != schema.get("name"):
        problems.append(f"{path}: file name must equal the record name '{schema.get('name')}'")
    owner = producer_of(path)
    if owner and schema.get("namespace") != owner[2]:
        problems.append(f"{path}: namespace must be '{owner[2]}' for {owner[0]}, found '{schema.get('namespace')}'")
    return problems


def topic_of_namespace(namespace):
    for _, (topic, ns) in PRODUCERS.items():
        if ns == namespace:
            return topic
    return None


def check_reader(registry, path, schema):
    """A consumer's reader schema must be able to read what the producer's latest schema writes (Avro resolution)."""
    topic = topic_of_namespace(schema.get("namespace"))
    if topic is None:
        return [f"{path}: namespace '{schema.get('namespace')}' is not a producer's namespace"]
    subject = f"{topic}-{schema['namespace']}.{schema['name']}"
    status, text = call(registry, "POST", f"/compatibility/subjects/{subject}/versions/latest", {"schema": json.dumps(schema)})
    if status == 404:
        return [f"{path}: no producer schema is registered as {subject}; a consumer cannot read an event nobody publishes"]
    if status != 200:
        return [f"{path}: compatibility check failed with {status} {text}"]
    if not json.loads(text).get("is_compatible", False):
        return [f"{path}: this reader schema cannot read what the producer writes ({subject}); a field it needs has no default in the producer's schema, or its type differs"]
    return []


def removed_fields(base_schema, new_schema):
    new_names = {field["name"] for field in new_schema.get("fields", [])}
    return [field["name"] for field in base_schema.get("fields", []) if field["name"] not in new_names]


def self_test(registry):
    """The gate must reject a breaking change; a gate that passes everything is worse than none."""
    subject = "selftest.events-ds.selftest.Breaking"
    v1 = {"type": "record", "name": "Breaking", "namespace": "ds.selftest",
          "fields": [{"name": "Id", "type": "string"}]}
    v2 = {"type": "record", "name": "Breaking", "namespace": "ds.selftest",
          "fields": [{"name": "Id", "type": "string"}, {"name": "Required", "type": "int"}]}
    ok, message = register(registry, subject, json.dumps(v1))
    if not ok:
        return [f"self-test could not register its baseline: {message}"]
    ok, _ = register(registry, subject, json.dumps(v2))
    if ok:
        return ["self-test: the registry ACCEPTED a field added without a default; the compatibility gate is not working"]
    return []


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--registry", default="http://localhost:8081/apis/ccompat/v7")
    parser.add_argument("--base", default="origin/main", help="git ref holding the schemas already released")
    args = parser.parse_args()
    root = Path(git("rev-parse", "--show-toplevel").strip())
    problems = []

    status, _ = call(args.registry, "GET", "/subjects")
    if status != 200:
        print(f"registry not reachable at {args.registry} ({status})", file=sys.stderr)
        return 2

    problems += self_test(args.registry)

    current = {str(p.relative_to(root)).replace("\\", "/"): json.loads(p.read_text(encoding="utf-8"))
               for p in root.glob("backend/**/*.avsc") if "/obj/" not in str(p) and "/bin/" not in str(p)}
    try:
        base_files = [f for f in git("ls-tree", "-r", "--name-only", args.base, "backend").splitlines() if f.endswith(".avsc")]
    except subprocess.CalledProcessError:
        base_files = []
        print(f"note: base ref {args.base} not found, treating every schema as new")
    base = {f: json.loads(git("show", f"{args.base}:{f}")) for f in base_files}

    for path, schema in current.items():
        problems += lint(path, schema)
    for path, schema in base.items():
        if path in current:
            removed = removed_fields(schema, current[path])
            if removed:
                problems.append(
                    f"{path}: field(s) removed: {', '.join(removed)}. Deprecate instead: keep the field with a default "
                    "and doc 'Deprecated' (ADR 0023)")
        else:
            problems.append(f"{path}: a released schema was deleted or renamed; event types are never removed")

    # Only producer schemas go to the registry in this step; reader schemas are checked against them elsewhere.
    for label, files in (("released", base), ("proposed", current)):
        for path, schema in sorted(files.items()):
            owner = producer_of(path)
            if not owner:
                continue
            subject = f"{owner[1]}-{schema['namespace']}.{schema['name']}"
            ok, message = register(args.registry, subject, json.dumps(schema))
            if not ok:
                problems.append(f"{path} ({label}, subject {subject}): {message}")

    # Reader schemas of consumers, against the producers' latest (registered above).
    readers = {p: s for p, s in current.items() if READER_DIR in p.replace("\\", "/")}
    for path, schema in sorted(readers.items()):
        problems += check_reader(args.registry, path, schema)

    checked = sum(1 for p in current if producer_of(p))
    if problems:
        print(f"FAIL: {len(problems)} problem(s) in {checked} producer schema(s)")
        for problem in problems:
            print(f"  - {problem}")
        return 1
    print(f"OK: {len(readers)} reader schema(s) read what the producers write; {checked} producer schema(s) are BACKWARD_TRANSITIVE-compatible with everything released on {args.base}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
