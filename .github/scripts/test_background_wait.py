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
from collections.abc import Callable
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
    def cancel(self, directory: Path) -> int: ...
    def cleanup(self, directory: Path) -> int: ...
    def queue_resume_supported(self, codex: str, cwd: Path) -> bool: ...
    def queue_notification(
        self, codex: str, thread: str, message: str, directory: Path
    ) -> dict[str, object]: ...
    def start_tray_indicator(self, command_pid: int, job_id: str) -> None: ...
    def find_executable(self, command: str, allow_unc: bool = False) -> str | None: ...
    def write_json(self, path: Path, data: dict[str, object]) -> None: ...
    def copy_bounded(
        self, source: BinaryIO, destination: BinaryIO, status: dict[str, object]
    ) -> None: ...


background_wait = cast(BackgroundWaitModule, cast(object, module))


class QueueProcess:
    """Поддельный процесс очереди уведомления с наблюдаемыми вызовами."""

    def __init__(
        self, stdout: str = "queued", stderr: str = "", exit_code: int = 0
    ) -> None:
        self.stdout: str = stdout
        self.stderr: str = stderr
        self.returncode: int = exit_code
        self.communicate_calls: int = 0
        self.kill_calls: int = 0
        self.wait_calls: int = 0
        self.timeout: Exception | None = None
        self.before: Callable[[], None] | None = None

    def communicate(self, timeout: float | None = None) -> tuple[str, str]:
        _ = timeout
        self.communicate_calls += 1
        if self.before is not None:
            self.before()
        if self.timeout is not None:
            raise self.timeout
        return (self.stdout, self.stderr)

    def kill(self) -> None:
        self.kill_calls += 1

    def wait(self, timeout: float | None = None) -> int:
        _ = timeout
        self.wait_calls += 1
        return self.returncode


# Ограниченное ожидание завершения дочерних процессов перед удалением каталога.
CLEANUP_TIMEOUT_SECONDS = 30.0
CLEANUP_RETRY_SECONDS = 0.05
# Windows отвечает этими кодами, пока каталог занят живым процессом или его файл открыт.
SHARING_ERROR_CODES = frozenset({5, 32, 145})


@unittest.skipUnless(os.name == "nt", "Для проверки требуется Windows")
class BackgroundWaitTests(unittest.TestCase):
    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.root: Path = Path()
        self.bin: Path = Path()
        self.queue: Path = Path()
        self.env: dict[str, str] = {}

    @override
    def setUp(self) -> None:
        self.root = Path(tempfile.mkdtemp(prefix="anketa-background-wait-"))
        self.addCleanup(self.remove_root)
        self.bin = self.root / "bin"
        self.bin.mkdir()
        self.queue = self.bin / "codex.cmd"
        _ = self.queue.write_text(self.queue_script("echo %*\r\n"), encoding="ascii")
        self.env = dict(os.environ)
        self.env["PATH"] = str(self.bin) + os.pathsep + self.env["PATH"]
        self.env["CODEX_HOME"] = str(self.root / "codex")
        self.env["CODEX_THREAD_ID"] = str(uuid.uuid4())

    def remove_root(self) -> None:
        """Удалить каталог задания, повторяя только sharing-ошибки Windows.

        Пока дочерний процесс завершает работу (в том числе запись данных
        покрытия), каталог занят как его рабочий каталог. Повторение ограничено
        по времени и не подавляет другие исключения: по истечении срока ошибка
        пробрасывается, поэтому незавершённый процесс не остаётся незамеченным.
        """
        deadline = time.monotonic() + CLEANUP_TIMEOUT_SECONDS
        while True:
            try:
                shutil.rmtree(self.root)
                return
            except FileNotFoundError:
                return
            except OSError as error:
                sharing = getattr(error, "winerror", None) in SHARING_ERROR_CODES
                if not sharing or time.monotonic() >= deadline:
                    raise
            time.sleep(CLEANUP_RETRY_SECONDS)

    def queue_script(self, body: str) -> str:
        """Сценарий очереди: отвечает на справку о --resume и выполняет тест."""
        return (
            "@echo off\r\n"
            'if "%~1"=="queue" if "%~2"=="--help" (\r\n'
            "  echo Usage: codex queue --thread THREAD --message TEXT [--resume]\r\n"
            "  exit /b 0\r\n"
            ")\r\n"
            f"{body}"
        )

    def blocked_source(self, name: str) -> tuple[Path, Path, str]:
        """Команда сообщает о запуске и ждёт сигнал теста, а не задержку."""
        ready = self.root / f"{name}-ready"
        release = self.root / f"{name}-release"
        source = (
            "import time\n"
            "from pathlib import Path\n"
            f"Path({str(ready)!r}).write_text('ready', encoding='utf-8')\n"
            "deadline = time.monotonic() + 30\n"
            f"while not Path({str(release)!r}).exists() and time.monotonic() < deadline:\n"
            "    time.sleep(0.02)\n"
            "print('finished')\n"
        )
        return ready, release, source

    def wait_for_file(self, path: Path, timeout: float = 15) -> None:
        deadline = time.monotonic() + timeout
        while not path.exists() and time.monotonic() < deadline:
            time.sleep(0.02)
        self.assertTrue(path.exists(), f"Не дождались файла {path.name}")

    def queue_process(
        self, stdout: str = "queued", stderr: str = "", exit_code: int = 0
    ) -> QueueProcess:
        """Поддельный процесс очереди с заданным выводом и кодом возврата."""
        return QueueProcess(stdout, stderr, exit_code)

    def popen_results(self, command_process: object, queue_process: object) -> object:
        """Различать запуск наблюдаемой команды и процесс очереди уведомления."""

        def popen(args: list[str], **_kwargs: object) -> object:
            return (
                queue_process if Path(str(args[0])) == self.queue else command_process
            )

        return popen

    def communicate_bounded(
        self, process: subprocess.Popen[bytes]
    ) -> tuple[bytes, bytes]:
        """Дождаться процесса, не оставляя прогон на бесконечном ожидании."""
        try:
            return process.communicate(timeout=8)
        except subprocess.TimeoutExpired:
            process.kill()
            _ = process.wait(timeout=8)
            self.fail("Проверяемый процесс не завершился за 8 секунд")

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
        ready, release, source = self.blocked_source("cancel")
        job = self.start(source)
        self.wait_for_file(ready)
        canceled = self.call("cancel", str(job))
        self.assertEqual(canceled.returncode, 0, canceled.stderr)
        _ = release.write_text("go", encoding="utf-8")
        self.assertEqual(self.wait_for(job / "delivery.json")["state"], "suppressed")
        self.assertEqual(
            json.loads((job / "result.json").read_text(encoding="utf-8"))["exit_code"],
            0,
        )
        self.assertIn(b"finished", (job / "stdout.log").read_bytes())

    def test_queue_failure_is_not_reported_as_delivery(self) -> None:
        _ = self.queue.write_text(self.queue_script("exit /b 3\r\n"), encoding="ascii")
        job = self.start("print('done')")
        delivery = self.wait_for(job / "delivery.json")
        self.assertEqual(delivery["state"], "uncertain")
        self.assertEqual(delivery["exit_code"], 3)

    def test_queue_warning_is_not_reported_as_confirmed_delivery(self) -> None:
        _ = self.queue.write_text(
            self.queue_script("echo resume failed 1>&2\r\nexit /b 0\r\n"),
            encoding="ascii",
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
            mock.patch.object(
                background_wait, "queue_resume_supported", return_value=True
            ),
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
            mock.patch.object(
                background_wait, "queue_resume_supported", return_value=True
            ),
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
            mock.patch.object(
                background_wait, "queue_resume_supported", return_value=True
            ),
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

        queue_process = self.queue_process()
        queue_process.before = lambda: events.append("queue")
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator") as indicator,
            mock.patch.object(background_wait, "write_json", side_effect=write),
            mock.patch.object(
                subprocess,
                "Popen",
                side_effect=self.popen_results(process, queue_process),
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
        queue_process = self.queue_process()
        queue_process.timeout = subprocess.TimeoutExpired("codex", 60)
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator"),
            mock.patch.object(
                subprocess,
                "Popen",
                side_effect=self.popen_results(process, queue_process),
            ),
        ):
            _ = background_wait.worker(job)
        delivery = cast(
            dict[str, object],
            json.loads((job / "delivery.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(delivery["state"], "uncertain")
        # После завершения процесса очереди остаток вывода не собирается:
        # иначе ожидание закрытия унаследованных каналов не ограничено.
        self.assertEqual(queue_process.kill_calls, 1)
        self.assertEqual(queue_process.wait_calls, 1)
        self.assertEqual(queue_process.communicate_calls, 1)

    def test_queue_timeout_does_not_wait_for_a_surviving_grandchild(self) -> None:
        stub = self.bin / "queue_stub.py"
        _ = stub.write_text(
            # Уцелевший потомок не должен удерживать каталог задания как рабочий.
            """import os, subprocess, sys, tempfile, time
os.chdir(tempfile.gettempdir())
subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(12)'])
time.sleep(12)
""",
            encoding="utf-8",
        )
        _ = self.queue.write_text(
            f'@echo off\r\n"{sys.executable}" "{stub}"\r\n', encoding="ascii"
        )
        job = self.root / "notification-job"
        job.mkdir()
        with mock.patch.object(background_wait, "QUEUE_TIMEOUT_SECONDS", 1):
            started = time.monotonic()
            delivery = background_wait.queue_notification(
                str(self.queue), self.env["CODEX_THREAD_ID"], "message", job
            )
            elapsed = time.monotonic() - started
        self.assertEqual(delivery["state"], "uncertain")
        self.assertLess(elapsed, 6, "Ожидание не ограничено завершением потомка")

    def test_delivery_save_failure_keeps_the_queue_outcome(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        background_wait.write_json(
            job / "request.json",
            {
                "thread": self.env["CODEX_THREAD_ID"],
                "cwd": str(self.root),
                "command": [sys.executable, "-c", "print('done')"],
                "codex": str(self.queue),
            },
        )
        states: list[object] = []
        real_write = background_wait.write_json

        def write(path: Path, data: dict[str, object]) -> None:
            if path.name == "delivery.json":
                states.append(data.get("state"))
                if len(states) == 1:
                    raise OSError("временный сбой записи")
            real_write(path, data)

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator"),
            mock.patch.object(background_wait, "write_json", side_effect=write),
            mock.patch("sys.stdout", new_callable=io.StringIO),
        ):
            self.assertEqual(background_wait.worker(job), 0)
        # Ошибка записи не подменяет принятое уведомление ошибкой очереди,
        # а повторяется только сохранение статуса.
        self.assertEqual(states, ["queued", "queued"])
        delivery = cast(
            dict[str, object],
            json.loads((job / "delivery.json").read_text(encoding="utf-8")),
        )
        self.assertEqual(delivery["state"], "queued")

    def test_probe_timeout_does_not_wait_for_a_surviving_grandchild(self) -> None:
        stub = self.bin / "probe_stub.py"
        _ = stub.write_text(
            # Уцелевший потомок не должен удерживать каталог прогона как рабочий.
            """import os, subprocess, sys, tempfile, time
os.chdir(tempfile.gettempdir())
subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(12)'])
time.sleep(12)
""",
            encoding="utf-8",
        )
        _ = self.queue.write_text(
            f'@echo off\r\n"{sys.executable}" "{stub}"\r\n', encoding="ascii"
        )
        with mock.patch.object(background_wait, "PROBE_TIMEOUT_SECONDS", 1):
            started = time.monotonic()
            supported = background_wait.queue_resume_supported(
                str(self.queue), self.root
            )
            elapsed = time.monotonic() - started
        self.assertFalse(supported)
        self.assertLess(elapsed, 6, "Проверка справки ждала завершения потомка")

    def test_cancel_is_refused_while_cleanup_is_in_progress(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        background_wait.write_json(job / "request.json", {"state": "completed"})
        background_wait.write_json(job / "cleanup.json", {"state": "in_progress"})
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch("sys.stdout", new_callable=io.StringIO) as output,
        ):
            self.assertEqual(background_wait.cancel(job), 1)
        # Отмена не создаёт файлов, иначе каталог перестанет удаляться.
        self.assertFalse((job / "cancel.json").exists())
        self.assertIn("cleaning_up", output.getvalue())

    def test_failed_removal_restores_the_cleanup_confirmation(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        for name in ("request.json", "result.json", "delivery.json"):
            background_wait.write_json(job / name, {"state": "completed"})
        real_rmdir = Path.rmdir
        failed = {"count": 0}

        def rmdir(self: Path) -> None:
            if not failed["count"]:
                failed["count"] = 1
                raise OSError("каталог занят")
            real_rmdir(self)

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(Path, "rmdir", rmdir),
            mock.patch("sys.stdout", new_callable=io.StringIO),
            self.assertRaises(OSError),
        ):
            _ = background_wait.cleanup(job)
        self.assertTrue((job / "cleanup.json").exists())
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch("sys.stdout", new_callable=io.StringIO),
        ):
            self.assertEqual(background_wait.cleanup(job), 0)
        self.assertFalse(job.exists())

    def test_cleanup_can_be_repeated_after_a_partial_failure(self) -> None:
        job = Path(self.env["CODEX_HOME"]) / "background-wait" / str(uuid.uuid4())
        job.mkdir(parents=True)
        for name in ("request.json", "result.json", "delivery.json"):
            background_wait.write_json(job / name, {"state": "completed"})
        _ = (job / "stdout.log").write_text("log", encoding="utf-8")
        real_unlink = Path.unlink
        failed = {"count": 0}

        def unlink(self: Path, missing_ok: bool = False) -> None:
            if self.name == "stdout.log" and not failed["count"]:
                failed["count"] = 1
                raise OSError("журнал занят")
            real_unlink(self, missing_ok)

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(Path, "unlink", unlink),
            mock.patch("sys.stdout", new_callable=io.StringIO),
            self.assertRaises(OSError),
        ):
            _ = background_wait.cleanup(job)
        self.assertTrue(all(entry.is_file() for entry in job.iterdir()))
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch("sys.stdout", new_callable=io.StringIO),
        ):
            self.assertEqual(background_wait.cleanup(job), 0)
        self.assertFalse(job.exists())

    def test_start_reports_the_job_directory_for_a_non_ascii_path(self) -> None:
        environment = dict(self.env)
        environment["CODEX_HOME"] = str(self.root / "Кодекс")
        environment["PYTHONIOENCODING"] = "cp1252"
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
                "print('done')",
            ],
            env=environment,
            capture_output=True,
            encoding="utf-8",
            timeout=25,
            check=False,
        )
        self.assertEqual(result.returncode, 0, result.stderr)
        response = cast(dict[str, str], json.loads(result.stdout))
        self.assertEqual(
            Path(response["job_dir"]),
            Path(environment["CODEX_HOME"]) / "background-wait" / response["job_id"],
        )
        _ = self.wait_for(Path(response["job_dir"]) / "delivery.json")

    def test_start_requires_queue_resume_support(self) -> None:
        _ = self.queue.write_text("@echo off\r\nexit /b 0\r\n", encoding="ascii")
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=str(self.root),
            command=[sys.executable],
        )
        with (
            mock.patch.dict(os.environ, self.env),
            self.assertRaises(ValueError) as raised,
        ):
            _ = background_wait.start(args)
        self.assertIn("--resume", str(raised.exception))
        root = Path(self.env["CODEX_HOME"]) / "background-wait"
        self.assertEqual(list(root.iterdir()), [])

    def test_failed_json_replacement_removes_the_temporary_file(self) -> None:
        path = self.root / "written.json"
        with (
            mock.patch.object(os, "replace", side_effect=OSError("replace failure")),
            self.assertRaises(OSError),
        ):
            background_wait.write_json(path, {"value": 1})
        self.assertFalse((self.root / "written.json.tmp").exists())
        self.assertFalse(path.exists())

    def test_failed_json_write_removes_the_temporary_file(self) -> None:
        path = self.root / "written.json"
        real_write = Path.write_text

        def write(self: Path, *_args: object, **_kwargs: object) -> int:
            _ = real_write(self, "{")
            raise OSError("write failure")

        with (
            mock.patch.object(Path, "write_text", write),
            self.assertRaises(OSError),
        ):
            background_wait.write_json(path, {"value": 1})
        self.assertFalse((self.root / "written.json.tmp").exists())

    def test_notification_ignores_the_inherited_directory(self) -> None:
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
        calls: list[dict[str, object]] = []

        def popen(args: list[str], **kwargs: object) -> object:
            if Path(str(args[0])) == self.queue:
                calls.append(kwargs)
                return self.queue_process()
            return process

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(background_wait, "start_tray_indicator"),
            mock.patch.object(subprocess, "Popen", side_effect=popen),
        ):
            _ = background_wait.worker(job)
        self.assertEqual(len(calls), 1)
        # Shim codex.cmd разрешает node по имени: унаследованный каталог
        # вызывающей стороны не должен становиться каталогом поиска.
        self.assertEqual(calls[0].get("cwd"), job)
        self.assertNotEqual(calls[0].get("cwd"), Path.cwd())

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
        spawns: list[str] = []

        def popen(args: list[str], **_kwargs: object) -> object:
            if Path(str(args[0])) == self.queue:
                return self.queue_process()
            spawns.append(str(args[0]))
            if len(spawns) == 2:
                raise OSError("tray unavailable")
            return process

        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(
                background_wait, "find_executable", return_value=sys.executable
            ),
            mock.patch.object(subprocess, "Popen", side_effect=popen),
        ):
            self.assertEqual(background_wait.worker(job), 0)
        self.assertEqual(len(spawns), 2)
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
            stdout, stderr = self.communicate_bounded(tray)
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
            stdout, stderr = self.communicate_bounded(tray)
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
        # cancel.json входит в набор не запущенного задания, поэтому отказ
        # проверяется на самой ссылке, а не на постороннем имени файла.
        link = job / "cancel.json"
        result = subprocess.run(  # noqa: S603 - создание изолированного junction
            [os.environ["COMSPEC"], "/c", "mklink", "/J", str(link), str(target)],
            capture_output=True,
            check=False,
        )
        self.assertEqual(result.returncode, 0)
        refused = self.call("cleanup", str(job))
        self.assertNotEqual(refused.returncode, 0)
        self.assertIn("неизвестные файлы или ссылки", refused.stderr)
        self.assertEqual(marker.read_text(encoding="utf-8"), "data")
        self.assertTrue(link.is_junction())
        link.rmdir()

    def test_cleanup_removes_confirmed_watcher_start_failure(self) -> None:
        args = argparse.Namespace(
            thread=self.env["CODEX_THREAD_ID"],
            cwd=str(self.root),
            command=[sys.executable],
        )
        with (
            mock.patch.dict(os.environ, self.env),
            mock.patch.object(
                background_wait, "queue_resume_supported", return_value=True
            ),
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
        ready, release, source = self.blocked_source("cleanup")
        job = self.start(source)
        self.wait_for_file(ready)
        result = self.call("cleanup", str(job))
        self.assertNotEqual(result.returncode, 0)
        _ = release.write_text("go", encoding="utf-8")
        _ = self.wait_for(job / "delivery.json")
        self.assertTrue((job / "result.json").exists())

    def test_completion_tracks_launched_process_not_detached_descendant(self) -> None:
        marker = self.root / "child-done"
        release = self.root / "child-release"
        child = (
            "import time\n"
            "from pathlib import Path\n"
            "deadline = time.monotonic() + 30\n"
            f"while not Path({str(release)!r}).exists() and time.monotonic() < deadline:\n"
            "    time.sleep(0.02)\n"
            f"Path({str(marker)!r}).write_text('done', encoding='utf-8')\n"
        )
        source = (
            "import subprocess, sys; "
            f"subprocess.Popen([sys.executable, '-c', {child!r}], "
            "stdin=subprocess.DEVNULL, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL, "
            "creationflags=subprocess.CREATE_NO_WINDOW)\n"
        )
        job = self.start(source)
        _ = self.wait_for(job / "delivery.json")
        self.assertFalse(marker.exists())
        _ = release.write_text("go", encoding="utf-8")
        self.wait_for_file(marker)


if __name__ == "__main__":
    _ = unittest.main()
