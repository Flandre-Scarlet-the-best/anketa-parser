"""Проверки запуска и уведомления для фоновых команд Windows."""

import argparse
import importlib.util
import io
import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import types
import unittest
import uuid
from pathlib import Path
from typing import BinaryIO, Protocol, cast, override
from unittest import mock

SCRIPT = (
    Path(__file__).resolve().parents[2]
    / ".agents/skills/background-wait/scripts/background_wait.py"
)
spec = importlib.util.spec_from_file_location("background_wait", SCRIPT)
if spec is None or spec.loader is None:
    raise RuntimeError("Не удалось загрузить проверяемый модуль")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)


class BackgroundWaitModule(Protocol):
    def start(self, args: argparse.Namespace) -> int: ...
    def worker(self, directory: Path) -> int: ...
    def start_tray_indicator(self, command_pid: int, job_id: str) -> None: ...
    def find_executable(self, command: str, allow_unc: bool = False) -> str | None: ...
    def write_json(self, path: Path, data: dict[str, object]) -> None: ...
    def copy_bounded(
        self, source: BinaryIO, destination: BinaryIO, status: dict[str, object]
    ) -> None: ...


background_wait = cast(BackgroundWaitModule, cast(object, module))


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
        self.assertFalse(result["stdout_truncated"])
        self.assertFalse(result["stderr_truncated"])
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

    def test_cancel_rejects_foreign_directory_without_overwriting(self) -> None:
        foreign = self.root / "foreign"
        foreign.mkdir()
        _ = (foreign / "request.json").write_text("{}", encoding="utf-8")
        existing = foreign / "cancel.json"
        _ = existing.write_text("valuable", encoding="utf-8")
        result = self.call("cancel", str(foreign))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(existing.read_text(encoding="utf-8"), "valuable")

    def test_start_records_the_resolved_executable(self) -> None:
        result = self.call(
            "start", "--cwd", str(self.root), "--", "python", "-c", "print('ok')"
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        job = Path(cast(dict[str, str], json.loads(result.stdout))["job_dir"])
        request = cast(
            dict[str, object],
            json.loads((job / "request.json").read_text(encoding="utf-8")),
        )
        command = cast(list[str], request["command"])
        _ = self.wait_for(job / "delivery.json")
        self.assertEqual(
            Path(command[0]).resolve(),
            Path(cast(str, shutil.which("python"))).resolve(),
        )

    def test_unc_path_is_rejected_before_network_access(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=r"\\server\share",
            command=[sys.executable],
        )
        with (
            mock.patch.object(
                Path, "is_dir", side_effect=AssertionError("network access")
            ),
            self.assertRaises(ValueError),
        ):
            _ = background_wait.start(args)

    def test_explicit_unc_authorization_preserves_the_path(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=r"\\server\share",
            command=[sys.executable],
            allow_unc=True,
        )

        def spawn(command: list[str], **_kwargs: object) -> None:
            background_wait.write_json(Path(command[-1]) / "started.json", {"pid": 1})

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(Path, "is_dir", return_value=True),
            mock.patch.object(subprocess, "Popen", side_effect=spawn),
            mock.patch("sys.stdout", new_callable=io.StringIO) as output,
        ):
            self.assertEqual(background_wait.start(args), 0)
        job = Path(cast(dict[str, str], json.loads(output.getvalue()))["job_dir"])
        request = cast(
            dict[str, object],
            json.loads((job / "request.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(request["cwd"], "\\\\server\\share\\")

    def test_command_unc_path_is_rejected_before_network_access(self) -> None:
        for command in (r"\\server\share\tool.exe", "//server/share/tool.exe"):
            with (
                self.subTest(command=command),
                mock.patch.object(
                    shutil, "which", side_effect=AssertionError("network access")
                ),
                self.assertRaises(ValueError),
            ):
                _ = background_wait.start(
                    argparse.Namespace(
                        thread=self.env["CODEX_THREAD_ID"],
                        cwd=str(self.root),
                        command=[command],
                    )
                )

    def test_unc_path_entry_is_not_probed_without_authorization(self) -> None:
        resolve = Path.resolve

        def guarded(self: Path) -> Path:
            if str(self).replace("/", "\\").startswith("\\\\"):
                raise AssertionError("network access")
            return resolve(self)

        for entry in (r"\\server\share", "//server/share"):
            with (
                self.subTest(entry=entry),
                mock.patch.object(os, "get_exec_path", return_value=[entry]),
                mock.patch.object(Path, "resolve", guarded),
            ):
                self.assertIsNone(background_wait.find_executable("tool.exe"))

    def test_explicit_unc_command_authorization_preserves_the_path(self) -> None:
        command = r"\\server\share\tool.exe"
        with mock.patch.object(shutil, "which", return_value=command):
            self.assertEqual(background_wait.find_executable(command, True), command)

    def test_start_prioritizes_result_over_started_marker(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=str(self.root),
            command=[sys.executable],
        )

        def spawn(command: list[str], **_kwargs: object) -> None:
            job = Path(command[-1])
            background_wait.write_json(job / "started.json", {"pid": 1})
            background_wait.write_json(
                job / "result.json", {"state": "completed", "exit_code": 0}
            )

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(subprocess, "Popen", side_effect=spawn),
            mock.patch("sys.stdout", new_callable=io.StringIO) as output,
        ):
            self.assertEqual(background_wait.start(args), 0)
        self.assertEqual(json.loads(output.getvalue())["state"], "completed")

    def test_start_propagates_worker_start_failure(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=str(self.root),
            command=[sys.executable],
        )

        def spawn(command: list[str], **_kwargs: object) -> None:
            background_wait.write_json(
                Path(command[-1]) / "result.json", {"state": "start_failed"}
            )

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(subprocess, "Popen", side_effect=spawn),
            mock.patch("sys.stdout", new_callable=io.StringIO) as output,
        ):
            self.assertEqual(background_wait.start(args), 1)
        self.assertEqual(json.loads(output.getvalue())["state"], "start_failed")

    def test_worker_waits_when_started_metadata_write_fails(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        background_wait.write_json(
            job / "request.json",
            {
                "thread": self.env["CODEX_THREAD_ID"],
                "cwd": str(self.root),
                "command": [sys.executable],
                "codex": str(self.queue),
            },
        )
        real_write = background_wait.write_json
        events: list[str] = []
        wait = mock.Mock(side_effect=lambda: events.append("wait") or 0)
        process = types.SimpleNamespace(
            pid=123, wait=wait, stdout=io.BytesIO(), stderr=io.BytesIO()
        )

        def write(path: Path, data: dict[str, object]) -> None:
            if path.name == "started.json":
                raise OSError("metadata failure")
            real_write(path, data)

        def queue(
            *_args: object, **_kwargs: object
        ) -> subprocess.CompletedProcess[str]:
            events.append("queue")
            return subprocess.CompletedProcess([], 0, "queued", "")

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator") as indicator,
            mock.patch.object(background_wait, "write_json", side_effect=write),
            mock.patch.object(subprocess, "Popen", return_value=process),
            mock.patch.object(
                subprocess,
                "run",
                side_effect=queue,
            ),
        ):
            _ = background_wait.worker(job)
        wait.assert_called_once()
        indicator.assert_called_once_with(123, job.name)
        result = cast(
            dict[str, object],
            json.loads((job / "result.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(result["state"], "completed")
        self.assertEqual(result["started_error"], "metadata failure")
        self.assertEqual(events, ["wait", "queue"])

    def test_queue_timeout_is_uncertain(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        background_wait.write_json(
            job / "request.json",
            {
                "thread": self.env["CODEX_THREAD_ID"],
                "cwd": str(self.root),
                "command": [sys.executable],
                "codex": str(self.queue),
            },
        )
        process = types.SimpleNamespace(
            pid=123,
            wait=mock.Mock(return_value=0),
            stdout=io.BytesIO(),
            stderr=io.BytesIO(),
        )
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator"),
            mock.patch.object(subprocess, "Popen", return_value=process),
            mock.patch.object(
                subprocess,
                "run",
                side_effect=subprocess.TimeoutExpired("codex", 60),
            ),
        ):
            _ = background_wait.worker(job)
        delivery = cast(
            dict[str, object],
            json.loads((job / "delivery.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(delivery["state"], "uncertain")

    def test_tray_launch_error_does_not_interrupt_worker(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        background_wait.write_json(
            job / "request.json",
            {
                "thread": self.env["CODEX_THREAD_ID"],
                "cwd": str(self.root),
                "command": [sys.executable],
                "codex": str(self.queue),
            },
        )
        process = types.SimpleNamespace(
            pid=123,
            wait=mock.Mock(return_value=0),
            stdout=io.BytesIO(),
            stderr=io.BytesIO(),
        )
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(
                background_wait, "find_executable", return_value=sys.executable
            ),
            mock.patch.object(
                subprocess, "Popen", side_effect=[process, OSError("tray unavailable")]
            ) as spawn,
            mock.patch.object(
                subprocess,
                "run",
                return_value=subprocess.CompletedProcess([], 0, "queued", ""),
            ),
        ):
            self.assertEqual(background_wait.worker(job), 0)
        self.assertEqual(spawn.call_count, 2)
        self.assertEqual(
            json.loads((job / "result.json").read_text(encoding="utf-8"))["exit_code"],
            0,
        )
        self.assertEqual(
            json.loads((job / "delivery.json").read_text(encoding="utf-8"))["state"],
            "queued",
        )

    def test_tray_process_exits_after_command_finishes(self) -> None:
        powershell = shutil.which("powershell.exe")
        self.assertIsNotNone(powershell)
        tray_script = SCRIPT.with_name("tray_indicator.ps1")
        with (
            subprocess.Popen(  # noqa: S603 - изолированная проверочная команда
                [sys.executable, "-c", "import time; time.sleep(3)"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ) as command,
            subprocess.Popen(  # noqa: S603 - запуск проверяемого индикатора
                [
                    cast(str, powershell),
                    "-NoProfile",
                    "-NonInteractive",
                    "-STA",
                    "-WindowStyle",
                    "Hidden",
                    "-File",
                    str(tray_script),
                    str(command.pid),
                    str(os.getpid()),
                    str(uuid.uuid4()),
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ) as tray,
        ):
            time.sleep(1.5)
            self.assertIsNone(tray.poll(), "Индикатор завершился до команды")
            self.assertEqual(command.wait(timeout=8), 0)
            stdout, stderr = tray.communicate(timeout=8)
            self.assertEqual(tray.returncode, 0, repr((stdout, stderr)))

    def test_tray_process_exits_if_watcher_dies(self) -> None:
        powershell = shutil.which("powershell.exe")
        self.assertIsNotNone(powershell)
        with (
            subprocess.Popen(  # noqa: S603 - изолированная проверочная команда
                [sys.executable, "-c", "import time; time.sleep(8)"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ) as command,
            subprocess.Popen(  # noqa: S603 - изолированный наблюдатель
                [sys.executable, "-c", "import time; time.sleep(3)"],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.DEVNULL,
                stderr=subprocess.DEVNULL,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ) as watcher,
            subprocess.Popen(  # noqa: S603 - запуск проверяемого индикатора
                [
                    cast(str, powershell),
                    "-NoProfile",
                    "-NonInteractive",
                    "-STA",
                    "-WindowStyle",
                    "Hidden",
                    "-File",
                    str(SCRIPT.with_name("tray_indicator.ps1")),
                    str(command.pid),
                    str(watcher.pid),
                    str(uuid.uuid4()),
                ],
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                creationflags=subprocess.CREATE_NO_WINDOW,
            ) as tray,
        ):
            time.sleep(1)
            self.assertIsNone(tray.poll())
            self.assertEqual(watcher.wait(timeout=8), 0)
            stdout, stderr = tray.communicate(timeout=8)
            self.assertEqual(tray.returncode, 0, repr((stdout, stderr)))
            self.assertIsNone(command.poll())

    def test_worker_rejects_foreign_uuid_directory_before_writing(self) -> None:
        job = self.root / str(uuid.uuid4())
        job.mkdir()
        log = job / "stdout.log"
        _ = log.write_text("valuable", encoding="utf-8")
        result = self.call("worker", str(job))
        self.assertNotEqual(result.returncode, 0)
        self.assertNotEqual(self.call("cleanup", str(job)).returncode, 0)
        self.assertEqual(log.read_text(encoding="utf-8"), "valuable")
        self.assertEqual({p.name for p in job.iterdir()}, {"stdout.log"})

    def test_implicit_lookup_ignores_current_directory_for_command_and_codex(
        self,
    ) -> None:
        _ = (self.root / "codex.cmd").write_text(
            "@echo off\r\nexit /b 44\r\n", encoding="ascii"
        )
        _ = shutil.copy2(sys.executable, self.root / "python.exe")
        self.env["PATH"] = os.pathsep.join(
            (str(self.root), str(self.bin), str(Path(sys.executable).parent))
        )
        result = subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [
                sys.executable,
                str(SCRIPT),
                "start",
                "--cwd",
                str(self.root),
                "--",
                "python",
                "-c",
                "print('ok')",
            ],
            cwd=self.root,
            env=self.env,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        job = Path(cast(dict[str, str], json.loads(result.stdout))["job_dir"])
        request = cast(
            dict[str, object],
            json.loads((job / "request.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(
            Path(cast(list[str], request["command"])[0]).resolve(),
            Path(sys.executable).resolve(),
        )
        self.assertEqual(Path(cast(str, request["codex"])), self.queue)
        self.assertEqual(self.wait_for(job / "delivery.json")["state"], "queued")

    def test_explicit_absolute_executable_in_current_directory_is_allowed(self) -> None:
        result = subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [
                sys.executable,
                str(SCRIPT),
                "start",
                "--cwd",
                str(self.root),
                "--",
                sys.executable,
                "-c",
                "print('explicit')",
            ],
            cwd=Path(sys.executable).parent,
            env=self.env,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr + result.stdout)
        job = Path(cast(dict[str, str], json.loads(result.stdout))["job_dir"])
        request = cast(
            dict[str, object],
            json.loads((job / "request.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(
            Path(cast(list[str], request["command"])[0]), Path(sys.executable)
        )
        self.assertEqual(self.wait_for(job / "delivery.json")["state"], "queued")

    def test_lookup_does_not_fall_back_to_current_directory(self) -> None:
        marker = self.root / "executed"
        _ = (self.root / "codex.cmd").write_text(
            f'@echo off\r\necho bad > "{marker}"\r\n', encoding="utf-8"
        )
        self.env["PATH"] = str(self.root)
        result = subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [
                sys.executable,
                str(SCRIPT),
                "start",
                "--cwd",
                str(self.root),
                "--",
                sys.executable,
            ],
            cwd=self.root,
            env=self.env,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(marker.exists())
        self.assertFalse((Path(self.env["CODEX_HOME"]) / "background-wait").exists())

    def test_command_lookup_does_not_fall_back_to_current_directory(self) -> None:
        _ = shutil.copy2(sys.executable, self.root / "python.exe")
        self.env["PATH"] = os.pathsep.join((str(self.root), str(self.bin)))
        result = subprocess.run(  # noqa: S603 - запуск проверяемого локального сценария
            [
                sys.executable,
                str(SCRIPT),
                "start",
                "--cwd",
                str(self.root),
                "--",
                "python",
            ],
            cwd=self.root,
            env=self.env,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
            check=False,
        )
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse((Path(self.env["CODEX_HOME"]) / "background-wait").exists())

    def test_worker_and_cleanup_reject_junction_job(self) -> None:
        root = Path(self.env["CODEX_HOME"]) / "background-wait"
        root.mkdir(parents=True)
        target = self.root / "target"
        target.mkdir()
        marker = target / "stdout.log"
        _ = marker.write_text("valuable", encoding="utf-8")
        job = root / str(uuid.uuid4())
        result = subprocess.run(  # noqa: S603 - создание изолированного junction
            [os.environ["COMSPEC"], "/c", "mklink", "/J", str(job), str(target)],
            capture_output=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0)
        for action in ("worker", "cleanup"):
            self.assertNotEqual(self.call(action, str(job)).returncode, 0)
        self.assertEqual(marker.read_text(encoding="utf-8"), "valuable")
        job.rmdir()

    def test_cleanup_rejects_junction_inside_failed_job(self) -> None:
        root = Path(self.env["CODEX_HOME"]) / "background-wait"
        job = root / str(uuid.uuid4())
        job.mkdir(parents=True)
        target = self.root / "target"
        target.mkdir()
        marker = target / "valuable"
        _ = marker.write_text("data", encoding="utf-8")
        for name in ("request.json", "startup-error.json"):
            _ = (job / name).write_text("{}", encoding="utf-8")
        link = job / "stdout.log"
        result = subprocess.run(  # noqa: S603 - создание изолированного junction
            [os.environ["COMSPEC"], "/c", "mklink", "/J", str(link), str(target)],
            capture_output=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0)
        self.assertNotEqual(self.call("cleanup", str(job)).returncode, 0)
        self.assertEqual(marker.read_text(encoding="utf-8"), "data")
        link.rmdir()

    def test_cleanup_removes_confirmed_watcher_start_failure(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=str(self.root),
            command=[sys.executable],
        )
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(subprocess, "Popen", side_effect=OSError("failed")),
            mock.patch("sys.stdout", new_callable=io.StringIO) as output,
        ):
            self.assertEqual(background_wait.start(args), 1)
        response = cast(dict[str, str], json.loads(output.getvalue()))
        self.assertEqual(response["state"], "startup_failed")
        job = Path(response["job_dir"])
        self.assertNotEqual(self.call("worker", str(job)).returncode, 0)
        self.assertFalse((job / "stdout.log").exists())
        result = self.call("cleanup", str(job))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(job.exists())

    def test_cleanup_rejects_uncertain_or_contaminated_start_failure(self) -> None:
        for extra in (None, "started.json", "user-data.txt"):
            with self.subTest(extra=extra):
                job = (
                    Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
                )
                job.mkdir(parents=True, exist_ok=True)
                _ = (job / "request.json").write_text("{}", encoding="utf-8")
                if extra is not None:
                    _ = (job / "startup-error.json").write_text("{}", encoding="utf-8")
                    _ = (job / extra).write_text("valuable", encoding="utf-8")
                result = self.call("cleanup", str(job))
                self.assertNotEqual(result.returncode, 0)
                self.assertTrue(job.exists())

    def test_log_output_is_bounded_without_blocking_command(self) -> None:
        job = self.start(
            "import sys; sys.stdout.buffer.write(b'x' * (17 * 1024 * 1024)); "
            + "sys.stderr.buffer.write(b'done' + b'y' * (17 * 1024 * 1024))"
        )
        _ = self.wait_for(job / "delivery.json")
        result = cast(
            dict[str, object],
            json.loads((job / "result.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(result["exit_code"], 0)
        self.assertLessEqual((job / "stdout.log").stat().st_size, 16 * 1024 * 1024)
        self.assertLessEqual((job / "stderr.log").stat().st_size, 16 * 1024 * 1024)
        self.assertTrue(result["stdout_truncated"])
        self.assertTrue(result["stderr_truncated"])
        self.assertIn(b"done", (job / "stderr.log").read_bytes())

    def test_incomplete_log_write_is_reported_and_source_drained(self) -> None:
        class ShortWriter(io.BytesIO):
            @override
            def write(self, buffer: object) -> int:
                data = cast(bytes, buffer)
                return super().write(data[:1])

        source = io.BytesIO(b"source-data")
        destination = ShortWriter()
        status: dict[str, object] = {"truncated": False}
        background_wait.copy_bounded(source, destination, status)
        self.assertEqual(source.tell(), len(b"source-data"))
        self.assertEqual(destination.getvalue(), b"s")
        self.assertTrue(status["truncated"])
        self.assertEqual(status["error"], "Неполная запись журнала")

    def test_cleanup_removes_only_an_explicit_finished_job(self) -> None:
        job = self.start("print('done')")
        _ = self.wait_for(job / "delivery.json")
        result = self.call("cleanup", str(job))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(job.exists())

    def test_cleanup_preserves_unknown_file_and_job(self) -> None:
        job = self.start("print('done')")
        _ = self.wait_for(job / "delivery.json")
        unknown = job / "user-data.txt"
        _ = unknown.write_text("valuable", encoding="utf-8")
        result = self.call("cleanup", str(job))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(unknown.read_text(encoding="utf-8"), "valuable")
        self.assertTrue((job / "result.json").exists())

    def test_cleanup_rejects_a_running_job(self) -> None:
        job = self.start("import time; time.sleep(2); print('done')")
        result = self.call("cleanup", str(job))
        self.assertNotEqual(result.returncode, 0)
        _ = self.wait_for(job / "delivery.json")
        self.assertTrue((job / "result.json").exists())

    def test_completion_tracks_launched_process_not_detached_descendant(self) -> None:
        marker = self.root / "child-done"
        child = f"import time; from pathlib import Path; time.sleep(2); Path({str(marker)!r}).write_text('done')"
        source = (
            "import subprocess, sys; "
            f"subprocess.Popen([sys.executable, '-c', {child!r}], "
            "stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, "
            "creationflags=subprocess.CREATE_NO_WINDOW)"
        )
        job = self.start(source)
        _ = self.wait_for(job / "delivery.json")
        self.assertFalse(marker.exists())
        deadline = time.monotonic() + 10
        while not marker.exists() and time.monotonic() < deadline:
            time.sleep(0.05)
        self.assertTrue(marker.exists())


if __name__ == "__main__":
    _ = unittest.main()
