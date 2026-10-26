# Список stage-операций

Human authenticated GET `/api/stage-operations?limit=25&state=Leased&cursor=...` возвращает bounded operator metadata. Machine bearer не даёт доступа к human endpoint. Владелец видит только свои окружения с enabled project и текущим membership; Administrator видит все доступные stage records, включая disabled project. Query не выдаёт нового execution authority и не меняет lifecycle.

## Запрос

- limit:1..100, default25;0 и101 отклоняются400.
- state: отсутствует либо exact job.State `Pending`, `Leased`, `StageCompleted`, `NeedsAttention`. Это **не** фильтр ReconciliationCode.
- cursor: непрозрачный nextCursor из предыдущей страницы с тем же actor/admin role и state filter. Максимум512 ASCII base64url символов. Malformed/noncanonical/version/filter/actor mismatch даёт400 InvalidStageListQuery.

Ответ StageOperationsPage: items, nextCursor (null в конце), observedAt (SQL UTC). Каждый item содержит environment/request/job IDs, job/environment/reservation states, stagingBytes, unsettled flag, createdAt. Нет owner/agent identity, путей, hash, token, snapshot, SQL targets и свободного diagnostic text. Для подробного lifecycle используйте GET `/api/test-environments/{id}/stage-status`.

## Pagination

Keyset order: job.CreatedAt DESC, затем lowercase GUID text с SQL BIN2 collation DESC. Нельзя смешивать SQL uniqueidentifier ordering с .NET Guid.CompareTo. Cursor фиксирует последний возвращённый sort key, version, actor/admin и filter; SQL всегда заново применяет текущую авторизацию. Cursor unsigned: не является секретом или capability, его подделка позволяет лишь изменить position внутри текущего authorized query. Не использовать курсор как proof владения объектом.

Одна data projection query с Take(limit+1), без OFFSET, общего COUNT и N+1 detailed-status lookup. SQL UTC — отдельный scalar query. Целая страница читается coherent в Serializable read-only transaction и execution strategy retry; SaveChanges, sweep, audit writes, reset, cleanup и scheduler applocks отсутствуют.

Между страницами нет общего snapshot. Новые/изменённые/удалённые строки и смена ACL/filter membership могут изменить состав следующей страницы. Статичная выборка с tied createdAt не должна терять или дублировать records. Чтобы обновить список после stage transition, начать с первой страницы.

Output и materialization bounded, но это не гарантия bounded SQL work: выражение LOWER(CONVERT(GUID)) COLLATE может требовать sort/scan, для больших объёмов нужен отдельный индексируемый pagination key/план запроса. Такая schema/index оптимизация не маскируется под выполненную. Corrupt reciprocal env/request/job/reservation bindings исключены из списка; direct stage-status даёт409 для диагностируемой inconsistency. Поэтому список не является полным forensic audit/детектором всех corrupt rows.

## Ограничения

ProvisionArchive metadata только; probe jobs не включены. Не добавлены UI, export всех записей, reset/reassign, restore, Ready, GC и release reservation. Отсутствие строки или state StageCompleted не доказывает отсутствие/наличие файла. StagingBytes — held reservation metadata, не measured filesystem usage.

Предыдущий stage-status bc8f7d9 прошёл [Windows/Linux40333918506](https://github.com/nspirit2/bild_1c_server/actions/runs/40333918506) и [SQL40333917821](https://github.com/nspirit2/bild_1c_server/actions/runs/40333917821), оба success по GitHub API. Это не CI текущего списка.

## Проверки

Локально locked restore успешен, Release build0 warnings/0 errors; **300 passed, 0 failed, 53 SQL skipped** (domain39, API55, Agent135, Archive23, Process22, Pipeline26). Новые8 локальных HTTP/application cases проверяют human-only auth, query forwarding/defaults, invalidlimit/cursor/state до SQL и HTTP400 nonnumeric limit. Новые6 SQL facts скомпилированы/обнаружены, но без SQL opt-in явно skipped. ToQueryString подтвердил server-side TOP(limit+1), LOWER(CONVERT(GUID)) COLLATE BIN2 и совпадающее predicate/order; это translation check, не runtimeSQL proof. Статическое read-only review не выявило дополнительных серьёзных auth/cursor/keyset/read-only ошибок; unsigned cursor не заменяет текущую авторизацию, а ограниченный output не гарантирует ограниченный SQL scan/sort. SQL suite +6 facts, guard53 без skips; локально SQL opt-in отсутствует. SQLtranslation pagination выражения проверяется отдельно от реального runtimeSQL proof.
