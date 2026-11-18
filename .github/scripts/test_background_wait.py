"""Проверки запуска и уведомления для фоновых команд Windows."""

import json
import os
import subprocess
import sys
import tempfile
import time
import unittest
import uuid
from pathlib import Path
from typing import cast, override

SCRIPT = (
    Path(__file__).resolve().parents[2]
    / ".agents/skills/background-wait/scripts/background_wait.py"
)


@unittest.skipUnless(os.name == "nt", "Для проверки требуется Windows")
class BackgroundWaitTests(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.temp: tempfile.TemporaryDirectory[str] | None = None
        self.root: Path = Path()
        self.bin: Path = Path()
        self.queue: Path = Path()
        self.env: dict[str, str] = {}

    @override
    def setUp(self) -> None:
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.queue = self.bin / "codex.cmd"
        _ = self.queue.write_text("@echo off\r\necho %*\r\n", encoding="ascii")
        self.env = dict(os.environ)
        self.env["PATH"] = str(self.bin) + os.pathsep + self.env["PATH"]
        self.env["CODEX_HOME"] = str(self.root / "codex")
        self.env["CODEX_THREAD_ID"] = str(uuid.uuid4())

    def call(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [sys.executable, str(SCRIPT), *args],
            check=False,
            env=self.env,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
        )

    def start(self, source: str) -> Path:
        result = self.call(
            "start", "--cwd", str(self.root), "--", sys.executable, "-c", source
        )
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        response = cast(dict[str, str], json.loads(result.stdout))
        return Path(response["job_dir"])

    def wait_for(self, path: Path) -> dict[str, object]:
        deadline = time.monotonic() + 15
        while time.monotonic() < deadline:
            if path.exists():
                return cast(
                    dict[str, object], json.loads(path.read_text(encoding="utf-8"))
                )
            time.sleep(0.05)
        self.fail(f"Не появился файл {path.name}")

    def test_nonzero_exit_preserves_separate_output_and_queues_notice(self) -> None:
        job = self.start(
            "import sys; print('out-marker'); print('err-marker', file=sys.stderr); sys.exit(7)"
        )
        delivery = self.wait_for(job / "delivery.json")
        result = cast(
            dict[str, object],
            json.loads((job / "result.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(result["exit_code"], 7)
        self.assertIn(b"out-marker", (job / "stdout.log").read_bytes())
        self.assertIn(b"err-marker", (job / "stderr.log").read_bytes())
        self.assertEqual(delivery["state"], "queued")
        queued = cast(str, delivery["stdout"])
        self.assertIn("--resume", queued)
        self.assertIn(self.env["CODEX_THREAD_ID"], queued)
        self.assertIn(job.name, queued)
        self.assertNotIn("out-marker", queued)

    def test_cancel_suppresses_notice_without_terminating_command(self) -> None:
        job = self.start("import time; time.sleep(2); print('finished')")
        canceled = self.call("cancel", str(job))
        self.assertEqual(canceled.returncode, 0, canceled.stderr)
        self.assertEqual(self.wait_for(job / "delivery.json")["state"], "suppressed")
        self.assertEqual(
            json.loads((job / "result.json").read_text(encoding="utf-8"))["exit_code"],
            0,
        )
        self.assertIn(b"finished", (job / "stdout.log").read_bytes())

    def test_queue_failure_is_not_reported_as_delivery(self) -> None:
        _ = self.queue.write_text("@echo off\r\nexit /b 3\r\n", encoding="ascii")
        job = self.start("print('done')")
        delivery = self.wait_for(job / "delivery.json")
        self.assertEqual(delivery["state"], "uncertain")
        self.assertEqual(delivery["exit_code"], 3)

    def test_queue_warning_is_not_reported_as_confirmed_delivery(self) -> None:
        _ = self.queue.write_text(
            "@echo off\r\necho resume failed 1>&2\r\nexit /b 0\r\n", encoding="ascii"
        )
        job = self.start("print('done')")
        delivery = self.wait_for(job / "delivery.json")
        self.assertEqual(delivery["state"], "uncertain")
        self.assertEqual(delivery["exit_code"], 0)

    def test_invalid_thread_does_not_launch_command(self) -> None:
        self.env["CODEX_THREAD_ID"] = "not-a-uuid"
        marker = self.root / "marker"
        result = self.call(
            "start",
            "--cwd",
            str(self.root),
            "--",
            sys.executable,
            "-c",
            f"open({str(marker)!r}, 'w').close()",
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(marker.exists())

    def test_cancel_after_delivery_does_not_claim_suppression(self) -> None:
        job = self.start("print('done')")
        _ = self.wait_for(job / "delivery.json")
        result = self.call("cancel", str(job))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(json.loads(result.stdout)["state"], "already_finished")


if __name__ == "__main__":
    _ = unittest.main()
