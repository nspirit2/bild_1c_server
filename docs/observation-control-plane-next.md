# Следующий интеграционный срез: observation-only control plane

Первый реализуемый durable foundation описан отдельно: [historical receipt outbox](target-observation-outbox.md). Он ещё не замыкает server issuance/agent dispatch/server acceptance; локальный DTO assignment не является authentic server authority.

После paired coordinator основной gap — не новый safety wrapper, а доставка server-assigned observation до agent и durable receipt обратно. Ниже design, не реализованный endpoint/grant.

## Границы

Источник — существующий immutable nonexecutable restore intent и retained target/artifact pins. Отдельная server-generated observation request identity/fence, assigned approved agent; завершённый stage lease не переиспользуется. Server issuance/claim/report должны использовать текущие agent authorization и immutable bindings, а не arbitrary YAML step. Feature default-off, без production config/deployment.

Agent получает только наблюдение, не authority CREATE/restore. Tool, SQL connection и endpoint resolution остаются operator-owned; конфигурационный mapping GUID не доказывает physical alias identity. Concrete paired observer возвращает только полный outcome после обоих successful чтений. Ошибка не создаёт partial occupancy receipt и не выдаёт Absent.

Durable agent outbox сохраняет полный bounded receipt до HTTP delivery. После restart/lost ACK посылается тот же immutable receipt; повтор delivery не выдаётся за новое наблюдение. Server rejects wrong agent/request/fence/pins/changed replay; acknowledge не освобождает slots/artifact и не меняет stage/env lifecycle. Наблюдения — исторический evidence, не freshness proof; stored BothObservedAbsent не разрешает дальнейшие mutations.

## Acceptance

Один vertical integration сценарий issuance → claim → paired read → local durable report → server acceptance/ACK. Отдельно restart before delivery, lost ACK/exact replay, stale/unassigned reports, conflicting pins, failures/cancel without partial result, отсутствие публичного Executable=true/Ready/lease-promotion. SQL metadata tests только disposable fixture DB. Никаких новых внешних targets/restore.

Прежде actual CREATE останутся отдельные задачи: fresh executable issuance/lease/fence, physical exclusion/canonical mapping и durable creation ownership/reconciliation. Новая попытка на реальном сервере потребует конкретного одобрения владельца. Scheduled jobs deny до/после и после restart остаётся mandatory; security profile нет.
