"""Доверенная точка входа не импортирует и не выполняет подменённый код head."""

import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

from test_check_pr_body import CR_DELIMITER, CR_HEADER, CR_ROW, valid_body

WORKFLOWS = Path(__file__).resolve().parents[1] / "workflows"
TRUSTED = WORKFLOWS / "pr-body-trusted.yml"
# Команда точки входа из проверяемого checkout: литеральные аргументы,
# sys.executable и без shell, поэтому данные PR в неё не попадают.
ENTRY_COMMAND = [sys.executable, ".github/scripts/check_coderabbit_comment.py"]


class TrustedWorkflowTests(unittest.TestCase):
    """Доверенная проверка не исполняет код PR и берёт валидатор только из base."""

    def __init__(self, methodName: str = "runTest") -> None:
        super().__init__(methodName)
        self.text = ""
        self.yaml = ""

    def setUp(self):
        self.text = TRUSTED.read_text(encoding="utf-8")
        # Комментарии не участвуют в контракте: проверяется только сам YAML.
        self.yaml = "\n".join(
            line for line in self.text.splitlines() if not line.lstrip().startswith("#")
        )

    def test_runs_only_on_pull_request_target(self):
        trigger_match = re.search(r"(?m)^on:\n((?:[ \t]+.*\n)+)", self.yaml + "\n")
        if trigger_match is None:
            self.fail("Не найдена секция on в доверенном workflow")
        triggers = trigger_match.group(1)
        self.assertEqual(
            re.findall(r"(?m)^  (\w+):", triggers), ["pull_request_target"]
        )

    def test_checks_out_exact_base_sha_without_credentials(self):
        checkouts = re.findall(r"uses: actions/checkout@", self.yaml)
        self.assertEqual(len(checkouts), 1)
        self.assertIn("ref: ${{ github.event.pull_request.base.sha }}", self.yaml)
        self.assertIn("persist-credentials: false", self.yaml)

    def test_no_head_code_secrets_or_write_access(self):
        for forbidden in (
            "head.sha",
            "head.ref",
            "head.repo",
            "merge_commit_sha",
            "refs/pull",
            "secrets.",
            ": write",
            "write-all",
        ):
            self.assertNotIn(forbidden, self.yaml, forbidden)
        permission_match = re.search(
            r"(?m)^permissions:\n((?:[ \t]+.*\n)+)", self.yaml + "\n"
        )
        if permission_match is None:
            self.fail("Не найдена секция permissions в доверенном workflow")
        permissions = permission_match.group(1)
        self.assertEqual(
            permissions.split(), ["contents:", "read", "pull-requests:", "read"]
        )
        self.assertIn("GITHUB_TOKEN: ${{ github.token }}", self.yaml)
        self.assertIn("PR_NUMBER: ${{ github.event.pull_request.number }}", self.yaml)

    def test_runs_validator_from_checked_out_base(self):
        runs = re.findall(r"(?m)^\s+run: (.+)$", self.yaml)
        self.assertEqual(runs, ["python .github/scripts/check_coderabbit_comment.py"])
        self.assertIn("name: Доверенная структура PR", self.yaml)

    def test_validator_replaced_in_pr_does_not_change_trusted_result(self):
        # Запускаем ту же точку входа, что workflow, с API в памяти процесса.
        # Подменённый head также присутствует в PYTHONPATH, но не должен импортироваться.
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            base_scripts = root / "base" / ".github" / "scripts"
            head_scripts = root / "head" / ".github" / "scripts"
            shim = root / "shim"
            for directory in (base_scripts, head_scripts, shim):
                directory.mkdir(parents=True)
            for name in ("check_coderabbit_comment.py", "check_pr_body.py"):
                for directory in (base_scripts, head_scripts):
                    shutil.copy(Path(__file__).with_name(name), directory / name)

            # sitecustomize подменяет только транспорт и записывает происхождение
            # модулей; любой случайный выход в сеть завершается ошибкой.
            (shim / "sitecustomize.py").write_text(
                """\
import atexit
import io
import json
import os
import socket
import sys
import urllib.request
from pathlib import Path

def no_network(*args, **kwargs):
    raise AssertionError("Доступ к сети запрещён")
socket.socket.connect = no_network
socket.socket.connect_ex = no_network
socket.create_connection = no_network
socket.getaddrinfo = no_network

fixture = json.loads(Path(os.environ["API_FIXTURE"]).read_text(encoding="utf-8"))
requests = []
provenance = {}
class Response(io.BytesIO):
    status = 200
    headers = {}
class Opener:
    def open(self, request, timeout):
        provenance["entry"] = getattr(sys.modules["__main__"], "__file__", None)
        provenance["validator"] = getattr(sys.modules.get("check_pr_body"), "__file__", None)
        url = request.full_url
        requests.append(url)
        if request.get_header("Authorization") != "Bearer test-token" or timeout != 30:
            raise AssertionError("Неожиданный запрос")
        if url.endswith("/pulls/17"):
            data = fixture["pr"]
        elif url.endswith("/issues/17/comments?per_page=100&page=1"):
            data = fixture["comments"]
        else:
            raise AssertionError("Неожиданный URL API: " + url)
        return Response(json.dumps(data).encode("utf-8"))
urllib.request.build_opener = lambda *args: Opener()

def record():
    Path(os.environ["TRACE_PATH"]).write_text(json.dumps({
        **provenance, "requests": requests,
    }), encoding="utf-8")
atexit.register(record)
""",
                encoding="utf-8",
            )
            sha = "a" * 40
            event = root / "event.json"
            event.write_text(
                json.dumps(
                    {
                        "repository": {"full_name": "owner/repo"},
                        "pull_request": {
                            "number": 17,
                            "head": {"sha": sha},
                            "base": {"sha": "c" * 40},
                        },
                    }
                ),
                encoding="utf-8",
            )
            fixture = root / "api.json"
            trace = root / "trace.json"
            env = {
                **os.environ,
                "PYTHONIOENCODING": "utf-8",
                "PYTHONDONTWRITEBYTECODE": "1",
                "PYTHONPATH": str(shim) + os.pathsep + str(head_scripts),
                "GITHUB_REPOSITORY": "owner/repo",
                "PR_NUMBER": "17",
                "GITHUB_TOKEN": "test-token",
                "GITHUB_EVENT_PATH": str(event),
                "API_FIXTURE": str(fixture),
                "TRACE_PATH": str(trace),
            }
            pr = {
                "number": 17,
                "user": {"id": 1, "login": "someone"},
                "head": {"ref": "feat/x", "sha": sha},
                "body": valid_body(),
                "base": {"sha": "c" * 40, "ref": "feature/base"},
                "draft": True,
                "state": "open",
            }
            comment = {
                "id": 1,
                "user": {"id": 1},
                "body": f"<!-- coderabbit-disposition -->\n## CodeRabbit review и disposition\n{CR_HEADER}\n{CR_DELIMITER}\n{CR_ROW}",
            }
            cases = (
                ("valid", valid_body(), comment, 0, ""),
                (
                    "invalid_body",
                    "## Цель\nТолько цель.",
                    comment,
                    1,
                    "Отсутствует обязательный заголовок",
                ),
                (
                    "invalid_comment",
                    valid_body(),
                    {**comment, "body": "<!-- coderabbit-disposition -->\nнет таблицы"},
                    1,
                    "заголовок накопительной таблицы",
                ),
            )
            expected_requests = [
                "https://api.github.com/repos/owner/repo/pulls/17",
                "https://api.github.com/repos/owner/repo/issues/17/comments?per_page=100&page=1",
                "https://api.github.com/repos/owner/repo/pulls/17",
                "https://api.github.com/repos/owner/repo/issues/17/comments?per_page=100&page=1",
            ]

            def run_case(body, current_comment):
                fixture.write_text(
                    json.dumps(
                        {"pr": {**pr, "body": body}, "comments": [current_comment]}
                    ),
                    encoding="utf-8",
                )
                # Статическая команда запускает валидатор из base без shell.
                result = subprocess.run(  # noqa: S603
                    ENTRY_COMMAND,
                    cwd=root / "base",
                    env=env,
                    capture_output=True,
                    text=True,
                    encoding="utf-8",
                    check=False,
                )
                provenance = json.loads(trace.read_text(encoding="utf-8"))
                self.assertIsNotNone(provenance["entry"], result.stderr)
                self.assertIsNotNone(provenance["validator"], result.stderr)
                self.assertEqual(
                    Path(provenance["entry"]).resolve(),
                    base_scripts / "check_coderabbit_comment.py",
                )
                self.assertEqual(
                    Path(provenance["validator"]).resolve(),
                    base_scripts / "check_pr_body.py",
                )
                self.assertEqual(provenance["requests"], expected_requests)
                return result

            baseline = {}
            for name, body, current_comment, status, diagnostic in cases:
                with self.subTest(case=name, head="original"):
                    result = run_case(body, current_comment)
                    self.assertEqual(result.returncode, status, result.stderr)
                    self.assertIn(diagnostic, result.stderr)
                    baseline[name] = (result.returncode, result.stdout, result.stderr)

            (head_scripts / "check_coderabbit_comment.py").write_text(
                "print('ТОЧКА ВХОДА HEAD ВЫПОЛНЕНА')\n", encoding="utf-8"
            )
            (head_scripts / "check_pr_body.py").write_text(
                "raise RuntimeError('ВАЛИДАТОР HEAD ИМПОРТИРОВАН')\n", encoding="utf-8"
            )
            for name, body, current_comment, _, _ in cases:
                with self.subTest(case=name, head="replaced"):
                    result = run_case(body, current_comment)
                    self.assertEqual(
                        (result.returncode, result.stdout, result.stderr),
                        baseline[name],
                    )

            # Контроль: запуск из head действительно исполняет подменённую точку входа.
            # Команда фиксирована и запускается без shell.
            head_result = subprocess.run(  # noqa: S603
                ENTRY_COMMAND,
                cwd=root / "head",
                env=env,
                capture_output=True,
                text=True,
                encoding="utf-8",
                check=False,
            )
            self.assertEqual(head_result.returncode, 0, head_result.stderr)
            self.assertIn("ТОЧКА ВХОДА HEAD ВЫПОЛНЕНА", head_result.stdout)
