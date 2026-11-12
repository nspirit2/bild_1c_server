# Exact RAC registration readback: synthetic contract only

> Историческое описание первого synthetic-only среза. Позже по запросу владельца выполнен отдельный read-only probe и добавлен explicit observed-version profile; актуальные границы и provenance см. [RAC 8.3.22.2557 profile](registration-readback-8.3.22.2557.md). Старый default synthetic profile остаётся неизменным; утверждения ниже «live-команды не выполнялись» относятся к первоначальному срезу, не ко всей последующей истории.

Продолжение принятой conservative ownership policy: saved parsed ACK сам не устанавливает владение. Следующий компонент проверяет, что отдельный ответ registration readback сообщает тот же infobase UUID, reservation-generated name и ожидаемую SQL server/database связь с запретом scheduled jobs.

**Это synthetic contract profile, не подтверждённая совместимость с реальной версией RAC.** Нового production host/CLI/worker wiring нет. Observer по умолчанию выключен; никакие live-команды не выполнялись, ownership/restore acceptance не добавлены.

## Scope и readback

InfobaseRegistrationReadbackScope содержит operation/fence/incarnation/reservation, canonical cluster/SQL IDs, отдельные RAC/SQL observation mappings, attempt/claim, infobase UUID из сохранённого ACK, observationId и точные ожидаемые имена/SQL literal. Canonical cluster ID НЕ physical RAC UUID: последний отдельно задаётся доверенной конфигурацией observer. ObservationId — корреляция запроса, не freshness/replay proof.

Profile `rac-registration-contract-v1` принимает ровно шесть полей: infobase, name, dbms, db-server, db-name, scheduled-jobs-deny. Contract spelling `MSSQLServer` и `on` пока НЕ подтверждены real-version fixture. Duplicate/missing/unknown fields, malformed UUID, controls, bare CR, пустые записи, превышение16384 characters/лимитов отвергаются fixed errors. CRLF/LF допустимы, один terminal newline необязателен. Строгое сопоставление SQL literal Ordinal: никаких alias resolution, case folding, DNS inference или обнаружения physical SQL identity.

Result parser-only, immutable, без stdout/stderr/secret material. Executable/FreshAuthority/OwnershipEstablished/RestoreAllowed/RetryAllowed=false; ScheduledJobsBlocked=true только как разобранное statement; RequiresIndependentVerification=true. Parsed statement не доказывает continuing jobs block, lock, independently observed SQL state или владение.

## Default-off observer

Используется уже существующая command shape `infobase info --cluster=<physical UUID> --infobase=<ACK UUID>` с sensitive cluster credentials и trusted endpoint/tool/workdir. Перед runner проверяются все scope/config pins и credential bounds. Capture bounded, thread-safe, stderr/truncated/callback failure/invalid completion/PID/cancellation отвергаются. Whole elapsed budget finite, включает ожидание cooperative runner и parser; не detach process при timeout. TimeProvider нужен для deterministic fixture tests; нет обещания принудительно прервать синхронную/некооперативную работу.

Default-off и profile opt-in — defensive design, НЕ разрешение включать этот synthetic profile для настоящей цели. Нет command line ввода от pipeline/пользовательского API, новых прав или dependency на live stand.

## Saved-ACK bridge

Bridge читает ACK именно из существующего AgentJournal, проверяет trusted agent/canonical cluster/SQL и creation endpoint mapping, подставляет точные immutable intent/binding/claim/reported UUID в новый observation scope. Отсутствие saved ACK не заменяется CLI bool, public record или чтением имени. Journal открывается обычным writable initialization path; forensic physically-read-only обещания нет. Bridge ничего не пишет и не проверяет live targets. Expired claim timestamps сохраняют исторический смысл и не возобновляют authority.

## Что не сделано

Нет ownership promotion, server evidence delivery/acceptance, restore permit, slot release или job unblock. Нет SQL GUID readback: текущий sys.databases fingerprint не становится долговечным доказательством инкарнации. Между RAC и SQL наблюдениями нет атомарного cross-system snapshot или защиты от внешнего администратора.

Для следующего interoperability gate нужны full RAC/platform build и безопасно очищенный реальный stdout `infobase info` для разрешённой тестовой ИБ. Нельзя считать create flags доказательством названий output fields. До real fixture нельзя честно включить exact-registration verifier или заявить independent target verification completed. Получение образца — отдельное разрешённое read-only действие администратора; этот срез его не выполняет.

## Проверки

Pure parser contract fixtures, fake IProcessRunner и локальные SQLite journal fixtures, без network/process/SQL target I/O. Проверяются exact identity/mapping, redaction, deadlines/cancellation, bounds, malformed output, disabled/no-runner paths, actual saved ACK и отсутствие state mutation. Focused111casespassed; полный локальный Release2593passed/0failed,277SQLexplicitlyskipped; Node84passed/0failed. Новые cases: parser57, observer38, saved-ACK bridge16. Независимый final review без блокеров после sanitization clock failures/negative elapsed и final capture-failure gate. Первичная компиляция исправила test-only xUnit Assert.Single(predicate) analyzer без ослабления проверки. Первый CI3572d07: SQL metadata integration зелёный, все новые registration tests прошли; Windows Build упал только в ранее существующем ProcessLaunchDeadlineTests.OptionalNullAndLiveDeadlineBothLaunchNormally: managed fixture startup под нагрузкой превысил helper timeout3sec. Test-only correction задаёт30sec только двум успешным echo fixture runs; исходный manual-clock launch budget1sec и production guards неизменны. Повторный Release/CI фиксируется отдельно; никакая synthetic fixture не выдаётся за real RAC acceptance.