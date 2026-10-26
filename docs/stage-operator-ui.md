# Встроенный операторский stage экран

После развёртывания BuildSystem.Api открыть `https://<api-origin>/stage`. Это экран именно этого проекта, не DeepSeek Harness GUI. Новый сервер/публикация не запускались в окружении разработки. Stage shell общедоступен, но не содержит пользовательских metadata; все data endpoints независимо требуют human authentication и применяют owner/admin ACL.

## Возможности

- Cookie-вход существующего пользователя через `/api/auth/login` с anti-CSRF token, без machine bearer.
- GET списка stage-операций по25 записей; фильтр по job state; переходы next/back на основе cursor.
- Детали выбранной операции через `/api/test-environments/{id}/stage-status`.
- Ручное обновление, SQL observation timestamp, warning о Held reservation/unsettled provisioning, ContentVerified не является Ready или proof формата/наличия файла.
- Выход с CSRF; при неопределённом исходе logout metadata сразу скрываются, остаётся явная кнопка повторить выход.

Нет reset/reassign/restore/cleanup/admin buttons, automatic polling, browser token storage и сохранения пароля. Единственные POST — login/logout, оба с CSRF. Пароль очищается после завершения попытки входа, не логируется. Все запросы same-origin credentials/no-store/redirect:error. HTTPS и защищённые cookie настраиваются сервером; сертификат нельзя обходить.

## Безопасность UI

DOM строится createElement/textContent/replaceChildren, без HTML интерпретации данных. Inline scripts/styles/handlers и внешние зависимости отсутствуют. CSP default-src none, script/style/connect self, frame-ancestors none, form-action self и base-uri none. Также no-store, nosniff, Referrer-Policy no-referrer, X-Frame-Options DENY. Ресурсы embedded в API assembly и выдаются только allowlisted маршрутами; arbitrary filesystem static serving не включён.

Отдельные AbortController + generation markers для main/detail не позволяют ответу старого запроса заполнить экран после logout/filter/refresh. Список очищается при смене контекста. 401 возвращает login;403/404 и ошибки показываются без произвольного response body. Error details и API metadata не вставляются в HTML.

Сам read-only shell не доказывает SQL/agent доступность. При network logout failure cookie может остаться действующей на сервере: UI честно показывает неизвестный исход и предлагает retry, не утверждая, что сервер завершил сеанс.

## Проверки и ограничения

Локально locked restore и Release build успешны,0 warnings/0 errors. **308 .NET passed,0 failed,53 SQL skipped** плюс **4 Node behavior tests passed**. API63, domain39, Agent135, Archive23, Process22, Pipeline26. Сначала HTTP tests выявили ambiguous `/stage` и `/stage/`: ASP.NET считает их одним маршрутом, duplicate mapping удалён, оба URL теперь проверены. Дополнительно статическое read-only security review не выявило серьёзных XSS/session/stale-response дефектов. HTTP tests проверяют embedded HTML/JS/CSS, security headers, public shell/human data separation и allowlisted resources. Node built-in tests с минимальным mock DOM проверяют anonymous flow, CSRF/password cleanup, stale list after logout и logout retry. Это не полноценный browser E2E: реальный Identity cookie login, CSP enforcement, accessibility/viewport и visual review должны проверяться браузером на Windows стенде. Runtime screenshots не создавались.

Предыдущий list commit9a37d5b: [SQL40335810723](https://github.com/nspirit2/bild_1c_server/actions/runs/40335810723) success; [build40335810774](https://github.com/nspirit2/bild_1c_server/actions/runs/40335810774) Ubuntu success, Windows test step failure. Причина Windows failure неизвестна до TRX; не считать её исправленной этим UI пакетом. Последующий UI commitad12849 прошёл [Windows/Ubuntu40340526535](https://github.com/nspirit2/bild_1c_server/actions/runs/40340526535) и [SQL40340526089](https://github.com/nspirit2/bild_1c_server/actions/runs/40340526089), оба success подтверждены GitHub API. Это зелёный последующий прогон, не установленная причина прежнего failure. SQL schema/agent protocol не меняются; SQL guard остаётся53.

### Расширенные тесты UI

Node suite расширен с4 до **9 passing scenarios**: filter сбрасывает cursor history и отбрасывает late prior-filter response; next/back URL-encode opaque cursor; detail выводит15 полей без новых mutation requests; late detail после filter не открывает panel; list401 очищает metadata и возвращает login. Локальная повторная .NET регрессия: **308 passed,0 failed,53 SQL skipped**. Команда: `node --test tests/stage-ui.test.cjs`.

Headless browser инструментария в этой среде нет: playwright/puppeteer и Chromium/Chrome отсутствуют, browser endpoint не настроен. Реальный browser E2E не запускался и не подменяется mock DOM проверкой.
