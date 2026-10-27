# Заказ из архива в UI

Форма в /stage использует существующий human POST `/api/environment-requests`. Вход через cookies, fresh anti-CSRF token на каждую попытку, Idempotency-Key UUID. Это заказ существующего ProvisionArchive/stage workflow, **не restore** и не обещание Ready; stage flags defaultoff и доступность/quota повторно проверяет сервер.

## Протокол клиента

Выбор только из текущего project/archive page, validated GUID; displayName1..120 без controls/крайних пробелов, ttlDays1..36500 (server quotas могут быть строже). Запрос содержит projectId/displayName/mode=archive/source.archiveVersionId/ttlDays, никаких server paths/hash/owner/targets.

Перед первой отправкой фиксируется immutable JSON+UUID intent в памяти. Повтор при network/5xx/malformedreceipt/неизвестном исходе использует тот же ключ и payload, даже если оператор попытался поменять поля. Busy double-click не создаёт второй POST. Контекст selection/navigation блокируется при outstanding intent; не заменяет серверную авторизацию.

Только202 с корректными непустыми request/environment/job GUID receipt считается подтверждённым принятием. Это не успешное завершение stage. Retry получает новый CSRF, но не новый Idempotency-Key. Никаких automatic retry POST.

Logout/session loss/перезагрузка страницы теряют memory-only intent; это не отмена принятого сервером заказа. Перед уходом при unresolved intent browser beforeunload warning; browser может не показать его без user interaction. Explicit abandon требует осознанного подтверждения и не отменяет серверную операцию; перед новым заказом после неизвестного исхода нужно проверить историю. Секреты/key/payload не сохраняются в browser storage/URL/logs. Автовосстановление после browser crash не реализовано; durable outbox здесь не заявлен.

## Проверки

Локальная регрессия: **324 .NET passed,0 failed,63 SQL skipped**, **24 Node cases passed** на финальном повторном прогоне, включая дополнительный logout retry warning case. API79,domain39,Agent135,Archive23,Process22,Pipeline26. Read-only review выявил потерю unknown-outcome warning при финальных logout messages/retry; исправлено сохранением warning в session-local forgotten flag, без storage. Остальные серьёзные exactretry/CSRF/stale-response дефекты review не установил. Node mock tests проверяют отправку/CSRF/exactretry/duplicateclick/invalidinput/malformedreceipt/sessionraces; не заменяют настоящий browser E2E. HTTP application-port tests подтверждают key/payload forwarding, acceptedreceipt и409/conflict; stub не доказывает SQL идемпотентность. Существующий SQL suite уже проверяет реальные admissions/replays, но UI+Identity+SQL в одном E2E пока не проверены.

Предыдущий project/archive browser78be36b прошёл [Windows/Ubuntu40511572208](https://github.com/nspirit2/bild_1c_server/actions/runs/40511572208) и [SQL40511572087](https://github.com/nspirit2/bild_1c_server/actions/runs/40511572087), оба success поGitHubAPI. Реальный Windows SQL63/63 отражён в project-archive-browser.md; нельзя переименовать его в test именно текущего UI.

В рамках этого пакета дополнительно выполнен повтор **9 существующих SqlEnvironmentOrderTests на Windows SQL:9passed,0failed,0skipped**,11s. Использован уже проверенный isolated test package предыдущего project-browser с тем же неизменённым admission implementation; это recheck серверной идемпотентности, не E2E нового JS. После прогона оставшихся bs_ci_metadata_GUID databases0. Основная BuildSystem не использовалась fixture. TRX сохранён в существующем TestRuns directory, новое SDK/службы не устанавливались.

Deployment в этом пакете не выполнялся, Windows API остаётся наc3a665a. Никакие реальные заказы в application DB не создавались. Restore,Ready,reset,reassign,cleanup и agent service не добавлены.
