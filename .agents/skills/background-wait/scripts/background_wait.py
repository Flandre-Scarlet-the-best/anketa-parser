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
from collections.abc import Generator
from pathlib import Path
from typing import BinaryIO, cast

# Ограничение для каждого потока не мешает дочернему процессу писать дальше.
MAX_LOG_BYTES = 16 * 1024 * 1024

# Ожидание очереди ограничено: уведомление не должно удерживать блокировку задания.
QUEUE_TIMEOUT_SECONDS = 60

# Проверка справки CLI не должна задерживать запуск задания.
PROBE_TIMEOUT_SECONDS = 30

# Подтверждение разрешённой очистки: переживает прерывание удаления.
CLEANUP_MARKER = "cleanup.json"

# Фоновое ожидание опирается на Windows: msvcrt, junction и скрытые процессы.
SUPPORTED_PLATFORMS = frozenset({"win32"})


def write_json(path: Path, data: dict[str, object]) -> None:
    temporary = path.with_suffix(path.suffix + ".tmp")
    try:
        _ = temporary.write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, path)
    except OSError:
        # Оставшийся временный файл считается неизвестным, и cleanup отклоняет
        # каталог задания. Ошибка удаления не должна скрыть исходную причину.
        with contextlib.suppress(OSError):
            temporary.unlink()
        raise


def read_json(path: Path) -> dict[str, object]:
    return cast(dict[str, object], json.loads(path.read_text(encoding="utf-8")))


def print_json(data: dict[str, object]) -> None:
    """Вывести машиночитаемый ответ независимо от кодировки stdout.

    Экранирование не-ASCII сохраняет путь задания при перенаправлении вывода
    в кодировку, которая не содержит нужных символов; `json.loads` восстановит
    исходное значение.
    """
    print(json.dumps(data))


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
def notification_lock(directory: Path) -> Generator[None]:
    import msvcrt

    path = directory / "notification.lock"
    if path.is_symlink() or path.is_junction():
        raise ValueError("Файл блокировки не должен быть ссылкой")
    with path.open("a+b") as lock:
        _ = lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_LOCK, 1)
        try:
            yield
        finally:
            _ = lock.seek(0)
            msvcrt.locking(lock.fileno(), msvcrt.LK_UNLCK, 1)


def run_bounded(
    command: list[str], cwd: Path, timeout: float
) -> tuple[int, str, str] | None:
    """Запустить CLI и вернуть код возврата с выводом, либо None при тайм-ауте.

    Ожидание ограничено самим процессом: на Windows повторный сбор вывода после
    kill() ждёт закрытия каналов всеми потомками и может не завершиться.
    """
    process = subprocess.Popen(  # noqa: S603 - аргументы собирает вызывающая сторона
        command,
        stdin=subprocess.DEVNULL,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        cwd=cwd,
        text=True,
        encoding="utf-8",
        errors="replace",
        creationflags=subprocess.CREATE_NO_WINDOW,
    )
    try:
        stdout, stderr = process.communicate(timeout=timeout)
    except subprocess.TimeoutExpired:
        with contextlib.suppress(OSError):
            process.kill()
        with contextlib.suppress(subprocess.TimeoutExpired):
            _ = process.wait(timeout=timeout)
        return None
    code = process.returncode
    return (0 if code is None else code, stdout, stderr)


def queue_resume_supported(codex: str, cwd: Path) -> bool:
    """Проверить, принимает ли выбранный Codex CLI возобновление очереди.

    Справка выполняется в каталоге помощника: shim codex.cmd разрешает node по
    имени, а cmd.exe начинает поиск с текущего каталога.
    """
    try:
        probe = run_bounded([codex, "queue", "--help"], cwd, PROBE_TIMEOUT_SECONDS)
    except OSError:
        return False
    if probe is None:
        return False
    returncode, stdout, _ = probe
    return returncode == 0 and "--resume" in stdout


def start(args: argparse.Namespace) -> int:
    if sys.platform not in SUPPORTED_PLATFORMS:
        raise ValueError("Фоновый запуск поддерживается только в Windows")
    thread = str(
        uuid.UUID(
            cast(str | None, getattr(args, "thread", None))
            or os.environ.get("CODEX_THREAD_ID", "")
        )
    )
    allow_unc = bool(getattr(args, "allow_unc", False))
    cwd = Path(cast(str, args.cwd))
    ensure_unc_authorized(str(cwd), allow_unc)
    if not cwd.is_absolute() or not cwd.is_dir():
        raise ValueError("Рабочий каталог должен быть существующим абсолютным путём")
    command = [str(part) for part in cast("list[object]", args.command)]
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
    root = job_root()
    root.mkdir(parents=True, exist_ok=True)
    if not queue_resume_supported(codex, root):
        raise ValueError(
            "Codex CLI не поддерживает queue --resume: уведомление невозможно"
        )

    job_id = str(uuid.uuid4())
    directory = root / job_id
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
        _ = subprocess.Popen(  # noqa: S603 - запуск собственного процесса-наблюдателя
            [sys.executable, str(Path(__file__).resolve()), "worker", str(directory)],
            stdin=subprocess.DEVNULL,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            close_fds=True,
            creationflags=flags,
        )
    except OSError as error:
        write_json(directory / "startup-error.json", {"error": str(error)})
        print_json({"state": "startup_failed", "job_dir": str(directory)})
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
                print_json(
                    {"state": state, "job_id": job_id, "job_dir": str(directory)}
                )
                return 0 if state in {"running", "completed"} else 1
        time.sleep(0.05)
    print_json({"state": "unknown", "job_id": job_id, "job_dir": str(directory)})
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
        delivery: dict[str, object]
        try:
            delivery = queue_notification(
                cast(str, request["codex"]), thread, message, directory
            )
        except OSError as error:
            delivery = {"state": "failed", "error": str(error)}
        # Ошибка сохранения статуса не подменяет принятое уведомление ошибкой
        # очереди: повторяется только запись, а не вызов очереди.
        try:
            write_json(directory / "delivery.json", delivery)
        except OSError:
            write_json(directory / "delivery.json", delivery)
    return 0


def queue_notification(
    codex: str, thread: str, message: str, directory: Path
) -> dict[str, object]:
    """Поставить уведомление в очередь и вернуть наблюдаемый статус доставки."""
    # Shim codex.cmd ищет node по имени, а cmd.exe начинает с текущего каталога.
    # Уведомление выполняется в собственном каталоге задания, который создаёт
    # помощник, а не в унаследованном рабочем каталоге.
    result = run_bounded(
        [codex, "queue", "--resume", "--thread", thread, "--message", message],
        directory,
        QUEUE_TIMEOUT_SECONDS,
    )
    if result is None:
        return {
            "state": "uncertain",
            "error": f"Тайм-аут очереди {QUEUE_TIMEOUT_SECONDS} с",
        }
    returncode, stdout, stderr = result
    return {
        "state": "queued" if returncode == 0 and not stderr.strip() else "uncertain",
        "exit_code": returncode,
        "stdout": stdout,
        "stderr": stderr,
    }


def cancel(directory: Path) -> int:
    directory = owned_job(directory)
    # Очистка уже разрешена: новый запрос отмены сделал бы каталог непустым.
    if (directory / CLEANUP_MARKER).exists():
        print_json({"state": "cleaning_up", "job_dir": str(directory)})
        return 1
    if not (directory / "request.json").is_file():
        raise ValueError("Указанный каталог не содержит задания")
    with notification_lock(directory):
        if (directory / CLEANUP_MARKER).exists():
            print_json({"state": "cleaning_up", "job_dir": str(directory)})
            return 1
        if (directory / "delivery.json").exists():
            print_json({"state": "already_finished", "job_dir": str(directory)})
            return 1
        marker = directory / "cancel.json"
        if not marker.exists():
            with marker.open("x", encoding="utf-8") as file:
                json.dump({"requested": True}, file)
    print_json({"state": "suppression_requested", "job_dir": str(directory)})
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
        CLEANUP_MARKER,
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
        if CLEANUP_MARKER not in names and (
            "request.json" not in names or not (completed or startup_failed)
        ):
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
        # Подтверждение разрешённой очистки сохраняется до её завершения:
        # иначе неудаление отдельного файла оставляло бы каталог неочищаемым.
        write_json(directory / CLEANUP_MARKER, {"state": "in_progress"})
        entries = list(directory.iterdir())
    for entry in entries:
        if entry.name != CLEANUP_MARKER:
            entry.unlink()
    (directory / CLEANUP_MARKER).unlink()
    try:
        directory.rmdir()
    except OSError:
        # Каталог снова стал непустым: подтверждение восстанавливается, чтобы
        # повторная очистка оставалась возможной.
        write_json(directory / CLEANUP_MARKER, {"state": "in_progress"})
        raise
    print_json({"state": "removed", "job_dir": str(directory)})
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    subparsers = parser.add_subparsers(dest="action", required=True)
    start_parser = subparsers.add_parser("start")
    _ = start_parser.add_argument("--cwd", required=True)
    _ = start_parser.add_argument("--thread")
    _ = start_parser.add_argument("--allow-unc", action="store_true")
    _ = start_parser.add_argument("command", nargs=argparse.REMAINDER)
    for name in ("worker", "cancel", "cleanup"):
        _ = subparsers.add_parser(name).add_argument("directory", type=Path)
    args = parser.parse_args()
    action = cast(str, args.action)
    directory = cast(Path, getattr(args, "directory", None))
    try:
        if action == "start":
            return start(args)
        if action == "worker":
            return worker(directory)
        if action == "cleanup":
            return cleanup(directory)
        return cancel(directory)
    except (OSError, ValueError) as error:
        print(f"Ошибка: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
