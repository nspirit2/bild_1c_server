# Human read-only каталог archive versions

GET `/api/projects/{projectId}/archive-versions?limit=25&cursor=...` предназначен для выбора версии перед будущей формой заказа. Это не inventory ReportAsync агента и не разрешение restore.

## Авторизация и scope

Human auth required; anonymous/machine bearer не допускаются. Project должен быть enabled. Member видит только текущие разрешённые ему проекты; Administrator bypass membership совпадает с CreateAsync admission, но **не** bypass root/project ACL и enabled root. Missing/disabled/foreign project даёт404 ProjectNotFound без перечисления чужих resources. ACL применяется на каждой странице.

Версии: Ready, format dt/bak, положительные size/estimatedDB bytes; root enabled и явно разрешён проекту. Current selection metadata не обещает available agent/pool/capacity и не заменяет все повторные проверки CreateAsync при заказе. Catalog Ready — состояние публикации metadata, не proof формата/наличия файла.

DTO item только ArchiveVersionId, Format, SizeBytes, EstimatedDbBytes. Нет source path, filename, root/agent identity, hash, snapshot, SQL targets или free-text errors. Для usability versionId пока единственная безопасная label; friendly catalog title — отдельная будущая модель, не basename source path.

## Pagination и read-only

Limit1..100 default25. Invalidquery/cursor даёт400 InvalidArchiveCatalogQuery до SQL. Cursor opaque/canonical/base64url bounded512, version/project/actor/role bound; unsigned cursor не authority. Меняя cursor, нельзя расширить current authorized query. Sort lower GUID textual BIN2 ASC; next key сравнивается в SQL тем же выражением. Take(limit+1), без count/offset/N+1. Materialization bounded, SQL sort/scan не гарантирован O(limit): computed text key пока без индексной оптимизации.

Страница читается в execution-strategy Serializable no-tracking transaction, SQLUTC ObservedAt, без sweep/SaveChanges/audit writes. Между страницами нет общего snapshot; ACL/version changes могут изменить состав. Corrupt/неподходящие source metadata исключены, список не является forensic audit. Source root ACL не раскрывает путь.

## Проверки

Локально locked restore успешен, Release build0 warnings/0 errors. **315 .NET passed,0 failed,59 SQL skipped**, плюс **9 Node behavior tests passed**. API70, domain39, Agent135, Archive23, Process22, Pipeline26. Новые7 HTTP/application cases проверяют human/machine boundary, query forwarding,404 и invalidquery до SQL. Общий SQL guard повышен до59; SQL runtime требует CI без skips. Добавлены6 SQL facts: membership/admin, root ACL/enabled/Ready/format/bytes, disabledproject/revocation, bounded cursor pagination/context, invalidquery, exact4field item allowlist и отсутствие persistent writes. SQL opt-in локально отсутствует; compile/skip не runtimeSQL proof.

Deployment этой функции не выполнялся. Уже работающий Windows API остаётся на c3a665a; DB migrations/сервисы/stage не меняются. Новый human catalogue interface IArchiveCatalogBrowse отделён от существующего machine IArchiveCatalog.ReportAsync; старый inventory API/tests сохранены.
