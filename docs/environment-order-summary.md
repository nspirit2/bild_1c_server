# Исходный заказ из истории окружения

После выбора окружения на `/stage` явная кнопка «Показать исходный заказ» выполняет GET `/api/test-environments/{environmentId}/order-summary`. Полная история запросов-collection не вводится. Пользователь восстанавливает точные requestId/jobId и archiveVersionId через конкретное окружение после перезагрузки, а не угадывает корреляцию одинаковых имён.

## Контракт и ограничения

Root12: environmentId,projectId,displayName,environmentState,requestId,requestState,createdAt,ttlDays,jobId,jobKind,jobState,source. Source4: archiveVersionId,format,sha256,sizeBytes. Обрабатываются ProvisionArchive и ObserveArchiveStage, .dt/.bak. Нет source file path, root path, friendly archive name (такого поля в модели нет), snapshot JSON, credential, topology, idempotency key или replay authority. Исторические metadata state/TTL/source hash не доказывают Ready, удаление, владение целью, наличие staging-файла или разрешение restore.

Существующая cookie policy: administrator либо owner с текущим ProjectMembership; enabled project дополнительно не требуется. Авторизация environment-first до related queries, недоступное/отсутствующее окружение404. Пустой ID400 InvalidEnvironmentOrderSummary; malformed GUID404 route constraint; invalid actor403. Одно serializable read transaction, AsNoTracking, narrow projections без загрузки execution snapshot или пути. Проверяются reciprocal environment/request/job links и owner/project correlation, поддерживаемые job kinds, bounded metadata, UTC createdAt, TTL, source hash/size/format. Повреждённые связи дают фиксированный409 EnvironmentOrderSummaryInconsistent, без внутренних сведений. Source version — историческая идентичность принятого заказа; current catalog/root state не подменяет существующий environment ACL.

## Stage и удержания

После валидного ProvisionArchive summary доступна отдельная кнопка чтения существующих stage-status/cleanup-status. Request/job pins сверяются с summary (cleanup содержит jobId, не requestId). Это независимые наблюдения, не общая атомарная truth и не новый gate. Для ObserveArchiveStage существующие status ports не поддерживают этот сценарий: кнопка не делает запрос, UI явно говорит о недоступности.

У старых stage/cleanup ports свои predicates: stage требует enabled project у non-admin, cleanup — даже у admin; оба исключают stage-only jobs. Поэтому после успешного summary статус может быть недоступен. 404/403/409/сбой не означают отсутствие удержаний или свободное место. Новая связка не меняет существующий protocol и не переводит окружение в Ready.

## UI безопасность

Явные GET без polling/storage/retry, same-origin/no-store/redirect:error. Closed12+4 shape и ID/project correlation; textContent, hash/size/date validation. Отдельный generation/controller summary. Смена строки, страницы, refresh, сеанса, pagehide/pageshow/скрытие вкладки очищают сведения; late body/transport не восстанавливают их. Историей открытый stage-panel также закрывается/отменяется при инвалидации этой истории; самостоятельный старый stage flow не захватывается. 401/403 нового flow очищают workspace через login/reset. Legacy environment details остаются доступны при недоступном summary. Order intent/idempotency/retry создания не меняются.

## Проверки

API fixtures с real protected Identity cookie проверяют точные schema/values, роли, оба job kinds/formats, auth failures, missing/malformed/empty IDs, safe409, no CSRF и no Create dispatch. SQL disposable fixtures проверяют реальные CreateAsync для обоих видов, same-name exact correlation, membership loss/admin, disabled catalog/project history, reciprocal corruption и неизменность lifecycle/audit. UI fixtures проверяют explicit read, stale/auth clearing, unsupported stage-only, stage/cleanup pins и unavailable != no holds. Полный локальный Release2449passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed, включая8newsummary cases. Новые API12passed и offline summary validation1passed;2SQLcases требуют disposable CI database. Независимый review без блокеров. Первый full Release выявил incidental '/cleanup' token в пояснении, который срабатывал на существующий source-level запрет mutation routes; исправлен только текст пояснения без ослабления assertion, повторный full Release/Node зелёный. CI после push фиксируется отдельно; локальные SQL skips не считаются passed.