# Lease-aware stage coordinator

## Назначение и границы

StageCoordinator связывает существующие fingerprint/staging и artifact outbox. Это standalone API для будущего закрытого обработчика archive.stage, не включение ProvisionArchive dispatcher. Production Worker по-прежнему только ControlPlaneProbe: coordinator не зарегистрирован в его loop/registry/DI и не получает пользовательские JSON snapshot напрямую. Входной StageIntent должен построить будущий проверенный compiler/dispatcher из серверного immutable intent, не браузер.

Предыдущий outbox коммит `644cdec` прошёл [Windows/Linux CI](https://github.com/nspirit2/bild_1c_server/actions/runs/39946303044) и [SQL CI](https://github.com/nspirit2/bild_1c_server/actions/runs/39946303121), оба success по GitHub API. Эти результаты не заменяют проверку текущего coordinator.

## Durable intent

Перед файловыми side effects SaveStageIntent записывает job/fence, reservation/version/root, canonical relative path и fingerprint. Это чувствительные локальные metadata: абсолютный путь, токен и snapshot не сохраняются; относительный путь нужен для восстановления через operator-approved root mapping и не должен логироваться. Каталог журнала защищается ACL.

Новые intent допускаются только для живого незавершённого ProvisionArchive lease. Ключ job/fence и unique reservation/version предотвращают перепривязку; exact replay не меняет pinned values и не стирает failure. Schema создаётся совместимо в существующей SQLite БД. Если intent есть, новый artifact enqueue должен совпасть с его fingerprint и ссылками; failed intent не допускает создание нового отчёта.

## Порядок выполнения

1. Persist immutable intent до copy.
2. Проверить локальную незавершённость/тип/expiry lease и вызвать injected server guard.
3. Обновить локальный expiry из успешного server status.
4. Вызвать ArchiveStaging с linked cancellation до текущего lease deadline. Не продлевать lease во время copy автоматически.
5. После copy снова проверить caller cancellation и server guard, до enqueue.
6. Сверить результат по size/hash/ArtifactId; enqueue того же durable report.

Guard должен проверять текущий server lease/agent/fence. Сам delegate не является доказательством авторизации; тестовый guard не выдаётся за работающий server protocol. Штатный server heartbeat сейчас не продлевает dormant ProvisionArchive/Leased задания — archive scheduler/heartbeat контракт ещё надо согласовать. Нельзя подключать этот API к production Poll просто добавлением capability.

## Crash/restart

Падение после intent, но до copy: ResumeAsync использует прежние pins.
Падение после атомарной публикации data/manifest, но до enqueue: ResumeAsync повторно проверяет тот же детерминированный artifact и enqueues его, не создавая вторую копию. Исходник при этом уже может отсутствовать: проверяется сохранённая копия.
Падение после enqueue: existing artifact payload идемпотентен; доставка остаётся обязанностью outbox dispatcher. Ack не завершает lease и не открывает новую операцию.

Неуспешный lease guard, expiry, отмена или ArchiveChanged фиксируют закрытый failure code и блокируют автоматический resume. Published artifact не удаляется даже при post-copy отказе: это ресурс для reconciliation. Код ошибки может дополнить локальный artifact Blocked, но никогда не превратить его в Acked. Operator reset/reconciliation API отсутствует; очистка вручную без сверки не допускается.

Resume доверяет операторской привязке RootId к root и staging directory. Их изменение между запусками требует отдельной проверки конфигурации/переноса metadata; coordinator не решает миграцию storage автоматически. Принятая cancellation не доказывает отсутствие опубликованных байтов.

## Ограничения

Нет restore/VERIFYONLY/1С вызовов, независимой форматной валидации и Ready transition. Deadline копирования кооперативный; при UNC/kernel IO отмена может задержаться, но post-copy guard не допускает enqueue после обнаруженного stale lease. Revocation во время copy не наблюдается непрерывно: копирование локального файла может закончиться, однако приём receipt требует нового live server check.

StageCoordinator и ArtifactDelivery должны вызываться последовательно: AgentJournal — один serialized owner, не thread-safe store. Вызов Resume с уже Acked artifact допустим для idempotency, но не повторного исполнения. Sqlite ошибки/неожиданные managed exceptions оставляют durable intent и не приводят к ложному ack; нужны диагностика и reconciliation.

## Проверки

Фактический локальный результат: locked restore успешен, Release build0 warnings/0 errors, **228 passed, 0 failed, 26 SQL skipped** (domain39, API40, Agent78, Archive23, Process22, Pipeline26). Новые17 тестов: stage intent journal6, coordinator11, включая re-open SQLite, crash после публикации до enqueue с удалённым source, corrupt existingartifact, конфликтpins, два guard, postcopyrejection, cancellation, probe/expiry отказ и сохранение Acked/Blocked при replay.

Read-only review выявило ошибочный outcome Enqueued для ранее Blocked artifact replay: исправлено проверкой persisted delivery state и отдельным regression test. Других серьёзных rebinding/false-ack/crash ошибок в заданном scope не установлено; review статическое.

Проект Agent теперь ссылается на Archives library; lockfiles Agent/AgentTests обновлены обычным restore. SQL model не меняется, новой server migration нет. Новый пакет пока локальный, без commit/push/merge. Windows/UNC, actual server lease и power-loss испытания отдельны от Linux filesystem тестов и ещё не выполнены для этого кода.
