#!/usr/bin/env python3
"""Запуск команды с уведомлением исходной сессии Codex после завершения."""

from __future__ import annotations

import argparse
import contextlib
import json
import os
import shutil
import subprocess
import sys
import threading
import time
import uuid
from collections.abc import Iterator
from pathlib import Path
from typing import BinaryIO, cast

# Ограничение для каждого потока не мешает дочернему процессу писать дальше.
MAX_LOG_BYTES = 16 * 1024 * 1024


def write_json(path: Path, data: dict[str, object]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    try:
        temporary.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, path)
    except OSError:
        # Оставшийся временный файл считается неизвестным, и cleanup отклоняет
        # каталог задания. Ошибка удаления не должна скрыть исходную причину.
        with contextlib.suppress(OSError):
            temporary.unlink()
        raise


def read_json(path: Path) -> dict[str, object]:
    return json.loads(path.read_text(encoding="utf-8"))


def job_root() -> Path:
    configured = os.environ.get("CODEX_HOME")
    return (
        Path(configured) if configured else Path.home() / ".codex"
    ) / "background-wait"


def owned_job(directory: Path) -> Path:
    root = job_root().absolute()
    candidate = directory.absolute()
    if os.path.normcase(str(candidate.parent)) != os.path.normcase(str(root)):
        raise ValueError("Каталог задания находится вне каталога Codex")
    if str(uuid.UUID(candidate.name)) != candidate.name:
        raise ValueError("Некорректный идентификатор задания")
    if candidate.is_symlink() or candidate.is_junction():
        raise ValueError("Каталог задания не должен быть ссылкой")
    if candidate.resolve(strict=True).parent != root.resolve(strict=True):
        raise ValueError("Каталог задания находится вне каталога Codex")
    return candidate


def is_unc_path(value: str) -> bool:
    """UNC и пространства имён Windows начинаются с двух любых разделителей.

    Прямые слеши приводятся к обратным без обращения к файловой системе:
    Windows принимает `//server/share` как UNC, а параметры команды и элементы
    PATH проверяются до нормализации через `Path`.
    """
    return value.replace("/", "\\").startswith("\\\\")


def ensure_unc_authorized(value: str, allow_unc: bool) -> None:
    if is_unc_path(value) and not allow_unc:
        raise ValueError("Для UNC и пространств имён Windows требуется --allow-unc")


def find_executable(command: str, allow_unc: bool = False) -> str | None:
    if os.path.dirname(command):
        # shutil.which обращается к указанному пути напрямую, без поиска в PATH.
        if is_unc_path(command) and not allow_unc:
            return None
        return shutil.which(command)
    current = Path.cwd().resolve()
    for entry in os.get_exec_path():
        # Разрешение сетевого элемента PATH уже обращается к серверу.
        if is_unc_path(entry) and not allow_unc:
            continue
        path = Path(entry)
        if not path.is_absolute() or path.resolve() == current:
            continue
        found = shutil.which(str(path / command))
        if found:
            return str(Path(found).absolute())
    return None


def start_tray_indicator(command_pid: int, job_id: str) -> None:
    try:
        powershell = find_executable("powershell.exe")
        if not powershell:
            return
        _ = subprocess.Popen(  # noqa: S603 - запуск собственного визуального индикатора
            [
                powershell,
                "-NoProfile",
                "-NonInteractive",
                "-STA",
                "-WindowStyle",
                "Hidden",
                "-File",
                str(Path(__file__).with_name("tray_indicator.ps1")),
                str(command_pid),
                str(os.getpid()),
                job_id,
            ],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
        )
    except OSError:
        pass


def copy_bounded(
    source: BinaryIO, destination: BinaryIO, status: dict[str, object]
) -> None:
    written = 0
    while True:
        try:
            chunk = source.read(65536)
        except OSError as error:
            status["error"] = str(error)
            status["truncated"] = True
            return
        if not chunk:
            return
        remaining = max(0, MAX_LOG_BYTES - written)
        if len(chunk) > remaining:
            status["truncated"] = True
        if remaining and "error" not in status:
            try:
                part = chunk[:remaining]
                count = destination.write(part)
                written += count
                if count != len(part):
                    status["error"] = "Неполная запись журнала"
                    status["truncated"] = True
            except OSError as error:
                status["error"] = str(error)
                status["truncated"] = True


@contextlib.contextmanager
def notification_lock(directory: Path) -> Iterator[None]:
    import msvcrt

    path = directory / "notification.lock"
    if path.is_symlink() or path.is_junction():
        raise ValueError("Файл блокировки не должен быть ссылкой")
    with path.open("a+b") as lock:
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        try:
            yield
        finally:
            lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def start(args: argparse.Namespace) -> int:
    if os.name != "nt":
        raise ValueError("Фоновый запуск поддерживается только в Windows")
    thread = str(uuid.UUID(args.thread or os.environ.get("CODEX_THREAD_ID", "")))
    allow_unc = bool(getattr(args, "allow_unc", False))
    cwd = Path(args.cwd)
    ensure_unc_authorized(str(cwd), allow_unc)
    if not cwd.is_absolute() or not cwd.is_dir():
        raise ValueError("Рабочий каталог должен быть существующим абсолютным путём")
    command = args.command
    if command and command[0] == "--":
        command = command[1:]
    if not command:
        raise ValueError("После -- необходимо указать исполняемый файл и аргументы")
    requested = str(command[0])
    ensure_unc_authorized(requested, allow_unc)
    executable = find_executable(requested, allow_unc)
    if not executable or Path(executable).suffix.lower() in {".cmd", ".bat"}:
        raise ValueError(
            "Укажите доступный исполняемый файл, а не cmd/bat или псевдоним"
        )
    command[0] = str(Path(executable).absolute())
    codex = find_executable("codex.cmd", allow_unc) or find_executable(
        "codex.exe", allow_unc
    )
    if not codex:
        raise ValueError("Не найден исполняемый файл Codex CLI")

    job_id = str(uuid.uuid4())
    directory = job_root() / job_id
    directory.mkdir(parents=True, exist_ok=False)
    write_json(
        directory / "request.json",
        {"thread": thread, "cwd": str(cwd), "command": command, "codex": codex},
    )
    flags = (
        subprocess.CREATE_NEW_PROCESS_GROUP
        | subprocess.DETACHED_PROCESS
        | subprocess.CREATE_NO_WINDOW
    )
    try:
        subprocess.Popen(  # noqa: S603 - запуск собственного процесса-наблюдателя
            [sys.executable, str(Path(__file__).resolve()), "worker", str(directory)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
    except OSError as error:
        write_json(directory / "startup-error.json", {"error": str(error)})
        print(
            json.dumps(
                {"state": "startup_failed", "job_dir": str(directory)},
                ensure_ascii=False,
            )
        )
        return 1

    deadline = time.monotonic() + 15
    while time.monotonic() < deadline:
        for name in ("result.json", "started.json"):
            path = directory / name
            if path.exists():
                state = (
                    read_json(path).get("state", "unknown")
                    if name == "result.json"
                    else "running"
                )
                print(
                    json.dumps(
                        {"state": state, "job_id": job_id, "job_dir": str(directory)},
                        ensure_ascii=False,
                    )
                )
                return 0 if state in {"running", "completed"} else 1
        time.sleep(0.05)
    print(
        json.dumps(
            {"state": "unknown", "job_id": job_id, "job_dir": str(directory)},
            ensure_ascii=False,
        )
    )
    return 1


def worker(directory: Path) -> int:
    directory = owned_job(directory)
    job_id = directory.name
    if (directory / "startup-error.json").exists():
        raise ValueError("Наблюдатель для этого задания не был запущен")
    request = read_json(directory / "request.json")
    thread = str(uuid.UUID(str(request["thread"])))
    result: dict[str, object]
    started = time.monotonic()
    try:
        with (
            (directory / "stdout.log").open("wb", buffering=0) as stdout,
            (directory / "stderr.log").open("wb", buffering=0) as stderr,
        ):
            process = subprocess.Popen(  # noqa: S603 - команда явно передана вызывающей стороной
                cast(list[str], request["command"]),
                cwd=cast(str, request["cwd"]),
                stdin=subprocess.DEVNULL,
                stdout=subprocess.PIPE,
                stderr=subprocess.PIPE,
                close_fds=True,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            start_tray_indicator(process.pid, job_id)
            child_stdout = cast(BinaryIO, process.stdout)
            child_stderr = cast(BinaryIO, process.stderr)
            out_status: dict[str, object] = {"truncated": False}
            err_status: dict[str, object] = {"truncated": False}
            readers = [
                threading.Thread(
                    target=copy_bounded, args=(child_stdout, stdout, out_status)
                ),
                threading.Thread(
                    target=copy_bounded, args=(child_stderr, stderr, err_status)
                ),
            ]
            for reader in readers:
                reader.start()
            started_error = None
            try:
                write_json(directory / "started.json", {"pid": process.pid})
            except OSError as error:
                started_error = str(error)
            exit_code = process.wait()
            for reader in readers:
                reader.join()
            child_stdout.close()
            child_stderr.close()
        result = {
            "state": "completed",
            "exit_code": exit_code,
            "duration_seconds": time.monotonic() - started,
            "stdout_truncated": out_status["truncated"],
            "stderr_truncated": err_status["truncated"],
        }
        if started_error is not None:
            result["started_error"] = started_error
        if "error" in out_status or "error" in err_status:
            result["log_errors"] = {
                "stdout": out_status.get("error"),
                "stderr": err_status.get("error"),
            }
    except (OSError, ValueError) as error:
        result = {
            "state": "start_failed",
            "error": str(error),
            "duration_seconds": time.monotonic() - started,
        }
    write_json(directory / "result.json", result)
    with notification_lock(directory):
        if (directory / "cancel.json").exists():
            write_json(directory / "delivery.json", {"state": "suppressed"})
            return 0
        message = (
            f"Завершена фоновая задача {job_id}. Это машинное уведомление. "
            "Проверь сохранённые result.json и журналы; продолжай только актуальную задачу."
        )
        try:
            # Shim codex.cmd ищет node по имени, а cmd.exe начинает с текущего
            # каталога. Уведомление выполняется в собственном каталоге задания,
            # который создаёт помощник, а не в унаследованном рабочем каталоге.
            delivery = subprocess.run(  # noqa: S603 - фиксированные аргументы очереди и UUID
                [
                    cast(str, request["codex"]),
                    "queue",
                    "--resume",
                    "--thread",
                    thread,
                    "--message",
                    message,
                ],
                check=False,
                cwd=directory,
                capture_output=True,
                text=True,
                encoding="utf-8",
                errors="replace",
                timeout=60,
                creationflags=subprocess.CREATE_NO_WINDOW,
            )
            write_json(
                directory / "delivery.json",
                {
                    "state": "queued"
                    if delivery.returncode == 0 and not delivery.stderr.strip()
                    else "uncertain",
                    "exit_code": delivery.returncode,
                    "stdout": delivery.stdout,
                    "stderr": delivery.stderr,
                },
            )
        except subprocess.TimeoutExpired as error:
            write_json(
                directory / "delivery.json", {"state": "uncertain", "error": str(error)}
            )
        except OSError as error:
            write_json(
                directory / "delivery.json", {"state": "failed", "error": str(error)}
            )
    return 0


def cancel(directory: Path) -> int:
    directory = owned_job(directory)
    if not (directory / "request.json").is_file():
        raise ValueError("Указанный каталог не содержит задания")
    with notification_lock(directory):
        if (directory / "delivery.json").exists():
            print(
                json.dumps(
                    {"state": "already_finished", "job_dir": str(directory)},
                    ensure_ascii=False,
                )
            )
            return 1
        marker = directory / "cancel.json"
        if not marker.exists():
            with marker.open("x", encoding="utf-8") as file:
                json.dump({"requested": True}, file)
    print(
        json.dumps(
            {"state": "suppression_requested", "job_dir": str(directory)},
            ensure_ascii=False,
        )
    )
    return 0


def cleanup(directory: Path) -> int:
    directory = owned_job(directory)
    allowed = {
        "request.json",
        "started.json",
        "result.json",
        "delivery.json",
        "cancel.json",
        "startup-error.json",
        "notification.lock",
        "stdout.log",
        "stderr.log",
    }
    with notification_lock(directory):
        names = {entry.name for entry in directory.iterdir()}
        completed = {"result.json", "delivery.json"} <= names
        startup_failed = "startup-error.json" in names and names <= {
            "request.json",
            "startup-error.json",
            "cancel.json",
            "notification.lock",
        }
        if "request.json" not in names or not (completed or startup_failed):
            raise ValueError(
                "Удалять можно только завершённое или не запущенное задание"
            )
        entries = list(directory.iterdir())
        if any(
            entry.name not in allowed
            or entry.is_symlink()
            or entry.is_junction()
            or not entry.is_file()
            for entry in entries
        ):
            raise ValueError("Каталог содержит неизвестные файлы или ссылки")
    for entry in entries:
        entry.unlink()
    directory.rmdir()
    print(
        json.dumps({"state": "removed", "job_dir": str(directory)}, ensure_ascii=False)
    )
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    start_parser = subparsers.add_parser("start")
    start_parser.add_argument("--cwd", required=True)
    start_parser.add_argument("--thread")
    start_parser.add_argument("--allow-unc", action="store_true")
    start_parser.add_argument("command", nargs=argparse.REMAINDER)
    for name in ("worker", "cancel", "cleanup"):
        subparsers.add_parser(name).add_argument("directory", type=Path)
    args = parser.parse_args()
    try:
        if args.action == "start":
            return start(args)
        if args.action == "worker":
            return worker(args.directory)
        if args.action == "cleanup":
            return cleanup(args.directory)
        return cancel(args.directory)
    except (OSError, ValueError) as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
