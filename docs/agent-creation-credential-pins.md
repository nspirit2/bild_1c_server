# Agent-local persistence of creation credential pins

## Purpose

Server permit уже закрепляет credential ID / endpoint mapping ID за единственной попыткой. Agent local journal теперь должен сохранять ту же пару как inert correlation metadata, не credentials и не executable grant. Positional constructor binding сохраняется; дополнительные nullable свойства позволяют читать legacy unbound записи. Всегда нужны оба GUID либо обаNULL; Guid.Empty недопустим. Само наличие IDs не подтверждает server receipt, current revocation, endpoint trust или physical target.

## SQLite compatibility and atomicity

Существующая base table не переписывается и не получает выдуманных IDs. Новый sidecar `target_creation_permit_credential_pins` связывает `permit` с immutable `credential` / `endpoint_mapping`. PK и FK к исходному permit; SQLite foreign_keys=ON проверяется при открытии. Sidecar UPDATE/DELETE запрещены triggers. Дополнительный BEFORE INSERT WHEN EXISTS отклоняет duplicate inserts, в том числе INSERT OR REPLACE при recursive_triggers=OFF. Это не защита от владельца DB, который способен удалить triggers или менять schema; corrupt storage не становится authority.

Новый bound Save пишет base binding и sidecar в одной transaction. При failure сохраняется neither partial binding nor orphan pin. Get LEFT JOIN восстанавливает оба pins со строгой проверкой canonical D и nonempty IDs. Legacy rows без sidecar остаются null-pair и непригодны для будущего исполнения. Ошибочная half-pair/format отклоняется, не нормализуется в unbound.

Replay сравнивает весь immutable record: смена credential, mapping или bound/unbound всегда conflict, включая CreationInFlight и ReconciliationRequired. Exact replay — `NewlyRecorded=false`; не начало новой попытки и не повтор CREATE. Запись pins не меняет state/revision intent, не освобождает slots, не сбрасывает quarantine и не отменяет durable one-attempt barrier.

## Boundaries

Нет секретных значений, decrypt/resolver, server transport или HTTP/worker consumer. Credentials продолжают храниться только encrypted в service SQL; здесь только IDs. Existing local DB owner/ACL/single-owner serialization requirements сохраняются. Не отправлять local receipt напрямую в process runner. Future verified transport должен сверить pins с server-issued permit и intent, а future mutation — online authority, nonrevoked credentials, held namespace и fresh SQL/RAC observations.

## Verification

58новыхlocalSQLitecasesпрошли: migration/reopen безbase rewrite; recordequality/replayвовсехstates; badpairsдоIO; constraints/FK/triggersиORREPLACE; atomicrollbackобеихтаблиц+retry; malformed/partial/present-bothnullreader. Existinglegacypermitregressionпрошёл. Независимыйfinalsource/testreviewбезblockers; nonblockinggap: malformedreaderfixturesweakenschema намеренно, direct malformedINSERTs вproductionCHECK отдельнонепроверяются. ПолныйlocalRelease1617passed/0failed,229SQLexplicitlyskipped. CIafterpushотдельно. Live/stand не изменяются.