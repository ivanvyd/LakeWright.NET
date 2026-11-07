#!/usr/bin/env python3
"""Exercise the trusted release workflow's public-index gate without publication."""

from contextlib import ExitStack, redirect_stdout
import io
import json
from pathlib import Path
import subprocess
import tempfile
import textwrap
import types
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts/nuget-availability-tests"
SOURCE = (ROOT / ".github/workflows/release.yml").read_text(encoding="utf-8")
STEP = SOURCE.split("      - name: Verify NuGet public availability\n", 1)[1].split("\n      - ", 1)[0]
SCRIPT = textwrap.dedent(STEP.split("        run: |\n", 1)[1])
CODE = SCRIPT.split("python3 - <<'PY'\n", 1)[1].rsplit("\nPY", 1)[0]
gate = types.ModuleType("nuget_availability")
exec(compile(CODE, "release.yml:Verify NuGet public availability", "exec"), gate.__dict__)


class AvailabilityTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(dir=ARTIFACTS)
        self.addCleanup(self.temporary.cleanup)
        self.directory = Path(self.temporary.name)
        self.index = self.directory / "index.json"
        self.now = 0
        self.calls = []

    def packages(self, *names):
        for name in names:
            (self.directory / name).touch()

    def transport(self, responses):
        def run(arguments, **options):
            self.calls.append((arguments, options))
            response = responses[min(len(self.calls) - 1, len(responses) - 1)]
            self.now += response.get("elapsed", 0)
            if response.get("timeout"):
                raise subprocess.TimeoutExpired(arguments, options["timeout"])
            destination = Path(arguments[arguments.index("--output") + 1])
            body = response.get("body", b'{"versions":["3.0.0"]}')
            destination.write_bytes(body)
            return subprocess.CompletedProcess(arguments, response.get("exit", 0))
        return run

    def wait(self, responses):
        def sleep(seconds):
            self.now += seconds

        with ExitStack() as stack:
            stack.enter_context(patch.object(gate.time, "monotonic", side_effect=lambda: self.now))
            stack.enter_context(patch.object(gate.time, "sleep", side_effect=sleep))
            stack.enter_context(patch.object(gate.subprocess, "run", side_effect=self.transport(responses)))
            output = stack.enter_context(redirect_stdout(io.StringIO()))
            gate.wait_for_packages(self.directory, "3.0.0")
        return output.getvalue()

    def test_schema_requires_an_object_and_an_array_of_version_strings(self):
        bodies = (
            (b'{"versions":["2.0.0","3.0.0"]}', True),
            (b'{"versions":[]}', False),
            (b'{"versions":["2.0.0"]}', False),
            (b'{"versions":"3.0.0"}', False),
            (b'{"versions":{"3.0.0":true}}', False),
            (b'{"versions":["3.0.0",42]}', False),
            (b'{"versions":null}', False),
            (b'{"versions":true}', False),
            (b'{}', False), (b'[]', False), (b'null', False),
            (b'{"versions":["3.0.0"]', False),
            (b'{"versions":[],"versions":["3.0.0"]}', False),
            (b'{"versions":["3.0.0"],"other":NaN}', False),
            (b'\xff', False),
        )
        for body, expected in bodies:
            with self.subTest(body=body), patch.object(gate.subprocess, "run", side_effect=self.transport([{"body": body}])):
                self.calls.clear()
                self.assertEqual(expected, gate.available("example", "3.0.0", self.index, 15))

    def test_response_size_is_bounded_even_if_transport_ignores_its_limit(self):
        body = json.dumps({"versions": ["3.0.0"], "padding": "x" * gate.MAX_BYTES}).encode()
        with patch.object(gate.subprocess, "run", side_effect=self.transport([{"body": body}])):
            self.assertFalse(gate.available("example", "3.0.0", self.index, 15))

    def test_requests_have_https_byte_connect_and_total_time_bounds(self):
        with patch.object(gate.subprocess, "run", side_effect=self.transport([{}])):
            self.assertTrue(gate.available("example", "3.0.0", self.index, 2))
        arguments, options = self.calls[0]
        self.assertEqual("=https", arguments[arguments.index("--proto") + 1])
        self.assertEqual(str(gate.MAX_BYTES), arguments[arguments.index("--max-filesize") + 1])
        self.assertEqual("2", arguments[arguments.index("--connect-timeout") + 1])
        self.assertEqual("2", arguments[arguments.index("--max-time") + 1])
        self.assertEqual(2, options["timeout"])
        self.assertEqual("https://api.nuget.org/v3-flatcontainer/example/index.json", arguments[-1])

    def test_transport_errors_and_timeouts_are_not_availability(self):
        for response in ({"exit": 22}, {"exit": 28}, {"timeout": True}):
            with self.subTest(response=response), patch.object(gate.subprocess, "run", side_effect=self.transport([response])):
                self.calls.clear()
                self.assertFalse(gate.available("example", "3.0.0", self.index, 15))

    def test_all_packages_must_be_present_before_success(self):
        self.packages("Example.One.3.0.0.nupkg", "Example.Two.3.0.0.nupkg")
        output = self.wait([{}, {"body": b'{"versions":[]}'}, {}, {}])
        self.assertEqual(4, len(self.calls))
        self.assertEqual(60, self.now)
        self.assertIn("Waiting for NuGet indexing of: example.two", output)
        self.assertIn("All 2 packages are publicly available", output)

    def test_prerelease_package_identity_and_index_are_supported(self):
        self.packages("Example.Package.3.0.1-rc.1.nupkg")
        with patch.object(gate.subprocess, "run", side_effect=self.transport([
            {"body": b'{"versions":["3.0.0","3.0.1-rc.1"]}'},
        ])), redirect_stdout(io.StringIO()) as output:
            gate.wait_for_packages(self.directory, "3.0.1-rc.1")
        self.assertEqual(1, len(self.calls))
        self.assertEqual("https://api.nuget.org/v3-flatcontainer/example.package/index.json", self.calls[0][0][-1])
        self.assertIn("All 1 packages are publicly available", output.getvalue())

    def test_transient_errors_and_malformed_responses_can_recover(self):
        self.packages("Example.3.0.0.nupkg")
        output = self.wait([{"exit": 22}, {"timeout": True}, {"body": b"bad json"}, {}])
        self.assertEqual(4, len(self.calls))
        self.assertEqual(180, self.now)
        self.assertIn("All 1 packages are publicly available", output)

    def test_deadline_exhaustion_cannot_succeed_with_a_partial_set(self):
        self.packages("Example.One.3.0.0.nupkg", "Example.Two.3.0.0.nupkg")
        with self.assertRaisesRegex(TimeoutError, "example.two"):
            self.wait([{}, {"body": b'{"versions":[]}', "elapsed": 3600}])
        self.assertEqual(3600, self.now)
        self.assertEqual(2, len(self.calls))

    def test_request_time_counts_against_the_overall_deadline(self):
        self.packages("Example.3.0.0.nupkg")
        with self.assertRaisesRegex(TimeoutError, "one hour"):
            self.wait([{"exit": 28, "elapsed": 15}])
        self.assertEqual(3600, self.now)
        self.assertEqual(48, len(self.calls))

    def test_no_request_or_success_after_deadline(self):
        self.packages("Example.One.3.0.0.nupkg", "Example.Two.3.0.0.nupkg")
        with self.assertRaisesRegex(TimeoutError, "example.two"):
            self.wait([{"elapsed": 3600}])
        self.assertEqual(1, len(self.calls))

    def test_empty_wrong_version_or_invalid_artifacts_fail_before_network(self):
        fixtures = ((), ("Example.2.0.0.nupkg",), (".3.0.0.nupkg",),
                    ("Example package.3.0.0.nupkg",))
        for names in fixtures:
            with self.subTest(names=names), tempfile.TemporaryDirectory(dir=ARTIFACTS) as temporary:
                directory = Path(temporary)
                for name in names:
                    (directory / name).touch()
                with patch.object(gate.subprocess, "run", side_effect=AssertionError("Invalid artifacts reached the network")) as request, self.assertRaises(ValueError):
                    gate.wait_for_packages(directory, "3.0.0")
                request.assert_not_called()

    def test_case_insensitive_package_identity_collisions_are_rejected(self):
        self.packages("Example.3.0.0.nupkg")
        first = self.directory / "Example.3.0.0.nupkg"
        second = self.directory / "example.3.0.0.nupkg"
        with patch.object(Path, "glob", return_value=[first, second]), \
                patch.object(Path, "is_file", return_value=True), self.assertRaises(ValueError):
            gate.package_ids(self.directory, "3.0.0")

    def test_directories_cannot_masquerade_as_packages(self):
        (self.directory / "Example.3.0.0.nupkg").mkdir()
        with self.assertRaises(ValueError):
            gate.package_ids(self.directory, "3.0.0")

    def test_symlink_artifacts_are_rejected(self):
        self.packages("Example.3.0.0.nupkg")
        with patch.object(Path, "is_symlink", return_value=True), self.assertRaisesRegex(ValueError, "Invalid publication artifact"):
            gate.package_ids(self.directory, "3.0.0")

    def test_the_publisher_executes_only_trusted_workflow_code(self):
        publish = SOURCE.split("  publish:\n", 1)[1]
        self.assertNotIn("actions/checkout", publish)
        self.assertNotIn("python3 scripts/", publish)
        self.assertLess(publish.index("Verify NuGet public availability"), publish.index("Create GitHub release"))


if __name__ == "__main__":
    unittest.main()
