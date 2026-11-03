# Immutable server assignment для target observation

## Назначение и выключение по умолчанию

Сервис создаёт один observation-only assignment на существующий immutable restore intent. В этом срезе нет HTTP route, machine claim/poll, receipt acceptance и Worker dispatch; feature Enabled=false по умолчанию и DI не запускает наблюдение. Ни stage-only lease, ни assignment не разрешают CREATE/restore.

Запрос содержит environment и expected restore intent ID. Остальные pins сервер извлекает из retained tuple; operation/request GUID генерирует сервер. FencingToken — retained stage correlation label, не новая execution lease/active fence. Agent/intent/environment/stageJob/reservation/incarnation/canonical IDs не выбираются вызывающим. Имя derive bs_test_<reservation N>. RAC/SQL endpoint mapping IDs берутся из operator-owned pool mapping, defensive copy options; это не discovery physical aliases.

## Текущие проверки и replay

Owner/admin + текущая enabled project membership; полный retained intent/stage/artifact/source/profile/snapshot/accounting lifecycle через общий validator. Дополнительно current root/pool enabled, DT/jobs support, approved assigned agent и scope mappings, current ACL root↔project/pool↔project/profile. Active/NeedsAttention lease блокирует assignment. Serializable tx использует общий agent-control → root → pool lock порядок; execution не выдаётся.

Повтор запроса возвращает исходный assignment только после повторной текущей авторизации/согласованности. Drift endpoint mapping или любого сохранённого pin даёт409, а не repin. Один request на intent навсегда; EF update/delete запрещены, SQL UNIQUE и CHECK Executable=0/jobsblocked=1. Assignment не освобождает удерживаемые артефакты/слоты, не меняет reservation quota, job/environment/request/lease states и не записывает Ready.

Независимый read-only review service/model не обнаружил actionable ACL/replay/authority ошибок. Отдельная миграция20261103104323_TargetObservationAssignment создаёт только TargetObservations metadata table и constraints; design-time generation не подключается к production. Первичная generation до завершения model hookup отказала с missing PK без создания миграции; после ConfigureTargetObservationModel generation и model parity успешны.

Полный локальный Release **877passed/0failed**, SQL146explicitly skipped без opt-in; Windows Server2019 + SQL2019 **146/146passed**,0failed/0skipped. TRX `E:\BuildSystem\TestRuns\server-observation-20261103\results\ustiantsev.is_SK-CICD-1C-01_2026-11-03_13_45_23.trx`. EF has-pending-model-changes no changes, locked restore и diff check successful. Portable RID-only lock updates не войдут в commit. Production IIS/metadata/агент не обновлялись; disposable SQL fixture создаёт/удаляет только собственные bs_ci_metadata_* DB.

## Проверки

SQL metadata tests используют только disposable fixture bs_ci_metadata_* DB, stage completion/restore intent создаются как metadata, без внешних SQL/1С targets или /RestoreIB. Нужны concurrency/replay/current ACL/current config/historical cleanup/source hash и DB constraints regressions. Model/API tests проверяют default-off DI без DB IO, sync/async immutability и migration parity.

Следующий интеграционный шаг — dedicated observation claim/report/ACK, строгая AgentToken scope и durable доставки receipt. Текущий assignment DTO/локальный outbox не заменяют аутентификацию или актуальность evidence. Scheduled-jobs-deny=on до/после restore и после restart обязателен; security profile не mandatory gate.
