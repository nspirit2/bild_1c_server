# Stage lease renewal во время копирования

## Задача

Stage-only протокол 9ce3872 прошёл Windows/Linux и SQL CI. Ранее copy был ограничен единственным lease deadline после предварительного heartbeat. Этот инкремент добавляет периодический heartbeat во время copy; режим stage остаётся opt-in и не получает restore/Ready разрешения.

## Последовательность и потокобезопасность

Coordinator запускает только файловую операцию ArchiveStaging как async task. Один управляющий цикл ожидает завершения copy либо renewal timer. Journal и server guard вызываются последовательно этим циклом; отдельного фонового dispatcher/heartbeat, использующего SQLite, нет. Весь coordinator по-прежнему требует одного serialized caller. ProbeWorker и StageHostedWorker не запускаются одновременно.

Интервал renewal по умолчанию30 секунд. Настраиваемый constructor interval должен быть положительным и не более30 секунд; service host использует default. В тестах допускается короткий interval, без отдельного production HTTP/path injection. Internal stage callback нужен только для управляемых тестов длительного копирования.

Каждый heartbeat ограничен **старым** локально известным deadline. Продление применяется только после валидного ответа, полученного до истечения прежнего lease, с future non-regressing expiry. Исключение — первый pre-copy guard: до запуска файловой операции допустимо консервативно сократить локальный срок до server-authoritative future expiry; дальнейшее periodic/final сокращение отклоняется. Принимается только server-authoritative expiry; локальная догадка о продлении запрещена. Полученный после старого deadline ответ не возрождает authority. Local UTC — консервативный guard; часы Windows хоста должны быть синхронизированы.

Renewal продлевает deadline копирования. Перед enqueue остаётся обязательный final heartbeat, даже если copy закончилось во время renewal. Receipt dispatcher и серверный receipt API сохраняют собственные live lease/current token проверки.

## Ошибки и остановка

401/403/409, сетевой сбой, timeout, malformed status или недопустимый срок не разрешают продолжить copy. Linked cancellation останавливает файловую операцию; coordinator наблюдает её завершение/ошибку до возврата и до изменения failure/outbox. Caller cancellation сохраняет Cancelled, expiry LeaseExpired, отказ authority LeaseRejected. Публичные логи не включают response body, token или archive path.

Операции cancel кооперативные. UNC/kernel IO может не завершиться немедленно; coordinator не считает его остановленным только по вызову Cancel и не отпускает журнал следующей итерации, пока copy task не завершилась. Guard implementation должна соблюдать cancellation; штатный AgentTransport ограничивает request/response deadline. Принудительное убийство процесса оставляет durable intent и возможную опубликованную копию для restart/reconciliation.

Если копия уже опубликована перед revoke/cancel/final rejection, байты не удаляются автоматически, receipt не enqueue-ится. После durable Blocked/Cancelled автоматический resume не разблокирует операцию. Внешний reconciliation/reset всё ещё не реализован.

## Не входит в пакет

Нет восстановления баз, 1С/SQL providers, VERIFYONLY, Ready transition, освобождения quota/reservation, завершения stage lease или cleanup. После Acked heartbeat loop не продолжает lease бесконечно: существующее unresolved состояние остаётся для reconciliation. Нет доказательства корректности формата архива и гарантий реального UNC throughput.

SQL/wire schema и server heartbeat контракт не меняются. Отдельное server flag и агентский stage flag остаются default false. Включение на рабочем стенде требует controlled Windows run и сверки source/staging/journal ACL.

## Проверки

Фактический локальный прогон: locked restore успешен, Release build0 warnings/0 errors, **270 passed, 0 failed, 34 SQL skipped** (domain39, API43, Agent117, Archive23, Process22, Pipeline26). Новые13 renewal cases проверяют copy за начальным deadline, periodic expired/http/timeout/regressed/malformed/IO, caller cancel с cleanup, зависший guard и недопустимое возрождение lease, fault copy, final rejection и отсутствие перекрывающихся guard. Для timing используются short real timers и controlled callbacks; реальный долгий UNC copy не проверялся.

Статическое read-only review не выявило дополнительных серьёзных stale-authority/timer/cleanup ошибок. Исходные тесты адаптированы к linked caller+deadline token; промежуточная несовместимость первоначального срока с тестовыми guard исправлена консервативным pre-copy shortening, а periodic/final regression по-прежнему запрещён. SQL/wire model не изменена. Windows/Linux и SQL CI должны проверить текущий commit отдельно. Linux unit/SQLite/fake-server timing tests не являются proof TLS/SCM/DPAPI/UNC либо power-loss.
