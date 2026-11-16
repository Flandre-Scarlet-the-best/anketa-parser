#!/usr/bin/env python3
"""Проверить опубликованные PR body и комментарий CodeRabbit через GitHub API."""

import http.client
import json
import os
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from collections.abc import Mapping, Sequence
from pathlib import Path
from typing import IO, TypeGuard, cast, override

from check_pr_body import (
    FENCE_CLOSE,
    fence_open,
    is_automation_pr,
    strip_comments,
    validate,
    validate_coderabbit,
)

MARKER = "<!-- coderabbit-disposition -->"
type JSONValue = (
    None | bool | int | float | str | list[JSONValue] | Mapping[str, JSONValue]
)
API = "https://api.github.com"
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")
LINK = re.compile(r'\s*<([^<>]+)>\s*;\s*rel="([a-z]+)"\s*')


def _parse_json(value: str | bytes) -> JSONValue:
    # json.loads возвращает Any в typeshed.
    # После успешного разбора возможны только типы JSON.
    return cast(JSONValue, json.loads(value))


class CheckError(Exception):
    """Невозможно достоверно проверить опубликованное состояние."""


class NoRedirect(urllib.request.HTTPRedirectHandler):
    # Токен никогда не отправляется на иной адрес после перенаправления.
    @override
    def redirect_request(
        self,
        req: urllib.request.Request,
        fp: IO[bytes],
        code: int,
        msg: str,
        headers: http.client.HTTPMessage,
        newurl: str,
    ) -> urllib.request.Request | None:
        return None


def _is_json_object(value: JSONValue) -> TypeGuard[dict[str, JSONValue]]:
    return isinstance(value, dict)


def _is_json_array(value: JSONValue) -> TypeGuard[list[JSONValue]]:
    return isinstance(value, list)


def positive_id(value: object) -> TypeGuard[int]:
    return type(value) is int and value > 0


def get_json(path: str, token: str) -> tuple[JSONValue, str | None]:
    # Адрес собран из константы API и проверенного path; NoRedirect обрывает
    # перенаправление, поэтому токен не уходит на чужой хост, а ссылки
    # заголовка Link дополнительно разбираются и проверяются в next_page.
    request = urllib.request.Request(  # noqa: S310
        API + path,
        headers={
            "Accept": "application/vnd.github+json",
            "Authorization": "Bearer " + token,
            "X-GitHub-Api-Version": "2022-11-28",
            "User-Agent": "pr-disposition-validator",
        },
    )
    opener = urllib.request.build_opener(NoRedirect)
    for attempt in range(3):
        try:
            # stubs описывают результат open как Any; для http(s) это HTTPResponse,
            # и его поля проверяются ниже, поэтому тип уточняется один раз здесь.
            with cast(
                http.client.HTTPResponse, opener.open(request, timeout=30)
            ) as response:
                if 500 <= response.status <= 599:
                    retry = True
                elif response.status != 200:
                    raise CheckError("GitHub API вернул неожиданный HTTP-статус")
                else:
                    payload = _parse_json(response.read())
                    return payload, response.headers.get("Link")
        except urllib.error.HTTPError as exc:
            # HTTPError также содержит открытый ответ; закрыть до повтора или отказа.
            retry = 500 <= exc.code <= 599
            exc.close()
            if not retry:
                raise CheckError("GitHub API вернул неожиданный HTTP-статус") from None
        except OSError, urllib.error.URLError, http.client.HTTPException:
            retry = True
        except ValueError, UnicodeError:
            raise CheckError("Некорректный JSON GitHub API") from None
        if retry and attempt < 2:
            time.sleep(0.1 * (attempt + 1))
            continue
        # Исключения могут содержать URL, тело ответа или токен.
        raise CheckError(
            "Не удалось получить ответ GitHub API после повторов"
        ) from None
    raise CheckError("Не удалось получить ответ GitHub API после повторов")


def next_page(link: str | None, path: str, current: int) -> int | None:
    """Проверить Link, не переходя по недоверенному URL с токеном."""
    if link is None:
        return None
    links = link.split(",")
    relations: dict[str, str] = {}
    for item in links:
        match = LINK.fullmatch(item)
        if not match or match.group(2) in relations:
            raise CheckError("Некорректный заголовок пагинации GitHub API")
        relations[match.group(2)] = match.group(1)
    resource = re.fullmatch(r"/repos/[^/]+/[^/]+(/issues/[1-9][0-9]*/comments)", path)
    # GitHub может вернуть каноническую ссылку /repositories/{id}/... вместо
    # /repos/{owner}/{repo}/.... По ссылке не переходим: следующий запрос всё
    # равно строится из проверенного исходного path.
    pages: dict[str, int] = {}
    for relation, destination in relations.items():
        try:
            url = urllib.parse.urlsplit(destination)
            params = urllib.parse.parse_qs(url.query, strict_parsing=True)
        except ValueError:
            raise CheckError("Некорректная ссылка пагинации") from None
        canonical = resource and re.fullmatch(
            r"/repositories/[1-9][0-9]*" + re.escape(resource.group(1)), url.path
        )
        page = params.get("page", [])
        if (
            relation not in {"first", "prev", "next", "last"}
            or url.scheme != "https"
            or url.netloc != "api.github.com"
            or (url.path != path and not canonical)
            or url.fragment
            or set(params) != {"page", "per_page"}
            or len(page) != 1
            or not re.fullmatch(r"[1-9][0-9]{0,4}", page[0])
            or params["per_page"] != ["100"]
        ):
            raise CheckError("Недопустимая ссылка пагинации")
        pages[relation] = int(page[0])
    if (
        ("next" in pages and pages["next"] != current + 1)
        or ("prev" in pages and pages["prev"] != current - 1)
        or ("first" in pages and pages["first"] != 1)
        or ("last" in pages and pages["last"] < current)
        or (pages.get("last", current) > current and "next" not in pages)
        or ("next" in pages and pages.get("last", current + 1) < pages["next"])
    ):
        raise CheckError("Несогласованная пагинация GitHub API")
    return pages.get("next")


def list_comments(
    repository: str, number: int, token: str
) -> list[dict[str, JSONValue]]:
    path = f"/repos/{repository}/issues/{number}/comments"
    page = 1
    comments: list[dict[str, JSONValue]] = []
    ids: set[int] = set()
    while True:
        if page > 10000:
            raise CheckError("Превышен предел страниц комментариев")
        data, link = get_json(f"{path}?per_page=100&page={page}", token)
        if not _is_json_array(data) or len(data) > 100:
            raise CheckError("Некорректная страница комментариев GitHub API")
        for comment in data:
            if not _is_json_object(comment):
                raise CheckError("Некорректный ID комментария GitHub API")
            comment_id = comment.get("id")
            if type(comment_id) is not int or comment_id <= 0:
                raise CheckError("Некорректный ID комментария GitHub API")
            if comment_id in ids:
                raise CheckError("Повторный ID комментария при пагинации")
            ids.add(comment_id)
            if not isinstance(comment.get("body"), str):
                raise CheckError("Некорректное тело комментария GitHub API")
            comments.append(comment)
        following = next_page(link, path, page)
        if following is None:
            # Страница ровно на 100 записей может быть последней; запросить ещё
            # одну, чтобы не пропустить записи при отсутствующем Link.
            if len(data) < 100:
                return comments
            following = page + 1
        if not data:
            raise CheckError("Пустая страница содержит ссылку продолжения")
        page = following


def _comment_snapshot(
    comments: Sequence[Mapping[str, JSONValue]],
) -> list[tuple[JSONValue, ...]]:
    snapshot: list[tuple[JSONValue, ...]] = []
    for comment in comments:
        user = comment.get("user")
        if _is_json_object(user):
            author_id = user.get("id")
            author_login = user.get("login")
        else:
            author_id = None
            author_login = None
        snapshot.append(
            (
                comment.get("id"),
                author_id,
                author_login,
                comment.get("body"),
                comment.get("updated_at"),
            )
        )
    return snapshot


def _html_block_terminator(line: str) -> str | None:
    """Вернуть закрывающий маркер HTML-блоков CommonMark типов 3–5."""
    if len(line) - len(line.lstrip(" ")) > 3:
        return None
    stripped = line.lstrip(" ")
    if stripped.startswith("<?"):
        return "?>"
    if stripped.startswith("<![CDATA["):
        return "]]>"
    if re.match(r"<![A-Z]", stripped):
        return ">"
    return None


def visible_comment(body: str) -> str:
    """Оставить только видимый Markdown вне fenced-блоков и HTML-комментариев."""
    lines = strip_comments(body.replace("\r\n", "\n").replace("\r", "\n").split("\n"))
    result: list[str] = []
    char = ""
    length = 0
    html_block = False
    raw_tag = ""
    html_terminator = ""
    for line in lines:
        if raw_tag:
            if re.search(rf"</{raw_tag}\s*>", line, re.IGNORECASE):
                raw_tag = ""
            result.append("")
            continue
        if html_terminator:
            if html_terminator in line:
                html_terminator = ""
            result.append("")
            continue
        if html_block:
            if not line.strip():
                html_block = False
            result.append("")
            continue
        if char:
            close = FENCE_CLOSE.match(line)
            if close and close.group(1)[0] == char and len(close.group(1)) >= length:
                char = ""
            result.append("")
        elif opening := fence_open(line):
            char, length = opening
            result.append("")
        elif terminator := _html_block_terminator(line):
            if terminator not in line:
                html_terminator = terminator
            result.append("")
        elif tag := re.match(
            r"^ {0,3}<(?P<closing>/)?(?P<name>[A-Za-z][A-Za-z0-9-]*)(?:\s|/?>)", line
        ):
            name = tag.group("name").lower()
            if name in {"script", "pre", "style", "textarea"} and not tag.group(
                "closing"
            ):
                if not re.search(rf"</{name}\s*>", line, re.IGNORECASE):
                    raw_tag = name
            else:
                html_block = True
            result.append("")
        else:
            result.append(line)
    return "\n".join(result)


def validate_comments(
    comments: Sequence[Mapping[str, JSONValue]], author_id: int
) -> list[str]:
    marked: list[tuple[Mapping[str, JSONValue], str]] = []
    for comment in comments:
        body = comment.get("body")
        if not isinstance(body, str):
            return ["Некорректное тело комментария GitHub API"]
        if MARKER in body:
            marked.append((comment, body))
    if len(marked) > 1:
        return ["Найдено несколько служебных комментариев CodeRabbit"]
    if not marked:
        return []
    comment, body = marked[0]
    user = comment.get("user")
    if (
        not _is_json_object(user)
        or not positive_id(user.get("id"))
        or user["id"] != author_id
    ):
        return ["Автор служебного комментария CodeRabbit не является автором PR"]
    body = body.replace("\r\n", "\n").replace("\r", "\n")
    if body.count(MARKER) != 1 or MARKER not in body.split("\n"):
        return ["Маркер CodeRabbit должен встречаться один раз отдельной строкой"]
    # Проверить, что маркер не вложен в иной HTML-комментарий или код-блок.
    sentinel = "CODERABBIT_MARKER_LOCATION_SENTINEL"
    marker_line = body.split("\n").index(MARKER)
    if (
        visible_comment(body.replace(MARKER, sentinel)).split("\n")[marker_line]
        != sentinel
    ):
        return ["Маркер CodeRabbit скрыт внутри комментария или блока кода"]
    return validate_coderabbit(visible_comment(body))


def run(env: Mapping[str, str]) -> list[str]:
    repository = env.get("GITHUB_REPOSITORY", "")
    number_text = env.get("PR_NUMBER", "")
    token = env.get("GITHUB_TOKEN", "")
    event_path = env.get("GITHUB_EVENT_PATH", "")
    if (
        not REPOSITORY.fullmatch(repository)
        or any(part in {".", ".."} for part in repository.split("/"))
        or not re.fullmatch(r"[1-9][0-9]{0,14}", number_text)
    ):
        raise CheckError("Некорректные GITHUB_REPOSITORY или PR_NUMBER")
    if not token or not event_path or any(not 33 <= ord(c) <= 126 for c in token):
        raise CheckError(
            "Отсутствуют или некорректны GITHUB_TOKEN или GITHUB_EVENT_PATH"
        )
    try:
        event_data = _parse_json(Path(event_path).read_text(encoding="utf-8"))
    except (OSError, UnicodeError, ValueError, KeyError, TypeError) as exc:
        raise CheckError("Некорректный файл события pull_request") from exc
    if not _is_json_object(event_data):
        raise CheckError("Некорректный файл события pull_request")
    event_pr_data = event_data.get("pull_request")
    event_repository_data = event_data.get("repository")
    if not _is_json_object(event_pr_data) or not _is_json_object(event_repository_data):
        raise CheckError("Некорректный файл события pull_request")
    event_head_data = event_pr_data.get("head")
    event_base_data = event_pr_data.get("base")
    if not _is_json_object(event_head_data) or not _is_json_object(event_base_data):
        raise CheckError("Некорректный файл события pull_request")
    event_repo = event_repository_data.get("full_name")
    event_number = event_pr_data.get("number")
    event_sha = event_head_data.get("sha")
    event_base = event_base_data.get("sha")
    number = int(number_text)
    if (
        event_repo != repository
        or not positive_id(event_number)
        or event_number != number
        or not isinstance(event_sha, str)
        or not SHA.fullmatch(event_sha)
        or not isinstance(event_base, str)
        or not SHA.fullmatch(event_base)
    ):
        raise CheckError("Номер, репозиторий, base или HEAD события некорректны")
    pr_data, _ = get_json(f"/repos/{repository}/pulls/{number}", token)
    if not _is_json_object(pr_data):
        raise CheckError("Некорректный объект PR из GitHub API")
    pr = pr_data
    try:
        author_data = pr["user"]
        head_data = pr["head"]
        base_data = pr["base"]
        if (
            not _is_json_object(author_data)
            or not _is_json_object(head_data)
            or not _is_json_object(base_data)
        ):
            raise TypeError
        author = author_data
        head = head_data
        base = base_data
        author_id = author["id"]
        login = author["login"]
        branch = head["ref"]
        sha = head["sha"]
        body = pr["body"]
        pr_number = pr["number"]
        base_sha = base["sha"]
        base_ref = base["ref"]
        draft = pr["draft"]
        state = pr["state"]
    except (KeyError, TypeError) as exc:
        raise CheckError("Некорректный объект PR из GitHub API") from exc
    if (
        not positive_id(pr_number)
        or pr_number != number
        or not positive_id(author_id)
        or not isinstance(login, str)
        or not login
        or not isinstance(branch, str)
        or not branch
        or not isinstance(sha, str)
        or not SHA.fullmatch(sha)
        or (body is not None and not isinstance(body, str))
        or not isinstance(base_sha, str)
        or not SHA.fullmatch(base_sha)
        or not isinstance(base_ref, str)
        or not base_ref
        or type(draft) is not bool
        or state not in ("open", "closed")
    ):
        raise CheckError("Некорректные поля PR из GitHub API")
    if sha != event_sha:
        raise CheckError("HEAD PR изменился после события: требуется новая проверка")
    if base_sha != event_base:
        raise CheckError("Base PR изменился после события: требуется новая проверка")
    problems: list[str] = (
        [] if is_automation_pr({"pull_request": pr}) else validate(body or "")
    )
    comments = list_comments(repository, number, token)
    problems.extend(validate_comments(comments, author_id))
    # PR мог измениться во время постраничного чтения комментариев; не выдавать
    # результат на основе смешанных версий опубликованных метаданных и body.
    current_data, _ = get_json(f"/repos/{repository}/pulls/{number}", token)
    if not _is_json_object(current_data):
        raise CheckError("Некорректный PR при повторном чтении")
    current = current_data
    try:
        current_author_data = current["user"]
        current_head_data = current["head"]
        current_base_data = current["base"]
        if (
            not _is_json_object(current_author_data)
            or not _is_json_object(current_head_data)
            or not _is_json_object(current_base_data)
        ):
            raise TypeError
        current_author = current_author_data
        current_head = current_head_data
        current_base = current_base_data
        before = (
            pr["number"],
            author["id"],
            author["login"],
            head["sha"],
            head["ref"],
            pr["body"],
            base["sha"],
            base["ref"],
            pr["draft"],
            pr["state"],
        )
        after = (
            current["number"],
            current_author["id"],
            current_author["login"],
            current_head["sha"],
            current_head["ref"],
            current["body"],
            current_base["sha"],
            current_base["ref"],
            current["draft"],
            current["state"],
        )
    except (KeyError, TypeError) as exc:
        raise CheckError("Некорректный PR при повторном чтении") from exc
    if (
        not positive_id(after[0])
        or not positive_id(after[1])
        or type(after[8]) is not bool
        or after != before
    ):
        raise CheckError("PR изменился во время проверки: запустите её повторно")
    latest_comments = list_comments(repository, number, token)
    if _comment_snapshot(latest_comments) != _comment_snapshot(comments):
        raise CheckError(
            "Комментарии изменились во время проверки: запустите её повторно"
        )
    return problems


def safe_message(message: object) -> str:
    # Не позволять данным из PR сформировать команду workflow или управляющие строки.
    return "".join(c if ord(c) >= 32 and ord(c) != 127 else " " for c in str(message))


def main() -> int:
    try:
        problems = run(os.environ)
    except CheckError as exc:
        print("Не удалось проверить PR: " + safe_message(exc), file=sys.stderr)
        return 2
    if problems:
        for problem in problems:
            print("Ошибка: " + safe_message(problem), file=sys.stderr)
        return 1
    print("Опубликованные PR body и комментарий CodeRabbit соответствуют контракту")
    return 0


if __name__ == "__main__":
    sys.exit(main())
