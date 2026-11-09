# Central database storage for target credentials

## Owner decision and protection

Владелец указал хранить credentials централизованно в БД сервиса; доступы относятся к тестовым средам. Секреты не хранятся открытым текстом: четыре значения (RAC user/password, SQL user/password) сериализуются в строгий UTF-8 envelope и шифруются ASP.NET Data Protection. В SQL сохраняется ciphertext и metadata, не четыре plaintext-колонки. Domain purpose v1 дополнительно привязан к credential ID и canonical cluster / canonical SQL server / endpoint mapping ID. Перенос ciphertext между bindings или смена protector не должны расшифровывать запись.

Ключи находятся **вне БД credentials**. Существующий API использует DataProtectionDirectory с service-only ACL и Windows DPAPI key protection, application name BuildSystem.Api; production Linux намеренно не поддержан текущей key configuration. Для будущего подключения storage следует использовать именно этот provider, не ephemeral provider тестов. Backup/restore БД без соответствующего keyring не восстанавливает credentials. Потеря keyring — потеря возможности decrypt; оператор обязан защищать и резервировать оба компонента независимо. Это не отменяет секреты инфраструктуры bootstrap (SQL connection/service identity и key protection), необходимые до открытия БД.

## Bounded internal storage port

Default-off administrative application port без HTTP/UI/agent delivery. Current DB Administrator проверяется внутри serializable transaction под общим agent-control lock, включая metadata reads и revoke. Actor.IsAdministrator сам по себе не полномочие. Store создаёт новый ID однократно: существующий ID всегда conflict, без overwrite, secret-comparison replay или decrypt. Rotation в будущем создаёт новый credential ID и явно обновляет потребителя; этот slice не имеет active selector, resolver или assignment. Несколько IDs на один mapping не означают выбор последнего или новый grant.

Metadata-only ответы содержат binding, state, revision и SQL UTC timestamps; ciphertext/credentials в ответ не входят. Stored означает только сохранение, не разрешение использовать секрет. Revoke с expected revision переводит Stored/1 в Revoked/2, сохраняет ciphertext и binding; нет unrevoke/reset/delete. Выданный ранее секрет невозможно отозвать одним DB-флагом — отзыв блокирует будущую выдачу, а внешние пароли меняются отдельно. Сейчас secret-delivery отсутствует.

Audit содержит action/actor/credential ID/time без input, ciphertext, логинов или паролей. Secrets/input/options/records имеют redacted diagnostic representations. Plaintext byte arrays очищаются best effort; immutable managed strings нельзя гарантированно обнулить. Не передавать secret DTO в generic JSON/logging/middleware. При будущем HTTP import обязательны current-admin CSRF/body limits и запрет request-body logging; здесь HTTP route не добавляется.

EF guards запрещают изменение anchors/ciphertext/created-at и удаление; SQL CHECK constraints контролируют GUIDs, bounded ciphertext, state/revision/UTC timestamps. Это не защита от владельца SQL DB, который может менять схему/ключи; database administration остаётся trusted boundary. Shared global lock защищает кооперативные app mutations, не произвольного DB writer.

## No implicit migration or target access

Существующие observation DPAPI credentials и integrated SQL identity не переносятся автоматически; не расширяются их привилегии. Новая таблица готовится migration-кодом, но стенд и его schema/config/секреты не изменяются. API/DI wiring, admin UI, секретная выдача assigned agent по exact operation/permit/attempt и provisioning/rotation workflow остаются отдельными этапами. Никакого CREATE/restore/drop или подключения к целевым средам.

## Verification

28 offline protection cases + 2 disabled/admin-port offline cases прошли; 6 opt-in SQL metadata facts добавлены (без opt-in локально skipped). Полный local Release1542passed/0failed,218 SQLexplicitlyskipped; integration251. Независимыйreviewбезblockingfindings после замены stream writer на собственный fixed IBufferWriter без growth; очищаются собственные byte buffers, не обещается удаление library-internal scratch/immutable strings. Добавлен FrameworkReference Microsoft.AspNetCore.App, locked restore прошёл без обновления package lockfiles. EF migration сгенерирована через explicit design connection, не применялась на стенде. CIevidence afterpush отдельно. Disposable metadata SQL в CI не является тестовой target DB для 1С.