# Локальный барьер попытки создания цели

## Назначение

Перед вызовом SQL CREATE/регистрацией IB необходимо сохранить намерение и факт начала попытки. Сбой между внешним действием и ответом не разрешает слепой повтор. Локальный SQLite-журнал хранит recovery metadata отдельно от stage-only lease: stage completion/grant не становится полномочием создания/restore.

Это **не журнал доказанного владения**, не server restore grant, не distributed lock и не автоматическое создание ресурса. Единственный агент сериализует доступ к AgentJournal; canonical cluster/SQL IDs должен заранее разрешить доверенный операторский mapping. DNS/SQL aliases не канонизируются этим журналом. Между разными агентами/копиями журнала локальные unique constraints не координируют ресурсы.

## Контракт

Immutable intent фиксирует operation/environment/job/fence/reservation/incarnation, canonical cluster и SQL-server IDs, точные IB/DB имена bs_test_{reservation:N}. Раздельные UNIQUE ограничения SQL-server+DB и cluster+IB запрещают частичные коллизии; целиком новый mapping GUID не должен обходить локальный конфликт физической цели.

Состояния: IntentRecorded → CreationInFlight → ReconciliationRequired. Intent записывается до внешних действий. Начало попытки использует expected revision и attempt ID. Повтор того же начала возвращает NewlyRecorded=false, а не повторный сигнал исполнения. Смена attempt/пинов после начала не позволяет retry. Quarantine сохраняет пины, попытку и занятые слоты. Никакого reset/release/delete/adopt/MarkOwned/успешной внешней операции в этом срезе нет. Quarantine повтор со старым expected revision отклоняет, а не подтверждает идемпотентно: после неизвестного ответа сначала перечитать запись. Inventory — ограниченная первая страница до1000 записей, не полнота всего журнала и не очередь исполнения; pagination отложена.

Даже NewlyRecorded=true означает только новую durable запись, **не разрешение side effect**: будущий coordinator обязан отдельно проверить fresh server authority, owned empty target prerequisites, artifact, sessions и scheduled jobs deny. До начала/после restore и после рестарта нужны отдельные свежие readbacks; наблюдения не доказывают состояние между проверками.

## Проверки

Release AgentTests284passed, включая12новых cases. Полный локальный Release628passed/0failed, 114SQL explicitly skipped; locked restore success, diff check success. Тесты проверяют reopen каждого состояния без recovery, exact replay, изменённые пины, независимые частичные коллизии, CAS и отсутствие повторного begin-сигнала после quarantine, сохранение барьеров при stage lease expiry/settlement, SQL CHECK null pairing, additive initialization старого journal и redacted diagnostics. Независимый review обнаружил SQLite CHECK NULL особенность; исправлено явным attempt_revision IS NOT NULL и закреплено direct storage tests. Обычные SQL EF migrations не менялись; has-pending-model-changes сообщает отсутствие изменений.

## Границы разработки

Нет подключения к Worker/StepRegistry/CLI/production deployment. Нет EF/server schema изменения и новых IB/SQL DB на стенде. Существующая ручная smoke IB не усыновляется и не перезаписывается автоматически. Запрет регламентных заданий обязателен; security profile не является обязательным этапом по решению владельца.
