# Agent addressed HTTPS transport и explicit durable outbox delivery

Этот срез добавляет отдельный транспортный primitive, но не подключает Worker, poll, scheduler или автоматическое наблюдение/restore.

## HTTPS transport

Client получает trusted root HTTPS BaseUri, expected AgentId и credential provider. Production handler не следует redirect и не использует cookies; запросы адресуются только двум fixed observation routes. Bearer credential получается заново на запрос, не пишется в shared headers/journal/log. Custom HttpMessageHandler для тестов — trusted dependency, не sandbox от злонамеренного handler.

Response читается bounded, status только200, JSON строгий: no unknown/duplicate members, immutable assignment pins/name/safety flags и ACK receipt/request/state совпадают. Getter-only false/true нельзя считать проверкой wire flags: raw JSON safety values проверяются отдельно до deserialize. ACK Recorded — historical persistence, не execution grant и не freshness.

Timeout сочетает linked CTS и monotonic elapsed checks. Это deadline detection, не hard preemption произвольного handler/provider, игнорирующего cancellation. Исключения транспорта фиксированные, без URL/responsebody/credential/inner exception details. Response bounds не означают полное управление allocations внешнего handler/decompressor.

## Explicit outbox delivery

Одноразовый bounded DeliverAsync читает существующие pending reports из SQLite journal, последовательно отправляет неизменённый payload и только после exact ACK помечает его ACKed. При потере ACK/timeout/error payload остаётся pending; новый observation ID и readback не создаются. Уже ACKed history сохраняется. Partial batch: предыдущие ACKed остаются, ошибочный и последующие ждут retry. Повторный параллельный вызов на одном instance отклоняется; между процессами нет физического cross-process delivery lease, exact immutable server dedup остаётся обязательным.

Outbox не вызывает assignment GET и не создаёт/переназначает assignment. Адресное получение assignment через transport ещё должно пройти отдельную host orchestration в будущем. Отзыв credentials/current bindings может блокировать историческую доставку — нет обхода через локальный ACK.

Scheduled-jobs-deny=on остаётся mandatory до/после restore и после restart; security profile не mandatory gate. Production/config/schema/target DB не менялись.

Независимый review выявил conditional resource cleanup gap при late stream acquisition после cancellation: response мог быть disposed до eventual stream task completion. Исправлено cleanup eventual stream и наблюдением late task fault; это отдельная resource hygiene мера, не обещание hard preemption provider. SendAsync late success тоже освобождается, fault должен наблюдаться без включения secret в exception.

Полный локальный Release **1012passed/0failed**, SQL161explicitly skipped без opt-in; Agent tests **407passed** (новых transport/outbox97cases). Locked restore и diff check успешны. Первичный compile поймал CS8619 в null-ACK test fake; исправлена явная nonnullable Task generic с intentional null!, негативная проверка сохранена. SQL schema/server/services не менялись в этом срезе; предыдущий f485a57 CI Windows/Ubuntu и SQL163 полностью зелёный. Windows транспортные тесты будут дополнительно проверены после push в обычном CI, отдельный live сетевой стенд не запускался.

Commit7bba569: [Build41627452313](https://github.com/nspirit2/bild_1c_server/actions/runs/41627452313) success Windows/Ubuntu; [SQL41627452278](https://github.com/nspirit2/bild_1c_server/actions/runs/41627452278) success163/163.

## Проверки

Fake-handler transport tests и real SQLite journal tests проверяют fixed paths, HTTPS/root/config bounds, credentials rotation, wire JSON/ACK/limits/deadline failures, immutable retransmission/restart, partial ACK/cancellation/concurrency. Это не network TLS E2E, live adapter invocation и не автоматический restore.
